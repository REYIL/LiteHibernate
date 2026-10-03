using LiteHibernate.Core;
using LiteHibernate.Infrastructure;
using LiteHibernate.Windows;

namespace LiteHibernate.Tests;

internal static class Program
{
    private static int passed, failed;
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--watchdog-test")
        {
            var storage = new Storage(args[4]);
            var mode = args.ElementAtOrDefault(5) ?? "disabled";
            var command = new[] { "--watchdog", args[1], args[2], args[3] };
            if (mode == "disabled") command = [.. command, "--no-restart"];
            return Watchdog.Run(command, storage, new JournalTestPowerApi(storage), new TestWatchdogRecovery(storage, mode), TimeSpan.FromSeconds(3));
        }
        if (args.FirstOrDefault() == "--restarted-watchdog-test") return await TestWatchdogRecovery.RunRestarted(args);
        await Test("Graceful completion precedes hibernation", async () =>
        {
            var env = new Environment();
            env.Session.Read = (_, call) => call < 3 ? [new("app", 2)] : [new("app", 0)];
            Equal(ScenarioStage.Completed, (await env.Run(Rule())).Stage);
            Equal(0, env.Session.Kills.Count); Equal(1, env.Hibernate.Calls);
            True(env.Session.Refreshes >= 4); True(env.Session.Disposed);
        });
        await Test("All applications exiting early skips their maximum wait times", async () =>
        {
            var env = new Environment();
            env.Session.Read = (_, _) =>
                [new("a", env.Clock.Elapsed < TimeSpan.FromSeconds(0.5) ? 1 : 0),
                 new("b", env.Clock.Elapsed < TimeSpan.FromSeconds(1.25) ? 1 : 0)];
            var result = await env.Coordinator.RunAsync([Rule(timeout: 30, id: "a"), Rule(timeout: 60, id: "b")], 0, default, default);
            Equal(ScenarioStage.Completed, result.Stage); Equal(1, env.Hibernate.Calls);
            True(env.Clock.Elapsed >= TimeSpan.FromSeconds(1.25) && env.Clock.Elapsed < TimeSpan.FromSeconds(2));
            Equal(0, env.Session.Kills.Count);
        });
        await Test("Opening lid during N cancels without closing processes", async () =>
        {
            var env = new Environment(); using var opened = new CancellationTokenSource();
            env.Clock.BeforeDelay = _ => opened.Cancel();
            Equal(ScenarioStage.Cancelled, (await env.Run(Rule(), 5, opened.Token)).Stage);
            Equal(0, env.Controller.Created); Equal(0, env.Hibernate.Calls);
        });
        await Test("Opening lid after N does not cancel", async () =>
        {
            var env = new Environment(); using var opened = new CancellationTokenSource();
            env.Session.Read = (_, _) => { opened.Cancel(); return [new("app", 0)]; };
            Equal(ScenarioStage.Completed, (await env.Run(Rule(), 5, opened.Token)).Stage);
            True(env.Clock.Elapsed >= TimeSpan.FromSeconds(5));
        });
        await Test("Manual trigger ignores closed-lid cancellation", async () =>
        {
            var env = new Environment(); using var opened = new CancellationTokenSource(); opened.Cancel();
            Equal(ScenarioStage.Completed, (await env.Run(Rule(), 0, opened.Token)).Stage);
            True(env.Clock.Elapsed < TimeSpan.FromSeconds(1));
        });
        await Test("Kill happens only after individual timeout and exit is awaited", async () =>
        {
            var env = new Environment();
            env.Session.Read = (s, _) => [new("app", s.Kills.Count == 0 || env.Clock.Elapsed < TimeSpan.FromSeconds(2.5) ? 1 : 0)];
            Equal(ScenarioStage.Completed, (await env.Run(Rule(timeout: 2))).Stage);
            True(env.Session.Kills.All(k => k.Time >= TimeSpan.FromSeconds(2))); True(env.Clock.Elapsed >= TimeSpan.FromSeconds(2.5));
        });
        await Test("Cancel policy never hibernates or kills", async () =>
        {
            var env = new Environment(); env.Session.Read = (_, _) => [new("app", 1)];
            Equal(ScenarioStage.Cancelled, (await env.Run(Rule(TimeoutPolicy.CancelHibernate, 2))).Stage);
            Equal(0, env.Session.Kills.Count); Equal(0, env.Hibernate.Calls);
        });
        await Test("Slow discovery does not consume the graceful shutdown timeout", async () =>
        {
            var env = new Environment();
            env.Session.Read = (session, call) =>
            {
                if (call == 1) env.Clock.Advance(TimeSpan.FromSeconds(5));
                return [new("app", session.Kills.Count == 0 ? 1 : 0)];
            };
            Equal(ScenarioStage.Completed, (await env.Run(Rule(timeout: 2))).Stage);
            True(env.Session.Kills.First().Time >= TimeSpan.FromSeconds(7));
        });
        await Test("Continue policy permits its remaining processes", async () =>
        {
            var env = new Environment(); env.Session.Read = (_, _) => [new("app", 3)];
            Equal(ScenarioStage.Completed, (await env.Run(Rule(TimeoutPolicy.ContinueHibernate, 2))).Stage);
            Equal(0, env.Session.Kills.Count); Equal(1, env.Hibernate.Calls);
        });
        await Test("Parallel deadlines are not added together", async () =>
        {
            var env = new Environment(); env.Session.Read = (_, _) => [new("a", 1), new("b", 1)];
            var result = await env.Coordinator.RunAsync([Rule(TimeoutPolicy.ContinueHibernate, 2, "a"), Rule(TimeoutPolicy.ContinueHibernate, 4, "b")], 0, default, default);
            Equal(ScenarioStage.Completed, result.Stage); True(env.Clock.Elapsed < TimeSpan.FromSeconds(5)); True(env.Clock.Elapsed >= TimeSpan.FromSeconds(4));
        });
        await Test("Cancellation policy wins over simultaneous kill deadlines", async () =>
        {
            var env = new Environment(); env.Session.Read = (_, _) => [new("a", 1), new("b", 1)];
            var result = await env.Coordinator.RunAsync([Rule(TimeoutPolicy.ForceTerminate, 2, "a"), Rule(TimeoutPolicy.CancelHibernate, 2, "b")], 0, default, default);
            Equal(ScenarioStage.Cancelled, result.Stage); Equal(0, env.Session.Kills.Count);
        });
        await Test("Unsuccessful kill aborts after five extra seconds", async () =>
        {
            var env = new Environment(); env.Session.Read = (_, _) => [new("app", 1)];
            Equal(ScenarioStage.Failed, (await env.Run(Rule(timeout: 2))).Stage);
            Equal(0, env.Hibernate.Calls); True(env.Clock.Elapsed >= TimeSpan.FromSeconds(7));
        });
        await Test("Access denied prevents hibernation", async () =>
        {
            var env = new Environment(); env.Session.Read = (_, _) => [new("app", 1)]; env.Session.KillError = new UnauthorizedAccessException("Access denied");
            var result = await env.Run(Rule(timeout: 1));
            Equal(ScenarioStage.Failed, result.Stage); Equal(0, env.Hibernate.Calls);
            True(ReferenceEquals(env.Session.KillError, result.Error));
        });
        await Test("Unavailable hibernation never closes applications", async () =>
        {
            var env = new Environment(); env.Hibernate.AvailabilityError = new InvalidOperationException("No S4");
            Equal(ScenarioStage.Failed, (await env.Run(Rule())).Stage); Equal(0, env.Controller.Created);
        });
        await Test("Hibernation API error is reported", async () =>
        {
            var env = new Environment(); env.Hibernate.HibernateError = new IOException("S4 failed");
            Equal(ScenarioStage.Failed, (await env.Run(Rule())).Stage);
        });
        await Test("User cancellation stops future kills", async () =>
        {
            var env = new Environment(); using var cancellation = new CancellationTokenSource(); env.Session.Read = (_, _) => [new("app", 1)];
            env.Clock.BeforeDelay = _ => cancellation.Cancel();
            Equal(ScenarioStage.Cancelled, (await env.Run(Rule(), cancellation: cancellation.Token)).Stage);
            Equal(0, env.Session.Kills.Count); Equal(0, env.Hibernate.Calls);
        });
        await Test("Final verification catches a restarted process", async () =>
        {
            var env = new Environment(); env.Session.Read = (_, call) => [new("app", call == 2 ? 1 : 0)];
            Equal(ScenarioStage.Completed, (await env.Run(Rule())).Stage); True(env.Session.Refreshes >= 4);
        });
        await Test("Progress countdown follows lid delay and parallel deadlines", async () =>
        {
            var env = new Environment();
            var updates = new List<ScenarioProgress>();
            env.Coordinator.Progress += updates.Add;
            env.Session.Read = (_, _) => [new("a", 1), new("b", 1)];
            await env.Coordinator.RunAsync([Rule(TimeoutPolicy.ContinueHibernate, 2, "a"), Rule(TimeoutPolicy.ContinueHibernate, 4, "b")], 3, default, default);
            True(updates.Where(p => p.Stage == ScenarioStage.Countdown).Select(p => p.RemainingSeconds).SequenceEqual(new int?[] { 3, 2, 1 }));
            var waiting = updates.Where(p => p.Stage == ScenarioStage.Waiting).ToArray();
            Equal(4, waiting.First().RemainingSeconds!.Value);
            Equal(1, waiting.Last().RemainingSeconds!.Value);
            True(waiting.All(p => p.RemainingSeconds is >= 1 and <= 4));
        });
        await Test("Countdown respects an earlier cancellation deadline", async () =>
        {
            var env = new Environment();
            var updates = new List<ScenarioProgress>();
            env.Coordinator.Progress += updates.Add;
            env.Session.Read = (_, _) => [new("a", 1), new("b", 1)];
            await env.Coordinator.RunAsync([Rule(TimeoutPolicy.CancelHibernate, 2, "a"), Rule(TimeoutPolicy.ContinueHibernate, 60, "b")], 0, default, default);
            Equal(2, updates.First(p => p.Stage == ScenarioStage.Waiting).RemainingSeconds!.Value);
            Equal(0, env.Hibernate.Calls);
        });
        await Test("Forced termination countdown uses its five second confirmation", async () =>
        {
            var env = new Environment();
            var updates = new List<ScenarioProgress>();
            env.Coordinator.Progress += updates.Add;
            env.Session.Read = (_, _) => [new("app", 1)];
            await env.Run(Rule(timeout: 2));
            var waiting = updates.Where(p => p.Stage == ScenarioStage.Waiting).ToArray();
            Equal(2, waiting.First().RemainingSeconds!.Value);
            True(waiting.Any(p => p.RemainingSeconds == 5));
            Equal(1, waiting.Last().RemainingSeconds!.Value);
        });
        await Test("Only one scenario runs at a time", async () =>
        {
            var env = new Environment(); var release = new TaskCompletionSource();
            env.Clock.Block = release.Task;
            var first = env.Run(Rule(), 5);
            Equal(ScenarioStage.Cancelled, (await env.Run(Rule())).Stage);
            Equal(0, env.Controller.Created); release.SetResult();
            Equal(ScenarioStage.Completed, (await first).Stage); Equal(1, env.Controller.Created);
        });
        await Test("Reopening waits for resume and excludes inactive or surviving applications", async () =>
        {
            var env = new Environment(); var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            env.Hibernate.Resume = resumed.Task;
            env.Session.Read = (_, _) => [new("closed", 0, true), new("inactive", 0), new("surviving", 1)];
            var rules = new[]
            {
                Rule(id: "closed", path: @"C:\closed.exe") with { ReopenAfterHibernate = true },
                Rule(id: "inactive", path: @"C:\inactive.exe") with { ReopenAfterHibernate = true },
                Rule(TimeoutPolicy.ContinueHibernate, 1, "surviving", @"C:\surviving.exe") with { ReopenAfterHibernate = true }
            };
            var task = env.Coordinator.RunAsync(rules, 0, default, default);
            Equal(1, env.Hibernate.Calls); Equal(0, env.Restarter.Calls); True(!task.IsCompleted);
            resumed.SetResult();
            Equal(ScenarioStage.Completed, (await task).Stage); Equal(1, env.Restarter.Calls);
            True(env.Restarter.Rules.Select(rule => rule.Id).SequenceEqual(["closed"]));
        });
        await Test("Forced closure can reopen once while default-disabled rules stay closed", async () =>
        {
            var env = new Environment();
            env.Session.Read = (session, _) => [new("enabled", session.Kills.Count == 0 ? 1 : 0), new("disabled", 0, true)];
            var result = await env.Coordinator.RunAsync(
                [Rule(timeout: 1, id: "enabled", path: @"C:\enabled.exe") with { ReopenAfterHibernate = true }, Rule(id: "disabled")], 0, default, default);
            Equal(ScenarioStage.Completed, result.Stage); Equal(1, env.Restarter.Calls);
            True(env.Restarter.Rules.Select(rule => rule.Id).SequenceEqual(["enabled"]));
        });
        await Test("Failed or cancelled hibernation never reopens applications", async () =>
        {
            var rule = Rule() with { ReopenAfterHibernate = true };
            var failed = new Environment(); failed.Session.Read = (_, _) => [new("app", 0, true)];
            failed.Hibernate.HibernateError = new IOException("Simulated hibernation failure.");
            Equal(ScenarioStage.Failed, (await failed.Run(rule)).Stage); Equal(0, failed.Restarter.Calls);
            var cancelled = new Environment(); cancelled.Session.Read = (_, _) => [new("app", 1)];
            Equal(ScenarioStage.Cancelled, (await cancelled.Run(rule with { Policy = TimeoutPolicy.CancelHibernate, TimeoutSeconds = 1 })).Stage);
            Equal(0, cancelled.Restarter.Calls);
            var waiting = new Environment(); waiting.Session.Read = (_, _) => [new("app", 0, true)];
            waiting.Hibernate.Resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            using var stop = new CancellationTokenSource();
            var task = waiting.Run(rule, cancellation: stop.Token); stop.Cancel();
            Equal(ScenarioStage.Cancelled, (await task).Stage); Equal(0, waiting.Restarter.Calls);
        });
        await Test("Reopening errors preserve successful hibernation and are returned to the UI", async () =>
        {
            var env = new Environment(); env.Session.Read = (_, _) => [new("app", 0, true)];
            var error = new IOException("Simulated reopen failure."); env.Restarter.Errors = [error];
            var result = await env.Run(Rule() with { ReopenAfterHibernate = true });
            Equal(ScenarioStage.Completed, result.Stage); Equal(1, env.Hibernate.Calls);
            True(ReferenceEquals(error, result.ReopenErrors.Single()));
        });
        await Test("Settings reject unknown policy and invalid timeout", () =>
        {
            Throws(() => new AppSettings { Applications = [Rule((TimeoutPolicy)99)] }.Validate());
            Throws(() => new AppSettings { Applications = [Rule(timeout: 0)] }.Validate());
            Throws(() => new AppSettings { LidDelaySeconds = -1 }.Validate());
            return Task.CompletedTask;
        });
        await Test("Settings reject null application fields and invalid identifiers in both lists", () => WithStorage(storage =>
        {
            var invalidRules = new[]
            {
                Rule() with { Id = null! }, Rule() with { Id = "" }, Rule() with { Id = " " },
                Rule() with { Name = null! }, Rule() with { ExecutablePath = null! }
            };
            foreach (var hidden in new[] { false, true })
            foreach (var rule in invalidRules)
            {
                storage.Write(storage.SettingsPath, new AppSettings
                {
                    Applications = hidden ? [] : [rule], DeletedApplications = hidden ? [rule] : []
                });
                try { storage.LoadSettings(); throw new Exception("Invalid settings were accepted."); }
                catch (InvalidDataException) { }
            }
            // Installed entries without a known EXE remain valid and searchable.
            storage.SaveSettings(new() { Applications = [Rule() with { ExecutablePath = "" }] });
            Equal("", storage.LoadSettings().Applications[0].ExecutablePath);
        }));
        await Test("Saving invalid application metadata preserves the last valid settings", () => WithStorage(storage =>
        {
            storage.SaveSettings(new() { LidDelaySeconds = 7, Applications = [Rule()] });
            var original = File.ReadAllText(storage.SettingsPath);
            foreach (var hidden in new[] { false, true })
            foreach (var rule in new[] { Rule() with { Id = null! }, Rule() with { Name = null! }, Rule() with { ExecutablePath = null! } })
            {
                try
                {
                    storage.SaveSettings(new() { Applications = hidden ? [] : [rule], DeletedApplications = hidden ? [rule] : [] });
                    throw new Exception("Invalid settings were written.");
                }
                catch (InvalidDataException) { }
                Equal(original, File.ReadAllText(storage.SettingsPath));
                Equal(7, storage.LoadSettings().LidDelaySeconds);
            }
            True(!Directory.EnumerateFiles(storage.DirectoryPath, "*.tmp").Any());
        }));
        await Test("Tree expansion respects parent creation time and PID reuse", () =>
        {
            var root = new ProcessInfo(1, 0, 20, @"C:\a.exe", 1, true);
            var child = new ProcessInfo(2, 1, 30, @"C:\b.exe", 1, false);
            var reusedPidChild = new ProcessInfo(3, 1, 10, @"C:\c.exe", 1, false);
            Equal(2, ProcessInventory.ExpandTree([root], [root, child, reusedPidChild]).Count);
            True(!ProcessInventory.IsSelectablePath(@"C:\Windows\explorer.exe"));
            return Task.CompletedTask;
        });
        await Test("Settings survive atomic replacement", () => WithStorage(storage =>
        {
            storage.SaveSettings(new() { LidDelaySeconds = 7, WatchdogEnabled = false, Applications = [Rule() with { ReopenAfterHibernate = true }],
                DeletedApplications = [Rule(id: "removed", path: @"C:\removed.exe") with { TimeoutSeconds = 48, ReopenAfterHibernate = true }] });
            var loaded = storage.LoadSettings(); Equal(7, loaded.LidDelaySeconds); True(!loaded.WatchdogEnabled); Equal(1, loaded.Applications.Count);
            True(loaded.Applications[0].ReopenAfterHibernate);
            Equal(1, loaded.DeletedApplications.Count); Equal(48, loaded.DeletedApplications[0].TimeoutSeconds);
            True(loaded.DeletedApplications[0].ReopenAfterHibernate);
            storage.Write(storage.SettingsPath, new { Version = 1, Applications = new[] { new { Id = "legacy", Name = "Legacy", ExecutablePath = @"C:\legacy.exe" } } });
            True(!storage.LoadSettings().Applications[0].ReopenAfterHibernate);
            Equal(0, storage.LoadSettings().DeletedApplications.Count);
            storage.SaveSettings(new() { LidDelaySeconds = 8 }); Equal(8, storage.LoadSettings().LidDelaySeconds);
            True(!Directory.EnumerateFiles(storage.DirectoryPath, "*.tmp").Any());
        }));
        await Test("Logs preserve UTF-8 error details and rotate to one bounded archive", () => WithStorage(storage =>
        {
            storage.Log("Обработка крышки включена.");
            storage.Log(new InvalidOperationException("Ошибка завершения приложения.").ToString(), true);
            var log = File.ReadAllText(storage.LogPath);
            True(log.Contains("[INFO]") && log.Contains("[ERROR]") && log.Contains($"[PID {System.Environment.ProcessId}]"));
            True(log.Contains("Обработка крышки включена.") && log.Contains("InvalidOperationException: Ошибка завершения приложения."));
            File.WriteAllText(storage.LogPath, new string('x', 2_000_001));
            storage.Log("После ротации.");
            True(File.Exists(storage.LogPath + ".old"));
            Equal(2_000_001L, new FileInfo(storage.LogPath + ".old").Length);
            True(File.ReadAllText(storage.LogPath).Contains("После ротации."));
            Equal(2, Directory.EnumerateFiles(storage.LogsDirectoryPath).Count());
        }));
        await Test("Independent log writers preserve every concurrent entry", () => WithStorage(storage =>
        {
            Parallel.For(0, 64, index => new Storage(storage.DirectoryPath).Log($"parallel.{index}"));
            var lines = File.ReadAllLines(storage.LogPath);
            Equal(64, lines.Length);
            for (var index = 0; index < 64; index++) True(lines.Any(line => line.EndsWith($"parallel.{index}", StringComparison.Ordinal)));
        }));
        await Test("Scenario profile preserves rules without system integration settings", () => WithStorage(storage =>
        {
            var profile = new ScenarioProfile { LidDelaySeconds = 9, Applications = [Rule(TimeoutPolicy.CancelHibernate, 42, path: @"C:\Apps\editor.exe") with { ReopenAfterHibernate = true }] };
            var path = Path.Combine(storage.DirectoryPath, "profile.json");
            profile.Export(storage, path);
            var imported = ScenarioProfile.Import(storage, path);
            Equal(9, imported.LidDelaySeconds); Equal(42, imported.Applications[0].TimeoutSeconds);
            Equal(TimeoutPolicy.CancelHibernate, imported.Applications[0].Policy);
            True(imported.Applications[0].Selected);
            True(imported.Applications[0].ReopenAfterHibernate);
            var json = File.ReadAllText(path);
            True(!json.Contains("LidEnabled") && !json.Contains("AutoStart") && !json.Contains("WatchdogEnabled"));
        }));
        await Test("Invalid imported profile cannot overwrite saved settings", () => WithStorage(storage =>
        {
            storage.SaveSettings(new() { LidDelaySeconds = 7 });
            var path = Path.Combine(storage.DirectoryPath, "invalid.json");
            storage.Write(path, new ScenarioProfile { Applications = [Rule(timeout: 0)] });
            Throws(() => ScenarioProfile.Import(storage, path));
            Equal(7, storage.LoadSettings().LidDelaySeconds);
            Throws(() => new ScenarioProfile { Applications = [Rule(path: "relative.exe")] }.Validate());
            Throws(() => new ScenarioProfile { Applications = [Rule(id: "a"), Rule(id: "b")] }.Validate());
            Throws(() => new ScenarioProfile { Version = 2 }.Validate());
        }));
        await Test("AC and DC settings restore independently", () => WithStorage(storage =>
        {
            var api = new FakePower(); var original = api.Read(api.Active); var power = new PowerSettings(storage, api);
            power.Enable(); Equal(new SchemeBackup(api.Active, 0, 0), api.Read(api.Active)); True(File.Exists(storage.JournalPath));
            power.Restore(); Equal(original, api.Read(api.Active)); True(!File.Exists(storage.JournalPath)); True(!power.Enabled);
        }));
        await Test("Power scheme switches preserve each original and active selection", () => WithStorage(storage =>
        {
            var api = new FakePower(); var first = api.Read(api.Active); var second = new SchemeBackup(Guid.NewGuid(), 2, 3); api.Values[second.Scheme] = second;
            var power = new PowerSettings(storage, api); power.Enable(); api.Active = second.Scheme; power.EnsureActiveScheme(); power.EnsureActiveScheme();
            power.Restore(); Equal(first, api.Read(first.Scheme)); Equal(second, api.Read(second.Scheme)); Equal(second.Scheme, api.Active);
        }));
        await Test("Journal is durable before partial power write failure", () => WithStorage(storage =>
        {
            var api = new FakePower(); var original = api.Read(api.Active); var power = new PowerSettings(storage, api); api.FailNextWrite = true;
            Throws(power.Enable); True(File.Exists(storage.JournalPath)); True(!power.Enabled);
            new PowerSettings(storage, api).Restore(); Equal(original, api.Read(api.Active));
        }));
        await Test("Failed recovery retains journal for next launch", () => WithStorage(storage =>
        {
            var api = new FakePower(); var original = api.Read(api.Active); var power = new PowerSettings(storage, api); power.Enable(); api.FailNextWrite = true;
            Throws(power.Restore); True(File.Exists(storage.JournalPath));
            new PowerSettings(storage, api).Restore(); Equal(original, api.Read(api.Active)); True(!File.Exists(storage.JournalPath));
        }));
        await Test("Corrupt recovery journal is never silently discarded", () => WithStorage(storage =>
        {
            File.WriteAllText(storage.JournalPath, "invalid json");
            Throws(new PowerSettings(storage, new FakePower()).Restore); True(File.Exists(storage.JournalPath));
        }));
        await RegressionTests.Run(Test);
        if (args.Contains("--integration")) await IntegrationTests.Run(Test);
        Console.WriteLine($"\n{passed} passed, {failed} failed. No real hibernation was invoked.");
        return failed == 0 ? 0 : 1;
    }
    private static async Task Test(string name, Func<Task> test)
    {
        try { await test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    internal static void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    internal static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected an exception."); }
    private static Task WithStorage(Action<Storage> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "LiteHibernate.Tests." + Guid.NewGuid().ToString("N"));
        try { test(new(path)); }
        finally
        {
            // Only files directly created in this uniquely named temporary test directory.
            CleanupTestDirectory(path);
        }
        return Task.CompletedTask;
    }
    internal static void CleanupTestDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        // Delete only files in the unique test directory and its known Logs child.
        var logs = Path.Combine(path, "Logs");
        if (Directory.Exists(logs))
        {
            foreach (var file in Directory.EnumerateFiles(logs)) File.Delete(file);
            Directory.Delete(logs);
        }
        foreach (var file in Directory.EnumerateFiles(path)) File.Delete(file);
        Directory.Delete(path);
    }
    internal static AppRule Rule(TimeoutPolicy policy = TimeoutPolicy.ForceTerminate, int timeout = 30, string id = "app", string path = @"C:\app.exe")
        => new() { Id = id, Name = id, ExecutablePath = path, Selected = true, TimeoutSeconds = timeout, Policy = policy };
    private sealed class FakeClock : IScenarioClock
    {
        public TimeSpan Elapsed { get; private set; }
        public void Advance(TimeSpan duration) => Elapsed += duration;
        public Action<TimeSpan>? BeforeDelay;
        public Task? Block;
        public async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            BeforeDelay?.Invoke(duration); cancellationToken.ThrowIfCancellationRequested();
            if (Block != null) await Block.WaitAsync(cancellationToken);
            Elapsed += duration;
        }
    }
    private sealed class FakeHibernate : IHibernationService
    {
        public int Calls; public Exception? AvailabilityError, HibernateError;
        public Task? Resume;
        public void EnsureAvailable() { if (AvailabilityError != null) throw AvailabilityError; }
        public async Task HibernateAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; if (HibernateError != null) throw HibernateError; if (Resume != null) await Resume.WaitAsync(token); }
    }
    private sealed class FakeRestarter : IApplicationRestarter
    {
        public int Calls; public IReadOnlyList<AppRule> Rules = []; public IReadOnlyList<Exception> Errors = [];
        public Task<IReadOnlyList<Exception>> ReopenAsync(IReadOnlyList<AppRule> rules, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Rules = rules; return Task.FromResult(Errors); }
    }
    private sealed class FakeSession(FakeClock clock) : IShutdownSession
    {
        public Func<FakeSession, int, IReadOnlyList<GroupState>> Read = (_, _) => [new("app", 0)];
        public List<(string Id, TimeSpan Time)> Kills = []; public int Refreshes; public bool Disposed; public Exception? KillError;
        public Task<IReadOnlyList<GroupState>> RefreshAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Read(this, ++Refreshes)); }
        public Task ForceTerminateAsync(string id, CancellationToken token) { token.ThrowIfCancellationRequested(); if (KillError != null) throw KillError; Kills.Add((id, clock.Elapsed)); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class FakeController(FakeSession session) : IProcessController
    {
        public int Created;
        public Task<IShutdownSession> CreateSessionAsync(IReadOnlyList<AppRule> rules, CancellationToken token) { Created++; return Task.FromResult<IShutdownSession>(session); }
    }
    private sealed class Environment
    {
        public FakeClock Clock = new(); public FakeHibernate Hibernate = new(); public FakeRestarter Restarter = new(); public FakeSession Session; public FakeController Controller; public ShutdownCoordinator Coordinator;
        public Environment() { Session = new(Clock); Controller = new(Session); Coordinator = new(Controller, Hibernate, Clock, Restarter); }
        public Task<ScenarioResult> Run(AppRule rule, int delay = 0, CancellationToken lid = default, CancellationToken cancellation = default) => Coordinator.RunAsync([rule], delay, lid, cancellation);
    }
    private sealed class FakePower : IPowerApi
    {
        public Guid Active = Guid.NewGuid(); public Dictionary<Guid, SchemeBackup> Values = []; public bool FailNextWrite;
        public FakePower() { Values[Active] = new(Active, 1, 2); }
        public Guid ActiveScheme() => Active;
        public bool SchemeExists(Guid scheme) => Values.ContainsKey(scheme);
        public SchemeBackup Read(Guid scheme) => Values[scheme];
        public void Write(SchemeBackup values)
        {
            if (FailNextWrite) { FailNextWrite = false; Values[values.Scheme] = Values[values.Scheme] with { Ac = values.Ac }; throw new IOException("Simulated partial write failure"); }
            Values[values.Scheme] = values;
        }
        public void Apply(Guid scheme) => Active = scheme;
    }
}
