namespace LiteHibernate.Core;

public enum TimeoutPolicy { ForceTerminate, CancelHibernate, ContinueHibernate }
public enum ScenarioStage { Idle, Countdown, Closing, Waiting, ForceTerminating, Hibernating, Completed, Cancelled, Failed, Restoring }

public sealed record AppRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public bool Selected { get; set; }
    public bool ManuallyAdded { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public TimeoutPolicy Policy { get; set; } = TimeoutPolicy.ForceTerminate;
    public bool ReopenAfterHibernate { get; set; }
    public AppRule NormalizePath()
    {
        if (string.IsNullOrEmpty(ExecutablePath)) return this;
        if (!Path.IsPathFullyQualified(ExecutablePath)) throw new InvalidDataException("Нужен полный путь к приложению.");
        var path = Path.GetFullPath(ExecutablePath);
        return path == ExecutablePath ? this : this with { ExecutablePath = path };
    }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool LidEnabled { get; set; }
    public bool AutoStart { get; set; }
    public bool WatchdogEnabled { get; set; } = true;
    public int LidDelaySeconds { get; set; } = 5;
    public List<AppRule> Applications { get; set; } = [];
    public List<AppRule> DeletedApplications { get; set; } = [];
    public void NormalizePaths()
    {
        Validate();
        Applications = Applications.Select(rule => rule.NormalizePath()).ToList();
        DeletedApplications = DeletedApplications.Select(rule => rule.NormalizePath()).ToList();
    }
    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException("Неподдерживаемая версия настроек.");
        if (LidDelaySeconds is < 0 or > 3600) throw new InvalidDataException("Задержка крышки должна быть от 0 до 3600 секунд.");
        if (Applications == null || DeletedApplications == null || Applications.Concat(DeletedApplications).Any(a => a == null))
            throw new InvalidDataException("Некорректный список приложений.");
        var rules = Applications.Concat(DeletedApplications).ToArray();
        if (rules.Any(a => string.IsNullOrWhiteSpace(a.Id) || a.Name == null || a.ExecutablePath == null))
            throw new InvalidDataException("Некорректные сведения о приложении: проверьте идентификатор, название и путь.");
        if (rules.Any(a => a.TimeoutSeconds is < 1 or > 3600 || !Enum.IsDefined(a.Policy)))
            throw new InvalidDataException("Таймаут приложения должен быть от 1 до 3600 секунд, политика должна быть известна.");
        if (rules.Select(a => a.Id).Distinct().Count() != rules.Length)
            throw new InvalidDataException("Повторяющиеся идентификаторы приложений.");
    }
}

public sealed record GroupState(string Id, int RemainingCount, bool WasRunning = false);
public sealed record ScenarioProgress(ScenarioStage Stage, string Message, int? RemainingSeconds = null);
public sealed record ScenarioResult(ScenarioStage Stage, string Message)
{
    public Exception? Error { get; init; }
    public IReadOnlyList<Exception> ReopenErrors { get; init; } = [];
}

public interface IShutdownSession : IAsyncDisposable
{
    // Refresh discovers new instances/descendants and requests graceful closing once per process.
    Task<IReadOnlyList<GroupState>> RefreshAsync(CancellationToken cancellationToken);
    Task ForceTerminateAsync(string groupId, CancellationToken cancellationToken);
}
public interface IProcessController
{
    Task<IShutdownSession> CreateSessionAsync(IReadOnlyList<AppRule> rules, CancellationToken cancellationToken);
}
public interface IHibernationService
{
    void EnsureAvailable();
    // Completes after resume; an accepted power request alone is insufficient.
    Task HibernateAsync(CancellationToken cancellationToken);
}
public interface IApplicationRestarter
{
    Task<IReadOnlyList<Exception>> ReopenAsync(IReadOnlyList<AppRule> rules, CancellationToken cancellationToken);
}
public interface IScenarioClock
{
    TimeSpan Elapsed { get; }
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}
public sealed class ScenarioClock : IScenarioClock
{
    private readonly System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
    public TimeSpan Elapsed => stopwatch.Elapsed;
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.Delay(duration, cancellationToken);
}
