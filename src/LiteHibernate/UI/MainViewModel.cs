using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LiteHibernate.Core;
using LiteHibernate.Infrastructure;
using LiteHibernate.Windows;

namespace LiteHibernate.UI;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Storage storage;
    private readonly AppSettings settings;
    private readonly PowerSettings power;
    private readonly IWatchdog watchdog;
    private readonly WatchdogRestartContext? watchdogRestart;
    private readonly ProcessInventory inventory = new();
    private readonly ShutdownCoordinator coordinator;
    private readonly IHibernationService hibernation;
    private readonly Func<AppRow, bool> confirmRemoval;
    private readonly Func<AppRow, bool> confirmRestoration;
    private readonly Func<AppRow?, string?> chooseExecutable;
    private readonly Action<bool> setAutoStart;
    private readonly ICollectionView activeApplicationsView, deletedApplicationsView;
    private readonly ApplicationListOrder activeListOrder, deletedListOrder;
    private readonly LidMonitor lid;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer monitorTimer, powerTimer, saveTimer;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly CancellationTokenSource shutdownCancellation = new();
    private CancellationTokenSource? runCancellation, lidDelayCancellation;
    private Task? runningTask;
    private bool refreshing, checkingPower, changingEnabled, disposed, initialized;
    private volatile bool exiting;
    private bool windowVisible;
    public bool WindowVisible
    {
        get => windowVisible;
        set
        {
            if (windowVisible == value) return;
            windowVisible = value;
            UpdateMonitoring();
            if (value && !disposed && !exiting) _ = RefreshRunningAsync();
        }
    }
    public ObservableCollection<AppRow> Applications { get; } = [];
    public ObservableCollection<AppRow> DeletedApplications { get; } = [];
    public ICollectionView ApplicationsView => IsDeletedView ? deletedApplicationsView : activeApplicationsView;
    public bool IsDeletedView => SourceFilter == 4;
    public System.Windows.Visibility ActiveColumnsVisibility => IsDeletedView ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility DeletedColumnsVisibility => IsDeletedView ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string EmptyListTitle => IsDeletedView ? "Скрытые приложения не найдены" : "Приложения не найдены";
    public string EmptyListHint => IsDeletedView ? "Измените поиск или выберите другой источник" : "Измените поиск или добавьте EXE вручную";
    public static IReadOnlyList<PolicyChoice> Policies { get; } = new[]
    {
        new PolicyChoice(TimeoutPolicy.ForceTerminate, "Принудительно завершить"),
        new PolicyChoice(TimeoutPolicy.CancelHibernate, "Отменить гибернацию"),
        new PolicyChoice(TimeoutPolicy.ContinueHibernate, "Продолжить гибернацию")
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string, bool>? Notification;
    public ICommand ToggleEnabledCommand { get; }
    public ICommand HibernateCommand { get; }
    public ICommand ScenarioCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand LinkCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ToggleListPinCommand { get; }
    private bool isListPinned;
    public bool IsListPinned
    {
        get => isListPinned;
        set
        {
            if (isListPinned == value) return;
            activeListOrder.SetPinned(value); deletedListOrder.SetPinned(value);
            isListPinned = value; Notify(); Notify(nameof(ListPinHint));
            storage.Log(value ? "Порядок списка закреплён." : "Автоматическая сортировка списка включена.");
        }
    }
    public string ListPinHint => IsListPinned
        ? "Открепить список. Порядок строк будет обновляться автоматически."
        : "Закрепить список. RAM и статусы продолжат обновляться, а строки останутся на своих местах.";
    internal void SortPinnedApplications(string property, ListSortDirection direction, bool append)
        => (IsDeletedView ? deletedListOrder : activeListOrder).SortPinned(property, direction, append);
    public AppRow? SelectedRow { get; set; }
    public bool Enabled => power.Enabled;
    public bool PowerRestorePending => power.RestorePending;
    public bool LidAvailable => lid.HasLid;
    public string ToggleLabel => PowerRestorePending ? "Ожидание восстановления настроек крышки" : !LidAvailable ? "Крышка недоступна – используйте кнопку гибернации" : Enabled ? "Отключить отслеживание крышки" : "Включить отслеживание крышки";
    public string LidStatus => !LidAvailable ? "Крышка отсутствует или недоступна" : lid.IsOpen switch { true => "Крышка открыта", false => "Крышка закрыта", _ => "Состояние крышки пока неизвестно" };
    public string LidAutomationDescription => PowerRestorePending
        ? "Не удалось вернуть исходные настройки крышки. Повторная попытка – каждые 10 секунд. Подробности – в логе."
        : LidAvailable
        ? "При включении действие крышки в Windows меняется на «Бездействие» – для сети и батареи. Закрытие программ и гибернацию берёт на себя LiteHibernate."
        : "Крышка отсутствует или недоступна. Используйте кнопку «Перейти в гибернацию».";
    public string PauseHint => LidAvailable ? "Откройте крышку во время паузы, чтобы отменить сценарий." : "Во время паузы можно отменить гибернацию кнопкой.";
    public bool IsRunning => coordinator.IsRunning;
    public int SelectedCount => Applications.Count(row => row.Selected && row.CanSelect);
    public int RunningCount => Applications.Count(row => row.Status == "Running");
    public string EstimatedWriteSavings
    {
        get
        {
            var processes = Applications.Where(row => row.Selected && row.CanSelect)
                .SelectMany(row => row.RunningProcesses).DistinctBy(process => (process.Pid, process.Created)).ToArray();
            if (processes.Any(process => !process.Ram.HasValue)) return "Нет данных";
            var bytes = processes.Sum(process => process.Ram!.Value);
            return (bytes > 0 ? "≈ " : "") + AppRow.FormatMemory(bytes);
        }
    }
    private string status = "", search = "";
    private bool scenarioActive;
    private (ScenarioStage Stage, string Message)? loggedProgress;
    private bool? loggedLidState;
    private int? remainingSeconds;
    private bool CanCancelScenario => scenarioActive && coordinator.Stage == ScenarioStage.Countdown
        && runCancellation?.IsCancellationRequested == false;
    public string ScenarioButtonText => !scenarioActive ? "Перейти в гибернацию"
        : runCancellation?.IsCancellationRequested == true ? "Отмена…"
        : coordinator.Stage switch
        {
            ScenarioStage.Countdown => "Отменить гибернацию",
            ScenarioStage.Closing => "Закрытие приложений…",
            ScenarioStage.Waiting => "Ожидание завершения…",
            ScenarioStage.ForceTerminating => "Завершение приложений…",
            ScenarioStage.Restoring => "Открытие приложений…",
            _ => "Переход в гибернацию…"
        };
    public string ScenarioCounter => CanCancelScenario && remainingSeconds is int seconds ? $"{seconds} с" : "";
    public bool ScenarioActive => scenarioActive;
    private void NotifyScenario()
    {
        Notify(nameof(ScenarioActive)); Notify(nameof(ScenarioButtonText)); Notify(nameof(ScenarioCounter));
        CommandManager.InvalidateRequerySuggested();
    }
    private bool hasError;
    public bool HasError { get => hasError; private set { hasError = value; Notify(); } }
    private int sourceFilter;
    public string Status { get => status; private set => SetStatus(value); }
    private void SetStatus(string value, bool log = true)
    {
        if (status == value) return;
        status = value; HasError = false; Notify(nameof(Status));
        if (log && !string.IsNullOrWhiteSpace(value)) storage.Log(value);
    }
    public string Search { get => search; set { search = value; Notify(); activeApplicationsView.Refresh(); deletedApplicationsView.Refresh(); } }
    public int SourceFilter
    {
        get => sourceFilter;
        set
        {
            if (sourceFilter == value) return;
            sourceFilter = value; SelectedRow = null;
            Notify(); Notify(nameof(SelectedRow)); Notify(nameof(IsDeletedView)); Notify(nameof(ApplicationsView));
            Notify(nameof(ActiveColumnsVisibility)); Notify(nameof(DeletedColumnsVisibility));
            Notify(nameof(EmptyListTitle)); Notify(nameof(EmptyListHint));
            activeApplicationsView.Refresh(); deletedApplicationsView.Refresh();
        }
    }
    public int LidDelaySeconds
    {
        get => settings.LidDelaySeconds;
        set
        {
            if (value is < 0 or > 3600) throw new ArgumentOutOfRangeException(nameof(value), "Допустимо от 0 до 3600 секунд.");
            if (settings.LidDelaySeconds == value) return;
            settings.LidDelaySeconds = value; Notify(); ScheduleSave();
            storage.Log($"Пауза перед закрытием программ: {value} с.");
        }
    }
    public bool AutoStartEnabled
    {
        get => settings.AutoStart;
        set
        {
            try
            {
                if (settings.AutoStart == value) return;
                setAutoStart(value); settings.AutoStart = value; Notify(); Save();
                storage.Log($"Автозапуск: {(value ? "включён" : "выключен")}.");
            }
            catch (Exception ex) { Report(ex); Notify(); }
        }
    }
    public bool WatchdogEnabled
    {
        get => settings.WatchdogEnabled;
        set { if (settings.WatchdogEnabled != value) _ = SetWatchdogAsync(value); }
    }
    public MainViewModel(Storage storage, AppSettings settings, ShutdownCoordinator? scenario = null,
        Func<AppRow, bool>? confirmRemoval = null, Func<AppRow, bool>? confirmRestoration = null, Func<AppRow?, string?>? chooseExecutable = null,
        WatchdogRestartContext? watchdogRestart = null, LidMonitor? powerMonitor = null, IPowerApi? powerApi = null,
        Action<bool>? setAutoStart = null, IWatchdog? watchdogService = null, IHibernationService? hibernationService = null)
    {
        settings.NormalizePaths();
        this.storage = storage; this.settings = settings;
        this.setAutoStart = setAutoStart ?? AutoStart.Set;
        this.watchdogRestart = watchdogRestart; watchdog = watchdogService ?? new Watchdog(watchdogRestart);
        this.confirmRemoval = confirmRemoval ?? ApplicationListDialog.ConfirmRemoval;
        this.confirmRestoration = confirmRestoration ?? ApplicationListDialog.ConfirmRestoration;
        this.chooseExecutable = chooseExecutable ?? ChooseExecutable;
        dispatcher = Dispatcher.CurrentDispatcher;
        power = new(storage, powerApi ?? new WindowsPowerApi());
        lid = powerMonitor ?? new(storage: storage);
        hibernation = hibernationService ?? new HibernationService(lid);
        coordinator = scenario ?? new(new ProcessController(inventory, storage), hibernation, new ScenarioClock(), new ApplicationRestarter(inventory, storage));
        coordinator.Progress += progress =>
        {
            // Record the transition before hibernation can suspend the UI dispatcher.
            var key = (progress.Stage, progress.Message);
            if (loggedProgress != key)
            {
                storage.Log($"Сценарий [{progress.Stage}]: {progress.Message}", progress.Stage == ScenarioStage.Failed);
                loggedProgress = key;
            }
            dispatcher.InvokeAsync(() =>
            {
                SetStatus(progress.Message, false); Notify(nameof(IsRunning)); CommandManager.InvalidateRequerySuggested();
                HasError = progress.Stage == ScenarioStage.Failed;
                remainingSeconds = progress.RemainingSeconds;
                NotifyScenario();
            });
        };
        lid.LidChanged += OnLidChanged;
        lid.StateChanged += () =>
        {
            Notify(nameof(LidStatus));
            if (loggedLidState == lid.IsOpen) return;
            loggedLidState = lid.IsOpen;
            storage.Log(LidStatus);
        };
        lid.SchemeChanged += () => { storage.Log("Windows сообщила о смене схемы питания."); _ = CheckPowerAsync(); };
        lid.Resumed += () => { storage.Log("Возобновление работы Windows после сна или гибернации."); Notify(nameof(LidStatus)); _ = CheckPowerAsync(); };
        lid.SessionEnding += OnSessionEnding;
        activeApplicationsView = CollectionViewSource.GetDefaultView(Applications);
        activeApplicationsView.Filter = Filter;
        deletedApplicationsView = CollectionViewSource.GetDefaultView(DeletedApplications);
        deletedApplicationsView.Filter = MatchesSearch;
        activeListOrder = new((ListCollectionView)activeApplicationsView);
        deletedListOrder = new((ListCollectionView)deletedApplicationsView);
        foreach (var rule in settings.Applications) AddRow(new(rule, rule.ManuallyAdded ? AppSource.Manual : AppSource.Installed, true));
        foreach (var rule in settings.DeletedApplications) AddRow(new(rule, rule.ManuallyAdded ? AppSource.Manual : AppSource.Installed, true) { IsDeleted = true });
        ToggleEnabledCommand = new AsyncCommand(() => SetEnabledAsync(!Enabled), () => LidAvailable && !PowerRestorePending && !changingEnabled && !IsRunning);
        HibernateCommand = new AsyncCommand(() => StartScenarioAsync(false), () => !IsRunning && !changingEnabled);
        ScenarioCommand = new RelayCommand(() =>
        {
            if (scenarioActive) { runCancellation?.Cancel(); NotifyScenario(); }
            else _ = StartScenarioAsync(false);
        }, () => !disposed && !exiting && !changingEnabled && (!scenarioActive || CanCancelScenario));
        CancelCommand = new RelayCommand(() => { runCancellation?.Cancel(); NotifyScenario(); }, () => CanCancelScenario);
        RefreshCommand = new AsyncCommand(RefreshCatalogAsync, () => !IsRunning);
        AddCommand = new RelayCommand(() => PickExe(null), () => !IsRunning);
        ExportCommand = new RelayCommand(ExportProfile, () => !IsRunning);
        ImportCommand = new RelayCommand(ImportProfile, () => !IsRunning && !changingEnabled);
        LinkCommand = new RowCommand(PickExe, row => !row.CanSelect && !row.IsDeleted && !IsRunning);
        RemoveCommand = new RowCommand(RemoveApplication, row => !IsRunning && Applications.Contains(row));
        RestoreCommand = new RowCommand(RestoreApplication, row => !IsRunning && DeletedApplications.Contains(row));
        ToggleListPinCommand = new RelayCommand(() => IsListPinned = !IsListPinned, () => !IsRunning && !disposed && !exiting);
        saveTimer = new(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => { saveTimer!.Stop(); Save(); }, dispatcher);
        saveTimer.Stop();
        monitorTimer = new(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) =>
        {
            if (windowVisible && !disposed && !exiting) await RefreshRunningAsync();
        }, dispatcher);
        monitorTimer.Stop();
        // Notifications handle power scheme changes and resume immediately. This slower
        // check catches settings changes for which Windows supplies no notification.
        powerTimer = new(TimeSpan.FromSeconds(10), DispatcherPriority.Background, async (_, _) => await CheckPowerAsync(), dispatcher);
        powerTimer.Stop();
    }
    private void UpdateMonitoring()
    {
        if (initialized && windowVisible && !disposed && !exiting) monitorTimer.Start();
        else monitorTimer.Stop();
        if (initialized && (Enabled || PowerRestorePending || settings.WatchdogEnabled) && !disposed && !exiting) powerTimer.Start();
        else powerTimer.Stop();
    }
    public bool InitializationSucceeded { get; private set; }
    public async Task InitializeAsync()
    {
        // Recovery always precedes applying the saved preference.
        var desired = settings.LidEnabled && LidAvailable;
        var succeeded = true;
        Exception? autoStartFailure = null;
        try
        {
            if (watchdogRestart != null || settings.WatchdogEnabled) await watchdog.StartAsync(storage);
            await Task.Run(power.Restore);
            try { setAutoStart(settings.AutoStart); }
            catch (Exception ex)
            {
                autoStartFailure = new InvalidOperationException("Не удалось изменить автозапуск LiteHibernate: " + ex.Message, ex);
            }
            if (!LidAvailable && settings.LidEnabled)
            {
                settings.LidEnabled = false; Save();
                storage.Log("Автоматизация крышки недоступна. Ручная гибернация остаётся доступной.");
            }
            if (desired) await SetEnabledAsync(true);
        }
        catch (Exception ex)
        {
            succeeded = false; settings.LidEnabled = false;
            if (!watchdog.IsRunning) { settings.WatchdogEnabled = false; Notify(nameof(WatchdogEnabled)); }
            Save(); Report(ex);
        }
        await RefreshCatalogAsync();
        InitializationSucceeded = succeeded && !HasError && (!desired || Enabled);
        // Autostart is optional. Report its failure after discovery, which clears status,
        // without treating it as failed startup or changing the lid preference.
        if (autoStartFailure != null)
        {
            if (InitializationSucceeded) Report(autoStartFailure);
            else storage.Log(autoStartFailure.ToString(), true);
        }
        initialized = true;
        UpdateMonitoring(); NotifyPowerState();
    }
    private bool Filter(object value)
    {
        var row = (AppRow)value;
        return MatchesSearch(row) && (SourceFilter is not (1 or 2 or 3)
            || row.Source.HasFlag(SourceFilter switch { 1 => AppSource.Installed, 2 => AppSource.Running, _ => AppSource.Manual }));
    }
    private bool MatchesSearch(object value)
    {
        var row = (AppRow)value;
        return string.IsNullOrWhiteSpace(Search) || row.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || row.Rule.ExecutablePath.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }
    private void AddRow(AppRow row)
    {
        row.ConfigurationChanged -= ScheduleSave;
        row.ConfigurationChanged += ScheduleSave;
        row.PropertyChanged -= OnRowChanged;
        row.PropertyChanged += OnRowChanged;
        (row.IsDeleted ? DeletedApplications : Applications).Add(row);
        Notify(nameof(SelectedCount)); Notify(nameof(EstimatedWriteSavings));
    }
    private void OnRowChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (sender is AppRow { IsDeleted: false } && change.PropertyName == nameof(AppRow.RunningProcesses)) Notify(nameof(EstimatedWriteSavings));
    }
    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool MatchesEntry(AppRow row, CatalogEntry entry) => entry.ExecutablePath != null
        ? SamePath(row.Rule.ExecutablePath, entry.ExecutablePath)
        : !row.CanSelect && string.Equals(row.Name, entry.Name, StringComparison.OrdinalIgnoreCase);
    private void Merge(CatalogEntry entry)
    {
        var deleted = DeletedApplications.FirstOrDefault(row => MatchesEntry(row, entry)
            || !row.CanSelect && string.Equals(row.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        if (deleted != null) { deleted.Source |= entry.Source; return; }
        var existing = Applications.FirstOrDefault(row => MatchesEntry(row, entry));
        if (existing != null) { existing.Source |= entry.Source; return; }
        AddRow(new(new AppRule { Name = entry.Name, ExecutablePath = entry.ExecutablePath ?? "" }, entry.Source));
    }
    public async Task RefreshCatalogAsync()
    {
        try
        {
            Status = "Поиск установленных приложений…";
            var completion = new TaskCompletionSource<IReadOnlyList<CatalogEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.SetResult(new AppCatalog().Installed()); }
                catch (Exception ex) { completion.SetException(ex); }
            }) { IsBackground = true, Name = "LiteHibernate catalog" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            foreach (var entry in await completion.Task) Merge(entry);
            ApplicationsView.Refresh();
            await RefreshRunningAsync();
            if (!IsRunning) Status = "";
            storage.Log("Список установленных приложений обновлён.");
        }
        catch (Exception ex) { Report(ex); }
    }
    public async Task RefreshRunningAsync()
    {
        if (refreshing || disposed) return;
        refreshing = true;
        try
        {
            var processes = await Task.Run(inventory.Capture);
            if (disposed) return;
            var previousSources = Applications.ToDictionary(row => row, row => row.Source);
            foreach (var process in processes.DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
                Merge(new(Path.GetFileNameWithoutExtension(process.Path), process.Path, AppSource.Running));
            foreach (var row in Applications.Concat(DeletedApplications)) row.UpdateMetrics(processes);
            // RAM/status bindings update in place. Rebuild the view only if a source filter's membership changed.
            if (SourceFilter is 1 or 2 or 3 && previousSources.Any(pair => pair.Key.Source != pair.Value)) activeApplicationsView.Refresh();
            Notify(nameof(LidStatus));
            Notify(nameof(RunningCount)); Notify(nameof(SelectedCount));
        }
        catch (Exception ex) { Report(ex); }
        finally { refreshing = false; }
    }
    private void PickExe(AppRow? link)
    {
        try
        {
            var path = chooseExecutable(link);
            if (path == null || IsRunning || disposed || exiting) return;
            AddExecutable(path, link);
        }
        catch (Exception ex) { Report(ex); }
    }
    private static string? ChooseExecutable(AppRow? link)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Приложения (*.exe)|*.exe", CheckFileExists = true,
            Title = link?.IsDeleted == true ? "Выберите EXE, чтобы показать приложение" : "Выберите исполняемый файл приложения"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private void AddExecutable(string executable, AppRow? link)
    {
        if (!Path.IsPathFullyQualified(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выберите полный путь к файлу .exe.");
        var path = Path.GetFullPath(executable);
        if (!ProcessInventory.IsSelectablePath(path)) throw new InvalidOperationException("Это приложение защищено от завершения.");
        if (!File.Exists(path)) throw new FileNotFoundException("Файл приложения не найден. Выберите существующий EXE.", path);
        var existing = Applications.FirstOrDefault(row => SamePath(row.Rule.ExecutablePath, path));
        var deleted = DeletedApplications.Where(row => row == link || SamePath(row.Rule.ExecutablePath, path)).ToArray();
        var original = link ?? deleted.FirstOrDefault();
        var manuallyAdded = original?.IsDeleted != true || !SamePath(original.Rule.ExecutablePath, path);
        if (existing != null)
        {
            if (manuallyAdded) { existing.Source |= AppSource.Manual; existing.Rule.ManuallyAdded = true; }
            existing.Selected = true;
        }
        else
        {
            var rule = (original?.Rule ?? new AppRule { Name = Path.GetFileNameWithoutExtension(path) }) with
            {
                ExecutablePath = path, ManuallyAdded = manuallyAdded || original?.Rule.ManuallyAdded == true
            };
            if (link != null && !link.IsDeleted) DetachRow(link, Applications);
            AddRow(new(rule, (manuallyAdded ? AppSource.Manual : 0) | (original?.Source ?? 0), true));
        }
        if (existing != null && link != null && link != existing && !link.IsDeleted) DetachRow(link, Applications);
        foreach (var row in deleted) DetachRow(row, DeletedApplications);
        if (deleted.Length > 0) storage.Log($"Приложение показано в списке: {original!.Name} ({path}).");
        Save(); Notify(nameof(RunningCount)); Notify(nameof(SelectedCount)); Notify(nameof(EstimatedWriteSavings));
        _ = RefreshRunningAsync();
    }
    private void DetachRow(AppRow row, ObservableCollection<AppRow> collection)
    {
        row.ConfigurationChanged -= ScheduleSave; row.PropertyChanged -= OnRowChanged;
        collection.Remove(row);
        if (SelectedRow == row) { SelectedRow = null; Notify(nameof(SelectedRow)); }
    }
    private void ExportProfile()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Профиль LiteHibernate (*.json)|*.json", FileName = "LiteHibernate-profile.json",
                DefaultExt = ".json", AddExtension = true, Title = "Экспортировать приложения и правила"
            };
            if (dialog.ShowDialog() != true) return;
            new ScenarioProfile
            {
                LidDelaySeconds = settings.LidDelaySeconds,
                Applications = Applications.Where(row => row.CanSelect && (row.Configured || row.Selected || row.Rule.ManuallyAdded))
                    .Select(row => row.Rule with { }).ToList()
            }.Export(storage, dialog.FileName);
            Status = "Профиль экспортирован.";
        }
        catch (Exception ex) { Report(ex); }
    }
    private void ImportProfile()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Профиль LiteHibernate (*.json)|*.json", CheckFileExists = true,
                Title = "Импортировать приложения и правила"
            };
            if (dialog.ShowDialog() != true) return;
            var profile = ScenarioProfile.Import(storage, dialog.FileName);
            ApplyProfile(profile);
            Status = "Профиль импортирован.";
            _ = RefreshRunningAsync();
        }
        catch (Exception ex) { Report(ex); }
    }
    private void ApplyProfile(ScenarioProfile profile)
    {
        // Validate before changing either list. Explicitly imported rules supersede
        // matching deleted records, including an earlier path for the same rule ID.
        profile.Validate();
        saveTimer.Stop();
        foreach (var row in Applications) { row.ConfigurationChanged -= ScheduleSave; row.PropertyChanged -= OnRowChanged; }
        Applications.Clear(); SelectedRow = null; Notify(nameof(SelectedRow));
        settings.LidDelaySeconds = profile.LidDelaySeconds;
        foreach (var rule in profile.Applications)
        {
            foreach (var deleted in DeletedApplications.Where(deleted => deleted.Rule.Id == rule.Id || SamePath(deleted.Rule.ExecutablePath, rule.ExecutablePath)).ToArray())
                DetachRow(deleted, DeletedApplications);
            AddRow(new(rule, rule.ManuallyAdded ? AppSource.Manual : AppSource.Installed, true));
        }
        Notify(nameof(LidDelaySeconds)); Notify(nameof(SelectedCount)); Notify(nameof(RunningCount)); Notify(nameof(EstimatedWriteSavings));
        activeApplicationsView.Refresh(); deletedApplicationsView.Refresh(); Save();
    }
    private void RemoveApplication(AppRow row)
    {
        if (!confirmRemoval(row) || IsRunning || disposed || exiting || !Applications.Contains(row)) return;
        Applications.Remove(row);
        row.IsDeleted = true;
        DeletedApplications.Add(row);
        if (SelectedRow == row) { SelectedRow = null; Notify(nameof(SelectedRow)); }
        Save();
        storage.Log($"Приложение скрыто из списка: {row.Name} ({row.Rule.ExecutablePath}).");
        Notify(nameof(SelectedCount)); Notify(nameof(RunningCount)); Notify(nameof(EstimatedWriteSavings));
    }
    private void RestoreApplication(AppRow row)
    {
        try
        {
            if (!confirmRestoration(row) || IsRunning || disposed || exiting || !DeletedApplications.Contains(row)) return;
            if (!File.Exists(row.Rule.ExecutablePath)) PickExe(row);
            else AddExecutable(row.Rule.ExecutablePath, row);
        }
        catch (Exception ex) { Report(ex); }
    }
    public async Task SetEnabledAsync(bool value)
    {
        if (changingEnabled || disposed || exiting) return;
        if (value && PowerRestorePending) { NotifyPowerState(); return; }
        if (value && !LidAvailable) { storage.Log(ToggleLabel); Notify(nameof(Enabled)); return; }
        changingEnabled = true; CommandManager.InvalidateRequerySuggested();
        await lifecycle.WaitAsync();
        try
        {
            if (exiting) return;
            if (value)
            {
                hibernation.EnsureAvailable();
                if (settings.WatchdogEnabled) await watchdog.StartAsync(storage);
                if (exiting) return;
                var shutdownToken = shutdownCancellation.Token;
                try { await Task.Run(() => { if (!exiting) power.Enable(shutdownToken); }); }
                catch
                {
                    await Task.Run(power.Restore);
                    throw;
                }
                if (exiting) return;
                Status = "Обработка крышки включена. Windows: Do nothing (сеть и батарея).";
            }
            else
            {
                runCancellation?.Cancel();
                if (runningTask != null) await runningTask;
                await Task.Run(power.Restore);
                Status = "Обработка крышки выключена. Исходные настройки восстановлены.";
            }
            settings.LidEnabled = value; Save();
        }
        catch (OperationCanceledException) when (exiting || disposed)
        {
            storage.Log("Включение отслеживания крышки отменено при завершении приложения.");
        }
        catch (Exception ex) { settings.LidEnabled = power.Enabled; Save(); Report(ex); }
        finally { lifecycle.Release(); changingEnabled = false; UpdateMonitoring(); NotifyPowerState(); }
    }
    private async Task SetWatchdogAsync(bool value)
    {
        if (settings.WatchdogEnabled == value) return;
        if (changingEnabled || IsRunning || disposed || exiting) { Notify(nameof(WatchdogEnabled)); return; }
        changingEnabled = true; CommandManager.InvalidateRequerySuggested();
        await lifecycle.WaitAsync();
        try
        {
            if (exiting) return;
            if (value) await watchdog.StartAsync(storage);
            if (!value) watchdog.Disarm();
            settings.WatchdogEnabled = value; Save();
            storage.Log($"Фоновый сторож: {(value ? "включён" : "выключен")}.");
        }
        catch (Exception ex) { Report(ex); }
        finally { lifecycle.Release(); changingEnabled = false; UpdateMonitoring(); Notify(nameof(WatchdogEnabled)); CommandManager.InvalidateRequerySuggested(); }
    }
    private async Task CheckPowerAsync()
    {
        if (!initialized || !(Enabled || PowerRestorePending || settings.WatchdogEnabled) || checkingPower || changingEnabled || disposed || exiting) return;
        checkingPower = true;
        await lifecycle.WaitAsync();
        try
        {
            if (exiting) return;
            if (settings.WatchdogEnabled && !watchdog.IsRunning) throw new IOException("Фоновый сторож неожиданно остановился.");
            if (PowerRestorePending)
            {
                try
                {
                    await Task.Run(power.Restore);
                    Status = "Исходные настройки крышки восстановлены.";
                }
                catch (Exception ex)
                {
                    // The first failure was reported. Retry without repeating tray notifications.
                    storage.Log("Повторная попытка восстановления настроек крышки: " + ex, true);
                    HasError = true;
                    return;
                }
            }
            await Task.Run(power.EnsureActiveScheme);
        }
        catch (Exception ex)
        {
            runCancellation?.Cancel();
            if (runningTask != null) await runningTask;
            try { await Task.Run(power.Restore); }
            catch (Exception restore) { storage.Log(restore.ToString(), true); }
            if (settings.WatchdogEnabled && !watchdog.IsRunning) { settings.WatchdogEnabled = false; Notify(nameof(WatchdogEnabled)); }
            settings.LidEnabled = false; Save(); Report(ex);
        }
        finally { lifecycle.Release(); checkingPower = false; UpdateMonitoring(); NotifyPowerState(); }
    }
    private void OnLidChanged(bool open)
    {
        Notify(nameof(LidStatus));
        if (open) lidDelayCancellation?.Cancel();
        else if (Enabled && !IsRunning && !changingEnabled) _ = StartScenarioAsync(true);
    }
    public Task StartScenarioAsync(bool fromLid)
    {
        if (fromLid && !LidAvailable || IsRunning || runningTask is { IsCompleted: false } || changingEnabled || disposed || exiting) return Task.CompletedTask;
        runningTask = RunScenarioAsync(fromLid);
        return runningTask;
    }
    private async Task RunScenarioAsync(bool fromLid)
    {
        loggedProgress = null;
        runCancellation = new(); lidDelayCancellation = new();
        scenarioActive = true; remainingSeconds = null; NotifyScenario();
        try
        {
            Save();
            var rules = Applications.Where(row => row.Selected && row.CanSelect).Select(row => row.Rule with { }).ToArray();
            storage.Log($"Запуск сценария: {(fromLid ? "закрытие крышки" : "ручной запуск")}; пауза {settings.LidDelaySeconds} с; выбрано приложений: {rules.Length}.");
            foreach (var rule in rules) storage.Log($"Правило: {rule.Name}; EXE: {rule.ExecutablePath}; ожидание: {rule.TimeoutSeconds} с; после таймаута: {rule.Policy}; открыть после гибернации: {rule.ReopenAfterHibernate}.");
            using var awake = new KeepAwake();
            var result = await coordinator.RunAsync(rules, settings.LidDelaySeconds,
                fromLid ? lidDelayCancellation.Token : CancellationToken.None, runCancellation.Token);
            SetStatus(result.Message, false);
            HasError = result.Stage == ScenarioStage.Failed;
            if (result.Error != null) storage.Log("Ошибка сценария: " + result.Error, true);
            if (result.Stage is ScenarioStage.Failed or ScenarioStage.Cancelled) Notification?.Invoke(result.Message, result.Stage == ScenarioStage.Failed);
            if (result.ReopenErrors.Count > 0)
            {
                HasError = true;
                Notification?.Invoke(string.Join(Environment.NewLine, result.ReopenErrors.Select(error => error.Message)), true);
            }
        }
        catch (Exception ex) { Report(ex); }
        finally
        {
            runCancellation.Dispose(); lidDelayCancellation.Dispose(); runCancellation = lidDelayCancellation = null;
            scenarioActive = false; remainingSeconds = null; NotifyScenario();
            Notify(nameof(IsRunning)); CommandManager.InvalidateRequerySuggested(); await RefreshRunningAsync();
        }
    }
    private void ScheduleSave() { Notify(nameof(SelectedCount)); Notify(nameof(EstimatedWriteSavings)); if (!disposed) { saveTimer.Stop(); saveTimer.Start(); } }
    public void Save()
    {
        try
        {
            settings.Applications = Applications.Where(row => row.CanSelect && (row.Configured || row.Selected || row.Rule.ManuallyAdded)).Select(row => row.Rule).ToList();
            settings.DeletedApplications = DeletedApplications.Select(row => row.Rule).ToList();
            storage.SaveSettings(settings);
        }
        catch (Exception ex) { Report(ex); }
    }
    private void OnSessionEnding()
        => RestoreOnShutdown();
    public void RestoreOnShutdown(bool intentional = true)
    {
        if (intentional) watchdog.PrepareForExit();
        storage.Log("Завершение сеанса Windows: восстановление настроек крышки.");
        exiting = true; monitorTimer.Stop(); powerTimer.Stop(); saveTimer.Stop();
        shutdownCancellation.Cancel();
        runCancellation?.Cancel();
        Save();
        try { power.Restore(); }
        catch (Exception ex) { storage.Log("Завершение сеанса: " + ex, true); }
        finally { NotifyPowerState(); }
    }
    public async Task ExitAsync()
    {
        watchdog.PrepareForExit();
        storage.Log("Выход из приложения: восстановление настроек крышки.");
        exiting = true;
        monitorTimer.Stop(); powerTimer.Stop(); saveTimer.Stop(); runCancellation?.Cancel();
        if (runningTask != null) await runningTask;
        await lifecycle.WaitAsync();
        try
        {
            Save();
            try { await Task.Run(power.Restore); watchdog.Disarm(); storage.Log("Настройки крышки восстановлены. Приложение завершает работу."); }
            catch (Exception ex)
            {
                Report(ex);
                // Leave the watchdog armed; it will retry when this process exits.
                if (!watchdog.IsRunning)
                {
                    exiting = false; UpdateMonitoring(); throw;
                }
            }
        }
        finally { lifecycle.Release(); NotifyPowerState(); }
    }
    private void NotifyPowerState()
    {
        Notify(nameof(Enabled)); Notify(nameof(PowerRestorePending)); Notify(nameof(ToggleLabel)); Notify(nameof(LidAutomationDescription));
        CommandManager.InvalidateRequerySuggested();
    }
    private void Report(Exception ex) { SetStatus(ex.Message, false); HasError = true; storage.Log(ex.ToString(), true); Notification?.Invoke(ex.Message, true); }
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; shutdownCancellation.Cancel(); shutdownCancellation.Dispose();
        monitorTimer.Stop(); powerTimer.Stop(); saveTimer.Stop(); lid.Dispose(); watchdog.Dispose();
    }
}
