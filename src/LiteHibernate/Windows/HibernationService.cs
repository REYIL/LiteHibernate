using System.Runtime.InteropServices;
using LiteHibernate.Core;

namespace LiteHibernate.Windows;

public sealed class HibernationService(LidMonitor? monitor = null) : IHibernationService
{
    public void EnsureAvailable()
    {
        var capabilities = new byte[256];
        if (!Native.GetPwrCapabilities(capabilities)) throw Native.Error("Не удалось проверить поддержку гибернации.");
        if (capabilities[6] == 0 || capabilities[8] == 0 || capabilities[22] == 1)
            throw new InvalidOperationException("Гибернация недоступна. Включите полную гибернацию в настройках Windows (powercfg /h on). Приложения не закрыты.");
        EnableShutdownPrivilege();
    }
    private static void EnableShutdownPrivilege()
    {
        using var process = Native.OpenProcess(0x1000, false, Environment.ProcessId);
        if (!Native.OpenProcessToken(process, 0x28, out var token)) throw Native.Error("Нет доступа к правам управления питанием.");
        using (token)
        {
            if (!Native.LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) throw Native.Error("Не удалось найти право гибернации.");
            var privileges = new Native.TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!Native.AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) throw Native.Error("Не удалось включить право гибернации.");
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw Native.Error("Windows запрещает гибернацию для этого пользователя.", error);
        }
    }
    public Task HibernateAsync(CancellationToken cancellationToken) => SuspendAndWaitForResumeAsync(token => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!Native.SetSuspendState(true, false, false)) throw Native.Error("Windows не смогла перейти в гибернацию.");
    }, token), cancellationToken);
    private async Task SuspendAndWaitForResumeAsync(Func<CancellationToken, Task> suspend, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (monitor == null) throw new InvalidOperationException("Нет подписки на события пробуждения Windows.");
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResumed() => resumed.TrySetResult();
        monitor.Resumed += OnResumed;
        try
        {
            await suspend(cancellationToken);
            // The API may finish before the dispatcher delivers the resume broadcast.
            // Both success and an actual resume are required before reopening anything.
            await resumed.Task.WaitAsync(cancellationToken);
        }
        finally { monitor.Resumed -= OnResumed; }
    }
}

internal sealed class KeepAwake : IDisposable
{
    private readonly IntPtr handle;
    internal KeepAwake()
    {
        var reason = new Native.PowerReason { Version = 0, Flags = 1, Reason = "LiteHibernate ожидает завершения приложений" };
        handle = Native.PowerCreateRequest(ref reason);
        if (handle == new IntPtr(-1)) throw Native.Error("Не удалось удержать систему активной во время сценария.");
        if (!Native.PowerSetRequest(handle, 1))
        {
            var error = Native.Error("Не удалось приостановить автоматический сон.");
            Native.CloseHandle(handle);
            throw error;
        }
    }
    public void Dispose() { Native.PowerClearRequest(handle, 1); Native.CloseHandle(handle); }
}
