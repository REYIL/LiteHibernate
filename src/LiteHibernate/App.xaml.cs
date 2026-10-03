using System.Drawing;
using System.Security.Principal;
using System.Windows;
using LiteHibernate.Infrastructure;
using LiteHibernate.UI;
using LiteHibernate.Windows;
using Forms = System.Windows.Forms;

namespace LiteHibernate;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private Forms.NotifyIcon? tray;
    private Icon? trayIcon;
    private MainViewModel? model;
    private MainWindow? window;
    private bool exiting;
    private bool crashed;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--check-runtime"))
        {
            // Packaging smoke check: load WPF/WinForms without creating settings or changing Windows.
            _ = typeof(Forms.NotifyIcon).Assembly.GetName();
            _ = System.Windows.Media.Colors.White;
            Shutdown(); return;
        }
        var storage = new Storage();
        DispatcherUnhandledException += (_, args) => { crashed = true; storage.Log("Необработанная ошибка интерфейса: " + args.Exception, true); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { crashed = true; storage.Log("Необработанная ошибка приложения: " + args.ExceptionObject, true); };
        storage.Log(e.Args.FirstOrDefault() == "--watchdog" ? "Запуск фонового сторожа." : "Запуск LiteHibernate.");
        if (e.Args.FirstOrDefault() == "--watchdog")
        {
            // Keep the WPF dispatcher free to process system broadcasts while the worker
            // blocks on kernel handles without polling or creating windows.
            var recovery = new WatchdogRecovery(message => Dispatcher.Invoke(() =>
            {
                trayIcon ??= TrayTheme.CreateIcon();
                tray ??= new Forms.NotifyIcon { Text = "LiteHibernate", Icon = trayIcon, Visible = true };
                Notify(message, true);
            }));
            var result = await Task.Run(() => Watchdog.Run(e.Args, storage, recovery: recovery));
            if (result != 0 && tray != null) await Task.Delay(6500);
            Shutdown(result);
            return;
        }
        instance = new Mutex(false, @"Local\LiteHibernate.Instance." + WindowsIdentity.GetCurrent().User!.Value);
        bool acquired;
        try { acquired = instance.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { instance.Dispose(); instance = null; Shutdown(); return; }
        WatchdogRestartContext? restartContext = null;
        var startupComplete = false;
        try
        {
            restartContext = WatchdogRestartContext.Parse(e.Args);
            if (restartContext != null) storage.Log("Запуск LiteHibernate после сбоя под наблюдением сторожа.");
            await Task.Run(() => new PowerSettings(storage, new WindowsPowerApi()).Restore());
            var settings = storage.LoadSettings();
            model = new(storage, settings, watchdogRestart: restartContext);
            model.Notification += (message, error) =>
            {
                if (restartContext != null && !startupComplete) { storage.Log("Сообщение при восстановлении: " + message, error); return; }
                Notify(message, error);
            };
            window = new(model);
            MainWindow = window;
            await model.InitializeAsync();
            if (restartContext != null && !model.InitializationSucceeded)
                throw new IOException("Не удалось восстановить работу LiteHibernate и применить настройки крышки.");
            var menu = TrayTheme.CreateMenu();
            menu.Items.Add("Открыть LiteHibernate", null, (_, _) => ShowWindow());
            var toggle = menu.Items.Add(model.ToggleLabel, null, async (_, _) =>
            {
                if (model.LidAvailable && !model.PowerRestorePending && !model.IsRunning) await model.SetEnabledAsync(!model.Enabled);
            });
            var hibernate = menu.Items.Add("Перейти в гибернацию", null, async (_, _) => await model.StartScenarioAsync(false));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
            foreach (Forms.ToolStripItem item in menu.Items) item.Padding = new Forms.Padding(10, 7, 10, 7);
            menu.Opening += (_, _) =>
            {
                toggle.Text = model.ToggleLabel; toggle.Enabled = model.LidAvailable && !model.PowerRestorePending && !model.IsRunning;
                hibernate.Enabled = !model.IsRunning;
            };
            trayIcon = TrayTheme.CreateIcon();
            tray = new Forms.NotifyIcon { Text = "LiteHibernate", Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (_, _) => ShowWindow();
            if (!e.Args.Contains("--tray")) ShowWindow();
            restartContext?.SignalStarted();
            startupComplete = true;
            storage.Log(restartContext == null ? "LiteHibernate готов к работе." : "LiteHibernate успешно восстановил работу после сбоя.");
        }
        catch (Exception ex)
        {
            storage.Log(ex.ToString(), true);
            if (restartContext != null)
            {
                try { restartContext.SignalFailed(); }
                catch (Exception signal) { storage.Log("Не удалось сообщить сторожу об ошибке запуска: " + signal, true); }
            }
            if (model != null)
            {
                try { await model.ExitAsync(); }
                catch (Exception recovery) { storage.Log("Ошибка восстановления при запуске: " + recovery, true); }
            }
            if (restartContext == null) System.Windows.MessageBox.Show(ex.Message, "LiteHibernate", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private void Notify(string message, bool error)
    {
        if (tray != null)
        {
            tray.BalloonTipTitle = "LiteHibernate"; tray.BalloonTipText = message;
            tray.BalloonTipIcon = error ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info;
            tray.ShowBalloonTip(6000);
        }
        else System.Windows.MessageBox.Show(message, "LiteHibernate", MessageBoxButton.OK, error ? MessageBoxImage.Error : MessageBoxImage.Information);
    }
    private void ShowWindow()
    {
        if (window == null || exiting) return;
        window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        try
        {
            if (model != null) await model.ExitAsync();
            if (window != null) { window.AllowClose = true; window.Close(); }
            Shutdown();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Не удалось восстановить настройки", MessageBoxButton.OK, MessageBoxImage.Error);
            exiting = false; ShowWindow();
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        model?.RestoreOnShutdown(intentional: !crashed && e.ApplicationExitCode == 0);
        var menu = tray?.ContextMenuStrip;
        tray?.Dispose(); menu?.Dispose(); trayIcon?.Dispose(); model?.Dispose();
        if (instance != null) { instance.ReleaseMutex(); instance.Dispose(); }
        base.OnExit(e);
    }
}
