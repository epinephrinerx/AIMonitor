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

    public event Action<string>? ThemeChanged;

    public event Action? ExitRequested;

    public event Action? RefreshIntervalChanged;

    public event Action? OpenSettingsRequested;

    public event Action? OpenConnectionsRequested;

    public event Action? OpenAboutRequested;

    public event Action? OpenUsageLogRequested;

    /// <summary>Raised after every refresh, successful or not, so the menu bar and the widget
    /// redraw from the same readings the dashboard is showing.</summary>
    public event Action? Refreshed;

    public string Theme => _settings.Current.Theme;

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
            }
        }
    }

    public Task RefreshAsync() => RefreshAsync(manual: false);

    /// <param name="manual">The user asked for it. With the All agents tab open that also makes
    /// ccusage read again instead of answering from its cache.</param>
    public async Task RefreshAsync(bool manual)
    {
        if (IsRefreshing) return;

        IsRefreshing = true;
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
                disabledProviders: disabled);

            var result = await _refreshCoordinator
                .QueueAsync(Interlocked.Increment(ref _requestId), request)
                .ConfigureAwait(true);

            LatestSnapshots = result.Snapshots;
            foreach (var provider in Providers)
            {
                if (provider.ProviderId == "agy") continue;
                provider.Apply(result.Snapshots.GetValueOrDefault(provider.ProviderId));
            }

            // AGY is shown composed with what ccusage knows about Antigravity (R-018), so the raw
            // snapshot is kept and the tab is built from both.
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

            var failures = Providers.Count(p => p.HasError);
            Status = failures == 0
                ? $"Updated {DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}"
                : $"Updated {DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)} · {failures} service(s) reported a problem";
        }
        catch (OperationCanceledException)
        {
            Status = "Refresh cancelled";
        }
        finally
        {
            IsRefreshing = false;
            Refreshed?.Invoke();
        }

        // ccusage runs after the services, whatever they did, so the agent tabs never wait on them.
        if (_agentUsageRefresher is not null)
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
        Raise(nameof(RefreshInterval));
        RefreshIntervalChanged?.Invoke();

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
