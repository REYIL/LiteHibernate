using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using LiteHibernate.Core;

namespace LiteHibernate.Infrastructure;

public sealed class Storage
{
    public string DirectoryPath { get; }
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public string JournalPath => Path.Combine(DirectoryPath, "power-recovery.json");
    public string LogsDirectoryPath => Path.Combine(DirectoryPath, "Logs");
    public string LogPath => Path.Combine(LogsDirectoryPath, "events.log");
    private readonly string logMutexName;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public Storage(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteHibernate");
        Directory.CreateDirectory(DirectoryPath);
        var identity = Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        logMutexName = @"Local\LiteHibernate.Log." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    public AppSettings LoadSettings()
    {
        var settings = Read<AppSettings>(SettingsPath) ?? new();
        settings.NormalizePaths();
        return settings;
    }
    public void SaveSettings(AppSettings settings) { settings.Validate(); Write(SettingsPath, settings); }
    public T? Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Повреждён файл {Path.GetFileName(path)}.") : default;
    public void Write<T>(string path, T value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private readonly object logLock = new();
    public void Log(string message, bool error = false)
    {
        lock (logLock)
        {
            try
            {
                // The app and watchdog can log concurrently. Serialize their writes and
                // rotation so a sharing violation does not silently lose either entry.
                using var mutex = new Mutex(false, logMutexName);
                bool acquired;
                try { acquired = mutex.WaitOne(1000); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return;
                try
                {
                    Directory.CreateDirectory(LogsDirectoryPath);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000) File.Move(LogPath, LogPath + ".old", true);
                    File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} [{(error ? "ERROR" : "INFO")}] [PID {Environment.ProcessId}] {message}{Environment.NewLine}", Encoding.UTF8);
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
