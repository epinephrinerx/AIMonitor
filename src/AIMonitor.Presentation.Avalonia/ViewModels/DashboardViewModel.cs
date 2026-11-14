using System.Collections.ObjectModel;
using System.Globalization;
using AIMonitor.Domain;
using System.Windows.Input;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// The dashboard. It talks to <c>AIMonitor.Application</c> through the refresh coordinator and the
/// settings session only — never to <c>AIMonitor.Infrastructure</c> — per <c>Docs/ARCHITECTURE.md</c> §5.
/// </summary>
public sealed class DashboardViewModel : ObservableObject
{
    /// <summary>What <c>ActiveProvider</c> holds while the All agents tab is the one open, as on Windows.</summary>
    public const string AllAgentsTabId = "all-agents";

    private readonly LatestRefreshCoordinator _refreshCoordinator;
    private readonly SettingsSession _settings;
    // Settings can change on a thread-pool thread, and ccusage results arrive on one; both are
    // brought back here before they touch anything bound to the window.
    private readonly SynchronizationContext? _uiContext;
    private readonly AgentUsageRefresher? _agentUsageRefresher;
    private long _requestId;
    private string _status = "Ready";
    private bool _isRefreshing;
    private ProviderViewModel _selectedProvider;
    private object _selectedTab;
    private AgentUsageReport? _lastAgentUsageReport;
    private AgentUsageUnavailableException? _lastAgentUsageError;
    // The last AGY snapshot as the service sent it, so ccusage news and chart changes can compose
    // the AGY tab again without running agy (R-018 A3).
    private ProviderSnapshot? _latestRawAgySnapshot;
    private IReadOnlyDictionary<string, decimal> _agentBudgets;
    private int _chartRangeDays;
    private string _chartMetric;
    private DashboardOption<string> _selectedMetric;
    private DashboardOption<int> _selectedRange;
    private DashboardOption<int> _selectedInterval;
    // Set while the header selectors are being brought in line with saved settings, so they do not
    // save the same value straight back.
    private bool _syncingFromSettings;
    // A refresh asked for while one is running is not dropped: one more runs after it (PAR-003).
    private bool _pendingRefresh;
    private bool _pendingManual;
    private DateTimeOffset? _lastSuccess;
    // False while the footer is saying something else ("Refreshing…", a failure) that the
    // one-second tick must not paint over.
    private bool _showComposedStatus;
    private double? _lastWorkingSetMb;

    public DashboardViewModel(
        LatestRefreshCoordinator refreshCoordinator,
        SettingsSession settings,
        CachedAgentUsageSource? agentUsage = null)
    {
        _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _uiContext = SynchronizationContext.Current;

        Providers =
        [
            new ProviderViewModel("claude", "Claude"),
            new ProviderViewModel("openai", "OpenAI / Codex"),
            new ProviderViewModel("agy", "Gemini / AGY"),
        ];
        OpenCodeTab = new ProviderViewModel(OpenCodeSnapshotBuilder.ProviderId, "OpenCode");
        OverviewTab = new ProviderViewModel(OverviewSnapshotBuilder.ProviderId, "Overview");
        AllAgentsTab = new AllAgentsTabViewModel();
        Tabs = [.. Providers, OpenCodeTab, OverviewTab, AllAgentsTab];

        var current = _settings.Current;
        _agentBudgets = current.AgentBudgets ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        _chartRangeDays = current.ChartRangeDays;
        _chartMetric = current.ChartMetric;
        _selectedMetric = DashboardOptions.Find(DashboardOptions.Metrics, current.ChartMetric, 0);
        _selectedRange = DashboardOptions.Find(DashboardOptions.Ranges, current.ChartRangeDays, 1);
        _selectedInterval = DashboardOptions.Find(DashboardOptions.Intervals, current.RefreshIntervalSeconds, 2);
        _selectedProvider = Providers.FirstOrDefault(p => p.ProviderId == current.ActiveProvider) ?? Providers[0];
        _selectedTab = current.ActiveProvider == AllAgentsTabId
            ? AllAgentsTab
            : Tabs.OfType<ProviderViewModel>().FirstOrDefault(tab =>
                string.Equals(tab.ProviderId, current.ActiveProvider, StringComparison.OrdinalIgnoreCase)) ?? _selectedProvider;

        if (agentUsage is not null)
        {
            _agentUsageRefresher = new AgentUsageRefresher(agentUsage, () => DateTimeOffset.UtcNow, TimeZoneInfo.Local);
            _agentUsageRefresher.Updated += OnAgentUsageUpdated;
        }

        RefreshCommand = new AsyncCommand(() => RefreshAsync(manual: true), () => !IsRefreshing);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke());
        OpenConnectionsCommand = new RelayCommand(() => OpenConnectionsRequested?.Invoke());
        OpenAboutCommand = new RelayCommand(() => OpenAboutRequested?.Invoke());
        OpenUsageLogCommand = new RelayCommand(() => OpenUsageLogRequested?.Invoke());
        PrintUsageLogCommand = new RelayCommand(() => PrintUsageLogRequested?.Invoke());
        SwitchToWidgetCommand = new RelayCommand(() => SwitchToWidgetRequested?.Invoke());
        SetThemeCommand = new SetThemeCommandImplementation(this);

        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>The three services with quota windows: what the menu bar and the widget speak for.</summary>
    public ObservableCollection<ProviderViewModel> Providers { get; }

    public ProviderViewModel OpenCodeTab { get; }

    public ProviderViewModel OverviewTab { get; }

    public AllAgentsTabViewModel AllAgentsTab { get; }

    /// <summary>Every tab along the top of the dashboard, in order.</summary>
    public IReadOnlyList<object> Tabs { get; }

    /// <summary>
    /// The open tab. Opening a service tab also makes it the one the menu bar and widget follow;
    /// opening OpenCode, the Overview or All agents leaves them on the last service tab (E28).
    /// </summary>
    public object SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null || !Set(ref _selectedTab, value)) return;

            if (value is ProviderViewModel provider && Providers.Contains(provider))
            {
                SelectedProvider = provider;
                return;
            }

            var id = value is ProviderViewModel other ? other.ProviderId : AllAgentsTabId;
            _ = _settings.UpdateAsync(s => s with { ActiveProvider = id });
        }
    }

    public ICommand RefreshCommand { get; }

    public ICommand ExitCommand { get; }

    public ICommand SetThemeCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenConnectionsCommand { get; }

    public ICommand OpenAboutCommand { get; }

    public ICommand OpenUsageLogCommand { get; }

    public ICommand PrintUsageLogCommand { get; }

    /// <summary>Collapses the dashboard to the desk widget, as the Windows header button and Ctrl+W do.</summary>
    public ICommand SwitchToWidgetCommand { get; }

    public event Action<string>? ThemeChanged;

    public event Action? ExitRequested;

    public event Action? RefreshIntervalChanged;

    public event Action? OpenSettingsRequested;

    public event Action? OpenConnectionsRequested;

    public event Action? OpenAboutRequested;

    public event Action? OpenUsageLogRequested;

    public event Action? PrintUsageLogRequested;

    public event Action? SwitchToWidgetRequested;

    /// <summary>Raised after every refresh, successful or not, so the menu bar and the widget
    /// redraw from the same readings the dashboard is showing.</summary>
    public event Action? Refreshed;

    public string Theme => _settings.Current.Theme;

    /// <summary>Which Appearance item carries the check mark.</summary>
    public bool IsSystemTheme => Theme is not ("light" or "dark");

    public bool IsLightTheme => Theme == "light";

    public bool IsDarkTheme => Theme == "dark";

    /// <summary>The snapshots behind what is on screen, which is what the usage log reports on.</summary>
    public IReadOnlyDictionary<string, ProviderSnapshot> LatestSnapshots { get; private set; } =
        new Dictionary<string, ProviderSnapshot>(StringComparer.Ordinal);

    /// <summary>How many days of history the log covers, as the user set it.</summary>
    public int ChartRangeDays => _settings.Current.ChartRangeDays;

    public string ChartMetric => _settings.Current.ChartMetric;

    /// <summary>The ccusage report behind the agent tabs, for the agent names the Settings budget rows offer.</summary>
    public AgentUsageReport? LatestAgentUsage => _lastAgentUsageReport;

    /// <summary>Zero means "Manual only", which the caller reads as "do not run a timer".</summary>
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Max(0, _settings.Current.RefreshIntervalSeconds));

    public ProviderViewModel SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (value is null) return;
            if (Set(ref _selectedProvider, value) || !ReferenceEquals(_selectedTab, value))
            {
                _selectedTab = value;
                Raise(nameof(SelectedTab));
                _ = _settings.UpdateAsync(s => s with { ActiveProvider = value.ProviderId });
            }
        }
    }

    /// <summary>The footer line: "Updated 12s ago (14:03:22) · auto-refresh every 3 minutes · 85 MB resident".</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (Set(ref _isRefreshing, value))
            {
                (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                Raise(nameof(RefreshButtonText));
            }
        }
    }

    public string RefreshButtonText => IsRefreshing ? "Refreshing…" : "Refresh";

    public IReadOnlyList<DashboardOption<string>> MetricOptions => DashboardOptions.Metrics;

    public IReadOnlyList<DashboardOption<int>> RangeOptions => DashboardOptions.Ranges;

    public IReadOnlyList<DashboardOption<int>> IntervalOptions => DashboardOptions.Intervals;

    /// <summary>Chart metric; choosing one saves it and refreshes straight away, as on Windows.</summary>
    public DashboardOption<string> SelectedMetric
    {
        get => _selectedMetric;
        set
        {
            if (value is null || !Set(ref _selectedMetric, value) || _syncingFromSettings) return;
            _ = ApplyViewChangeAsync(s => s with { ChartMetric = value.Value });
        }
    }

    /// <summary>History range in days; choosing one saves it and refreshes straight away.</summary>
    public DashboardOption<int> SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (value is null || !Set(ref _selectedRange, value) || _syncingFromSettings) return;
            _ = ApplyViewChangeAsync(s => s with { ChartRangeDays = value.Value });
        }
    }

    /// <summary>Auto-refresh interval; the app re-arms its timer when the setting changes. Zero is manual only.</summary>
    public DashboardOption<int> SelectedInterval
    {
        get => _selectedInterval;
        set
        {
            if (value is null || !Set(ref _selectedInterval, value) || _syncingFromSettings) return;
            _ = SaveAsync(s => s with { RefreshIntervalSeconds = value.Value });
        }
    }

    /// <summary>
    /// Re-renders the footer once a second so "Updated 12s ago" stays true; a refresh in progress or
    /// a failure message is left alone.
    /// </summary>
    public void RefreshStatusText(DateTimeOffset now, double? workingSetMb)
    {
        if (!_showComposedStatus) return;

        _lastWorkingSetMb = workingSetMb ?? _lastWorkingSetMb;
        Status = StatusBarText.Compose(
            _lastSuccess, now, _settings.Current.RefreshIntervalSeconds, _lastWorkingSetMb, TimeZoneInfo.Local);
    }

    private async Task ApplyViewChangeAsync(Func<AppSettings, AppSettings> change)
    {
        await SaveAsync(change).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task SaveAsync(Func<AppSettings, AppSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(change).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _showComposedStatus = false;
            Status = $"Could not save setting: {exception.Message}";
        }
    }

    public Task RefreshAsync() => RefreshAsync(manual: false);

    /// <param name="manual">The user asked for it. With the All agents tab open that also makes
    /// ccusage read again instead of answering from its cache.</param>
    /// <param name="onlyProviderId">Refresh just this service ("Re-detect" on its connections card);
    /// the others keep what they last showed.</param>
    public async Task RefreshAsync(bool manual, string? onlyProviderId = null)
    {
        if (IsRefreshing)
        {
            // Whatever was asked for meanwhile (a key saved, a range picked) still gets one full run.
            _pendingRefresh = true;
            _pendingManual |= manual;
            return;
        }

        IsRefreshing = true;
        _showComposedStatus = false;
        Status = "Refreshing…";
        try
        {
            var settings = _settings.Current;
            var disabled = settings.Providers
                .Where(entry => !entry.Value.Enabled)
                .Select(entry => entry.Key);

            var request = new ProviderSnapshotRequest(
                historyDays: settings.ChartRangeDays,
                metric: settings.ChartMetric,
                // Asked for every time: the dashboard shows the daily chart and the breakdowns, and
                // a service that cannot produce history reports that separately without failing.
                includeHistory: true,
                disabledProviders: disabled,
                onlyProviderId: onlyProviderId);

            var result = await _refreshCoordinator
                .QueueAsync(Interlocked.Increment(ref _requestId), request)
                .ConfigureAwait(true);

            // A single-service re-detect returns just that service; the others keep their last snapshots.
            var snapshots = new Dictionary<string, ProviderSnapshot>(
                onlyProviderId is null ? new Dictionary<string, ProviderSnapshot>() : LatestSnapshots, StringComparer.Ordinal);
            foreach (var (id, snapshot) in result.Snapshots) snapshots[id] = snapshot;
            LatestSnapshots = snapshots;

            foreach (var provider in Providers)
            {
                if (provider.ProviderId == "agy") continue;
                if (onlyProviderId is not null && provider.ProviderId != onlyProviderId) continue;
                provider.Apply(result.Snapshots.GetValueOrDefault(provider.ProviderId));
            }

            // AGY is shown composed with what ccusage knows about Antigravity (R-018), so the raw
            // snapshot is kept and the tab is built from both.
            if (onlyProviderId is null or "agy")
            {
                var agyDisabled = settings.Providers.GetValueOrDefault("agy") is { Enabled: false };
                _latestRawAgySnapshot = agyDisabled ? null : result.Snapshots.GetValueOrDefault("agy");
                if (_latestRawAgySnapshot is null)
                {
                    AgyTab.Apply(null);
                }
                else
                {
                    UpdateAgyTab(Today(DateTimeOffset.UtcNow));
                }
            }

            _lastSuccess = DateTimeOffset.UtcNow;
            _showComposedStatus = true;
            RefreshStatusText(_lastSuccess.Value, null);
        }
        catch (OperationCanceledException)
        {
            _showComposedStatus = false;
            Status = "Refresh cancelled";
        }
        catch (Exception exception)
        {
            _showComposedStatus = false;
            Status = $"Refresh failed: {exception.Message}";
        }
        finally
        {
            IsRefreshing = false;
            Refreshed?.Invoke();
        }

        if (_pendingRefresh)
        {
            _pendingRefresh = false;
            var rerunManual = _pendingManual;
            _pendingManual = false;
            await RefreshAsync(rerunManual).ConfigureAwait(true);
            return;
        }

        // ccusage runs after the services, whatever they did, so the agent tabs never wait on them.
        if (_agentUsageRefresher is not null && onlyProviderId is null)
        {
            await _agentUsageRefresher
                .RefreshAsync(force: manual && ReferenceEquals(_selectedTab, AllAgentsTab), _uiContext)
                .ConfigureAwait(true);
        }
    }

    private ProviderViewModel AgyTab => Providers.First(provider => provider.ProviderId == "agy");

    private static DateOnly Today(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime);

    private void OnAgentUsageUpdated(AgentUsageReport? lastGood, AgentUsageUnavailableException? lastError)
    {
        // The refresher delivers on the context it was given, which is this one.
        _lastAgentUsageReport = lastGood;
        _lastAgentUsageError = lastError;

        var now = DateTimeOffset.UtcNow;
        var today = Today(now);
        AllAgentsTab.Update(lastGood, lastError, today, _chartRangeDays, TimeZoneInfo.Local);
        Providers[0].SetAgentUsage(AgentUsagePresenter.SummaryFor(lastGood, "claude", today));
        Providers[1].SetAgentUsage(AgentUsagePresenter.SummaryFor(lastGood, "codex", today));
        AgyTab.SetAgentUsage(AgentUsagePresenter.SummaryFor(lastGood, "antigravity", today));
        UpdateOpenCodeTab(today, now);
        UpdateOverviewTab(today, now);
        // Last, so a failure composing AGY cannot keep the other tabs from updating (R-018 D13).
        UpdateAgyTab(today);
        AgentUsageUpdated?.Invoke(lastGood, lastError);
    }

    /// <summary>Raised with each ccusage result, for the connections page's "other agents" list.</summary>
    public event Action<AgentUsageReport?, AgentUsageUnavailableException?>? AgentUsageUpdated;

    /// <summary>Monthly budgets apply at once: the OpenCode and Overview gauges are rebuilt from the
    /// ccusage result already in hand, without reading ccusage again (R-015 E2).</summary>
    public void ApplyAgentBudgets(IReadOnlyDictionary<string, decimal> budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        if (budgets.Count == _agentBudgets.Count
            && budgets.All(pair => _agentBudgets.TryGetValue(pair.Key, out var amount) && amount == pair.Value))
        {
            return;
        }

        _agentBudgets = budgets;
        var now = DateTimeOffset.UtcNow;
        UpdateOpenCodeTab(Today(now), now);
        UpdateOverviewTab(Today(now), now);
    }

    private void UpdateOpenCodeTab(DateOnly today, DateTimeOffset now)
    {
        var snapshot = OpenCodeSnapshotBuilder.Build(
            _lastAgentUsageReport, _lastAgentUsageError, _agentBudgets, today, now, TimeZoneInfo.Local, _chartRangeDays, _chartMetric);
        if (snapshot is not null) OpenCodeTab.Apply(snapshot);
        OpenCodeTab.SetAgentUsage(AgentUsagePresenter.SummaryFor(_lastAgentUsageReport, "opencode", today));
    }

    private void UpdateOverviewTab(DateOnly today, DateTimeOffset now)
    {
        var snapshot = OverviewSnapshotBuilder.Build(
            _lastAgentUsageReport, _lastAgentUsageError, _agentBudgets, today, now, TimeZoneInfo.Local);
        if (snapshot is not null) OverviewTab.Apply(snapshot);
        OverviewTab.UpdateOverviewEmptyMessage(
            OverviewSnapshotBuilder.EmptyMessage(_lastAgentUsageReport, _lastAgentUsageError, today, TimeZoneInfo.Local));
    }

    private void UpdateAgyTab(DateOnly today)
    {
        if (_latestRawAgySnapshot is not { } raw) return;
        AgyTab.Apply(AgyUsageComposer.Compose(raw, _lastAgentUsageReport, today, _chartRangeDays, _chartMetric));
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            _uiContext.Post(_ => OnSettingsChangedCore(settings), null);
            return;
        }

        OnSettingsChangedCore(settings);
    }

    private void OnSettingsChangedCore(AppSettings settings)
    {
        Raise(nameof(Theme));
        Raise(nameof(IsSystemTheme));
        Raise(nameof(IsLightTheme));
        Raise(nameof(IsDarkTheme));
        Raise(nameof(RefreshInterval));
        RefreshIntervalChanged?.Invoke();

        // Settings saved elsewhere (the Settings window) move the header selectors too.
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

        ApplyAgentBudgets(settings.AgentBudgets ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));

        // The OpenCode and AGY charts follow the range and metric at once, from what is in hand (R-018 D1).
        if (settings.ChartRangeDays != _chartRangeDays || !string.Equals(settings.ChartMetric, _chartMetric, StringComparison.Ordinal))
        {
            _chartRangeDays = settings.ChartRangeDays;
            _chartMetric = settings.ChartMetric;
            var now = DateTimeOffset.UtcNow;
            UpdateOpenCodeTab(Today(now), now);
            UpdateAgyTab(Today(now));
        }
    }

    private async Task ApplyThemeAsync(string theme)
    {
        if (theme is not ("system" or "light" or "dark")) return;

        await _settings.UpdateAsync(s => s with { Theme = theme }).ConfigureAwait(true);
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>Takes the theme name as the command parameter, so one command serves three menu items.</summary>
    private sealed class SetThemeCommandImplementation(DashboardViewModel owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => parameter is string;

        public void Execute(object? parameter)
        {
            if (parameter is string theme) _ = owner.ApplyThemeAsync(theme);
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
