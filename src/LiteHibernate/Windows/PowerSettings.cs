using System.Runtime.InteropServices;
using System.Security.Principal;
using LiteHibernate.Infrastructure;

namespace LiteHibernate.Windows;

public sealed record SchemeBackup(Guid Scheme, uint Ac, uint Dc);
public sealed class PowerJournal
{
    public int Version { get; set; } = 1;
    public List<SchemeBackup> Schemes { get; set; } = [];
    public ProcessIdentity? Owner { get; set; }
}
public interface IPowerApi
{
    void EnsureCanOverride() { }
    bool SchemeExists(Guid scheme);
    Guid ActiveScheme();
    SchemeBackup Read(Guid scheme);
    void Write(SchemeBackup values);
    void Apply(Guid scheme);
}
public sealed class WindowsPowerApi : IPowerApi
{
    public bool SchemeExists(Guid scheme)
    {
        for (uint index = 0; ; index++)
        {
            uint size = 16;
            var error = Native.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16, index, out var found, ref size);
            if (error == 259) return false; // ERROR_NO_MORE_ITEMS; other errors must retain the backup.
            Native.Check(error, "Не удалось проверить наличие схемы питания.");
            if (found == scheme) return true;
        }
    }
    public void EnsureCanOverride()
    {
        Native.Check(Native.PowerSettingAccessCheck(0, in Native.LidAction), "Windows запрещает изменение действия крышки от сети.");
        Native.Check(Native.PowerSettingAccessCheck(1, in Native.LidAction), "Windows запрещает изменение действия крышки от батареи.");
    }
    public Guid ActiveScheme()
    {
        Native.Check(Native.PowerGetActiveScheme(IntPtr.Zero, out var pointer), "Не удалось прочитать схему питания.");
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { Native.LocalFree(pointer); }
    }
    public SchemeBackup Read(Guid scheme)
    {
        Native.Check(Native.PowerReadACValueIndex(IntPtr.Zero, in scheme, in Native.Buttons, in Native.LidAction, out var ac), "Не удалось прочитать действие крышки от сети.");
        Native.Check(Native.PowerReadDCValueIndex(IntPtr.Zero, in scheme, in Native.Buttons, in Native.LidAction, out var dc), "Не удалось прочитать действие крышки от батареи.");
        return new(scheme, ac, dc);
    }
    public void Write(SchemeBackup values)
    {
        var scheme = values.Scheme;
        Native.Check(Native.PowerWriteACValueIndex(IntPtr.Zero, in scheme, in Native.Buttons, in Native.LidAction, values.Ac), "Не удалось изменить действие крышки от сети.");
        Native.Check(Native.PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in Native.Buttons, in Native.LidAction, values.Dc), "Не удалось изменить действие крышки от батареи.");
    }
    public void Apply(Guid scheme) => Native.Check(Native.PowerSetActiveScheme(IntPtr.Zero, in scheme), "Не удалось применить схему питания.");
}

public sealed class PowerSettings(Storage storage, IPowerApi api, ProcessIdentity? processOwner = null)
{
    private readonly ProcessIdentity owner = processOwner ?? ProcessIdentity.Current;
    private readonly object stateLock = new();
    private volatile bool enabled, restorePending;
    private int generation;
    public static string RecoveryMutexName => @"Local\LiteHibernate.Recovery." + WindowsIdentity.GetCurrent().User!.Value;
    public bool Enabled => enabled;
    public bool RestorePending => restorePending;
    public void Enable() => Enable(CancellationToken.None);
    public void Enable(CancellationToken cancellationToken)
    {
        int expected;
        lock (stateLock) expected = generation;
        WithLock(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (stateLock)
            {
                if (expected != generation) return;
                if (restorePending) throw new InvalidOperationException("Сначала нужно восстановить исходные настройки крышки.");
            }
            OverrideActive();
            lock (stateLock)
            {
                // A concurrent restore invalidates this operation before waiting for the mutex.
                cancellationToken.ThrowIfCancellationRequested();
                if (expected == generation && !restorePending) enabled = true;
            }
        });
    }
    public void EnsureActiveScheme()
    {
        int expected;
        lock (stateLock)
        {
            if (!enabled || restorePending) return;
            expected = generation;
        }
        WithLock(() =>
        {
            lock (stateLock)
                if (!enabled || restorePending || expected != generation) return;
            OverrideActive();
        });
    }
    private void OverrideActive()
    {
        api.EnsureCanOverride();
        var journal = ReadJournal();
        if (journal.Owner != null && journal.Owner != owner)
            throw new InvalidOperationException("Сначала восстановите журнал предыдущего запуска LiteHibernate.");
        var claimed = journal.Owner != owner;
        journal.Owner = owner;
        var scheme = api.ActiveScheme();
        var current = api.Read(scheme);
        if (journal.Schemes.All(s => s.Scheme != scheme))
        {
            journal.Schemes.Add(current);
            storage.Write(storage.JournalPath, journal);
            storage.Log($"Исходные настройки крышки сохранены: схема {scheme}; AC={current.Ac}; DC={current.Dc}.");
        }
        else if (claimed) storage.Write(storage.JournalPath, journal);
        if (current.Ac != 0 || current.Dc != 0)
        {
            api.Write(new(scheme, 0, 0));
            // Do not switch back if an external actor selected another scheme during the write.
            if (api.ActiveScheme() == scheme) api.Apply(scheme);
        }
        var check = api.Read(scheme);
        if (check.Ac != 0 || check.Dc != 0) throw new InvalidOperationException("Windows не применила Do nothing. Возможно, действует групповая политика.");
        if (current.Ac != 0 || current.Dc != 0) storage.Log($"Действие крышки изменено на Do nothing: схема {scheme}; AC=0; DC=0.");
    }
    public void Restore() => RestoreCore(null);
    // A watchdog may only restore the process generation it actually observed.
    // A new app restores the previous journal before claiming its own ownership.
    public bool RestoreOwned(ProcessIdentity expectedOwner) => RestoreCore(expectedOwner);
    private bool RestoreCore(ProcessIdentity? expectedOwner)
    {
        int restoringGeneration;
        lock (stateLock)
        {
            restoringGeneration = ++generation;
            enabled = false;
            restorePending = true;
        }
        var restored = true;
        WithLock(() =>
        {
            if (!File.Exists(storage.JournalPath)) return;
            var journal = ReadJournal();
            if (expectedOwner != null && journal.Owner != null && journal.Owner != expectedOwner)
            {
                restored = false;
                storage.Log("Старый сторож пропустил журнал другого запуска LiteHibernate.");
                return;
            }
            if (expectedOwner == null && journal.Owner != null && journal.Owner != owner && journal.Owner.IsAlive())
                throw new InvalidOperationException("Настройки крышки принадлежат работающему экземпляру LiteHibernate.");
            var errors = new List<Exception>();
            foreach (var backup in journal.Schemes.ToArray())
            {
                try
                {
                    var exists = api.SchemeExists(backup.Scheme);
                    if (exists)
                    {
                        api.Write(backup);
                        if (api.ActiveScheme() == backup.Scheme) api.Apply(backup.Scheme);
                        if (api.Read(backup.Scheme) != backup) throw new IOException("Проверка восстановленных значений не прошла.");
                    }
                    journal.Schemes.Remove(backup);
                    storage.Write(storage.JournalPath, journal);
                    storage.Log(exists ? $"Настройки крышки восстановлены: схема {backup.Scheme}; AC={backup.Ac}; DC={backup.Dc}."
                        : $"Удалённая схема питания пропущена при восстановлении: {backup.Scheme}.");
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count > 0) throw new AggregateException("Не удалось полностью восстановить настройки крышки. Журнал сохранён для повторной попытки.", errors);
            File.Delete(storage.JournalPath);
            storage.Log("Исходные действия крышки восстановлены.");
        });
        // Keep recovery pending on every failure, including failure to acquire the mutex.
        lock (stateLock)
            if (generation == restoringGeneration) restorePending = false;
        return restored;
    }
    private PowerJournal ReadJournal()
    {
        var journal = storage.Read<PowerJournal>(storage.JournalPath) ?? new();
        if (journal.Version != 1 || journal.Schemes == null || journal.Schemes.Any(s => s == null || s.Ac > 3 || s.Dc > 3)
            || journal.Owner is { Pid: <= 0 } or { Created: <= 0 }
            || journal.Schemes.Select(s => s.Scheme).Distinct().Count() != journal.Schemes.Count)
            throw new InvalidDataException("Некорректный журнал восстановления питания.");
        return journal;
    }
    private static void WithLock(Action action)
    {
        using var mutex = new Mutex(false, RecoveryMutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("Восстановление настроек занято другим процессом.");
            action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
