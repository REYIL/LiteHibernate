using LiteHibernate.Core;
using LiteHibernate.Infrastructure;
using LiteHibernate.Windows;

namespace LiteHibernate.Tests;

internal static class RegressionTests
{
    internal static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Imported and saved EXE paths normalize separators and parent segments", () => WithStorage(storage =>
        {
            const string path = "C:/Apps/old/../app.exe";
            var profile = new ScenarioProfile { Applications = [Program.Rule(path: path)] };
            var file = Path.Combine(storage.DirectoryPath, "profile.json");
            profile.Export(storage, file);
            Program.Equal(@"C:\Apps\app.exe", ScenarioProfile.Import(storage, file).Applications.Single().ExecutablePath);
            storage.SaveSettings(new() { Applications = [Program.Rule(path: path)], DeletedApplications = [Program.Rule(id: "hidden", path: "C:/Hidden/app.exe")] });
            var settings = storage.LoadSettings();
            Program.Equal(@"C:\Apps\app.exe", settings.Applications.Single().ExecutablePath);
            Program.Equal(@"C:\Hidden\app.exe", settings.DeletedApplications.Single().ExecutablePath);
        }));
        await test("Old recovery cannot restore or delete a replacement app's journal", () => WithStorage(storage =>
        {
            var api = new FakePower();
            var previous = ProcessIdentity.Current with { Created = ProcessIdentity.Current.Created - 1 };
            var oldPower = new PowerSettings(storage, api, previous);
            oldPower.Enable(); oldPower.Restore();
            var newPower = new PowerSettings(storage, api);
            newPower.Enable();
            Program.True(!oldPower.RestoreOwned(previous));
            Program.True(newPower.Enabled && File.Exists(storage.JournalPath));
            Program.Equal(0u, api.Read(api.Active).Ac);
            Program.Equal(ProcessIdentity.Current, storage.Read<PowerJournal>(storage.JournalPath)!.Owner);
            try { oldPower.Restore(); throw new Exception("Live foreign owner must be protected."); }
            catch (InvalidOperationException) { }
            Program.Equal(0u, api.Read(api.Active).Ac);
            newPower.Restore();
        }));
        await test("Legacy recovery journals restore before acquiring process ownership", () => WithStorage(storage =>
        {
            var api = new FakePower(); var original = api.Read(api.Active);
            storage.Write(storage.JournalPath, new PowerJournal { Schemes = [original] });
            api.Write(original with { Ac = 0, Dc = 0 });
            var power = new PowerSettings(storage, api);
            Program.True(power.RestoreOwned(ProcessIdentity.Current));
            Program.Equal(original, api.Read(api.Active));
            power.Enable();
            Program.Equal(ProcessIdentity.Current, storage.Read<PowerJournal>(storage.JournalPath)!.Owner);
            power.Restore();
        }));
        await test("Deleted power schemes are skipped without recreating them or blocking next startup", () => WithStorage(storage =>
        {
            var api = new FakePower(); var deleted = api.Active;
            var power = new PowerSettings(storage, api); power.Enable();
            api.Active = Guid.NewGuid(); var original = new SchemeBackup(api.Active, 2, 3); api.Values[api.Active] = original;
            power.EnsureActiveScheme(); api.Values.Remove(deleted);
            power.Restore(); new PowerSettings(storage, api).Restore();
            Program.Equal(original, api.Read(api.Active)); Program.True(!api.Values.ContainsKey(deleted));
            Program.True(!File.Exists(storage.JournalPath));
            Program.True(File.ReadAllText(storage.LogPath).Contains("Удалённая схема питания пропущена"));
        }));
        await test("Power enumeration failures preserve all recovery records", () => WithStorage(storage =>
        {
            var api = new FakePower(); var power = new PowerSettings(storage, api); power.Enable();
            api.FailEnumeration = true;
            try { power.Restore(); throw new Exception("Enumeration errors must not discard backups."); }
            catch (AggregateException) { }
            Program.Equal(1, storage.Read<PowerJournal>(storage.JournalPath)!.Schemes.Count);
            Program.Equal(0u, api.Read(api.Active).Ac);
            api.FailEnumeration = false; power.Restore();
        }));
        await test("Failed partial power recovery stays pending and cannot be overwritten by monitoring or enabling", () => WithStorage(storage =>
        {
            var api = new FakePower(); var original = api.Read(api.Active);
            var power = new PowerSettings(storage, api); power.Enable();
            api.RestoreFailures = 1;
            try { power.Restore(); throw new Exception("A partial restore failure must remain pending."); }
            catch (AggregateException) { }
            Program.True(!power.Enabled && power.RestorePending && File.Exists(storage.JournalPath));
            Program.Equal(original.Ac, api.Read(api.Active).Ac); Program.Equal(0u, api.Read(api.Active).Dc);
            var writes = api.Writes;
            power.EnsureActiveScheme();
            try { power.Enable(); throw new Exception("Enabling must not bypass pending recovery."); }
            catch (InvalidOperationException) { }
            Program.Equal(writes, api.Writes); Program.Equal(original.Ac, api.Read(api.Active).Ac);
            power.Restore();
            Program.True(!power.Enabled && !power.RestorePending && !File.Exists(storage.JournalPath));
            Program.Equal(original, api.Read(api.Active));
        }));
        await test("A power check waiting on the recovery mutex cannot override a completed restore", () => WithStorage(storage =>
            VerifyQueuedPowerOperation(storage, enable: false)));
        await test("An enable operation waiting on the recovery mutex is invalidated by restore", () => WithStorage(storage =>
            VerifyQueuedPowerOperation(storage, enable: true)));
        await test("Shutdown cancellation prevents a late enable operation from changing power settings", () => WithStorage(storage =>
        {
            var api = new FakePower(); var original = api.Read(api.Active);
            var power = new PowerSettings(storage, api);
            using var stopped = new CancellationTokenSource(); stopped.Cancel();
            try { power.Enable(stopped.Token); throw new Exception("A cancelled enable must not write power settings."); }
            catch (OperationCanceledException) { }
            Program.True(!power.Enabled && !power.RestorePending && !File.Exists(storage.JournalPath));
            Program.Equal(0, api.Writes); Program.Equal(original, api.Read(api.Active));
        }));
        await test("Restart Manager retries temporary session contention and logs its error code", async () =>
        {
            var api = new FakeRestartManager { StartErrors = new([353u, 0u]) };
            var logs = new List<(string Message, bool Error)>();
            using var request = new GracefulExitRequest(FakeProcess(), default, (message, error) => logs.Add((message, error)), api);
            await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Program.Equal(2, api.Starts); Program.Equal(1, api.Shutdowns); Program.Equal(1, api.Ends);
            Program.True(logs.Any(entry => entry.Error && entry.Message.Contains("RmStartSession: ошибка 353")));
        });
        await test("Restart Manager releases sessions before retrying resource registration", async () =>
        {
            var api = new FakeRestartManager { RegisterErrors = new([121u, 0u]) };
            using var request = new GracefulExitRequest(FakeProcess(), default, api: api);
            await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Program.Equal(2, api.Starts); Program.Equal(2, api.Ends); Program.Equal(1, api.Shutdowns);
        });
        await test("Restart Manager shutdown refusal is logged and never converted into a native forced shutdown", async () =>
        {
            var api = new FakeRestartManager { ShutdownError = 351 };
            var logs = new List<string>();
            using var request = new GracefulExitRequest(FakeProcess(), default, (message, _) => logs.Add(message), api);
            await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Program.Equal(1, api.Shutdowns); Program.Equal(1, api.Ends);
            Program.True(logs.Any(message => message.Contains("RmShutdown: ошибка 351")));
        });
        await test("Cancellation interrupts Restart Manager backoff without sending another request", async () =>
        {
            using var cancel = new CancellationTokenSource();
            var api = new FakeRestartManager { StartErrors = new([353u, 353u, 353u]) };
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var request = new GracefulExitRequest(FakeProcess(), cancel.Token, (_, _) => { cancel.Cancel(); failed.TrySetResult(); }, api);
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await request.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Program.Equal(1, api.Starts); Program.Equal(0, api.Shutdowns);
        });
    }
    private static ProcessInfo FakeProcess() => new(1, 0, 1, @"C:\Apps\fixture.exe", 0, false);
    private static void VerifyQueuedPowerOperation(Storage storage, bool enable)
    {
        var api = new FakePower(); var original = api.Read(api.Active);
        var power = new PowerSettings(storage, api); power.Enable();
        using var mutex = new Mutex(false, PowerSettings.RecoveryMutexName);
        mutex.WaitOne();
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try { if (enable) power.Enable(); else power.EnsureActiveScheme(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        worker.Start();
        try
        {
            // This worker can wait only at the recovery mutex. Hold it on this thread,
            // restore reentrantly, then let the stale operation acquire the mutex.
            Program.True(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, 5000));
            power.Restore();
        }
        finally { mutex.ReleaseMutex(); worker.Join(5000); }
        Program.True(!worker.IsAlive); if (error != null) throw error;
        Program.True(!power.Enabled && !power.RestorePending && !File.Exists(storage.JournalPath));
        Program.Equal(original, api.Read(api.Active)); Program.Equal(2, api.Writes);
    }
    private static Task WithStorage(Action<Storage> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LiteHibernate.Regression.Test." + Guid.NewGuid().ToString("N"));
        try { action(new Storage(directory)); }
        finally { Program.CleanupTestDirectory(directory); }
        return Task.CompletedTask;
    }
    internal sealed class FakePower : IPowerApi
    {
        public Guid Active = Guid.NewGuid();
        public readonly Dictionary<Guid, SchemeBackup> Values = [];
        public int Writes;
        public bool FailEnumeration;
        public int RestoreFailures, OverrideFailures;
        public FakePower() { Values[Active] = new(Active, 1, 2); }
        public Guid ActiveScheme() => Active;
        public bool SchemeExists(Guid scheme) => !FailEnumeration ? Values.ContainsKey(scheme) : throw new IOException("Simulated enumeration failure.");
        public SchemeBackup Read(Guid scheme) => Values[scheme];
        public void Write(SchemeBackup value)
        {
            Writes++; _ = Values[value.Scheme];
            if (value.Ac != 0 && RestoreFailures > 0)
            {
                RestoreFailures--; Values[value.Scheme] = Values[value.Scheme] with { Ac = value.Ac };
                throw new IOException("Simulated partial power restore failure.");
            }
            if (value.Ac == 0 && value.Dc == 0 && OverrideFailures > 0)
            {
                OverrideFailures--; Values[value.Scheme] = Values[value.Scheme] with { Ac = 0 };
                throw new IOException("Simulated power override failure.");
            }
            Values[value.Scheme] = value;
        }
        public void Apply(Guid scheme) => Active = scheme;
    }
    private sealed class FakeRestartManager : IRestartManagerApi
    {
        public Queue<uint> StartErrors = new(), RegisterErrors = new();
        public uint ShutdownError;
        public int Starts, Shutdowns, Ends;
        public uint Start(out uint session) { session = (uint)++Starts; return StartErrors.TryDequeue(out var error) ? error : 0; }
        public uint Register(uint session, ProcessInfo process) => RegisterErrors.TryDequeue(out var error) ? error : 0;
        public uint Shutdown(uint session) { Shutdowns++; return ShutdownError; }
        public uint Cancel(uint session) => 0;
        public uint End(uint session) { Ends++; return 0; }
    }
}
