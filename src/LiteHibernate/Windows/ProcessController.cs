using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using LiteHibernate.Core;
using LiteHibernate.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace LiteHibernate.Windows;

public sealed record ProcessInfo(int Pid, int ParentPid, long Created, string Path, long? Ram, bool HasVisibleWindow);
public sealed class ProcessInventory
{
    private readonly string userSid = WindowsIdentity.GetCurrent().User!.Value;
    private readonly int sessionId = Process.GetCurrentProcess().SessionId;
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "dwm", "explorer", "sihost", "fontdrvhost", "audiodg", "Memory Compression", "Secure System"
    };
    public static bool IsSelectablePath(string path) =>
        Path.IsPathFullyQualified(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
        && !ProtectedNames.Contains(Path.GetFileNameWithoutExtension(path))
        && !string.Equals(Path.GetFullPath(path), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ProcessInfo> Capture() => Capture(null, null);
    internal IReadOnlyList<ProcessInfo> Capture(IReadOnlyCollection<string>? requiredPaths, IReadOnlySet<int>? trackedParents)
    {
        var visible = new HashSet<int>();
        Native.EnumWindows((window, _) =>
        {
            if (Native.IsWindowVisible(window)) { Native.GetWindowThreadProcessId(window, out var pid); visible.Add(pid); }
            return true;
        }, IntPtr.Zero);
        using var snapshot = Native.CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw Native.Error("Не удалось получить список процессов.");
        var entry = new Native.ProcessEntry { Size = (uint)Marshal.SizeOf<Native.ProcessEntry>(), Exe = "" };
        var result = new List<ProcessInfo>();
        if (!Native.Process32First(snapshot, ref entry)) throw Native.Error("Не удалось прочитать список процессов.");
        do
        {
            var pid = (int)entry.Pid;
            if (pid == 0 || !Native.ProcessIdToSessionId(pid, out var session) || session != sessionId) continue;
            bool required = requiredPaths?.Any(path => string.Equals(Path.GetFileName(path), entry.Exe, StringComparison.OrdinalIgnoreCase)) == true
                || trackedParents?.Contains((int)entry.ParentPid) == true;
            // Keep a synchronizable handle so a process exiting during discovery is distinguishable
            // from a live process whose executable or security information cannot be read.
            using var handle = Native.OpenProcess(0x101000, false, pid);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (required && error != 87) throw Native.Error($"Нет доступа к выбранному процессу {entry.Exe}.", error);
                continue;
            }
            if (HasExited(handle)) continue;
            if (!OwnedByCurrentUser(handle, required)) continue;
            if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _))
            {
                ThrowIfRequiredAndAlive(handle, required, $"Не удалось проверить выбранный процесс {entry.Exe}.");
                continue;
            }
            var name = new StringBuilder(32768);
            var size = name.Capacity;
            if (!Native.QueryFullProcessImageName(handle, 0, name, ref size))
            {
                ThrowIfRequiredAndAlive(handle, required, $"Не удалось определить EXE выбранного процесса {entry.Exe}.");
                continue;
            }
            var path = name.ToString();
            if (!IsSelectablePath(path)) continue;
            if (!Native.IsProcessCritical(handle, out var critical))
            {
                ThrowIfRequiredAndAlive(handle, required, "Не удалось проверить защиту выбранного процесса.");
                continue;
            }
            if (critical)
            {
                if (requiredPaths?.Contains(path, StringComparer.OrdinalIgnoreCase) == true)
                    throw new InvalidOperationException("Выбранное приложение содержит критический процесс Windows.");
                continue;
            }
            long? ram = null;
            try { using var process = Process.GetProcessById(pid); ram = process.WorkingSet64; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { }
            result.Add(new(pid, (int)entry.ParentPid, created, path, ram, visible.Contains(pid)));
        } while (Native.Process32Next(snapshot, ref entry));
        var enumerationError = Marshal.GetLastWin32Error();
        if (enumerationError != 18) throw Native.Error("Список процессов прочитан не полностью.", enumerationError);
        return result;
    }
    private static bool HasExited(SafeProcessHandle process, uint timeout = 0)
    {
        var status = Native.WaitForSingleObject(process, timeout);
        if (status == uint.MaxValue) throw Native.Error("Не удалось проверить завершение процесса.");
        return status == 0;
    }
    private static void ThrowIfRequiredAndAlive(SafeProcessHandle process, bool required, string message)
    {
        // Preserve the failed metadata call's error before checking the process handle.
        var error = Marshal.GetLastWin32Error();
        // Metadata can disappear while Windows is still finishing process teardown.
        // Skip it only after confirmed exit; failures for live processes remain errors.
        if (required && !HasExited(process, 100)) throw Native.Error(message, error);
    }
    private bool OwnedByCurrentUser(SafeProcessHandle process, bool required)
    {
        if (!Native.OpenProcessToken(process, 8, out var token))
        {
            ThrowIfRequiredAndAlive(process, required, "Не удалось проверить владельца выбранного процесса.");
            return false;
        }
        using (token)
        {
            Native.GetTokenInformation(token, 1, IntPtr.Zero, 0, out var size);
            if (size <= 0) { ThrowIfRequiredAndAlive(process, required, "Не удалось прочитать владельца процесса."); return false; }
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!Native.GetTokenInformation(token, 1, buffer, size, out _))
                { ThrowIfRequiredAndAlive(process, required, "Не удалось прочитать владельца процесса."); return false; }
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value == userSid;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public static IReadOnlyList<ProcessInfo> ExpandTree(IEnumerable<ProcessInfo> roots, IReadOnlyList<ProcessInfo> all)
    {
        var found = roots.ToDictionary(p => p.Pid);
        bool changed;
        do
        {
            changed = false;
            foreach (var process in all)
                if (!found.ContainsKey(process.Pid) && found.TryGetValue(process.ParentPid, out var parent) && process.Created >= parent.Created)
                { found.Add(process.Pid, process); changed = true; }
        } while (changed);
        return found.Values.ToArray();
    }
}

public sealed class ProcessController(ProcessInventory inventory, Storage? storage = null) : IProcessController
{
    public Task<IShutdownSession> CreateSessionAsync(IReadOnlyList<AppRule> rules, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        rules = rules.Select(rule => rule.NormalizePath()).ToArray();
        if (rules.Any(r => !ProcessInventory.IsSelectablePath(r.ExecutablePath)))
            throw new InvalidOperationException("Выбрано недопустимое или защищённое приложение.");
        return Task.FromResult<IShutdownSession>(new ShutdownSession(inventory, rules, storage == null ? null : storage.Log));
    }

    private sealed class TrackedProcess(ProcessInfo info, SafeProcessHandle handle) : IDisposable
    {
        public ProcessInfo Info { get; } = info;
        public SafeProcessHandle Handle { get; } = handle;
        public bool CloseSent { get; set; }
        public GracefulExitRequest? ExitRequest { get; private set; }
        public void RequestExit(CancellationToken token, Action<string, bool>? log) => ExitRequest ??= new(Info, token, log);
        public bool IsAlive
        {
            get
            {
                var status = Native.WaitForSingleObject(Handle, 0);
                if (status == uint.MaxValue) throw Native.Error("Не удалось проверить завершение процесса.");
                return status == 258;
            }
        }
        public void Dispose() { ExitRequest?.Dispose(); Handle.Dispose(); }
    }
    private sealed class ShutdownSession(ProcessInventory inventory, IReadOnlyList<AppRule> rules, Action<string, bool>? log) : IShutdownSession
    {
        private readonly Dictionary<(int Pid, long Created), TrackedProcess> tracked = [];
        private readonly Dictionary<string, HashSet<(int Pid, long Created)>> groups = rules.ToDictionary(r => r.Id, _ => new HashSet<(int, long)>());
        public Task<IReadOnlyList<GroupState>> RefreshAsync(CancellationToken token) => Task.Run<IReadOnlyList<GroupState>>(() =>
        {
            token.ThrowIfCancellationRequested();
            var all = inventory.Capture(rules.Select(r => r.ExecutablePath).ToArray(), tracked.Keys.Select(k => k.Pid).ToHashSet());
            var byPid = all.ToDictionary(p => p.Pid);
            foreach (var rule in rules)
            {
                var group = groups[rule.Id];
                foreach (var process in all.Where(p => string.Equals(p.Path, rule.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                    Add(process, group);
                bool changed;
                do
                {
                    changed = false;
                    foreach (var process in all)
                    {
                        var identity = (process.Pid, process.Created);
                        if (group.Contains(identity)) continue;
                        var parent = group.Where(key => key.Pid == process.ParentPid && process.Created >= key.Created).OrderByDescending(key => key.Created).FirstOrDefault();
                        if (parent == default || (byPid.TryGetValue(process.ParentPid, out var currentParent) && currentParent.Created != parent.Created)) continue;
                        if (Add(process, group)) changed = true;
                    }
                } while (changed);
            }
            foreach (var process in tracked.Values)
            {
                token.ThrowIfCancellationRequested();
                if (!process.IsAlive) continue;
                if (process.CloseSent)
                {
                    // Closing a tray app's visible window can merely hide it.
                    if (!all.Any(info => info.Pid == process.Info.Pid && info.HasVisibleWindow)) process.RequestExit(token, log);
                    continue;
                }
                Exception? closeError = null;
                var foundWindow = false;
                Native.EnumWindows((window, _) =>
                {
                    Native.GetWindowThreadProcessId(window, out var pid);
                    if (pid == process.Info.Pid && process.IsAlive
                        && (Native.IsWindowVisible(window)
                            || (Native.GetWindowTextLength(window) > 0
                                && ((Native.GetWindowLong(window, -16) & 0x00C00000) == 0x00C00000
                                    || (Native.GetWindowLong(window, -20) & 0x40000) != 0))))
                    {
                        foundWindow = true;
                        if (!Native.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero))
                        {
                            var code = Marshal.GetLastWin32Error();
                            // A destroyed window is benign; access denied is not.
                            if (code == 5)
                            {
                                closeError = Native.Error($"Windows запрещает штатное закрытие {Path.GetFileName(process.Info.Path)}.", code);
                                return false;
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);
                if (closeError != null) throw closeError;
                process.CloseSent = foundWindow;
                if (!all.Any(info => info.Pid == process.Info.Pid && info.HasVisibleWindow)) process.RequestExit(token, log);
            }
            return groups.Select(group => new GroupState(group.Key, group.Value.Count(key => tracked[key].IsAlive), group.Value.Count > 0)).ToArray();
        }, token);
        private bool Add(ProcessInfo process, HashSet<(int Pid, long Created)> group)
        {
            var key = (process.Pid, process.Created);
            if (!tracked.ContainsKey(key))
            {
                var handle = Native.OpenProcess(0x101000, false, process.Pid);
                if (handle.IsInvalid)
                {
                    var code = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (code == 87) return false; // Already exited.
                    throw Native.Error($"Нет доступа к процессу {Path.GetFileName(process.Path)}.", code);
                }
                if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _))
                { handle.Dispose(); throw Native.Error("Не удалось проверить идентификатор процесса."); }
                if (created != process.Created) { handle.Dispose(); return false; }
                tracked.Add(key, new(process, handle));
            }
            return group.Add(key);
        }
        public Task ForceTerminateAsync(string groupId, CancellationToken token) => Task.Run(() =>
        {
            // Descendants first; tracked handles remain valid when their parent has exited.
            foreach (var key in groups[groupId].OrderByDescending(key => key.Created))
            {
                token.ThrowIfCancellationRequested();
                var process = tracked[key];
                process.ExitRequest?.Dispose();
                if (!process.IsAlive) continue;
                using var handle = Native.OpenProcess(0x101001, false, key.Pid);
                if (handle.IsInvalid)
                {
                    if (!process.IsAlive) continue;
                    throw Native.Error($"Нет прав на завершение {Path.GetFileName(process.Info.Path)}.");
                }
                if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _)) throw Native.Error("Не удалось проверить процесс перед завершением.");
                if (created != key.Created) throw new InvalidOperationException("Идентификатор процесса изменился. Гибернация отменена.");
                if (!Native.IsProcessCritical(handle, out var critical) || critical) throw new InvalidOperationException("Защищённый процесс нельзя завершить.");
                if (!Native.TerminateProcess(handle, 1) && process.IsAlive) throw Native.Error($"Не удалось завершить {Path.GetFileName(process.Info.Path)}.");
            }
        }, token);
        public ValueTask DisposeAsync()
        {
            foreach (var process in tracked.Values) process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
