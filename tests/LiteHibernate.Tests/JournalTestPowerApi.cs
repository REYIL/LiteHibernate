using LiteHibernate.Infrastructure;
using LiteHibernate.Windows;

namespace LiteHibernate.Tests;

// Persists fake power values so an isolated watchdog process can restore them.
internal sealed class JournalTestPowerApi(Storage storage) : IPowerApi
{
    private string PathValue => Path.Combine(storage.DirectoryPath, "fake-power.json");
    public Guid ActiveScheme() => storage.Read<SchemeBackup>(PathValue)!.Scheme;
    public bool SchemeExists(Guid scheme) => storage.Read<SchemeBackup>(PathValue)?.Scheme == scheme;
    public SchemeBackup Read(Guid scheme) => storage.Read<SchemeBackup>(PathValue)!;
    public void Write(SchemeBackup values)
    {
        if (values.Ac != 0 && File.Exists(Path.Combine(storage.DirectoryPath, "fail-restore"))) throw new IOException("Simulated recovery failure.");
        storage.Write(PathValue, values);
    }
    public void Apply(Guid scheme) { }
}
