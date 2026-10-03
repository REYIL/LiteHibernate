using System.Diagnostics;
using LiteHibernate.Core;
using LiteHibernate.Infrastructure;

namespace LiteHibernate.Windows;

public sealed class ApplicationRestarter(ProcessInventory inventory, Storage storage) : IApplicationRestarter
{
    public Task<IReadOnlyList<Exception>> ReopenAsync(IReadOnlyList<AppRule> rules, CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<Exception>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var errors = new List<Exception>();
            foreach (var rule in rules.Select(rule => rule.NormalizePath()).DistinctBy(rule => rule.ExecutablePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Windows or the user can reopen another app while this list is being
                    // processed. Check immediately before each launch, not just once.
                    if (inventory.Capture().Any(process => string.Equals(process.Path, rule.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        storage.Log($"Повторное открытие пропущено: {rule.Name} уже работает.");
                        continue;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ProcessInventory.IsSelectablePath(rule.ExecutablePath) || !File.Exists(rule.ExecutablePath))
                        throw new FileNotFoundException("Исполняемый файл приложения недоступен.", rule.ExecutablePath);
                    using var process = Process.Start(new ProcessStartInfo(rule.ExecutablePath)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(rule.ExecutablePath)!
                    });
                    storage.Log($"Открыто после гибернации: {rule.Name}; EXE: {rule.ExecutablePath}.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var error = new InvalidOperationException($"Не удалось открыть {rule.Name} после гибернации: {ex.Message}", ex);
                    errors.Add(error);
                    storage.Log(error.ToString(), true);
                }
            }
            return errors;
        }, cancellationToken);
}
