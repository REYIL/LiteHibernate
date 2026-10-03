using System.Diagnostics;
using LiteHibernate.Infrastructure;

namespace LiteHibernate.Windows;

public interface IWatchdog : IDisposable
{
    bool IsRunning { get; }
    Task StartAsync(Storage storage);
    void PrepareForExit();
    void Disarm();
}

public sealed class Watchdog(WatchdogRestartContext? restartContext = null) : IWatchdog
{
    private EventWaitHandle? ready, disarm, restoreOnly;
    private Process? child;
    private WatchdogRestartContext? connection = restartContext;
    private readonly bool mayRestart = restartContext == null;
    public bool IsRunning => child is { HasExited: false };
    public async Task StartAsync(Storage storage)
    {
        if (IsRunning) return;
        Dispose();
        if (connection != null)
        {
            using var handle = Native.OpenProcess(0x101000, false, connection.WatchdogPid);
            if (handle.IsInvalid || !Native.GetProcessTimes(handle, out var actual, out _, out _, out _) || actual != connection.WatchdogCreated)
                throw new IOException("Сторож восстановления недоступен.");
            ready = EventWaitHandle.OpenExisting(connection.Prefix + ".ready");
            disarm = EventWaitHandle.OpenExisting(connection.Prefix + ".disarm");
            restoreOnly = EventWaitHandle.OpenExisting(connection.Prefix + ".restore-only");
            child = Process.GetProcessById(connection.WatchdogPid);
            if (child.HasExited) throw new IOException("Сторож восстановления завершился.");
            connection = null;
            storage.Log("Перезапущенное приложение подключилось к существующему сторожу.");
            return;
        }
        var prefix = @"Local\LiteHibernate.Watchdog." + Guid.NewGuid().ToString("N");
        ready = new(false, EventResetMode.ManualReset, prefix + ".ready");
        disarm = new(false, EventResetMode.ManualReset, prefix + ".disarm");
        restoreOnly = new(false, EventResetMode.ManualReset, prefix + ".restore-only");
        using var parent = Native.OpenProcess(0x1000, false, Environment.ProcessId);
        if (!Native.GetProcessTimes(parent, out var created, out _, out _, out _)) throw Native.Error("Не удалось запустить сторож.");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--watchdog", Environment.ProcessId.ToString(), created.ToString(), prefix }) start.ArgumentList.Add(arg);
        if (!mayRestart) start.ArgumentList.Add("--no-restart");
        child = Process.Start(start) ?? throw new IOException("Не удалось запустить фоновый сторож.");
        var acknowledged = await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(10)));
        if (!acknowledged || child.HasExited)
        {
            Disarm();
            throw new IOException("Фоновый сторож не подтвердил готовность. Функция не включена.");
        }
        storage.Log("Фоновый сторож готов.");
    }
    public void PrepareForExit() => restoreOnly?.Set();
    public void Disarm()
    {
        disarm?.Set();
        if (child is { HasExited: false }) child.WaitForExit(2000);
        Dispose();
    }
    public void Dispose()
    {
        ready?.Dispose(); disarm?.Dispose(); restoreOnly?.Dispose(); child?.Dispose();
        ready = disarm = restoreOnly = null; child = null;
    }
    public static int Run(string[] args, Storage storage, IPowerApi? powerApi = null, IWatchdogRecovery? recovery = null, TimeSpan? startupTimeout = null)
    {
        recovery ??= new WatchdogRecovery();
        PowerSettings? power = null;
        ProcessIdentity? owner = null;
        try
        {
            if (args.Length is not (4 or 5) || args.Length == 5 && args[4] != "--no-restart"
                || !int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var created)
                || !args[3].StartsWith(@"Local\LiteHibernate.Watchdog.", StringComparison.Ordinal)) return 2;
            using var ready = EventWaitHandle.OpenExisting(args[3] + ".ready");
            using var disarm = EventWaitHandle.OpenExisting(args[3] + ".disarm");
            using var restoreOnly = EventWaitHandle.OpenExisting(args[3] + ".restore-only");
            using var parent = Native.OpenProcess(0x101000, false, pid);
            if (parent.IsInvalid || !Native.GetProcessTimes(parent, out var actual, out _, out _, out _) || actual != created) return 2;
            owner = new(pid, created);
            power = new(storage, powerApi ?? new WindowsPowerApi());
            ready.Set();
            storage.Log("Сторож ожидает завершения приложения или отключения.");
            // Both owned handles stay open throughout the wait. Index zero gives an intentional
            // disarm priority if it and the parent exit are signaled together.
            var signaled = Native.WaitForMultipleObjects(2,
                [disarm.SafeWaitHandle.DangerousGetHandle(), parent.DangerousGetHandle()], false, uint.MaxValue);
            if (signaled == uint.MaxValue) throw Native.Error("Не удалось дождаться завершения приложения.");
            if (signaled == 0) { storage.Log("Сторож штатно отключён."); return 0; }
            if (signaled != 1) throw new InvalidOperationException("Неожиданный результат ожидания сторожа.");
            if (disarm.WaitOne(0)) return 0;
            storage.Log("Сторож обнаружил завершение приложения. Восстановление настроек крышки.");
            var initialRecovery = Restore(power, storage, owner);
            if (initialRecovery == RecoveryResult.Superseded) return 0;
            if (initialRecovery == RecoveryResult.Failed) return Failure(recovery, storage, "Не удалось восстановить настройки крышки после сбоя LiteHibernate. Подробности в логе.");
            if (restoreOnly.WaitOne(0)) { storage.Log("Штатный выход: повторный запуск не требуется."); return 0; }
            if (args.Length == 5) return Failure(recovery, storage, "LiteHibernate повторно завершился. Исходные настройки крышки восстановлены. Запустите приложение вручную.");
            return RestartAndWatch(args[3], disarm, restoreOnly, power, storage, owner, recovery, startupTimeout ?? TimeSpan.FromSeconds(45));
        }
        catch (Exception ex)
        {
            storage.Log("Сторож: " + ex, true);
            var outcome = power != null && owner != null ? Restore(power, storage, owner) : RecoveryResult.Failed;
            if (outcome == RecoveryResult.Superseded) return 0;
            var restored = outcome == RecoveryResult.Restored;
            return Failure(recovery, storage, restored ? "Ошибка фонового сторожа. Исходные настройки крышки восстановлены. Подробности в логе."
                : "Ошибка фонового сторожа. Проверьте настройки действия крышки в Windows. Подробности в логе.");
        }
    }
    private static int RestartAndWatch(string prefix, EventWaitHandle disarm, EventWaitHandle restoreOnly,
        PowerSettings power, Storage storage, ProcessIdentity owner, IWatchdogRecovery recovery, TimeSpan startupTimeout)
    {
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".restarted");
        using var failed = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".failed");
        using var self = Native.OpenProcess(0x1000, false, Environment.ProcessId);
        if (!Native.GetProcessTimes(self, out var created, out _, out _, out _)) throw Native.Error("Не удалось подготовить перезапуск.");
        Process? restarted = null;
        try
        {
            storage.Log("Сторож запускает LiteHibernate повторно. Разрешена одна попытка.");
            restarted = recovery.Start(new(Environment.ProcessId, created, prefix));
            owner = ProcessIdentity.FromProcess(restarted);
            storage.Log($"Запущен процесс восстановления LiteHibernate: PID {restarted.Id}. Ожидание готовности.");
            var result = Native.WaitForMultipleObjects(4,
                [failed.SafeWaitHandle.DangerousGetHandle(), disarm.SafeWaitHandle.DangerousGetHandle(), restarted.SafeHandle.DangerousGetHandle(), started.SafeWaitHandle.DangerousGetHandle()],
                false, checked((uint)startupTimeout.TotalMilliseconds));
            if (result == 1) { storage.Log("Перезапущенное приложение штатно отключило сторож."); return 0; }
            if (result == uint.MaxValue) throw Native.Error("Не удалось проверить запуск LiteHibernate.");
            if (result == 258) throw new TimeoutException("LiteHibernate не подтвердил готовность после перезапуска.");
            if (result != 3 || restarted.HasExited) throw new IOException("LiteHibernate не смог успешно запуститься после сбоя.");
            storage.Log("LiteHibernate подтвердил успешный запуск. Сторож продолжает наблюдение без повторного перезапуска.");
            var stopped = Native.WaitForMultipleObjects(2,
                [disarm.SafeWaitHandle.DangerousGetHandle(), restarted.SafeHandle.DangerousGetHandle()], false, uint.MaxValue);
            if (stopped == 0) { storage.Log("Сторож штатно отключён после восстановления приложения."); return 0; }
            if (stopped == uint.MaxValue) throw Native.Error("Не удалось дождаться перезапущенного приложения.");
            if (stopped != 1) throw new IOException("Неожиданный результат наблюдения после перезапуска.");
            storage.Log("Перезапущенный LiteHibernate завершился. Повторное восстановление настроек крышки.");
            var outcome = Restore(power, storage, owner);
            if (outcome == RecoveryResult.Superseded) return 0;
            var restored = outcome == RecoveryResult.Restored;
            if (restored && restoreOnly.WaitOne(0)) return 0;
            return Failure(recovery, storage, restored ? "LiteHibernate повторно завершился. Исходные настройки крышки восстановлены. Запустите приложение вручную."
                : "LiteHibernate повторно завершился. Не удалось восстановить настройки крышки. Подробности в логе.");
        }
        catch (Exception ex)
        {
            storage.Log("Ошибка перезапуска LiteHibernate: " + ex, true);
            // Only stop the process launched by this recovery attempt. It must not
            // apply power settings after the startup timeout or failure fallback.
            if (restarted != null)
            {
                try
                {
                    if (!restarted.WaitForExit(1000))
                    {
                        storage.Log($"Завершение неуспешно запущенного LiteHibernate: PID {restarted.Id}.");
                        restarted.Kill();
                        if (!restarted.WaitForExit(5000)) throw new TimeoutException("Процесс восстановления не завершился.");
                    }
                }
                catch (Exception stopping) { storage.Log("Ошибка завершения процесса восстановления: " + stopping, true); }
            }
            var outcome = Restore(power, storage, owner);
            if (outcome == RecoveryResult.Superseded) return 0;
            var restored = outcome == RecoveryResult.Restored;
            return Failure(recovery, storage, restored ? "Не удалось перезапустить LiteHibernate. Исходные настройки крышки восстановлены. Подробности в логе."
                : "Не удалось перезапустить LiteHibernate и восстановить настройки крышки. Подробности в логе.");
        }
        finally { restarted?.Dispose(); }
    }
    private enum RecoveryResult { Restored, Superseded, Failed }
    private static RecoveryResult Restore(PowerSettings power, Storage storage, ProcessIdentity owner)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!power.RestoreOwned(owner)) return RecoveryResult.Superseded;
                storage.Log("Сторож восстановил настройки крышки."); return RecoveryResult.Restored;
            }
            catch (Exception ex) { storage.Log($"Ошибка восстановления сторожа, попытка {attempt + 1}: {ex}", true); if (attempt < 2) Thread.Sleep(1000); }
        }
        return RecoveryResult.Failed;
    }
    private static int Failure(IWatchdogRecovery recovery, Storage storage, string message)
    {
        storage.Log(message, true);
        try { recovery.NotifyFailure(message); }
        catch (Exception ex) { storage.Log("Не удалось показать уведомление сторожа: " + ex, true); }
        return 1;
    }
}
