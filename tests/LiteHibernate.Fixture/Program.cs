using System.Diagnostics;

namespace LiteHibernate.Fixture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var readyName = args.FirstOrDefault(a => a.StartsWith("--ready="))?[8..];
        using var ready = readyName == null ? null : EventWaitHandle.OpenExisting(readyName);
        if (args.Contains("--headless")) { ready?.Set(); Thread.Sleep(Timeout.Infinite); return; }
        if (args.FirstOrDefault(a => a.StartsWith("--child=")) is string childArgument)
        {
            using var childReady = new EventWaitHandle(false, EventResetMode.ManualReset, readyName + ".child");
            var start = new ProcessStartInfo(childArgument[8..]) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--headless"); start.ArgumentList.Add("--ready=" + readyName + ".child");
            using var child = Process.Start(start)!;
            if (!childReady.WaitOne(10000)) return;
        }
        ApplicationConfiguration.Initialize();
        using var form = new FixtureForm { Text = "LiteHibernate isolated test fixture", ShowInTaskbar = false, Opacity = 0, Width = 100, Height = 100 };
        form.Shown += (_, _) => { if (args.Contains("--hidden")) form.Hide(); ready?.Set(); };
        if (args.Contains("--tray")) form.FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; form.Hide(); }
        };
        if (args.Contains("--ignore-close")) form.FormClosing += (_, e) => e.Cancel = true;
        Application.Run(form);
    }
    private sealed class FixtureForm : Form { protected override bool ShowWithoutActivation => true; }
}
