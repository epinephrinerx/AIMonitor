using System.Windows.Input;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Tray;

namespace AIMonitor.Presentation.Wpf.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly LatestRefreshCoordinator _refreshCoordinator;
    private readonly SettingsSession _settingsSession;
    // F23: SettingsSession.Changed may fire on a thread-pool thread; sync from this context.
    private readonly SynchronizationContext? _uiContext;
    private readonly CachedAgentUsageSource? _agentUsageSource;
    private readonly AgentUsageRefresher? _agentUsageRefresher;
    private ITrayReadingsSink? _traySink;
    private IReadOnlyList<TrayReading>? _latestTrayReadings;
    private readonly Dictionary<string, IReadOnlyList<MeterDisplayItem>> _lastGoodMeters = new(StringComparer.Ordinal);

    private bool _isRefreshing;
    private bool _pendingRefresh;
    private string _lastRefreshStatus = "Ready";
    private ProviderTabViewModel _selectedTab;
    private object? _selectedTabItem;
    private bool _isAllAgentsSelected;
    private DashboardOption<string> _selectedMetric;
    private DashboardOption<int> _selectedRange;
    private DashboardOption<int> _selectedInterval;
    private bool _syncingFromSettings;
    private DateTimeOffset? _lastSuccess;
    private bool _showComposedStatus;
    private Task? _agentUsageTask;
    private AgentUsageReport? _lastAgentUsageReport;
    private AgentUsageUnavailableException? _lastAgentUsageError;
    // R-018 A3: the latest raw AGY quota snapshot, kept so ccusage updates and chart range/metric
    // changes can recompose the AGY tab without starting an agy refresh or a ccusage read.
    private Domain.ProviderSnapshot? _latestRawAgySnapshot;
    private IReadOnlyDictionary<string, decimal> _agentBudgets =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    private string _lastProviderTabId;

    public MainWindowViewModel(
        LatestRefreshCoordinator refreshCoordinator,
        SettingsSession settingsSession,
        ITrayReadingsSink? trayHost = null,
        CachedAgentUsageSource? agentUsage = null)
    {
        _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
        _settingsSession = settingsSession ?? throw new ArgumentNullException(nameof(settingsSession));
        _uiContext = SynchronizationContext.Current;
        _traySink = trayHost;
        _agentUsageSource = agentUsage;

        ClaudeTab = new ProviderTabViewModel("claude", "Claude");
        OpenAiTab = new ProviderTabViewModel("openai", "OpenAI / Codex");
        AgyTab = new ProviderTabViewModel("agy", "Gemini / AGY");
        OpenCodeTab = new ProviderTabViewModel(OpenCodeSnapshotBuilder.ProviderId, "OpenCode");
        OverviewTab = new ProviderTabViewModel(OverviewSnapshotBuilder.ProviderId, "Overview");
        AllAgentsTab = new AllAgentsTabViewModel();

        ProviderTabs = [ClaudeTab, OpenAiTab, AgyTab];
        Tabs = [ClaudeTab, OpenAiTab, AgyTab, OpenCodeTab, OverviewTab, AllAgentsTab];

        var settings = _settingsSession.Current;
        _agentBudgets = settings.AgentBudgets ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (settings.ActiveProvider == "all-agents")
        {
            _isAllAgentsSelected = true;
            _selectedTab = ClaudeTab;
            _selectedTabItem = AllAgentsTab;
        }
        else
        {
            // E4: restore any provider tab in Tabs (including OpenCode); ProviderTabs stays tray-only.
            _selectedTab = Tabs.OfType<ProviderTabViewModel>()
                .FirstOrDefault(t => string.Equals(t.ProviderId, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase))
                ?? ClaudeTab;
            _selectedTabItem = _selectedTab;
        }

        // E28: the widget starts on the startup provider tab when the selected tab is not one of them.
        _lastProviderTabId = ProviderTabs.Contains(_selectedTab) ? _selectedTab.ProviderId : ClaudeTab.ProviderId;

        _selectedMetric = DashboardOptions.Find(DashboardOptions.Metrics, settings.ChartMetric, 0);
        _selectedRange = DashboardOptions.Find(DashboardOptions.Ranges, settings.ChartRangeDays, 1);
        _selectedInterval = DashboardOptions.Find(DashboardOptions.Intervals, settings.RefreshIntervalSeconds, 2);

        if (_agentUsageSource is not null)
        {
            _agentUsageRefresher = new AgentUsageRefresher(_agentUsageSource, () => DateTimeOffset.UtcNow, TimeZoneInfo.Local);
            _agentUsageRefresher.Updated += OnAgentUsageUpdated;
        }

        _settingsSession.Changed += OnSettingsChanged;

        RefreshCommand = new RelayCommand(async () => await RefreshAsync(manual: true), () => !IsRefreshing);
        OpenSettingsCommand = new RelayCommand(() => RequestOpenSettings?.Invoke());
        SwitchToWidgetCommand = new RelayCommand(() => RequestSwitchToWidget?.Invoke());
        OpenLogCommand = new RelayCommand(() => RequestOpenLog?.Invoke());
        // F1 is the Readme in 1.3.3; the About menu names each page.
        OpenAboutCommand = new RelayCommand(() => RequestOpenAbout?.Invoke(AboutPage.Readme));
        CheckForUpdatesCommand = new RelayCommand(() => RequestCheckUpdates?.Invoke());
        ShowAboutCommand = new RelayCommand<string>(page =>
            RequestOpenAbout?.Invoke(Enum.TryParse<AboutPage>(page, out var parsed) ? parsed : AboutPage.Version));

        Connections = new ConnectionsViewModel(_settingsSession);
        Connections.ConnectRequested += id => RequestConnect?.Invoke(id);
        Connections.RedetectRequested += async id => await RefreshAsync(id);
        Connections.OpenDashboardRequested += () => IsConnectionsPageVisible = false;

        ExitCommand = new RelayCommand(() => RequestExit?.Invoke());
        SetThemeCommand = new RelayCommand<string>(theme =>
        {
            if (theme is "system" or "light" or "dark")
            {
                Theme.ThemeManager.Instance.ApplyTheme(theme);
                _ = SaveAsync(s => s with { Theme = theme });
            }
        });

        ShowConnectionsCommand = new RelayCommand(() => IsConnectionsPageVisible = true);
        ShowDashboardCommand = new RelayCommand(() => IsConnectionsPageVisible = false);

        _isConnectionsPageVisible = _settingsSession.Current.ShowConnectionsAtStartup;
    }

    public ProviderTabViewModel ClaudeTab { get; }
    public ProviderTabViewModel OpenAiTab { get; }
    public ProviderTabViewModel AgyTab { get; }
    public ProviderTabViewModel OpenCodeTab { get; }
    public ProviderTabViewModel OverviewTab { get; }
    public AllAgentsTabViewModel AllAgentsTab { get; }
    public IReadOnlyList<ProviderTabViewModel> ProviderTabs { get; }
    public IReadOnlyList<object> Tabs { get; }

    /// <summary>
    /// The provider tab the widget and tray speak for (E28): the selected tab while it is one of
    /// <see cref="ProviderTabs"/>, otherwise the last provider tab that was selected.
    /// </summary>
    public string ActiveProviderTabId =>
        ProviderTabs.Contains(_selectedTab) ? _selectedTab.ProviderId : _lastProviderTabId;

    public object? SelectedTabItem
    {
        get => _selectedTabItem;
        set
        {
            if (ReferenceEquals(_selectedTabItem, value))
            {
                return;
            }

            switch (value)
            {
                case ProviderTabViewModel provider:
                    _selectedTabItem = provider;
                    OnPropertyChanged();
                    IsAllAgentsSelected = false;
                    SelectedTab = provider;
                    break;
                case AllAgentsTabViewModel:
                    _selectedTabItem = AllAgentsTab;
                    OnPropertyChanged();
                    IsAllAgentsSelected = true;
                    _ = SaveAsync(s => s with { ActiveProvider = "all-agents" });
                    break;
                default:
                    _selectedTabItem = value;
                    OnPropertyChanged();
                    break;
            }
        }
    }

    public bool IsAllAgentsSelected
    {
        get => _isAllAgentsSelected;
        set => SetProperty(ref _isAllAgentsSelected, value);
    }

    internal Task? AgentUsageTask => _agentUsageTask;

    public IReadOnlyDictionary<string, Domain.ProviderSnapshot> LatestSnapshots => _latestSnapshots;
    private IReadOnlyDictionary<string, Domain.ProviderSnapshot> _latestSnapshots = new Dictionary<string, Domain.ProviderSnapshot>();

    public ProviderTabViewModel SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null)
            {
                return;
            }

            SetProperty(ref _selectedTab, value);

            if (ProviderTabs.Contains(value))
            {
                _lastProviderTabId = value.ProviderId;
            }

            OnPropertyChanged(nameof(ActiveProviderTabId));

            if (_selectedTabItem != value)
            {
                _selectedTabItem = value;
                OnPropertyChanged(nameof(SelectedTabItem));
            }

            if (_isAllAgentsSelected)
            {
                _isAllAgentsSelected = false;
                OnPropertyChanged(nameof(IsAllAgentsSelected));
            }

            if (!_syncingFromSettings)
            {
                _ = SaveAsync(s => s with { ActiveProvider = value.ProviderId });
            }
        }
    }

    public IReadOnlyList<DashboardOption<string>> MetricOptions => DashboardOptions.Metrics;
    public IReadOnlyList<DashboardOption<int>> RangeOptions => DashboardOptions.Ranges;
    public IReadOnlyList<DashboardOption<int>> IntervalOptions => DashboardOptions.Intervals;

    /// <summary>Chart metric; changing it saves the setting and refreshes straight away (1.3.3 <c>_on_view_changed</c>).</summary>
    public DashboardOption<string> SelectedMetric
    {
        get => _selectedMetric;
        set
        {
            if (value is not null && SetProperty(ref _selectedMetric, value))
            {
                RebuildUsageTabsForViewChange();
                if (!_syncingFromSettings)
                {
                    _ = ApplyViewChangeAsync(s => s with { ChartMetric = value.Value });
                }
            }
        }
    }

    /// <summary>History range in days; changing it saves the setting and refreshes straight away.</summary>
    public DashboardOption<int> SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (value is not null && SetProperty(ref _selectedRange, value))
            {
                RebuildUsageTabsForViewChange();
                if (!_syncingFromSettings)
                {
                    _ = ApplyViewChangeAsync(s => s with { ChartRangeDays = value.Value });
                }
            }
        }
    }

    /// <summary>Auto-refresh interval; the live settings applier re-arms the timer. Zero means manual only.</summary>
    public DashboardOption<int> SelectedInterval
    {
        get => _selectedInterval;
        set
        {
            if (value is not null && SetProperty(ref _selectedInterval, value) && !_syncingFromSettings)
            {
                _ = SaveAsync(s => s with { RefreshIntervalSeconds = value.Value });
            }
        }
    }

    /// <summary>
    /// Re-renders the footer ("Updated 12s ago (14:03:22) · auto-refresh …") while it is showing
    /// the normal status; a refresh in progress or a failure message is left alone.
    /// </summary>
    public void RefreshStatusText(DateTimeOffset now, double? workingSetMb)
    {
        if (!_showComposedStatus)
        {
            return;
        }

        _lastWorkingSetMb = workingSetMb ?? _lastWorkingSetMb;
        LastRefreshStatus = StatusBarText.Compose(
            _lastSuccess, now, _settingsSession.Current.RefreshIntervalSeconds, _lastWorkingSetMb, TimeZoneInfo.Local);
    }

    private double? _lastWorkingSetMb;

    private async Task ApplyViewChangeAsync(Func<AppSettings, AppSettings> change)
    {
        await SaveAsync(change).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task SaveAsync(Func<AppSettings, AppSettings> change)
    {
        try
        {
            await _settingsSession.UpdateAsync(change).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LastRefreshStatus = $"Could not save setting: {ex.Message}";
        }
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        // F23: SettingsSession.Changed can fire on a thread-pool thread (UpdateAsync continues after
        // ConfigureAwait(false)), so the _syncingFromSettings sync below — and the OpenCode rebuild
        // it may trigger — would run off the UI thread. Post (async, never Send) to the context
        // captured in the constructor; run inline when already on it or when none was captured.
        if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            _uiContext.Post(_ => OnSettingsChangedCore(settings), null);
            return;
        }

        OnSettingsChangedCore(settings);
    }

    /// <summary>
    /// The settings sync body, carried by <see cref="OnSettingsChanged"/>'s Post exactly once per
    /// raise: re-testing the context inside the posted callback would loop forever on hosts whose
    /// context does not set <see cref="SynchronizationContext.Current"/> during posted callbacks.
    /// </summary>
    private void OnSettingsChangedCore(AppSettings settings)
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        _syncingFromSettings = true;
        try
        {
            SelectedMetric = DashboardOptions.Find(DashboardOptions.Metrics, settings.ChartMetric, 0);
            SelectedRange = DashboardOptions.Find(DashboardOptions.Ranges, settings.ChartRangeDays, 1);
            SelectedInterval = DashboardOptions.Find(DashboardOptions.Intervals, settings.RefreshIntervalSeconds, 2);
        }
        finally
        {
            _syncingFromSettings = false;
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string LastRefreshStatus
    {
        get => _lastRefreshStatus;
        set => SetProperty(ref _lastRefreshStatus, value);
    }

    public ConnectionsViewModel Connections { get; }

    private bool _isConnectionsPageVisible;
    public bool IsConnectionsPageVisible
    {
        get => _isConnectionsPageVisible;
        set => SetProperty(ref _isConnectionsPageVisible, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand SwitchToWidgetCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenAboutCommand { get; }
    public ICommand ShowAboutCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand ShowConnectionsCommand { get; }
    public ICommand ShowDashboardCommand { get; }
    public ICommand ExitCommand { get; }

    /// <summary>Parameter is "system", "light" or "dark"; applies the theme immediately and saves it.</summary>
    public ICommand SetThemeCommand { get; }

    public bool IsThemeSystem => ThemePreference == "system";
    public bool IsThemeLight => ThemePreference == "light";
    public bool IsThemeDark => ThemePreference == "dark";

    private string ThemePreference => _settingsSession.Current.Theme;

    public event Action? RequestOpenSettings;
    public event Action? RequestSwitchToWidget;
    public event Action? RequestOpenLog;
    public event Action<AboutPage>? RequestOpenAbout;
    public event Action? RequestCheckUpdates;
    public event Action<string>? RequestConnect;
    public event Action? RequestExit;

    /// <summary>
    /// Attaches or detaches a tray readings sink. When a non-null sink is attached,
    /// any cached latest readings are immediately forwarded to it.
    /// When null is passed, the previous sink is detached and will receive no further updates.
    /// </summary>
    public void AttachTray(ITrayReadingsSink? traySink)
    {
        _traySink = traySink;
        if (_traySink is not null && _latestTrayReadings is not null)
        {
            _traySink.UpdateReadings(_latestTrayReadings);
        }
    }

    /// <summary>
    /// The window was just parked in the tray. The first time ever, say so with a balloon, so closing the window
    /// does not look like the app quit (1.3.3 <c>mark_once("trayHint")</c>).
    /// </summary>
    public void NotifyParkedInTray()
    {
        if (_settingsSession.Current.TrayHintShown || _traySink is not ITrayNotifier notifier)
        {
            return;
        }

        notifier.ShowBalloon(
            "Still watching",
            "AI Usage Monitor is in the notification area. Double-click its icon to reopen it, or use Exit to quit.");
        _ = _settingsSession.UpdateAsync(s => s with { TrayHintShown = true });
    }

    private long _requestIdCounter;

    private int ChartRangeDays => _selectedRange.Value;

    private void OnAgentUsageUpdated(AgentUsageReport? lastGood, AgentUsageUnavailableException? lastError)
    {
        // E2: keep the last pair so a live budget change can rebuild the OpenCode tab without
        // running ccusage again.
        _lastAgentUsageReport = lastGood;
        _lastAgentUsageError = lastError;

        // I8: capture now once and derive today in the local zone; the same pair is passed to
        // every ccusage-backed builder in this rebuild.
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime);
        AllAgentsTab.Update(lastGood, lastError, today, ChartRangeDays, TimeZoneInfo.Local);
        ClaudeTab.SetAgentUsage(AgentUsagePresenter.SummaryFor(lastGood, "claude", today));
        OpenAiTab.SetAgentUsage(AgentUsagePresenter.SummaryFor(lastGood, "codex", today));
        AgyTab.SetAgentUsage(AgentUsagePresenter.SummaryFor(lastGood, "antigravity", today));
        UpdateOpenCodeTab(today, now);
        UpdateOverviewTab(today, now);
        // R-018 A3/D13: recompose the AGY tab from the raw snapshot and the fresh report, after
        // OpenCode/Overview so an AGY failure cannot stop the other tabs.
        UpdateAgyTab(today, now);
    }

    /// <summary>
    /// Applies monthly budgets live (E2): stores them and rebuilds the OpenCode snapshot from the
    /// cached ccusage pair; it never starts a ccusage read. Call on the UI thread.
    /// </summary>
    public void ApplyAgentBudgets(IReadOnlyDictionary<string, decimal> budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);

        // E29: unrelated settings changes must not rebuild the OpenCode tab.
        if (BudgetsEqual(_agentBudgets, budgets))
        {
            return;
        }

        _agentBudgets = budgets;

        // I8: one now/today pair shared by both ccusage-backed builders.
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime);
        UpdateOpenCodeTab(today, now);
        UpdateOverviewTab(today, now);
    }

    /// <summary>Same keys (case-insensitive) and same values (E29).</summary>
    private static bool BudgetsEqual(
        IReadOnlyDictionary<string, decimal> left,
        IReadOnlyDictionary<string, decimal> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (agent, amount) in left)
        {
            if (right.TryGetValue(agent, out var other))
            {
                if (other != amount)
                {
                    return false;
                }

                continue;
            }

            var match = right.FirstOrDefault(
                pair => string.Equals(pair.Key.Trim(), agent.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match.Key is null || match.Value != amount)
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateOpenCodeTab(DateOnly today, DateTimeOffset now)
    {
        var snapshot = OpenCodeSnapshotBuilder.Build(
            _lastAgentUsageReport,
            _lastAgentUsageError,
            _agentBudgets,
            today,
            now,
            TimeZoneInfo.Local,
            ChartRangeDays,
            _selectedMetric.Value);
        if (snapshot is not null)
        {
            OpenCodeTab.UpdateFromSnapshot(snapshot, now);
        }

        OpenCodeTab.SetAgentUsage(AgentUsagePresenter.SummaryFor(_lastAgentUsageReport, "opencode", today));
    }

    /// <summary>
    /// Rebuilds the Overview tab from the cached ccusage pair (R-016 H1.2). Called alongside
    /// <see cref="UpdateOpenCodeTab"/> at every rebuild point; never starts a ccusage read.
    /// </summary>
    private void UpdateOverviewTab(DateOnly today, DateTimeOffset now)
    {
        var snapshot = OverviewSnapshotBuilder.Build(
            _lastAgentUsageReport,
            _lastAgentUsageError,
            _agentBudgets,
            today,
            now,
            TimeZoneInfo.Local);
        if (snapshot is not null)
        {
            OverviewTab.UpdateFromSnapshot(snapshot, now);
        }

        // I12: hand off the empty-state message from the builder.
        var emptyMessage = OverviewSnapshotBuilder.EmptyMessage(
            _lastAgentUsageReport,
            _lastAgentUsageError,
            today,
            TimeZoneInfo.Local);
        OverviewTab.UpdateOverviewEmptyMessage(emptyMessage);
    }

    /// <summary>
    /// Recomposes the AGY tab from the raw AGY snapshot and the cached ccusage report (R-018 A3).
    /// Never starts an agy refresh or a ccusage read; with no raw snapshot the tab is left as it
    /// is. Call on the UI thread.
    /// </summary>
    private void UpdateAgyTab(DateOnly today, DateTimeOffset now)
    {
        // D13: with AGY disabled in the current settings, never compose for it; the tab keeps its
        // last state. A missing "agy" entry counts as enabled (same rule as the refresh path).
        if (_settingsSession.Current.Providers.GetValueOrDefault("agy") is { Enabled: false })
        {
            return;
        }

        if (_latestRawAgySnapshot is not { } rawAgy)
        {
            return;
        }

        AgyTab.UpdateFromSnapshot(
            AgyUsageComposer.Compose(rawAgy, _lastAgentUsageReport, today, ChartRangeDays, _selectedMetric.Value),
            now);
    }

    /// <summary>
    /// Rebuilds the OpenCode tab and recomposes the AGY tab from the cached ccusage pair when the
    /// chart range or metric changes (F1, R-018 D1). It never starts a ccusage read. The range/
    /// metric setters call this synchronously so the charts are already correct before the
    /// follow-up settings save/refresh runs.
    /// </summary>
    private void RebuildUsageTabsForViewChange()
    {
        // I8: one now/today pair shared by every ccusage-backed builder.
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime);
        UpdateOpenCodeTab(today, now);
        UpdateAgyTab(today, now);
        // I15: do not rebuild Overview on chart range/metric changes.
    }

    /// <param name="onlyProviderId">Refresh just this service ("Re-detect" on its card); null refreshes all.</param>
    public async Task RefreshAsync(string? onlyProviderId = null, bool manual = false)
    {
        if (IsRefreshing)
        {
            _pendingRefresh = true;
            return;
        }

        IsRefreshing = true;
        _showComposedStatus = false;
        LastRefreshStatus = "Refreshing providers...";

        try
        {
            var settings = _settingsSession.Current;
            var disabledProviders = new HashSet<string>(
                settings.Providers.Where(p => !p.Value.Enabled).Select(p => p.Key),
                StringComparer.OrdinalIgnoreCase);
            var req = new ProviderSnapshotRequest(
                historyDays: settings.ChartRangeDays,
                metric: settings.ChartMetric,
                includeHistory: true,
                disabledProviders: disabledProviders,
                onlyProviderId: onlyProviderId);
            var requestId = Interlocked.Increment(ref _requestIdCounter);
            var result = await _refreshCoordinator.QueueAsync(requestId, req).ConfigureAwait(true);
            // A single-service re-detect returns just that service; keep the others' last snapshots.
            IReadOnlyDictionary<string, Domain.ProviderSnapshot> snapshots = result.Snapshots;
            if (onlyProviderId is not null)
            {
                var merged = new Dictionary<string, Domain.ProviderSnapshot>(_latestSnapshots, StringComparer.Ordinal);
                foreach (var (id, snapshot) in result.Snapshots)
                {
                    merged[id] = snapshot;
                }

                snapshots = merged;
            }

            _latestSnapshots = snapshots;

            // I8: one now/today pair for the AGY composition in this refresh (R-018 A3).
            var now = DateTimeOffset.UtcNow;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime);

            // Update tab ViewModels
            if (result.Snapshots.TryGetValue("claude", out var claudeSnap)) ClaudeTab.UpdateFromSnapshot(claudeSnap);
            if (result.Snapshots.TryGetValue("openai", out var openAiSnap)) OpenAiTab.UpdateFromSnapshot(openAiSnap);
            if (disabledProviders.Contains("agy"))
            {
                // D6/D13: AGY disabled in Settings (check the provider id, not the agent id) —
                // drop the raw snapshot and never compose for it.
                _latestRawAgySnapshot = null;
            }
            else if (result.Snapshots.TryGetValue("agy", out var agySnap))
            {
                // A3: keep the raw AGY snapshot so ccusage updates and chart view changes can
                // recompose the tab without another agy read.
                _latestRawAgySnapshot = agySnap;
                UpdateAgyTab(today, now);
            }

            // Update connections page view model with latest snapshot detections.
            // In AIMonitor, re-detect triggers a unified refresh across all providers in a single pass.
            Connections.Update(snapshots);

            // Update tray readings
            var trayReadings = TrayReadings.Build(ProviderTabs, _lastGoodMeters);
            _latestTrayReadings = trayReadings;

            _traySink?.UpdateReadings(trayReadings);

            _lastSuccess = DateTimeOffset.UtcNow;
            _showComposedStatus = true;
            RefreshStatusText(_lastSuccess.Value, null);
        }
        catch (Exception ex)
        {
            _showComposedStatus = false;
            LastRefreshStatus = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
            if (_pendingRefresh)
            {
                _pendingRefresh = false;
                await RefreshAsync();
            }
        }

        // Start a background ccusage read for the All agents tab after the provider refresh
        // (success or failure) so the All agents UI never depends on provider state.
        if (_agentUsageRefresher is not null && onlyProviderId is null)
        {
            var force = manual && IsAllAgentsSelected;
            _agentUsageTask = _agentUsageRefresher.RefreshAsync(force);
        }
    }
}
