namespace LiteHibernate.Core;

public sealed class ShutdownCoordinator(IProcessController processes, IHibernationService hibernation, IScenarioClock clock,
    IApplicationRestarter? restarter = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public event Action<ScenarioProgress>? Progress;
    public bool IsRunning { get; private set; }
    public ScenarioStage Stage { get; private set; } = ScenarioStage.Idle;

    public async Task<ScenarioResult> RunAsync(IReadOnlyList<AppRule> rules, int delaySeconds,
        CancellationToken lidOpenedDuringDelay, CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
            return new(ScenarioStage.Cancelled, "Сценарий уже выполняется.");
        IsRunning = true;
        try
        {
            if (delaySeconds is < 0 or > 3600 || rules.Any(r => r.TimeoutSeconds is < 1 or > 3600 || !Enum.IsDefined(r.Policy))
                || rules.Select(r => r.Id).Distinct().Count() != rules.Count)
                throw new ArgumentException("Недопустимый таймаут.");
            hibernation.EnsureAvailable();
            if (delaySeconds > 0)
            {
                using var countdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lidOpenedDuringDelay);
                var deadline = clock.Elapsed + TimeSpan.FromSeconds(delaySeconds);
                while (clock.Elapsed < deadline)
                {
                    var left = deadline - clock.Elapsed;
                    Set(ScenarioStage.Countdown, "Пауза перед закрытием приложений.", (int)Math.Ceiling(left.TotalSeconds));
                    await clock.DelayAsync(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), countdown.Token);
                }
                countdown.Token.ThrowIfCancellationRequested();
            }
            cancellationToken.ThrowIfCancellationRequested();
            Set(ScenarioStage.Closing, "Отправка запросов на штатное закрытие.");
            await using var session = await processes.CreateSessionAsync(rules, cancellationToken);
            TimeSpan? start = null;
            var ignored = new HashSet<string>();
            var killed = new Dictionary<string, TimeSpan>();
            var encountered = new HashSet<string>();
            Dictionary<string, GroupState> states;
            var stable = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                states = (await session.RefreshAsync(cancellationToken)).ToDictionary(s => s.Id);
                if (rules.Any(r => !states.ContainsKey(r.Id))) throw new InvalidOperationException("Не удалось проверить все выбранные приложения.");
                foreach (var state in states.Values.Where(state => state.WasRunning || state.RemainingCount > 0)) encountered.Add(state.Id);
                start ??= clock.Elapsed; // Give a full X seconds after the initial close requests.
                var pending = rules.Where(r => !ignored.Contains(r.Id) && states.GetValueOrDefault(r.Id)?.RemainingCount > 0).ToArray();
                var now = clock.Elapsed;
                // An expired cancellation policy takes priority over any kills in this refresh.
                var abort = pending.FirstOrDefault(r => r.Policy == TimeoutPolicy.CancelHibernate && now - start.Value >= TimeSpan.FromSeconds(r.TimeoutSeconds));
                if (abort != null) return Finish(ScenarioStage.Cancelled, $"Гибернация отменена: {abort.Name} не завершилось.");
                foreach (var rule in pending)
                {
                    if (now - start.Value < TimeSpan.FromSeconds(rule.TimeoutSeconds)) continue;
                    if (rule.Policy == TimeoutPolicy.ContinueHibernate)
                    {
                        ignored.Add(rule.Id);
                        continue;
                    }
                    if (!killed.TryGetValue(rule.Id, out var killStart))
                    {
                        Set(ScenarioStage.ForceTerminating, $"Принудительное завершение: {rule.Name}.");
                        await session.ForceTerminateAsync(rule.Id, cancellationToken);
                        killed[rule.Id] = clock.Elapsed;
                    }
                    else
                    {
                        if (now - killStart >= TimeSpan.FromSeconds(5))
                            return Finish(ScenarioStage.Failed, $"Процессы {rule.Name} не завершились после принудительного закрытия.");
                        // Include processes spawned during termination without extending the deadline.
                        await session.ForceTerminateAsync(rule.Id, cancellationToken);
                    }
                }
                var remaining = pending.Where(r => !ignored.Contains(r.Id)).ToArray();
                if (remaining.Length == 0)
                {
                    if (stable) break;
                    stable = true;
                }
                else
                {
                    stable = false;
                    var seconds = remaining.Max(r => (killed.TryGetValue(r.Id, out var killTime)
                        ? killTime + TimeSpan.FromSeconds(5)
                        : start.Value + TimeSpan.FromSeconds(r.TimeoutSeconds)) - clock.Elapsed);
                    foreach (var rule in remaining.Where(r => r.Policy == TimeoutPolicy.CancelHibernate))
                    {
                        var untilCancel = start.Value + TimeSpan.FromSeconds(rule.TimeoutSeconds) - clock.Elapsed;
                        if (untilCancel < seconds) seconds = untilCancel;
                    }
                    Set(ScenarioStage.Waiting, $"Ожидание завершения: {string.Join(", ", remaining.Select(r => r.Name))}.", Math.Max(0, (int)Math.Ceiling(seconds.TotalSeconds)));
                }
                await clock.DelayAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            hibernation.EnsureAvailable();
            var reopen = rules.Where(rule => rule.ReopenAfterHibernate && encountered.Contains(rule.Id)
                    && states[rule.Id].RemainingCount == 0)
                .DistinctBy(rule => rule.ExecutablePath, StringComparer.OrdinalIgnoreCase).Select(rule => rule with { }).ToArray();
            Set(ScenarioStage.Hibernating, "Переход в гибернацию…");
            await hibernation.HibernateAsync(cancellationToken);
            IReadOnlyList<Exception> reopenErrors = [];
            if (restarter != null && reopen.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Set(ScenarioStage.Restoring, "Открытие приложений после гибернации…");
                reopenErrors = await restarter.ReopenAsync(reopen, cancellationToken);
            }
            return Finish(ScenarioStage.Completed, "Сценарий завершён.") with { ReopenErrors = reopenErrors };
        }
        catch (OperationCanceledException) { return Finish(ScenarioStage.Cancelled, "Сценарий отменён."); }
        catch (Exception ex) { return Finish(ScenarioStage.Failed, ex.Message) with { Error = ex }; }
        finally { IsRunning = false; gate.Release(); }
    }
    private void Set(ScenarioStage stage, string message, int? remainingSeconds = null)
    {
        Stage = stage;
        Progress?.Invoke(new(stage, message, remainingSeconds));
    }
    private ScenarioResult Finish(ScenarioStage stage, string message)
    {
        Set(stage, message);
        return new(stage, message);
    }
}
