using System.Diagnostics;

namespace LiteHibernate.Windows;

public sealed record WatchdogRestartContext(int WatchdogPid, long WatchdogCreated, string Prefix)
{
    public const string Argument = "--watchdog-restart";
    public static WatchdogRestartContext? Parse(string[] args)
    {
        if (args.FirstOrDefault() != Argument) return null;
        if (args.Length != 5 || args[4] != "--tray" || !int.TryParse(args[1], out var pid) || pid <= 0
            || !long.TryParse(args[2], out var created) || created <= 0
            || !args[3].StartsWith(@"Local\LiteHibernate.Watchdog.", StringComparison.Ordinal))
            throw new ArgumentException("Некорректные параметры восстановления LiteHibernate.");
        return new(pid, created, args[3]);
    }
    public void SignalStarted() { using var signal = EventWaitHandle.OpenExisting(Prefix + ".restarted"); signal.Set(); }
    public void SignalFailed() { using var signal = EventWaitHandle.OpenExisting(Prefix + ".failed"); signal.Set(); }
}

public interface IWatchdogRecovery
{
    Process Start(WatchdogRestartContext context);
    void NotifyFailure(string message);
}

public sealed class WatchdogRecovery(Action<string>? notifyFailure = null) : IWatchdogRecovery
{
    public Process Start(WatchdogRestartContext context)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { WatchdogRestartContext.Argument, context.WatchdogPid.ToString(), context.WatchdogCreated.ToString(), context.Prefix, "--tray" })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Не удалось перезапустить LiteHibernate.");
    }
    public void NotifyFailure(string message) => notifyFailure?.Invoke(message);
}
