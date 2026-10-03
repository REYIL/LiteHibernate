using LiteHibernate.Core;

namespace LiteHibernate.Infrastructure;

// A portable scenario excludes Windows integration preferences and recovery journals.
public sealed class ScenarioProfile
{
    public int Version { get; set; } = 1;
    public int LidDelaySeconds { get; set; } = 5;
    public List<AppRule> Applications { get; set; } = [];

    public void Validate()
    {
        if (Applications == null || Applications.Any(rule => rule == null))
            throw new InvalidDataException("Некорректный список приложений.");
        new AppSettings { Version = Version, LidDelaySeconds = LidDelaySeconds, Applications = Applications }.Validate();
        if (Applications.Any(rule => string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Name)
            || !Path.IsPathFullyQualified(rule.ExecutablePath)
            || !string.Equals(Path.GetExtension(rule.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Для каждого приложения нужны название и полный путь к .exe.");
        if (Applications.Select(rule => Path.GetFullPath(rule.ExecutablePath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Applications.Count)
            throw new InvalidDataException("В профиле повторяются пути приложений.");
    }

    public static ScenarioProfile Import(Storage storage, string path)
    {
        var profile = storage.Read<ScenarioProfile>(path) ?? throw new FileNotFoundException("Файл профиля не найден.", path);
        profile.Validate();
        profile.Applications = profile.Applications.Select(rule => rule.NormalizePath()).ToList();
        return profile;
    }

    public void Export(Storage storage, string path) { Validate(); storage.Write(path, this); }
}
