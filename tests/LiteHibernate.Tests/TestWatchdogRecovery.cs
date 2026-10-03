using System.Diagnostics;
using LiteHibernate.Infrastructure;
using LiteHibernate.Windows;

namespace LiteHibernate.Tests;

// Only launches this test harness and writes fake power values/notifications.
internal sealed class TestWatchdogRecovery(Storage storage, string mode) : IWatchdogRecovery
{
    public Process Start(WatchdogRestartContext context)
    {
        var original = storage.Read<SchemeBackup>(Path.Combine(storage.DirectoryPath, "original-power.json"))!;
        Program.Equal(original, new JournalTestPowerApi(storage).Read(original.Scheme));
        Program.True(!File.Exists(storage.JournalPath));
        var attemptsPath = Path.Combine(storage.DirectoryPath, "restart-attempts.json");
        storage.Write(attemptsPath, storage.Read<int>(attemptsPath) + 1);
        if (mode == "launch-error") throw new IOException("Simulated restart launch failure.");
        var start = new ProcessStartInfo(System.Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--restarted-watchdog-test", context.WatchdogPid.ToString(), context.WatchdogCreated.ToString(), context.Prefix, storage.DirectoryPath, mode })
            start.ArgumentList.Add(arg);
        var process = Process.Start(start)!;
        storage.Write(Path.Combine(storage.DirectoryPath, "restarted-process.json"), new RestartedProcess(process.Id, process.StartTime.ToFileTimeUtc()));
        return process;
    }
    public void NotifyFailure(string message)
    {
        var path = Path.Combine(storage.DirectoryPath, "notifications.json");
        var notifications = storage.Read<List<string>>(path) ?? [];
        notifications.Add(message); storage.Write(path, notifications);
    }
    public static async Task<int> RunRestarted(string[] args)
    {
        var context = new WatchdogRestartContext(int.Parse(args[1]), long.Parse(args[2]), args[3]);
        var storage = new Storage(args[4]);
        var mode = args[5];
        using var watchdog = new Watchdog(context);
        await watchdog.StartAsync(storage); // Adopt the original watchdog, without spawning another.
        var power = new PowerSettings(storage, new JournalTestPowerApi(storage));
        if (mode != "manual") power.Enable();
        if (mode == "die-before-ready") return 2;
        if (mode == "startup-error") { context.SignalFailed(); return 2; }
        using var release = EventWaitHandle.OpenExisting(context.Prefix + ".release-child");
        if (mode != "timeout")
        {
            context.SignalStarted();
            using var ready = EventWaitHandle.OpenExisting(context.Prefix + ".child-ready"); ready.Set();
        }
        release.WaitOne();
        if (mode == "repeat-crash") return 2;
        watchdog.PrepareForExit(); power.Restore(); watchdog.Disarm();
        return 0;
    }
    internal sealed record RestartedProcess(int Pid, long Created);
}
