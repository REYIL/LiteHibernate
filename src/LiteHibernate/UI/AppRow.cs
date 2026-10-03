using System.ComponentModel;
using System.Runtime.CompilerServices;
using LiteHibernate.Core;
using LiteHibernate.Windows;

namespace LiteHibernate.UI;

public sealed record PolicyChoice(TimeoutPolicy Value, string Label);
public sealed class AppRow : INotifyPropertyChanged
{
    public AppRule Rule { get; }
    public string Name => Rule.Name;
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : System.Globalization.StringInfo.GetNextTextElement(Name.Trim()).ToUpperInvariant();
    public string PathDisplay => CanSelect ? Rule.ExecutablePath : "Файл не найден";
    public bool CanSelect => !string.IsNullOrEmpty(Rule.ExecutablePath) && ProcessInventory.IsSelectablePath(Rule.ExecutablePath);
    public bool Configured { get; private set; }
    private bool isDeleted;
    public bool IsDeleted { get => isDeleted; internal set { if (isDeleted != value) { isDeleted = value; Notify(); } } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ConfigurationChanged;
    private AppSource source;
    public AppSource Source { get => source; set { source = value; Notify(); Notify(nameof(SourceLabel)); } }
    public string SourceLabel => string.Join(", ", new[]
    {
        Source.HasFlag(AppSource.Installed) ? "Установлено" : null,
        Source.HasFlag(AppSource.Running) ? "Запущено" : null,
        Source.HasFlag(AppSource.Manual) ? "Вручную" : null
    }.Where(s => s != null));
    public bool Selected { get => Rule.Selected; set { if (Rule.Selected != value && CanSelect) { Rule.Selected = value; Changed(); Notify(); } } }
    public int TimeoutSeconds
    {
        get => Rule.TimeoutSeconds;
        set
        {
            if (value is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(value), "Допустимо от 1 до 3600 секунд.");
            if (Rule.TimeoutSeconds != value) { Rule.TimeoutSeconds = value; Changed(); Notify(); }
        }
    }
    public TimeoutPolicy Policy { get => Rule.Policy; set { if (Rule.Policy != value) { Rule.Policy = value; Changed(); Notify(); } } }
    public bool ReopenAfterHibernate
    {
        get => Rule.ReopenAfterHibernate;
        set { if (Rule.ReopenAfterHibernate != value && CanSelect) { Rule.ReopenAfterHibernate = value; Changed(); Notify(); } }
    }
    private string status = "Not running", ram = "–";
    public string Status { get => status; private set { status = value; Notify(); } }
    public string Ram { get => ram; private set { ram = value; Notify(); } }
    private long? ramBytes;
    public long? RamBytes { get => ramBytes; private set { if (ramBytes != value) { ramBytes = value; Notify(); } } }
    internal IReadOnlyList<ProcessInfo> RunningProcesses { get; private set; } = [];
    public AppRow(AppRule rule, AppSource source, bool configured = false) { Rule = rule.NormalizePath(); this.source = source; Configured = configured; }
    public void UpdateMetrics(IReadOnlyList<ProcessInfo> processes)
    {
        var roots = processes.Where(p => string.Equals(p.Path, Rule.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        var group = ProcessInventory.ExpandTree(roots, processes);
        RunningProcesses = group;
        Status = group.Count > 0 ? "Running" : "Not running";
        RamBytes = group.Count == 0 ? 0 : group.Any(p => !p.Ram.HasValue) ? null : group.Sum(p => p.Ram!.Value);
        Ram = group.Count == 0 || RamBytes == null ? "–" : FormatMemory(RamBytes.Value);
        var nextSource = group.Count > 0 ? Source | AppSource.Running : Source & ~AppSource.Running;
        if (nextSource != Source) Source = nextSource;
        Notify(nameof(RunningProcesses));
    }
    private static readonly string[] MemoryUnits = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
    internal static string FormatMemory(long bytes)
    {
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < MemoryUnits.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {MemoryUnits[unit]}";
    }
    private void Changed() { Configured = true; ConfigurationChanged?.Invoke(); }
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
