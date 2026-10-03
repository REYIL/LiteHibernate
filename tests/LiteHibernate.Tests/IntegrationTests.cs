using System.Diagnostics;
using LiteHibernate.Core;
using LiteHibernate.Infrastructure;
using LiteHibernate.Windows;

namespace LiteHibernate.Tests;

internal static class IntegrationTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await ViewModelRegressionTests.Run(test);
        var root = FindRoot();
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = outputDirectory.Parent!.Name;
        var fixture = Path.Combine(root, "tests", "LiteHibernate.Fixture", "bin", configuration, outputDirectory.Name, "LiteHibernate.Fixture.exe");
        if (!File.Exists(fixture)) throw new FileNotFoundException("Build the solution before integration tests.", fixture);
        await test("Windows: alternate imported EXE path closes the selected fixture before hibernation", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.Path.Test." + Guid.NewGuid().ToString("N"));
            using var app = Start(fixture);
            try
            {
                var storage = new Storage(directory); var file = Path.Combine(directory, "profile.json");
                new ScenarioProfile { Applications = [Program.Rule(TimeoutPolicy.CancelHibernate, 5, path: fixture.Replace('\\', '/'))] }.Export(storage, file);
                var profile = ScenarioProfile.Import(storage, file);
                var hibernate = new CountingHibernate();
                var runner = new ShutdownCoordinator(new ProcessController(new()), hibernate, new ScenarioClock());
                var result = await runner.RunAsync(profile.Applications, 0, default, default);
                Program.Equal(ScenarioStage.Completed, result.Stage); Program.True(app.HasExited); Program.Equal(1, hibernate.Calls);
            }
            finally { Cleanup(app); Program.CleanupTestDirectory(directory); }
        });
        await test("Windows: old watchdog fallback leaves the manual replacement's power journal untouched", () => TestReplacementWatchdog(fixture));
        await test("Windows: desktop without a lid supports cancellation, manual shutdown and reopening after resume", () => TestDesktopManualHibernate(fixture));
        await test("Windows: metadata failure ignores verified exit but preserves live access errors", () =>
        {
            using var process = Start(fixture);
            try
            {
                // Hold the same kernel process handle across exit, as discovery does after
                // taking a snapshot. Model an EXE lookup failing at that boundary.
                var handle = process.SafeHandle;
                var check = typeof(ProcessInventory).GetMethod("ThrowIfRequiredAndAlive",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                const string message = "Не удалось определить EXE выбранного процесса.";
                System.Runtime.InteropServices.Marshal.SetLastPInvokeError(5);
                try
                {
                    check.Invoke(null, [handle, true, message]);
                    throw new Exception("A live process metadata failure must not be ignored.");
                }
                catch (System.Reflection.TargetInvocationException ex)
                    when (ex.InnerException is System.ComponentModel.Win32Exception error)
                {
                    Program.Equal(5, error.NativeErrorCode);
                    Program.Equal(message, error.Message);
                }
                process.Kill(); // Only this isolated fixture; completion is checked on its retained handle.
                Program.True(process.WaitForExit(5000));
                System.Runtime.InteropServices.Marshal.SetLastPInvokeError(5);
                check.Invoke(null, [handle, true, message]);
                Program.True(!new ProcessInventory().Capture().Any(info => info.Pid == process.Id));
            }
            finally { Cleanup(process); }
            return Task.CompletedTask;
        });
        await test("Windows: gracefully closes all instances of fixture EXE", async () =>
        {
            using var first = Start(fixture); using var second = Start(fixture);
            try
            {
                var hibernate = new CountingHibernate();
                var controller = new ProcessController(new());
                var runner = new ShutdownCoordinator(controller, hibernate, new ScenarioClock());
                var result = await runner.RunAsync([Program.Rule(timeout: 5, path: fixture)], 0, default, default);
                Program.Equal(ScenarioStage.Completed, result.Stage);
                Program.True(first.HasExited && second.HasExited); Program.Equal(1, hibernate.Calls);
            }
            finally { Cleanup(first); Cleanup(second); }
        });
        await test("Windows: reopens a closed fixture only after resume and skips existing instances", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.Reopen.Test." + Guid.NewGuid().ToString("N"));
            using var original = Start(fixture, "--tray");
            var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var storage = new Storage(directory); var inventory = new ProcessInventory();
                var restarter = new ApplicationRestarter(inventory, storage);
                var runner = new ShutdownCoordinator(new ProcessController(inventory), new ControlledHibernate(entered, resumed), new ScenarioClock(), restarter);
                var rule = Program.Rule(timeout: 5, path: fixture) with { ReopenAfterHibernate = true };
                var task = runner.RunAsync([rule], 0, default, default);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Program.True(original.HasExited && !task.IsCompleted);
                Program.True(!inventory.Capture().Any(process => process.Path.Equals(fixture, StringComparison.OrdinalIgnoreCase)));
                resumed.SetResult();
                var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
                Program.Equal(ScenarioStage.Completed, result.Stage); Program.Equal(0, result.ReopenErrors.Count);
                var reopened = inventory.Capture().Where(process => process.Path.Equals(fixture, StringComparison.OrdinalIgnoreCase)).ToArray();
                Program.Equal(1, reopened.Length); Program.True(reopened[0].Pid != original.Id);
                Program.Equal(0, (await restarter.ReopenAsync([rule, rule with { Id = "duplicate" }], default)).Count);
                Program.Equal(1, inventory.Capture().Count(process => process.Path.Equals(fixture, StringComparison.OrdinalIgnoreCase)));
                Program.True(File.ReadAllText(storage.LogPath).Contains("уже работает"));
            }
            finally
            {
                Cleanup(original);
                foreach (var info in new ProcessInventory().Capture().Where(process => process.Path.Equals(fixture, StringComparison.OrdinalIgnoreCase)))
                {
                    using var process = System.Diagnostics.Process.GetProcessById(info.Pid);
                    if (process.StartTime.ToFileTimeUtc() == info.Created) Cleanup(process);
                }
                Program.CleanupTestDirectory(directory);
            }
        });
        await test("Windows: a missing executable does not block reopening other applications", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.ReopenErrors.Test." + Guid.NewGuid().ToString("N"));
            using var alreadyRunning = Start(fixture);
            try
            {
                var storage = new Storage(directory);
                var errors = await new ApplicationRestarter(new(), storage).ReopenAsync(
                    [Program.Rule(path: Path.Combine(directory, "missing.exe")), Program.Rule(path: fixture)], default);
                Program.Equal(1, errors.Count); Program.True(!alreadyRunning.HasExited);
                var log = File.ReadAllText(storage.LogPath);
                Program.True(log.Contains("[ERROR]") && log.Contains("уже работает"));
            }
            finally { Cleanup(alreadyRunning); Program.CleanupTestDirectory(directory); }
        });
        await test("Windows: a tray application exits gracefully before its maximum timeout", async () =>
        {
            using var process = Start(fixture, "--tray");
            try
            {
                var hibernate = new CountingHibernate(); var clock = new ScenarioClock();
                var runner = new ShutdownCoordinator(new ProcessController(new()), hibernate, clock);
                var result = await runner.RunAsync([Program.Rule(TimeoutPolicy.CancelHibernate, 10, path: fixture)], 0, default, default);
                Program.Equal(ScenarioStage.Completed, result.Stage); Program.True(process.HasExited);
                Program.True(clock.Elapsed < TimeSpan.FromSeconds(10)); Program.Equal(1, hibernate.Calls);
            }
            finally { Cleanup(process); }
        });
        await test("Windows: hidden application windows receive graceful close requests", async () =>
        {
            using var process = Start(fixture, "--hidden");
            try
            {
                var hibernate = new CountingHibernate();
                var runner = new ShutdownCoordinator(new ProcessController(new()), hibernate, new ScenarioClock());
                var result = await runner.RunAsync([Program.Rule(TimeoutPolicy.CancelHibernate, 5, path: fixture)], 0, default, default);
                Program.Equal(ScenarioStage.Completed, result.Stage); Program.True(process.HasExited);
                Program.Equal(1, hibernate.Calls);
            }
            finally { Cleanup(process); }
        });
        await test("Windows: stubborn fixture is force terminated after X", async () =>
        {
            using var process = Start(fixture, "--ignore-close");
            try
            {
                var hibernate = new CountingHibernate(); var clock = new ScenarioClock();
                var runner = new ShutdownCoordinator(new ProcessController(new()), hibernate, clock);
                var result = await runner.RunAsync([Program.Rule(timeout: 1, path: fixture)], 0, default, default);
                Program.Equal(ScenarioStage.Completed, result.Stage); Program.True(process.HasExited);
                Program.True(clock.Elapsed >= TimeSpan.FromSeconds(1)); Program.Equal(1, hibernate.Calls);
            }
            finally { Cleanup(process); }
        });
        await test("Windows: orphan child with different EXE stays tracked and is killed", async () =>
        {
            var childExe = Path.Combine(Path.GetDirectoryName(fixture)!, "LiteHibernate.Fixture.Child.exe");
            File.Copy(fixture, childExe, true);
            using var parent = Start(fixture, "--child=" + childExe);
            ProcessInfo[] childInfos = [];
            try
            {
                childInfos = new ProcessInventory().Capture().Where(p => p.Path.Equals(childExe, StringComparison.OrdinalIgnoreCase)).ToArray();
                Program.Equal(1, childInfos.Length);
                var hibernate = new CountingHibernate();
                var runner = new ShutdownCoordinator(new ProcessController(new()), hibernate, new ScenarioClock());
                var result = await runner.RunAsync([Program.Rule(timeout: 1, path: fixture)], 0, default, default);
                Program.Equal(ScenarioStage.Completed, result.Stage); Program.True(parent.HasExited);
                Program.True(!new ProcessInventory().Capture().Any(p => p.Path.Equals(childExe, StringComparison.OrdinalIgnoreCase)));
                Program.Equal(1, hibernate.Calls);
            }
            finally
            {
                Cleanup(parent);
                foreach (var info in childInfos)
                {
                    try { using var child = Process.GetProcessById(info.Pid); if (child.StartTime.ToFileTimeUtc() == info.Created) Cleanup(child); }
                    catch (ArgumentException) { }
                }
                File.Delete(childExe);
            }
        });
        await test("Windows: continue policy leaves only fixture running", async () =>
        {
            using var process = Start(fixture, "--ignore-close");
            try
            {
                var hibernate = new CountingHibernate(); var runner = new ShutdownCoordinator(new ProcessController(new()), hibernate, new ScenarioClock());
                var result = await runner.RunAsync([Program.Rule(TimeoutPolicy.ContinueHibernate, 1, path: fixture)], 0, default, default);
                Program.Equal(ScenarioStage.Completed, result.Stage); Program.True(!process.HasExited); Program.Equal(1, hibernate.Calls);
            }
            finally { Cleanup(process); }
        });
        await test("Windows: cancel policy leaves fixture running and prevents S4", async () =>
        {
            using var process = Start(fixture, "--ignore-close");
            try
            {
                var hibernate = new CountingHibernate(); var runner = new ShutdownCoordinator(new ProcessController(new()), hibernate, new ScenarioClock());
                var result = await runner.RunAsync([Program.Rule(TimeoutPolicy.CancelHibernate, 1, path: fixture)], 0, default, default);
                Program.Equal(ScenarioStage.Cancelled, result.Stage); Program.True(!process.HasExited); Program.Equal(0, hibernate.Calls);
            }
            finally { Cleanup(process); }
        });
        await test("Windows: read-only power API and hibernation capability check", () =>
        {
            var api = new WindowsPowerApi(); var scheme = api.ActiveScheme(); var read = api.Read(scheme);
            Program.True(read.Ac <= 3 && read.Dc <= 3);
            Program.True(api.SchemeExists(scheme)); Program.True(!api.SchemeExists(Guid.NewGuid()));
            // Capability and token checks only; never invoke SetSuspendState or write power settings.
            try { new HibernationService().EnsureAvailable(); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 5 or 1300)
            { Console.WriteLine("INFO Restricted test account lacks shutdown privilege; denial was correctly reported."); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Гибернация недоступна.", StringComparison.Ordinal))
            { Console.WriteLine("INFO This machine does not support hibernation; unavailability was correctly reported."); }
            return Task.CompletedTask;
        });
        await test("Windows: resume refreshes lid state without triggering another shutdown scenario", async () =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    using var monitor = new LidMonitor(hasLid: true);
                    var service = new HibernationService(monitor);
                    var wait = typeof(HibernationService).GetMethod("SuspendAndWaitForResumeAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    var resumedTask = (Task)wait.Invoke(service, [new Func<CancellationToken, Task>(_ => Task.CompletedTask), CancellationToken.None])!;
                    Program.True(!resumedTask.IsCompleted); // A successful request is not a resume notification.
                    var transitions = new List<bool>();
                    var stateUpdates = 0;
                    monitor.LidChanged += transitions.Add;
                    monitor.StateChanged += () => stateUpdates++;
                    var hook = typeof(LidMonitor).GetMethod("Hook", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    var data = System.Runtime.InteropServices.Marshal.AllocHGlobal(24);
                    try
                    {
                        System.Runtime.InteropServices.Marshal.StructureToPtr(new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3"), data, false);
                        System.Runtime.InteropServices.Marshal.WriteInt32(data, 16, 4);
                        void Lid(bool open)
                        {
                            System.Runtime.InteropServices.Marshal.WriteInt32(data, 20, open ? 1 : 0);
                            hook.Invoke(monitor, [IntPtr.Zero, 0x0218, new IntPtr(0x8013), data, false]);
                        }
                        Lid(true); Program.Equal(true, monitor.IsOpen!.Value); Program.Equal(0, transitions.Count);
                        Lid(false); Program.True(transitions.SequenceEqual([false]));
                        hook.Invoke(monitor, [IntPtr.Zero, 0x0218, new IntPtr(0x0012), IntPtr.Zero, false]);
                        Program.Equal(false, monitor.IsOpen!.Value); // Preserve known state while refreshing.
                        Lid(true); Program.Equal(true, monitor.IsOpen!.Value); Program.Equal(1, transitions.Count);
                        hook.Invoke(monitor, [IntPtr.Zero, 0x0218, new IntPtr(0x0007), IntPtr.Zero, false]);
                        Program.Equal(true, monitor.IsOpen!.Value);
                        Lid(false); Program.Equal(false, monitor.IsOpen!.Value); Program.Equal(1, transitions.Count);
                        Lid(true); Program.True(transitions.SequenceEqual([false, true]));
                        var registrations = (ICollection<IntPtr>)typeof(LidMonitor).GetField("notifications", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(monitor)!;
                        Program.Equal(2, registrations.Count); Program.Equal(5, stateUpdates);
                        Program.True(resumedTask.Wait(3000));
                        using var cancel = new CancellationTokenSource();
                        var cancelledWait = (Task)wait.Invoke(service, [new Func<CancellationToken, Task>(_ => Task.CompletedTask), cancel.Token])!;
                        cancel.Cancel();
                        try { cancelledWait.GetAwaiter().GetResult(); throw new Exception("Cancelled resume wait must not succeed."); }
                        catch (OperationCanceledException) { }
                    }
                    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(data); }
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            await completion.Task;
        });
        await test("Windows: exhausted watchdog retry restores fake power after parent crash", () => WatchdogTest(fixture, false));
        await test("Windows: intentionally disarmed watchdog leaves active settings alone", () => WatchdogTest(fixture, true));
        foreach (var mode in new[] { "success", "manual", "repeat-crash", "startup-error", "die-before-ready", "launch-error", "timeout", "intentional", "restore-error" })
            await test("Windows: watchdog restart recovery - " + mode, () => WatchdogRecoveryTest(fixture, mode));
        await test("Windows: installed catalog resolves EXE paths and preserves unresolved entries", async () =>
        {
            var completion = new TaskCompletionSource<IReadOnlyList<CatalogEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.SetResult(new AppCatalog().Installed()); }
                catch (Exception ex) { completion.SetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            var entries = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Program.True(entries.Count > 0);
            Program.True(entries.All(entry => entry.Source == AppSource.Installed && !string.IsNullOrWhiteSpace(entry.Name)
                && (entry.ExecutablePath == null || File.Exists(entry.ExecutablePath) && ProcessInventory.IsSelectablePath(entry.ExecutablePath))));
        });
        await test("Windows: WPF window loads, renders and binds without XAML errors", async () =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.UI.Test." + Guid.NewGuid().ToString("N"));
                try
                {
                    var uiHibernate = new CountingHibernate();
                    var uiController = new UiWaitingController();
                    var uiCoordinator = new ShutdownCoordinator(uiController, uiHibernate, new ScenarioClock());
                    using var vm = new LiteHibernate.UI.MainViewModel(new Storage(directory), new()
                    {
                        Applications =
                        [
                            Program.Rule(id: "Google Chrome", path: @"C:\Apps\Chrome\chrome.exe") with { ReopenAfterHibernate = true },
                            Program.Rule(TimeoutPolicy.CancelHibernate, 60, "Visual Studio Code", @"C:\Apps\VSCode\Code.exe"),
                            Program.Rule(TimeoutPolicy.ContinueHibernate, 15, "Spotify", @"C:\Apps\Spotify\Spotify.exe") with { Selected = false },
                            Program.Rule(id: "Telegram Desktop", path: @"C:\Apps\Telegram\Telegram.exe") with { Selected = false, ManuallyAdded = true }
                        ]
                    }, uiCoordinator);
                    var sampleProcesses = new[]
                    {
                        new ProcessInfo(101, 0, 1, @"C:\Apps\Chrome\chrome.exe", 384 * 1048576L, true),
                        new ProcessInfo(102, 101, 2, @"C:\Apps\Chrome\chrome.exe", 128 * 1048576L, false),
                        new ProcessInfo(103, 0, 3, @"C:\Apps\VSCode\Code.exe", 240 * 1048576L, true)
                    };
                    foreach (var row in vm.Applications) row.UpdateMetrics(sampleProcesses);
                    Program.Equal("≈ 752 MB", vm.EstimatedWriteSavings);
                    vm.Applications[0].Selected = false;
                    Program.Equal("≈ 240 MB", vm.EstimatedWriteSavings);
                    vm.Applications[0].Selected = true;
                    // A selected child also belongs to its selected parent's process group.
                    // Count its RAM once across both selected rules.
                    var overlapping = sampleProcesses.Select(info => info.Pid == 103 ? info with { ParentPid = 101 } : info).ToArray();
                    foreach (var row in vm.Applications) row.UpdateMetrics(overlapping);
                    Program.Equal("≈ 752 MB", vm.EstimatedWriteSavings);
                    foreach (var row in vm.Applications) row.UpdateMetrics(sampleProcesses.Select(info => info.Pid == 101 ? info with { Ram = null } : info).ToArray());
                    Program.Equal("Нет данных", vm.EstimatedWriteSavings);
                    foreach (var row in vm.Applications) row.UpdateMetrics([]);
                    Program.Equal("0 B", vm.EstimatedWriteSavings);
                    foreach (var row in vm.Applications) row.UpdateMetrics(sampleProcesses);
                    var window = new MainWindow(vm);
                    AssertMinimumWindowSize(window);
                    var content = (System.Windows.FrameworkElement)window.Content;
                    window.Content = null;
                    var preview = new System.Windows.Controls.Border
                    {
                        Background = window.Background, Child = content, DataContext = vm, Resources = window.Resources
                    };
                    System.Windows.Documents.TextElement.SetFontFamily(preview, window.FontFamily);
                    System.Windows.Documents.TextElement.SetFontSize(preview, window.FontSize);
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    preview.Measure(new System.Windows.Size(1320, 880)); preview.Arrange(new System.Windows.Rect(0, 0, 1320, 880)); preview.UpdateLayout();
                    var layoutFrame = new System.Windows.Threading.DispatcherFrame();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => layoutFrame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(layoutFrame);
                    var animationFrame = new System.Windows.Threading.DispatcherFrame();
                    var animationTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                    animationTimer.Tick += (_, _) => { animationTimer.Stop(); animationFrame.Continue = false; };
                    animationTimer.Start(); System.Windows.Threading.Dispatcher.PushFrame(animationFrame);
                    preview.UpdateLayout();
                    var grid = Descendants<System.Windows.Controls.DataGrid>(preview).Single();
                    Program.Equal(4, grid.Items.Count);
                    Program.True(Descendants<System.Windows.Controls.CheckBox>(preview).Any(toggle => toggle.Command == vm.ToggleEnabledCommand));
                    Program.Equal(2, vm.SelectedCount); Program.Equal(2, vm.RunningCount);
                    var rowToggle = Descendants<System.Windows.Controls.CheckBox>(grid).First(toggle => toggle.DataContext == vm.Applications[0]);
                    rowToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
                    Program.Equal(1, vm.SelectedCount);
                    rowToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                    var timeout = Descendants<System.Windows.Controls.TextBox>(grid).First(input => input.DataContext == vm.Applications[0]);
                    timeout.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "0"); timeout.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
                    Program.True(System.Windows.Controls.Validation.GetHasError(timeout)); Program.Equal(30, vm.Applications[0].TimeoutSeconds);
                    timeout.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "30"); timeout.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
                    Program.True(!System.Windows.Controls.Validation.GetHasError(timeout));
                    // Typing inside a template must not start a DataGrid edit transaction.
                    grid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(vm.Applications[0], grid.Columns[4]);
                    Program.True(!grid.BeginEdit());
                    Program.True(!((System.ComponentModel.IEditableCollectionView)vm.ApplicationsView).IsEditingItem);
                    vm.Search = "Chrome"; Program.Equal(1, grid.Items.Count);
                    vm.SourceFilter = 2; Program.Equal(1, grid.Items.Count);
                    vm.SourceFilter = 0; vm.Search = "";
                    preview.UpdateLayout();
                    var firstRow = (System.Windows.Controls.DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                    Program.True(!grid.CanUserResizeColumns && grid.Columns.All(column => !column.CanUserResize));
                    Program.True(!grid.Columns.Any(column => Equals(column.Header, "СТАТУС")));
                    Program.Equal("ВОЗОБНОВИТЬ", grid.Columns.Last(column => column.Visibility == System.Windows.Visibility.Visible).Header);
                    foreach (var row in Descendants<System.Windows.Controls.DataGridRow>(grid))
                    {
                        Program.True(row.ContextMenu == null);
                        var name = Descendants<System.Windows.Controls.TextBlock>(row).Single(element => element.Name == "AppName");
                        var tag = Descendants<System.Windows.Controls.Border>(row).Single(element => element.Name == "StatusTag");
                        var remove = Descendants<System.Windows.Controls.Button>(row).Single(element => element.Name == "RemoveAppButton");
                        var nameBounds = name.TransformToAncestor(row).TransformBounds(new System.Windows.Rect(0, 0, name.ActualWidth, name.ActualHeight));
                        var tagBounds = tag.TransformToAncestor(row).TransformBounds(new System.Windows.Rect(0, 0, tag.ActualWidth, tag.ActualHeight));
                        var removeBounds = remove.TransformToAncestor(row).TransformBounds(new System.Windows.Rect(0, 0, remove.ActualWidth, remove.ActualHeight));
                        Program.True(Math.Abs(nameBounds.Top - tagBounds.Top) < 1 && Math.Abs(nameBounds.Height - tagBounds.Height) < 1);
                        Program.True(tagBounds.Left >= nameBounds.Right && tagBounds.Right <= row.ActualWidth);
                        Program.True(removeBounds.Left >= tagBounds.Right && removeBounds.Right <= row.ActualWidth);
                        Program.True(Math.Abs(removeBounds.Top + removeBounds.Height / 2 - nameBounds.Top - nameBounds.Height / 2) < 1);
                        var icon = Descendants<System.Windows.Shapes.Path>(remove).Single(element => element.Name == "RemoveAppIcon");
                        var iconBounds = icon.TransformToAncestor(remove).TransformBounds(icon.Data.Bounds);
                        Program.True(Math.Abs(iconBounds.Left + iconBounds.Width / 2 - remove.ActualWidth / 2) < 0.5);
                        Program.True(Math.Abs(iconBounds.Top + iconBounds.Height / 2 - remove.ActualHeight / 2) < 0.5);
                    }
                    var normalBackground = firstRow.Background;
                    firstRow.IsSelected = true;
                    Program.Equal(normalBackground, firstRow.Background);
                    firstRow.IsSelected = false;
                    var cellBorder = Descendants<System.Windows.Controls.DataGridCell>(grid).First().Template;
                    Program.True(cellBorder.Triggers.Count == 0);

                    // A long list scrolls in pixels, including partial wheel steps.
                    var extraRows = Enumerable.Range(0, 32).Select(index => new LiteHibernate.UI.AppRow(
                        Program.Rule(id: "Scroll " + index, path: @"C:\Apps\Scroll\app" + index + ".exe") with { Selected = false }, AppSource.Manual)).ToArray();
                    foreach (var row in extraRows) vm.Applications.Add(row);
                    preview.UpdateLayout();
                    var viewer = (System.Windows.Controls.ScrollViewer)grid.Template.FindName("DG_ScrollViewer", grid);
                    Program.True(viewer.ScrollableHeight > 100);
                    var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent };
                    grid.RaiseEvent(wheel); preview.UpdateLayout();
                    Program.True(wheel.Handled && Math.Abs(viewer.VerticalOffset - 32) < 1);
                    grid.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -60)
                    { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
                    preview.UpdateLayout(); Program.True(Math.Abs(viewer.VerticalOffset - 48) < 1);
                    viewer.ScrollToTop();
                    foreach (var row in extraRows) vm.Applications.Remove(row);
                    preview.UpdateLayout();
                    var policy = Descendants<System.Windows.Controls.ComboBox>(grid).First(combo => combo.DataContext == vm.Applications[0]);
                    policy.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, TimeoutPolicy.ContinueHibernate);
                    Program.Equal(TimeoutPolicy.ContinueHibernate, vm.Applications[0].Policy);
                    policy.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, TimeoutPolicy.ForceTerminate);
                    Program.True(policy.Template.FindName("PART_Popup", policy) is System.Windows.Controls.Primitives.Popup);
                    // Invoke the actual column header, including values whose formatted strings sort differently.
                    vm.Applications[1].UpdateMetrics([new ProcessInfo(103, 0, 3, @"C:\Apps\VSCode\Code.exe", 1024 * 1048576L, true)]);
                    Program.Equal(512 * 1048576L, vm.Applications[0].RamBytes!.Value);
                    Program.Equal("512 MB", vm.Applications[0].Ram);
                    Program.Equal("1 GB", vm.Applications[1].Ram);
                    var ramHeader = Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid).Single(header => Equals(header.Column?.Header, "RAM"));
                    var headerPresenter = Descendants<System.Windows.Controls.Primitives.DataGridColumnHeadersPresenter>(grid).Single();
                    var presenterPeer = new System.Windows.Automation.Peers.DataGridColumnHeadersPresenterAutomationPeer(headerPresenter);
                    var ramPeer = new System.Windows.Automation.Peers.DataGridColumnHeaderItemAutomationPeer(ramHeader.Content, ramHeader.Column, presenterPeer);
                    var invoke = (System.Windows.Automation.Provider.IInvokeProvider)ramPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
                    invoke.Invoke(); PumpDispatcher();
                    Program.Equal(System.ComponentModel.ListSortDirection.Ascending, ramHeader.Column.SortDirection!.Value);
                    Program.True(grid.Items.Cast<LiteHibernate.UI.AppRow>().Select(row => row.RamBytes).SequenceEqual(new long?[] { 0, 0, 512 * 1048576L, 1024 * 1048576L }));
                    invoke.Invoke(); PumpDispatcher();
                    Program.Equal(System.ComponentModel.ListSortDirection.Descending, ramHeader.Column.SortDirection!.Value);
                    Program.True(grid.Items.Cast<LiteHibernate.UI.AppRow>().Select(row => row.RamBytes).SequenceEqual(new long?[] { 1024 * 1048576L, 512 * 1048576L, 0, 0 }));
                    var firstHeader = Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid).Single(header => header.Column == grid.Columns[0]);
                    var pin = Descendants<System.Windows.Controls.Button>(firstHeader).Single(button => button.Name == "PinListButton");
                    Program.True(!vm.IsListPinned && pin.Command == vm.ToggleListPinCommand);
                    var pinBounds = pin.TransformToAncestor(firstHeader).TransformBounds(new System.Windows.Rect(0, 0, pin.ActualWidth, pin.ActualHeight));
                    Program.True(Math.Abs(pinBounds.Left + pinBounds.Width / 2 - firstHeader.ActualWidth / 2) < 1);
                    var pinPeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(pin);
                    var pinInvoke = (System.Windows.Automation.Provider.IInvokeProvider)pinPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
                    pinInvoke.Invoke(); PumpDispatcher(); Program.True(vm.IsListPinned);
                    var pinnedRows = grid.Items.Cast<LiteHibernate.UI.AppRow>().ToArray();
                    vm.Applications[0].UpdateMetrics([new ProcessInfo(101, 0, 1, @"C:\Apps\Chrome\chrome.exe", 2 * 1024 * 1048576L, true)]);
                    PumpDispatcher(); Program.True(grid.Items.Cast<LiteHibernate.UI.AppRow>().SequenceEqual(pinnedRows));
                    Program.Equal(2 * 1024 * 1048576L, vm.Applications[0].RamBytes!.Value);
                    // A deliberate header click sorts once and keeps the resulting order pinned.
                    invoke.Invoke(); PumpDispatcher(); Program.True(vm.IsListPinned);
                    Program.Equal(System.ComponentModel.ListSortDirection.Ascending, ramHeader.Column.SortDirection!.Value);
                    Program.True(grid.Items.Cast<LiteHibernate.UI.AppRow>().Select(row => row.RamBytes).SequenceEqual(new long?[] { 0, 0, 1024 * 1048576L, 2 * 1024 * 1048576L }));
                    pinInvoke.Invoke(); PumpDispatcher(); Program.True(!vm.IsListPinned);
                    vm.Applications[1].UpdateMetrics([new ProcessInfo(103, 0, 3, @"C:\Apps\VSCode\Code.exe", 3 * 1024 * 1048576L, true)]);
                    PumpDispatcher();
                    Program.True(grid.Items.Cast<LiteHibernate.UI.AppRow>().Select(row => row.RamBytes).SequenceEqual(new long?[] { 0, 0, 2 * 1024 * 1048576L, 3 * 1024 * 1048576L }));
                    vm.ApplicationsView.SortDescriptions.Clear(); ramHeader.Column.SortDirection = null;
                    vm.Applications[1].UpdateMetrics([new ProcessInfo(103, 0, 3, @"C:\Apps\VSCode\Code.exe", 4831838208L, true)]);
                    Program.Equal($"{4.5:0.#} GB", vm.Applications[1].Ram);
                    foreach (var row in vm.Applications) row.UpdateMetrics(sampleProcesses);
                    preview.UpdateLayout();
                    var windowContent = (System.Windows.Controls.Grid)window.FindName("WindowContent");
                    Program.True(windowContent.Clip == null);
                    Program.Equal(new System.Windows.CornerRadius(0), ((System.Windows.Controls.Border)content).CornerRadius);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1320, 880, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(preview);
                    var path = Path.Combine(root, "artifacts", "ui-preview.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(path)) encoder.Save(stream);
                    Program.True(new FileInfo(path).Length > 15000);
                    preview.Measure(new System.Windows.Size(1320, 880)); preview.Arrange(new System.Windows.Rect(0, 0, 1320, 880)); preview.UpdateLayout();
                    var hibernateButton = Descendants<System.Windows.Controls.Button>(preview).Single(button => button.Command == vm.ScenarioCommand);
                    var bounds = hibernateButton.TransformToAncestor(preview).TransformBounds(new System.Windows.Rect(0, 0, hibernateButton.ActualWidth, hibernateButton.ActualHeight));
                    Program.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 1320 && bounds.Bottom <= 880);
                    Program.Equal(40.0, hibernateButton.ActualHeight);
                    var footerButtons = Descendants<System.Windows.Controls.Button>(preview).Where(button =>
                        button.Command == vm.AddCommand || button.Command == vm.ExportCommand || button.Command == vm.ImportCommand || button.Command == vm.ScenarioCommand).ToArray();
                    Program.Equal(4, footerButtons.Length);
                    var footerBounds = footerButtons.Select(button => button.TransformToAncestor(preview).TransformBounds(new System.Windows.Rect(0, 0, button.ActualWidth, button.ActualHeight))).ToArray();
                    Program.True(footerBounds.All(rect => Math.Abs(rect.Top - bounds.Top) < 1 && rect.Right <= 1320));
                    for (var i = 1; i < footerBounds.Length; i++) Program.True(footerBounds[i].Left >= footerBounds[i - 1].Right);
                    Program.True(!grid.Columns.Any(column => Equals(column.Header, "ИСТОЧНИК")));
                    Program.True(grid.Columns.Any(column => Equals(column.Header, "ОЖИДАНИЕ, С")));
                    foreach (var toggle in Descendants<System.Windows.Controls.CheckBox>(preview))
                    {
                        if (toggle.Template.FindName("Track", toggle) is not System.Windows.FrameworkElement track) continue;
                        var knob = (System.Windows.FrameworkElement)toggle.Template.FindName("Knob", toggle);
                        var trackBounds = track.TransformToAncestor(toggle).TransformBounds(new System.Windows.Rect(0, 0, track.ActualWidth, track.ActualHeight));
                        var knobBounds = knob.TransformToAncestor(toggle).TransformBounds(new System.Windows.Rect(0, 0, knob.ActualWidth, knob.ActualHeight));
                        Program.True(trackBounds.Right <= toggle.ActualWidth && knobBounds.Right <= trackBounds.Right - 2);
                    }
                    vm.Search = "no-matching-exe"; Program.Equal(0, grid.Items.Count); vm.Search = ""; Program.Equal(4, grid.Items.Count);
                    Program.Equal("Перейти в гибернацию", vm.ScenarioButtonText);
                    Program.True(!Descendants<System.Windows.Controls.Button>(preview).Any(button => button.Command == vm.CancelCommand));
                    var pausedTask = System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => vm.StartScenarioAsync(false)).Task.Unwrap();
                    var progressFrame = new System.Windows.Threading.DispatcherFrame();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => progressFrame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(progressFrame);
                    Program.True(vm.ScenarioActive);
                    Program.Equal("Отменить гибернацию", vm.ScenarioButtonText);
                    Program.Equal("5 с", vm.ScenarioCounter);
                    Program.True(vm.ScenarioCommand.CanExecute(null));
                    preview.UpdateLayout();
                    var cancelBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1320, 880, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    cancelBitmap.Render(preview);
                    var cancelEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); cancelEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cancelBitmap));
                    using (var stream = File.Create(Path.Combine(root, "artifacts", "ui-cancel-preview.png"))) cancelEncoder.Save(stream);
                    vm.ScenarioCommand.Execute(null);
                    Program.True(!vm.ScenarioCommand.CanExecute(null));
                    var cancelFrame = new System.Windows.Threading.DispatcherFrame();
                    var cancelTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                    var cancelDeadline = DateTime.UtcNow.AddSeconds(3);
                    cancelTimer.Tick += (_, _) => { if (pausedTask.IsCompleted || DateTime.UtcNow >= cancelDeadline) { cancelTimer.Stop(); cancelFrame.Continue = false; } };
                    cancelTimer.Start(); System.Windows.Threading.Dispatcher.PushFrame(cancelFrame);
                    Program.True(pausedTask.IsCompletedSuccessfully && !vm.ScenarioActive);
                    Program.Equal("Перейти в гибернацию", vm.ScenarioButtonText);
                    Program.Equal(0, uiHibernate.Calls);
                    vm.LidDelaySeconds = 0;
                    var waitingTask = System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => vm.StartScenarioAsync(false)).Task.Unwrap();
                    PumpDispatcher();
                    Program.Equal("Ожидание завершения…", vm.ScenarioButtonText);
                    Program.Equal("", vm.ScenarioCounter);
                    Program.True(!vm.ScenarioCommand.CanExecute(null) && !vm.CancelCommand.CanExecute(null));
                    vm.ScenarioCommand.Execute(null);
                    vm.CancelCommand.Execute(null);
                    Program.True(vm.ScenarioActive);
                    var waitFrame = new System.Windows.Threading.DispatcherFrame();
                    var waitTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                    waitTimer.Tick += (_, _) => { waitTimer.Stop(); waitFrame.Continue = false; };
                    waitTimer.Start(); System.Windows.Threading.Dispatcher.PushFrame(waitFrame);
                    uiController.Remaining = 0;
                    var finishFrame = new System.Windows.Threading.DispatcherFrame();
                    var finishTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                    var finishDeadline = DateTime.UtcNow.AddSeconds(3);
                    finishTimer.Tick += (_, _) => { if (waitingTask.IsCompleted || DateTime.UtcNow >= finishDeadline) { finishTimer.Stop(); finishFrame.Continue = false; } };
                    finishTimer.Start(); System.Windows.Threading.Dispatcher.PushFrame(finishFrame);
                    Program.True(waitingTask.IsCompletedSuccessfully && !vm.ScenarioActive);
                    Program.Equal(1, uiHibernate.Calls);
                    var scenarioLog = File.ReadAllLines(new Storage(directory).LogPath);
                    Program.Equal(1, scenarioLog.Count(line => line.Contains("Сценарий [Waiting]")));
                    Program.True(scenarioLog.Any(line => line.Contains("Сценарий [Hibernating]")) && scenarioLog.Any(line => line.Contains("Сценарий [Completed]")));
                    var activeInput = Descendants<System.Windows.Controls.TextBox>(grid).First(input => input.DataContext == vm.Applications[0]);
                    activeInput.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "45");
                    var refreshOperation = System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(async () => await vm.RefreshRunningAsync()).Task.Unwrap();
                    var refreshFrame = new System.Windows.Threading.DispatcherFrame();
                    var refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                    var refreshDeadline = DateTime.UtcNow.AddSeconds(5);
                    refreshTimer.Tick += (_, _) => { if (refreshOperation.IsCompleted || DateTime.UtcNow >= refreshDeadline) { refreshTimer.Stop(); refreshFrame.Continue = false; } };
                    refreshTimer.Start(); System.Windows.Threading.Dispatcher.PushFrame(refreshFrame);
                    if (!refreshOperation.IsCompletedSuccessfully || vm.HasError)
                        throw new Exception($"Refresh did not complete cleanly ({refreshOperation.Status}): {vm.Status}");
                    Program.True(ReferenceEquals(activeInput, Descendants<System.Windows.Controls.TextBox>(grid).First(input => input.DataContext == vm.Applications[0])));
                    Program.Equal("45", activeInput.Text);
                    activeInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
                    Program.Equal(45, vm.Applications[0].TimeoutSeconds);
                    window.AllowClose = true; window.Close();
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally
                {
                    Program.CleanupTestDirectory(directory);
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        });
        await test("Windows: popup mouse selection and single-click focus dismissal", TestPopupMouseInteraction);
        await test("Windows: maximized window and caption buttons stay inside the monitor work area", TestMaximizedWindowBounds);
        await test("Windows: removal requires confirmation and cancellation preserves the saved profile", TestRemovalConfirmation);
        await test("Windows: deleted applications persist, stay excluded on discovery and restore through the simplified table", () => TestDeletedApplications(fixture));
        await test("Windows: adding and restoring share EXE validation and never create duplicates", () => TestRestoreValidation(fixture));
    }
    private static Task TestDeletedApplications(string fixture)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.Deleted.Test." + Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            using var running = Start(fixture);
            try
            {
                var storage = new Storage(directory);
                var confirmed = false;
                var prompts = 0;
                using var vm = new LiteHibernate.UI.MainViewModel(storage, new()
                {
                    Applications = [Program.Rule(TimeoutPolicy.CancelHibernate, 47, "Archived app", fixture) with { ReopenAfterHibernate = true },
                        Program.Rule(id: "Keep me", path: @"C:\keep.exe")]
                }, confirmRemoval: _ => true, confirmRestoration: row =>
                {
                    prompts++;
                    var dialog = new LiteHibernate.UI.ApplicationListDialog(row.Name, restoring: true)
                    { Opacity = 0, ShowActivated = false, Owner = window };
                    var prompt = (System.Windows.Controls.TextBlock)dialog.FindName("RemovalPrompt");
                    var cancel = (System.Windows.Controls.Button)dialog.FindName("CancelRemovalButton");
                    var accept = (System.Windows.Controls.Button)dialog.FindName("ConfirmRemovalButton");
                    Program.True(cancel.IsDefault && cancel.IsCancel && prompt.Text.Contains(row.Name) && prompt.Text.StartsWith("Показать"));
                    Program.Equal("Показать", accept.Content);
                    dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(confirmed ? accept : cancel);
                        ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
                    }));
                    return dialog.ShowDialog() == true;
                });
                window = new MainWindow(vm) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
                var surface = (System.Windows.FrameworkElement)window.Content;
                surface.DataContext = vm; window.DataContext = null;
                window.Show(); PumpDispatcher();
                var grid = Descendants<System.Windows.Controls.DataGrid>(surface).Single();
                var archived = vm.Applications[0];
                vm.RemoveCommand.Execute(archived);
                Program.True(archived.IsDeleted && ReferenceEquals(archived, vm.DeletedApplications.Single()));
                Program.Equal(1, vm.SelectedCount); Program.Equal("0 B", vm.EstimatedWriteSavings);
                Program.True(!vm.ApplicationsView.Cast<LiteHibernate.UI.AppRow>().Contains(archived));
                Program.Equal(archived.Rule.Id, storage.LoadSettings().DeletedApplications.Single().Id);
                using (var reloaded = new LiteHibernate.UI.MainViewModel(storage, storage.LoadSettings()))
                {
                    Program.Equal(1, reloaded.DeletedApplications.Count);
                    var merge = typeof(LiteHibernate.UI.MainViewModel).GetMethod("Merge", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    merge.Invoke(reloaded, [new CatalogEntry(archived.Name, fixture, AppSource.Installed)]);
                    Program.True(!reloaded.Applications.Any(row => row.Rule.ExecutablePath.Equals(fixture, StringComparison.OrdinalIgnoreCase)));
                }
                WaitWithDispatcher(window.Dispatcher.InvokeAsync(async () => await vm.RefreshRunningAsync()).Task.Unwrap());
                Program.Equal("Running", archived.Status); Program.True(archived.RamBytes > 0);
                Program.True(!vm.Applications.Any(row => row.Rule.ExecutablePath.Equals(fixture, StringComparison.OrdinalIgnoreCase)));
                vm.SourceFilter = 4; PumpDispatcher();
                Program.Equal(1, grid.Items.Count);
                Program.True(grid.Columns.Where(column => column.Visibility == System.Windows.Visibility.Visible).Select(column => column.Header)
                    .SequenceEqual(new object[] { "ПРИЛОЖЕНИЕ", "RAM", "ПОКАЗАТЬ" }));
                var archivedRow = (System.Windows.Controls.DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                Program.True(!Descendants<System.Windows.Controls.CheckBox>(archivedRow).Any(check => check.IsVisible));
                Program.True(!Descendants<System.Windows.Controls.TextBox>(archivedRow).Any(input => input.IsVisible));
                Program.True(!Descendants<System.Windows.Controls.ComboBox>(archivedRow).Any(combo => combo.IsVisible));
                Program.True(!Descendants<System.Windows.Controls.Button>(archivedRow).Any(button => button.Command == vm.RemoveCommand && button.IsVisible));
                var restore = Descendants<System.Windows.Controls.Button>(archivedRow).Single(button => button.Name == "RestoreAppButton");
                Program.True(restore.IsVisible && restore.IsEnabled);
                vm.Search = "NOT PRESENT"; Program.True(vm.ApplicationsView.IsEmpty);
                vm.Search = "aRcHiVeD"; Program.Equal(1, grid.Items.Count);
                vm.Search = ""; PumpDispatcher();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(FindRoot(), "artifacts", "ui-deleted-preview.png"))) encoder.Save(stream);
                var savedBeforeCancel = File.ReadAllText(storage.SettingsPath);
                var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(restore);
                var invoke = (System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!;
                invoke.Invoke(); PumpDispatcher();
                Program.Equal(1, prompts); Program.Equal(1, vm.DeletedApplications.Count);
                Program.Equal(savedBeforeCancel, File.ReadAllText(storage.SettingsPath));
                confirmed = true; invoke.Invoke(); PumpDispatcher();
                Program.Equal(2, prompts); Program.Equal(0, vm.DeletedApplications.Count);
                var restored = vm.Applications.Single(row => row.Rule.ExecutablePath.Equals(fixture, StringComparison.OrdinalIgnoreCase));
                Program.True(!restored.IsDeleted && restored.Selected && restored.ReopenAfterHibernate);
                Program.Equal(47, restored.TimeoutSeconds); Program.Equal(TimeoutPolicy.CancelHibernate, restored.Policy);
                Program.Equal(0, storage.LoadSettings().DeletedApplications.Count);
                Program.True(vm.ApplicationsView.IsEmpty);
                vm.SourceFilter = 0; PumpDispatcher();
                Program.True(grid.Items.Cast<LiteHibernate.UI.AppRow>().Contains(restored));
                Program.Equal("ВОЗОБНОВИТЬ", grid.Columns.Last(column => column.Visibility == System.Windows.Visibility.Visible).Header);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally
            {
                if (window != null) { window.AllowClose = true; window.Close(); }
                Cleanup(running); Program.CleanupTestDirectory(directory);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
    private static Task TestRestoreValidation(string fixture)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.RestoreValidation.Test." + Guid.NewGuid().ToString("N"));
            try
            {
                var storage = new Storage(directory);
                var missingPath = Path.Combine(directory, "missing.exe");
                var invalidPath = Path.Combine(directory, "file.txt"); File.WriteAllText(invalidPath, "Not an EXE");
                var protectedPath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), "explorer.exe");
                string? chosen = null;
                using var vm = new LiteHibernate.UI.MainViewModel(storage, new()
                {
                    DeletedApplications = [Program.Rule(id: "Missing", timeout: 43, path: missingPath),
                        Program.Rule(id: "Protected", path: protectedPath), Program.Rule(id: "Duplicate", timeout: 77, path: fixture)]
                }, confirmRestoration: _ => true, chooseExecutable: _ => chosen);
                vm.Save(); var savedBefore = File.ReadAllText(storage.SettingsPath);
                var missing = vm.DeletedApplications[0]; var protectedApp = vm.DeletedApplications[1]; var duplicate = vm.DeletedApplications[2];
                vm.RestoreCommand.Execute(missing); // Cancelling the file picker preserves the deleted entry.
                Program.Equal(savedBefore, File.ReadAllText(storage.SettingsPath));
                foreach (var path in new[] { missingPath, invalidPath, "relative.exe", protectedPath })
                {
                    chosen = path;
                    vm.AddCommand.Execute(null); Program.True(vm.HasError && vm.Applications.Count == 0);
                    Program.Equal(savedBefore, File.ReadAllText(storage.SettingsPath));
                    vm.RestoreCommand.Execute(missing); Program.True(vm.HasError && vm.DeletedApplications.Count == 3);
                    Program.Equal(savedBefore, File.ReadAllText(storage.SettingsPath));
                }
                Program.True(File.Exists(protectedPath));
                vm.RestoreCommand.Execute(protectedApp);
                Program.True(vm.HasError && vm.Status.Contains("защищено") && vm.DeletedApplications.Count == 3);
                chosen = fixture;
                vm.RestoreCommand.Execute(missing); // Relink a moved executable and clear its duplicate tombstone.
                var restored = vm.Applications.Single(row => row.Rule.ExecutablePath.Equals(fixture, StringComparison.OrdinalIgnoreCase));
                Program.Equal(1, vm.DeletedApplications.Count);
                Program.Equal(43, restored.TimeoutSeconds); Program.Equal(missing.Rule.Id, restored.Rule.Id);
                Program.True(!vm.RestoreCommand.CanExecute(duplicate));
                // Background discovery may add unrelated processes after restoring an EXE.
                // Duplicate checks must concern that EXE, not the whole machine's catalog.
                var merge = typeof(LiteHibernate.UI.MainViewModel).GetMethod("Merge", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var unrelatedPath = Path.Combine(directory, "other-running-app.exe");
                merge.Invoke(vm, [new CatalogEntry("Other running app", unrelatedPath, AppSource.Running)]);
                Program.True(vm.Applications.Any(row => row.Rule.ExecutablePath == unrelatedPath));
                vm.AddCommand.Execute(null);
                Program.True(ReferenceEquals(restored, vm.Applications.Single(row => row.Rule.ExecutablePath.Equals(fixture, StringComparison.OrdinalIgnoreCase))));
                Program.Equal(43, restored.TimeoutSeconds);
                Program.Equal(1, storage.LoadSettings().Applications.Count);
                Program.Equal(restored.Rule.Id, storage.LoadSettings().Applications.Single().Id);
                Program.Equal(protectedApp.Rule.Id, storage.LoadSettings().DeletedApplications.Single().Id);
                // An already active EXE is merged when restoring a stale deleted record.
                using var stale = new LiteHibernate.UI.MainViewModel(storage, new()
                { Applications = [Program.Rule(id: "Existing", path: fixture)], DeletedApplications = [Program.Rule(id: "Stale", path: fixture)] }, confirmRestoration: _ => true);
                var existing = stale.Applications.Single();
                stale.RestoreCommand.Execute(stale.DeletedApplications.Single());
                Program.True(ReferenceEquals(existing, stale.Applications.Single(row => row.Rule.ExecutablePath.Equals(fixture, StringComparison.OrdinalIgnoreCase))));
                Program.Equal(0, stale.DeletedApplications.Count);
                using var imported = new LiteHibernate.UI.MainViewModel(storage, new()
                {
                    DeletedApplications = [Program.Rule(id: "imported", path: missingPath), Program.Rule(id: "same-path", path: fixture),
                        Program.Rule(id: "unrelated", path: @"C:\unrelated.exe"), Program.Rule(id: "unresolved", path: "")]
                });
                merge.Invoke(imported, [new CatalogEntry("unresolved", fixture, AppSource.Installed)]);
                Program.Equal(0, imported.Applications.Count);
                var apply = typeof(LiteHibernate.UI.MainViewModel).GetMethod("ApplyProfile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                apply.Invoke(imported, [new ScenarioProfile { Applications = [Program.Rule(id: "imported", path: fixture)] }]);
                Program.Equal(1, imported.Applications.Count); Program.Equal(2, imported.DeletedApplications.Count);
                Program.True(imported.DeletedApplications.Select(row => row.Rule.Id).SequenceEqual(new[] { "unrelated", "unresolved" }));
                var savedValidProfile = File.ReadAllText(storage.SettingsPath);
                try { apply.Invoke(imported, [new ScenarioProfile { Applications = [Program.Rule(timeout: 0)] }]); throw new Exception("Invalid import must fail."); }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
                Program.Equal(savedValidProfile, File.ReadAllText(storage.SettingsPath));
                Program.Equal(1, imported.Applications.Count); Program.Equal(2, imported.DeletedApplications.Count);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { Program.CleanupTestDirectory(directory); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static void WaitWithDispatcher(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= deadline) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
        Program.True(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    private static Task TestRemovalConfirmation()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.Remove.Test." + Guid.NewGuid().ToString("N"));
            try
            {
                var storage = new Storage(directory);
                var confirmed = false;
                var prompts = 0;
                using var vm = new LiteHibernate.UI.MainViewModel(storage, new()
                {
                    Applications = [Program.Rule(id: "Delete me"), Program.Rule(id: "Keep me", path: @"C:\keep.exe")]
                }, confirmRemoval: row =>
                {
                    prompts++;
                    var dialog = new LiteHibernate.UI.ApplicationListDialog(row.Name)
                    { Opacity = 0, ShowActivated = false };
                    var cancel = (System.Windows.Controls.Button)dialog.FindName("CancelRemovalButton");
                    var prompt = (System.Windows.Controls.TextBlock)dialog.FindName("RemovalPrompt");
                    Program.True(cancel.IsDefault && cancel.IsCancel && prompt.Text.Contains(row.Name));
                    dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        var button = confirmed ? (System.Windows.Controls.Button)dialog.FindName("ConfirmRemovalButton") : cancel;
                        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
                        ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
                    }));
                    return dialog.ShowDialog() == true;
                });
                var removed = vm.Applications[0];
                var kept = vm.Applications[1];
                vm.SelectedRow = kept;
                vm.Save();
                var savedBefore = File.ReadAllText(storage.SettingsPath);
                vm.RemoveCommand.Execute(removed);
                Program.Equal(1, prompts); Program.Equal(2, vm.Applications.Count);
                Program.Equal(savedBefore, File.ReadAllText(storage.SettingsPath));
                Program.True(ReferenceEquals(kept, vm.SelectedRow));
                confirmed = true;
                vm.RemoveCommand.Execute(removed);
                Program.Equal(2, prompts); Program.True(ReferenceEquals(kept, vm.Applications.Single()));
                Program.Equal(kept.Rule.Id, storage.LoadSettings().Applications.Single().Id);
                Program.True(ReferenceEquals(kept, vm.SelectedRow));
                vm.RemoveCommand.Execute(removed); Program.Equal(2, prompts);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { Program.CleanupTestDirectory(directory); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static Task TestMaximizedWindowBounds()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.Maximize.Test." + Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                using var vm = new LiteHibernate.UI.MainViewModel(new Storage(directory), new());
                window = new MainWindow(vm) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
                window.DataContext = null; // Do not start discovery or change power settings.
                window.Show(); PumpDispatcher();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var originalBounds = window.RestoreBounds;
                var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                window.WindowState = System.Windows.WindowState.Maximized;
                PumpDispatcher(); window.UpdateLayout();
                Program.True(GetWindowRect(handle, out var bounds));
                if (bounds.Left != work.Left || bounds.Top != work.Top || bounds.Right != work.Right || bounds.Bottom != work.Bottom)
                    throw new Exception($"Maximized bounds ({bounds.Left}, {bounds.Top}, {bounds.Right}, {bounds.Bottom}) exceed work area {work}.");
                var captionButtons = Descendants<System.Windows.Controls.Button>(window)
                    .Where(button => System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(button)).ToArray();
                Program.Equal(3, captionButtons.Length);
                foreach (var button in captionButtons)
                {
                    var topLeft = button.PointToScreen(new System.Windows.Point());
                    var bottomRight = button.PointToScreen(new System.Windows.Point(button.ActualWidth, button.ActualHeight));
                    Program.True(topLeft.X >= work.Left && topLeft.Y >= work.Top && bottomRight.X <= work.Right && bottomRight.Y <= work.Bottom);
                }
                window.WindowState = System.Windows.WindowState.Normal;
                PumpDispatcher();
                Program.Equal(originalBounds, window.RestoreBounds);
                window.WindowState = System.Windows.WindowState.Maximized;
                PumpDispatcher();
                Program.True(GetWindowRect(handle, out bounds));
                Program.Equal(work.Bottom, bounds.Bottom);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally
            {
                if (window != null) { window.AllowClose = true; window.Close(); }
                Program.CleanupTestDirectory(directory);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect rectangle);
    private static Task TestPopupMouseInteraction()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.Popup.Test." + Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                using var vm = new LiteHibernate.UI.MainViewModel(new Storage(directory), new()
                { Applications = [Program.Rule(path: @"C:\Apps\Test\app.exe")] });
                window = new MainWindow(vm) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
                var surface = (System.Windows.FrameworkElement)window.Content;
                // Keep automatic discovery stopped while exercising real HWND-backed popups.
                surface.DataContext = vm; window.DataContext = null;
                window.Show(); PumpDispatcher();
                var frame = (System.Windows.Controls.Border)surface;
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                Program.Equal(new System.Windows.CornerRadius(0), frame.CornerRadius);
                Program.Equal(new System.Windows.CornerRadius(0), chrome.CornerRadius);
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    Program.Equal(new System.Windows.Thickness(0), frame.BorderThickness);
                    Program.Equal(new System.Windows.Thickness(1), chrome.GlassFrameThickness);
                    var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    Program.Equal(0, DwmGetWindowAttribute(handle, 33, out var preference, sizeof(uint)));
                    Program.Equal(2u, preference);
                    Program.Equal(0, DwmGetWindowAttribute(handle, 1, out var frameRendering, sizeof(uint)));
                    Program.Equal(1u, frameRendering); // DWMWA_NCRENDERING_ENABLED is readable; BORDER_COLOR is set-only.
                }
                else Program.Equal(new System.Windows.Thickness(1), frame.BorderThickness);
                var grid = Descendants<System.Windows.Controls.DataGrid>(surface).Single();
                var row = (System.Windows.Controls.DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                Program.True(row.ContextMenu == null);
                var reopen = Descendants<System.Windows.Controls.CheckBox>(row).Single(element => element.Name == "ReopenCheckBox");
                Program.True(reopen.IsEnabled && reopen.IsChecked == false);
                var reopenPeer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(reopen);
                var reopenToggle = (System.Windows.Automation.Provider.IToggleProvider)reopenPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)!;
                reopenToggle.Toggle();
                PumpDispatcher();
                Program.True(vm.Applications[0].ReopenAfterHibernate && reopen.IsChecked == true);
                vm.Save(); Program.True(new Storage(directory).LoadSettings().Applications[0].ReopenAfterHibernate);
                foreach (var checkBox in Descendants<System.Windows.Controls.CheckBox>(grid))
                {
                    var peer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(checkBox);
                    var toggle = (System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)!;
                    var initial = checkBox.IsChecked;
                    for (var click = 0; click < 2; click++)
                    {
                        System.Windows.Input.FocusManager.SetFocusedElement(window, checkBox);
                        System.Windows.Input.Keyboard.Focus(checkBox);
                        Program.True(ReferenceEquals(checkBox, System.Windows.Input.FocusManager.GetFocusedElement(window)));
                        toggle.Toggle();
                        var checkedAfterClick = checkBox.IsChecked;
                        MouseClickEvent(checkBox, System.Windows.Input.Mouse.MouseUpEvent);
                        PumpDispatcher();
                        Program.Equal(checkedAfterClick!.Value, checkBox.IsChecked!.Value);
                        Program.True(!checkBox.IsKeyboardFocusWithin && ReferenceEquals(surface, System.Windows.Input.FocusManager.GetFocusedElement(window)));
                    }
                    Program.Equal(initial!.Value, checkBox.IsChecked!.Value);
                }
                AssertMinimumWindowSize(window);
                var pin = Descendants<System.Windows.Controls.Button>(grid).Single(button => button.Name == "PinListButton" && button.IsVisible);
                var pinPeer = new System.Windows.Automation.Peers.ButtonAutomationPeer(pin);
                var pinInvoke = (System.Windows.Automation.Provider.IInvokeProvider)pinPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!;
                for (var click = 0; click < 2; click++)
                {
                    System.Windows.Input.FocusManager.SetFocusedElement(window, pin);
                    System.Windows.Input.Keyboard.Focus(pin);
                    Program.True(ReferenceEquals(pin, System.Windows.Input.FocusManager.GetFocusedElement(window)));
                    pinInvoke.Invoke(); PumpDispatcher();
                    MouseClickEvent(pin, System.Windows.Input.Mouse.MouseUpEvent); PumpDispatcher();
                    Program.Equal(click == 0, vm.IsListPinned);
                    Program.True(!pin.IsKeyboardFocusWithin && ReferenceEquals(surface, System.Windows.Input.FocusManager.GetFocusedElement(window)));
                }
                // Keyboard activation retains focus so the button stays usable without a mouse.
                System.Windows.Input.FocusManager.SetFocusedElement(window, pin);
                System.Windows.Input.Keyboard.Focus(pin);
                pinInvoke.Invoke(); PumpDispatcher();
                Program.True(vm.IsListPinned && ReferenceEquals(pin, System.Windows.Input.FocusManager.GetFocusedElement(window)));
                pinInvoke.Invoke(); PumpDispatcher(); Program.True(!vm.IsListPinned);
                foreach (var header in Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid).Where(header => header.Column?.Header != null))
                {
                    Program.True(header.ToolTip is string text && text.Length > 0);
                    Program.True(header.Template.FindName("PART_RightHeaderGripper", header) == null);
                    var rightClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
                    { RoutedEvent = System.Windows.UIElement.PreviewMouseRightButtonUpEvent };
                    header.RaiseEvent(rightClick); PumpDispatcher();
                    Program.True(!rightClick.Handled && header.ContextMenu == null);
                }
                var combo = Descendants<System.Windows.Controls.ComboBox>(grid).Single();
                var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
                popup.Child.Opacity = 0;
                foreach (var choice in LiteHibernate.UI.MainViewModel.Policies)
                {
                    combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, true);
                    PumpDispatcher(); popup.Child.UpdateLayout();
                    Program.True(combo.IsDropDownOpen && popup.IsOpen);
                    var item = (System.Windows.Controls.ComboBoxItem)combo.ItemContainerGenerator.ContainerFromItem(choice);
                    Program.True(item != null);
                    MouseClickEvent(item!, System.Windows.Input.Mouse.PreviewMouseDownEvent);
                    Program.True(combo.IsDropDownOpen); // Preview must not swallow a popup item click.
                    MouseClickEvent(item!, System.Windows.Input.Mouse.MouseDownEvent);
                    MouseClickEvent(item!, System.Windows.Input.Mouse.MouseUpEvent);
                    PumpDispatcher();
                    Program.Equal(choice.Value, vm.Applications[0].Policy);
                    Program.True(!combo.IsDropDownOpen && !combo.IsKeyboardFocusWithin);
                    Program.True(ReferenceEquals(surface, System.Windows.Input.FocusManager.GetFocusedElement(window)));
                }
                combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, true);
                PumpDispatcher();
                Program.True(combo.IsDropDownOpen);
                // Captured outside clicks arrive with the ComboBox itself as OriginalSource.
                MouseClickEvent(combo, System.Windows.Input.Mouse.PreviewMouseDownEvent);
                MouseClickEvent(combo, System.Windows.Input.Mouse.MouseDownEvent);
                PumpDispatcher();
                Program.True(!combo.IsDropDownOpen && !combo.IsKeyboardFocusWithin);
                Program.True(ReferenceEquals(surface, System.Windows.Input.FocusManager.GetFocusedElement(window)));
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally
            {
                if (window != null) { window.AllowClose = true; window.Close(); }
                Program.CleanupTestDirectory(directory);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static void AssertMinimumWindowSize(MainWindow window)
    {
        // Small desktops (including CI runners) reduce the 1320x880 minimum
        // to fit the work area with a 24-DIP margin and a 600x500 floor.
        var workArea = System.Windows.SystemParameters.WorkArea;
        Program.Equal(Math.Min(1320, Math.Max(600, workArea.Width - 24)), window.MinWidth);
        Program.Equal(Math.Min(880, Math.Max(500, workArea.Height - 24)), window.MinHeight);
    }
    private static void MouseClickEvent(System.Windows.UIElement target, System.Windows.RoutedEvent routedEvent)
        => target.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        { RoutedEvent = routedEvent });
    private static void PumpDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, uint size);
    private sealed class UiWaitingController : IProcessController
    {
        public int Remaining { get; set; } = 1;
        public Task<IShutdownSession> CreateSessionAsync(IReadOnlyList<AppRule> rules, CancellationToken token)
            => Task.FromResult<IShutdownSession>(new UiWaitingSession(rules, this));
    }
    private sealed class ControlledHibernate(TaskCompletionSource entered, TaskCompletionSource resumed) : IHibernationService
    {
        public void EnsureAvailable() { }
        public async Task HibernateAsync(CancellationToken token) { entered.TrySetResult(); await resumed.Task.WaitAsync(token); }
    }
    private sealed class UiWaitingSession(IReadOnlyList<AppRule> rules, UiWaitingController owner) : IShutdownSession
    {
        public Task<IReadOnlyList<GroupState>> RefreshAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<GroupState>>(rules.Select(rule => new GroupState(rule.Id, owner.Remaining)).ToArray());
        public Task ForceTerminateAsync(string id, CancellationToken token) => throw new InvalidOperationException("UI test must cancel before termination.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static async Task WatchdogRecoveryTest(string fixture, string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.WatchdogRecovery.Test." + Guid.NewGuid().ToString("N"));
        using var parent = Start(fixture, "--ignore-close");
        var storage = new Storage(directory);
        var original = new SchemeBackup(Guid.NewGuid(), 1, 2);
        storage.SaveSettings(new() { LidEnabled = mode != "manual", WatchdogEnabled = true });
        storage.Write(Path.Combine(directory, "original-power.json"), original);
        if (mode != "manual") storage.Write(storage.JournalPath, new PowerJournal { Schemes = [original] });
        storage.Write(Path.Combine(directory, "fake-power.json"), mode == "manual" ? original : original with { Ac = 0, Dc = 0 });
        if (mode == "restore-error") File.WriteAllText(Path.Combine(directory, "fail-restore"), "Fail only fake restoration");
        var prefix = @"Local\LiteHibernate.Watchdog." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".ready");
        using var disarm = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".disarm");
        using var restoreOnly = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".restore-only");
        using var childReady = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".child-ready");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".release-child");
        var start = new ProcessStartInfo(System.Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--watchdog-test", parent.Id.ToString(), parent.StartTime.ToFileTimeUtc().ToString(), prefix, directory, mode }) start.ArgumentList.Add(arg);
        using var watcher = Process.Start(start)!;
        try
        {
            Program.True(await Task.Run(() => ready.WaitOne(10000)));
            if (mode == "intentional") restoreOnly.Set();
            Cleanup(parent);
            if (mode is "success" or "manual" or "repeat-crash")
            {
                Program.True(await Task.Run(() => childReady.WaitOne(10000)));
                // Successful startup must have applied the app setting again, while
                // preserving the original Windows values for a second restoration.
                Program.Equal(mode == "manual" ? original.Ac : 0u, new JournalTestPowerApi(storage).Read(original.Scheme).Ac);
                if (mode == "manual") Program.True(!File.Exists(storage.JournalPath));
                else Program.Equal(original, storage.Read<PowerJournal>(storage.JournalPath)!.Schemes.Single());
                Program.Equal(1, storage.Read<int>(Path.Combine(directory, "restart-attempts.json")));
                Program.True(!watcher.HasExited);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!ReadLiveLog(storage.LogPath).Contains("подтвердил успешный запуск") && DateTime.UtcNow < deadline) await Task.Delay(20);
                Program.True(ReadLiveLog(storage.LogPath).Contains("подтвердил успешный запуск"));
                release.Set();
            }
            await watcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var normal = mode is "success" or "manual" or "intentional";
            Program.Equal(normal ? 0 : 1, watcher.ExitCode);
            var failedRestoration = mode == "restore-error";
            Program.Equal(!failedRestoration, !File.Exists(storage.JournalPath));
            Program.Equal(failedRestoration ? original with { Ac = 0, Dc = 0 } : original, new JournalTestPowerApi(storage).Read(original.Scheme));
            var attempts = storage.Read<int>(Path.Combine(directory, "restart-attempts.json"));
            Program.Equal(mode is "intentional" or "restore-error" ? 0 : 1, attempts);
            var notifications = storage.Read<List<string>>(Path.Combine(directory, "notifications.json")) ?? [];
            Program.Equal(normal ? 0 : 1, notifications.Count);
            var log = File.ReadAllText(storage.LogPath);
            Program.True(log.Contains("Восстановление настроек крышки"));
            if (!normal) Program.True(log.Contains("[ERROR]"));
            if (mode == "repeat-crash") Program.True(log.Contains("повторно завершился"));
            if (mode == "timeout") Program.True(log.Contains("не подтвердил готовность"));
            var child = storage.Read<TestWatchdogRecovery.RestartedProcess>(Path.Combine(directory, "restarted-process.json"));
            if (child != null)
            {
                try
                {
                    using var process = Process.GetProcessById(child.Pid);
                    // Disarm lets the watchdog exit first; the child may still be returning
                    // from its wait for that exit. Await the owned generation, not a reused PID.
                    if (!process.HasExited && process.StartTime.ToFileTimeUtc() == child.Created)
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (ArgumentException) { } // The exited process is no longer in the process table.
            }
        }
        finally
        {
            release.Set(); Cleanup(watcher); Cleanup(parent);
            var child = storage.Read<TestWatchdogRecovery.RestartedProcess>(Path.Combine(directory, "restarted-process.json"));
            if (child != null)
            {
                try
                {
                    using var process = Process.GetProcessById(child.Pid);
                    if (!process.HasExited && process.StartTime.ToFileTimeUtc() == child.Created) Cleanup(process);
                }
                catch (ArgumentException) { }
            }
            Program.CleanupTestDirectory(directory);
        }
    }
    private static async Task TestReplacementWatchdog(string fixture)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.Replacement.Test." + Guid.NewGuid().ToString("N"));
        using var parent = Start(fixture, "--headless");
        using var replacement = Start(fixture, "--headless");
        var storage = new Storage(directory); var api = new RegressionTests.FakePower();
        var previous = ProcessIdentity.FromProcess(parent);
        new PowerSettings(storage, api, previous).Enable();
        var prefix = @"Local\LiteHibernate.Watchdog." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".ready");
        using var disarm = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".disarm");
        using var restoreOnly = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".restore-only");
        using var recovery = new PausedRecovery();
        var worker = Task.Run(() => Watchdog.Run(["--watchdog", previous.Pid.ToString(), previous.Created.ToString(), prefix], storage, api, recovery));
        try
        {
            Program.True(await Task.Run(() => ready.WaitOne(10000)));
            Cleanup(parent);
            Program.True(await Task.Run(() => recovery.Entered.WaitOne(10000)));
            var identity = ProcessIdentity.FromProcess(replacement);
            var newPower = new PowerSettings(storage, api, identity); newPower.Enable();
            recovery.Release.Set();
            Program.Equal(0, await worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Program.Equal(0, recovery.Notifications);
            Program.True(newPower.Enabled); Program.Equal(0u, api.Read(api.Active).Ac);
            Program.Equal(identity, storage.Read<PowerJournal>(storage.JournalPath)!.Owner);
            newPower.Restore();
        }
        finally
        {
            recovery.Release.Set(); disarm.Set(); Cleanup(parent);
            await worker.WaitAsync(TimeSpan.FromSeconds(15)); Cleanup(replacement); Program.CleanupTestDirectory(directory);
        }
    }
    private sealed class PausedRecovery : IWatchdogRecovery, IDisposable
    {
        public EventWaitHandle Entered = new(false, EventResetMode.ManualReset), Release = new(false, EventResetMode.ManualReset);
        public int Notifications;
        public Process Start(WatchdogRestartContext context)
        {
            Entered.Set(); if (!Release.WaitOne(10000)) throw new TimeoutException();
            throw new IOException("Simulated restart failure while the user launches another instance.");
        }
        public void NotifyFailure(string message) => Notifications++;
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
    private static Task TestDesktopManualHibernate(string fixture)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.Desktop.Test." + Guid.NewGuid().ToString("N"));
            Process? original = null;
            long fixtureCreated = 0;
            Exception? failure = null;
            var reopened = new List<ProcessInfo>();
            LiteHibernate.UI.MainViewModel? model = null;
            MainWindow? window = null;
            try
            {
                original = Start(fixture, "--tray"); fixtureCreated = original.StartTime.ToFileTimeUtc();
                var storage = new Storage(directory); var api = new RegressionTests.FakePower();
                var monitor = new LidMonitor(hasLid: false, storage: storage);
                var hibernate = new ResumeEventHibernate(monitor);
                var inventory = new ProcessInventory();
                var coordinator = new ShutdownCoordinator(new ProcessController(inventory, storage), hibernate, new ScenarioClock(), new ApplicationRestarter(inventory, storage));
                var autoStartCalls = 0;
                model = new(storage, new AppSettings
                {
                    LidEnabled = true, WatchdogEnabled = false, LidDelaySeconds = 1,
                    Applications = [Program.Rule(TimeoutPolicy.CancelHibernate, 10, path: fixture) with { ReopenAfterHibernate = true }]
                }, coordinator, powerMonitor: monitor, powerApi: api, setAutoStart: _ => autoStartCalls++);
                WaitWithDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => model.InitializeAsync()).Task.Unwrap());
                Program.True(model.InitializationSucceeded && !model.LidAvailable && !model.Enabled);
                Program.True(!storage.LoadSettings().LidEnabled); Program.Equal(1, autoStartCalls); Program.Equal(0, api.Writes);
                Program.True(!model.ToggleEnabledCommand.CanExecute(null) && model.ScenarioCommand.CanExecute(null));
                window = new MainWindow(model) { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
                window.Show(); window.UpdateLayout();
                var toggle = Descendants<System.Windows.Controls.CheckBox>(window).Single(box => box.Content is string text && text == "Включить");
                Program.True(!toggle.IsEnabled);
                WaitWithDispatcher(model.SetEnabledAsync(true)); Program.Equal(0, api.Writes);
                var cancelled = window.Dispatcher.InvokeAsync(() => model.StartScenarioAsync(false)).Task.Unwrap(); PumpDispatcher();
                Program.True(model.CancelCommand.CanExecute(null) && model.ScenarioCounter == "1 с");
                model.CancelCommand.Execute(null); WaitWithDispatcher(cancelled);
                Program.True(!original.HasExited); Program.Equal(0, hibernate.Calls);
                var scenario = window.Dispatcher.InvokeAsync(() => model.StartScenarioAsync(false)).Task.Unwrap();
                WaitWithDispatcher(hibernate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Program.True(original.HasExited && !scenario.IsCompleted);
                Program.True(!model.CancelCommand.CanExecute(null));
                Program.True(!inventory.Capture().Any(process => process.Path.Equals(fixture, StringComparison.OrdinalIgnoreCase)));
                var hook = typeof(LidMonitor).GetMethod("Hook", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                hook.Invoke(monitor, [IntPtr.Zero, 0x0218, new IntPtr(0x0012), IntPtr.Zero, false]);
                WaitWithDispatcher(scenario.WaitAsync(TimeSpan.FromSeconds(10)));
                reopened.AddRange(inventory.Capture().Where(process => process.Path.Equals(fixture, StringComparison.OrdinalIgnoreCase)));
                Program.Equal(1, reopened.Count); Program.Equal(1, hibernate.Calls);
                Program.True(monitor.IsOpen == null && !model.HasError && !model.ScenarioActive);
                Program.Equal(0, api.Writes); Program.True(!File.Exists(storage.JournalPath));
                Program.True(File.ReadAllText(storage.LogPath).Contains("Открыто после гибернации"));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try
                {
                    if (window != null) { window.AllowClose = true; window.Close(); }
                    model?.Dispose(); if (original != null) Cleanup(original);
                    // Finish fixture cleanup before allowing the next integration test to start.
                    if (fixtureCreated != 0)
                    foreach (var info in new ProcessInventory().Capture().Where(process => process.Path.Equals(fixture, StringComparison.OrdinalIgnoreCase) && process.Created >= fixtureCreated))
                    {
                        try { using var process = Process.GetProcessById(info.Pid); if (process.StartTime.ToFileTimeUtc() == info.Created) Cleanup(process); }
                        catch (ArgumentException) { }
                    }
                    original?.Dispose(); Program.CleanupTestDirectory(directory);
                }
                catch (Exception cleanup) { failure ??= cleanup; }
            }
            if (failure == null) completion.TrySetResult(); else completion.TrySetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    private sealed class ResumeEventHibernate(LidMonitor monitor) : IHibernationService
    {
        public int Calls;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void EnsureAvailable() { }
        public Task HibernateAsync(CancellationToken token)
        {
            Calls++;
            var wait = typeof(HibernationService).GetMethod("SuspendAndWaitForResumeAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            return (Task)wait.Invoke(new HibernationService(monitor), [new Func<CancellationToken, Task>(_ => { Entered.TrySetResult(); return Task.CompletedTask; }), token])!;
        }
    }
    private static string ReadLiveLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static async Task WatchdogTest(string fixture, bool disarm)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.Watchdog.Test." + Guid.NewGuid().ToString("N"));
        using var parent = Start(fixture, "--ignore-close");
        try
        {
            var storage = new Storage(directory);
            var original = new SchemeBackup(Guid.NewGuid(), 1, 2);
            storage.Write(storage.JournalPath, new PowerJournal { Schemes = [original] });
            storage.Write(Path.Combine(directory, "fake-power.json"), original with { Ac = 0, Dc = 0 });
            var prefix = @"Local\LiteHibernate.Watchdog." + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".ready");
            using var disarmed = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".disarm");
            using var restoreOnly = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".restore-only");
            var start = new ProcessStartInfo(System.Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--watchdog-test", parent.Id.ToString(), parent.StartTime.ToFileTimeUtc().ToString(), prefix, directory }) start.ArgumentList.Add(arg);
            using var watcher = Process.Start(start)!;
            try
            {
                Program.True(await Task.Run(() => ready.WaitOne(10000)));
                // With neither event signaled, the watcher must remain idle and preserve
                // the active settings and journal until disarm or the parent's exit.
                await Task.Delay(300);
                Program.True(!watcher.HasExited && File.Exists(storage.JournalPath));
                Program.Equal(0u, new JournalTestPowerApi(storage).Read(original.Scheme).Ac);
                if (disarm) disarmed.Set(); else Cleanup(parent);
                await watcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Program.Equal(disarm ? 0 : 1, watcher.ExitCode);
                if (disarm) { Program.True(File.Exists(storage.JournalPath)); Program.Equal(0u, new JournalTestPowerApi(storage).Read(original.Scheme).Ac); }
                else { Program.True(!File.Exists(storage.JournalPath)); Program.Equal(original, new JournalTestPowerApi(storage).Read(original.Scheme)); }
            }
            finally { Cleanup(watcher); }
        }
        finally
        {
            Cleanup(parent);
            Program.CleanupTestDirectory(directory);
        }
    }
    private static Process Start(string path, params string[] args)
    {
        var name = @"Local\LiteHibernate.Fixture." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--ready=" + name); foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start)!;
        if (!ready.WaitOne(10000)) { Cleanup(process); process.Dispose(); throw new TimeoutException("Fixture did not become ready."); }
        return process;
    }
    private static void Cleanup(Process process)
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
    }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "LiteHibernate.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root not found.");
    }
    private sealed class CountingHibernate : IHibernationService
    {
        public int Calls;
        public void EnsureAvailable() { }
        public Task HibernateAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
    }
}
