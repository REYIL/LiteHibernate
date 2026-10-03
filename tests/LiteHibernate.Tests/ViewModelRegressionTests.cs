using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Threading;
using LiteHibernate.Core;
using LiteHibernate.Infrastructure;
using LiteHibernate.UI;
using LiteHibernate.Windows;

namespace LiteHibernate.Tests;

internal static class ViewModelRegressionTests
{
    internal static async Task Run(Func<string, Func<Task>, Task> test)
    {
        foreach (var autoStart in new[] { false, true })
            await test($"Windows: denied autostart does not disable lid tracking or fail startup (autostart={autoStart})", () => OnDispatcher(async storage =>
            {
                var watchdog = new FakeWatchdog(); var api = new RegressionTests.FakePower();
                var original = api.Read(api.Active);
                var settings = new AppSettings { LidEnabled = true, AutoStart = autoStart };
                var notifications = 0; var attempts = 0;
                using var model = new MainViewModel(storage, settings, powerMonitor: new LidMonitor(hasLid: true), powerApi: api,
                    setAutoStart: requested =>
                    {
                        attempts++; Program.Equal(autoStart, requested);
                        throw new UnauthorizedAccessException("Simulated Run key access denied.");
                    }, watchdogService: watchdog, hibernationService: new AvailableHibernation());
                model.Notification += (_, error) => { if (error) notifications++; };
                await model.InitializeAsync();
                Program.True(model.InitializationSucceeded && model.Enabled && watchdog.IsRunning && model.WatchdogEnabled);
                Program.True(!model.PowerRestorePending && model.ScenarioCommand.CanExecute(null));
                Program.True(model.HasError && model.Status.Contains("автозапуск"));
                Program.Equal(1, attempts); Program.Equal(1, notifications);
                var saved = storage.LoadSettings(); Program.True(saved.LidEnabled); Program.Equal(autoStart, saved.AutoStart);
                Program.Equal(new SchemeBackup(api.Active, 0, 0), api.Read(api.Active));
                var log = File.ReadAllText(storage.LogPath);
                Program.True(log.Contains("[ERROR]") && log.Contains("UnauthorizedAccessException") && log.Contains("Simulated Run key access denied"));
                await model.SetEnabledAsync(false);
                Program.True(!model.Enabled && watchdog.IsRunning && !storage.LoadSettings().LidEnabled);
                Program.Equal(original, api.Read(api.Active)); Program.True(!File.Exists(storage.JournalPath));
                await model.ExitAsync();
            }));
        await test("Windows: desktop watchdog starts independently and follows its own switch", () => OnDispatcher(async storage =>
        {
            var watchdog = new FakeWatchdog(); var api = new RegressionTests.FakePower();
            using var model = CreateModel(storage, watchdog, api);
            // Initial subscription broadcasts must not check a watchdog that has not started yet.
            await Dispatcher.Yield(DispatcherPriority.Background);
            Program.True(model.WatchdogEnabled); Program.Equal(0, watchdog.Starts);
            await model.InitializeAsync();
            Program.True(model.InitializationSucceeded && !model.LidAvailable && !model.Enabled && model.WatchdogEnabled);
            Program.True(watchdog.IsRunning); Program.Equal(1, watchdog.Starts);
            Program.Equal(0, api.Writes); Program.True(!File.Exists(storage.JournalPath));
            await model.SetEnabledAsync(false);
            Program.True(watchdog.IsRunning); Program.Equal(0, watchdog.Disarms);
            model.WatchdogEnabled = false;
            await Dispatcher.Yield(DispatcherPriority.Background);
            Program.True(!watchdog.IsRunning && !model.WatchdogEnabled && !storage.LoadSettings().WatchdogEnabled);
            model.WatchdogEnabled = true;
            await Dispatcher.Yield(DispatcherPriority.Background);
            Program.True(watchdog.IsRunning && model.WatchdogEnabled); Program.Equal(2, watchdog.Starts);
            await model.ExitAsync();
            Program.True(watchdog.Prepared && !watchdog.IsRunning); Program.Equal(0, api.Writes);
        }));
        await test("Windows: disabling lid automation restores power without disarming the watchdog", () => OnDispatcher(async storage =>
        {
            var watchdog = new FakeWatchdog(); var api = new RegressionTests.FakePower();
            using var model = CreateModel(storage, watchdog, api);
            await model.InitializeAsync();
            var original = api.Read(api.Active);
            // Only the injected fake API is changed. Model an already-enabled lid scenario.
            var power = (PowerSettings)typeof(MainViewModel).GetField("power", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
            power.Enable(); Program.True(model.Enabled);
            await model.SetEnabledAsync(false);
            Program.True(!model.Enabled && watchdog.IsRunning); Program.Equal(0, watchdog.Disarms);
            Program.Equal(original, api.Read(api.Active)); Program.True(!File.Exists(storage.JournalPath));
            await model.ExitAsync();
        }));
        foreach (var watchdogEnabled in new[] { true, false })
            await test($"Windows: partial power restore retries without repeated notifications (watchdog={watchdogEnabled})", () => OnDispatcher(async storage =>
            {
                var watchdog = new FakeWatchdog(); var api = new RegressionTests.FakePower();
                using var model = CreateModel(storage, watchdog, api, watchdogEnabled);
                var notifications = 0; model.Notification += (_, error) => { if (error) notifications++; };
                await model.InitializeAsync();
                var original = api.Read(api.Active); PowerOf(model).Enable();
                api.RestoreFailures = 2;
                await model.SetEnabledAsync(false);
                Program.True(!model.Enabled && model.PowerRestorePending && model.HasError);
                Program.True(File.Exists(storage.JournalPath) && !storage.LoadSettings().LidEnabled);
                Program.True(model.LidAutomationDescription.Contains("10 секунд"));
                Program.True(!model.ToggleEnabledCommand.CanExecute(null) && model.ScenarioCommand.CanExecute(null));
                var timer = (DispatcherTimer)typeof(MainViewModel).GetField("powerTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
                Program.True(timer.IsEnabled); Program.Equal(TimeSpan.FromSeconds(10), timer.Interval);
                Program.Equal(1, notifications);
                var writes = api.Writes;
                await model.SetEnabledAsync(true); Program.Equal(writes, api.Writes);
                await CheckPower(model);
                Program.True(model.PowerRestorePending && model.HasError); Program.Equal(0, api.RestoreFailures);
                Program.Equal(1, notifications); Program.Equal(original.Ac, api.Read(api.Active).Ac);
                Program.True(File.ReadAllText(storage.LogPath).Contains("Повторная попытка восстановления"));
                await CheckPower(model);
                Program.True(!model.Enabled && !model.PowerRestorePending && !model.HasError);
                Program.Equal(original, api.Read(api.Active)); Program.True(!File.Exists(storage.JournalPath));
                Program.Equal(watchdogEnabled, watchdog.IsRunning); Program.Equal(watchdogEnabled, timer.IsEnabled);
                Program.Equal(1, notifications); await model.ExitAsync();
            }));
        await test("Windows: failed power monitoring rollback retries recovery without re-enabling lid tracking", () => OnDispatcher(async storage =>
        {
            var watchdog = new FakeWatchdog(); var api = new RegressionTests.FakePower();
            using var model = CreateModel(storage, watchdog, api);
            await model.InitializeAsync();
            var original = api.Read(api.Active); PowerOf(model).Enable();
            api.Values[api.Active] = original with { Ac = 3, Dc = 3 };
            api.OverrideFailures = api.RestoreFailures = 1;
            await CheckPower(model);
            Program.True(!model.Enabled && model.PowerRestorePending && model.HasError);
            Program.True(watchdog.IsRunning && !storage.LoadSettings().LidEnabled);
            await CheckPower(model);
            Program.True(!model.Enabled && !model.PowerRestorePending && !model.HasError);
            Program.Equal(original, api.Read(api.Active)); Program.True(!File.Exists(storage.JournalPath));
            await model.ExitAsync();
        }));
        await test("Windows: watchdog failures are logged and do not leave a misleading enabled switch", () => OnDispatcher(async storage =>
        {
            var watchdog = new FakeWatchdog { FailStart = true }; var api = new RegressionTests.FakePower();
            using var model = CreateModel(storage, watchdog, api);
            var notifications = 0; model.Notification += (_, error) => { if (error) notifications++; };
            await model.InitializeAsync();
            Program.True(!model.InitializationSucceeded && !model.WatchdogEnabled && !watchdog.IsRunning);
            Program.True(!storage.LoadSettings().WatchdogEnabled && model.ScenarioCommand.CanExecute(null));
            Program.True(File.ReadAllText(storage.LogPath).Contains("Simulated watchdog start failure"));
            watchdog.FailStart = false; model.WatchdogEnabled = true;
            await Dispatcher.Yield(DispatcherPriority.Background);
            Program.True(watchdog.IsRunning && model.WatchdogEnabled);
            watchdog.IsRunning = false;
            var check = typeof(MainViewModel).GetMethod("CheckPowerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)check.Invoke(model, null)!;
            Program.True(!model.WatchdogEnabled && !storage.LoadSettings().WatchdogEnabled);
            Program.True(notifications >= 2); Program.Equal(0, api.Writes);
            await model.ExitAsync();
        }));
        await test("Windows: RAM sorting updates live while pinning preserves positions across refresh and discovery", () => OnDispatcher(async _ =>
        {
            var a = Row("A"); var b = Row("B"); var rows = new ObservableCollection<AppRow> { a, b };
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(rows);
            var order = new ApplicationListOrder(view);
            Program.True(!order.IsPinned && view.IsLiveSorting == true);
            Metrics(a, 100); Metrics(b, 200);
            view.SortDescriptions.Add(new(nameof(AppRow.RamBytes), ListSortDirection.Descending));
            Program.True(view.Cast<AppRow>().SequenceEqual([b, a]));
            Metrics(a, 300); await Dispatcher.Yield(DispatcherPriority.Background);
            Program.True(view.Cast<AppRow>().SequenceEqual([a, b]));
            order.SetPinned(true);
            Metrics(b, 400); a.TimeoutSeconds = 15; a.ReopenAfterHibernate = true;
            await Dispatcher.Yield(DispatcherPriority.Background);
            Program.Equal(400L, b.RamBytes!.Value); Program.True(view.Cast<AppRow>().SequenceEqual([a, b]));
            view.Filter = row => ((AppRow)row).Name == "B"; Program.True(view.Cast<AppRow>().SequenceEqual([b]));
            view.Filter = null; view.Refresh(); Program.True(view.Cast<AppRow>().SequenceEqual([a, b]));
            var c = Row("C"); Metrics(c, 500); rows.Add(c); view.Refresh();
            Program.True(view.Cast<AppRow>().SequenceEqual([a, b, c]));
            order.SortPinned(nameof(AppRow.RamBytes), ListSortDirection.Descending, append: false);
            Program.True(order.IsPinned && view.Cast<AppRow>().SequenceEqual([c, b, a]));
            Metrics(a, 600); await Dispatcher.Yield(DispatcherPriority.Background);
            Program.True(view.Cast<AppRow>().SequenceEqual([c, b, a]));
            order.SetPinned(false); await Dispatcher.Yield(DispatcherPriority.Background);
            Program.True(view.Cast<AppRow>().SequenceEqual([a, c, b]));
            Program.True(view.CustomSort == null && view.IsLiveSorting == true);
            order.SetPinned(true);
            order.SortPinned(nameof(AppRow.Name), ListSortDirection.Ascending, append: false);
            Program.True(view.Cast<AppRow>().SequenceEqual([a, b, c]));
            order.SetPinned(false);
            Program.Equal(nameof(AppRow.Name), view.SortDescriptions.Single().PropertyName);
        }));
    }
    private static PowerSettings PowerOf(MainViewModel model)
        => (PowerSettings)typeof(MainViewModel).GetField("power", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
    private static Task CheckPower(MainViewModel model)
        => (Task)typeof(MainViewModel).GetMethod("CheckPowerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null)!;
    private static MainViewModel CreateModel(Storage storage, FakeWatchdog watchdog, RegressionTests.FakePower api, bool watchdogEnabled = true)
        => new(storage, new AppSettings { WatchdogEnabled = watchdogEnabled }, powerMonitor: new LidMonitor(hasLid: false), powerApi: api,
            setAutoStart: _ => { }, watchdogService: watchdog);
    private static AppRow Row(string name) => new(new AppRule { Name = name, ExecutablePath = @"C:\Apps\" + name + ".exe" }, AppSource.Manual);
    private static void Metrics(AppRow row, long bytes) => row.UpdateMetrics([new(row.Name[0], 0, 1, row.Rule.ExecutablePath, bytes, false)]);
    private static Task OnDispatcher(Func<Storage, Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.ViewModel.Test." + Guid.NewGuid().ToString("N"));
            Exception? failure = null;
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await body(new Storage(directory)); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
            try { Program.CleanupTestDirectory(directory); }
            catch (Exception ex) { failure ??= ex; }
            if (failure == null) completion.TrySetResult(); else completion.TrySetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    private sealed class AvailableHibernation : IHibernationService
    {
        public void EnsureAvailable() { }
        public Task HibernateAsync(CancellationToken token) => throw new InvalidOperationException("This test must not hibernate.");
    }
    private sealed class FakeWatchdog : IWatchdog
    {
        public bool IsRunning { get; set; }
        public bool FailStart, Prepared;
        public int Starts, Disarms;
        public Task StartAsync(Storage storage)
        {
            if (IsRunning) return Task.CompletedTask;
            if (FailStart) throw new IOException("Simulated watchdog start failure.");
            Starts++; IsRunning = true; return Task.CompletedTask;
        }
        public void PrepareForExit() => Prepared = true;
        public void Disarm() { Disarms++; IsRunning = false; }
        public void Dispose() { }
    }
}
