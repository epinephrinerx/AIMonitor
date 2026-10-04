using System.Windows.Input;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Wpf.Tray;

namespace AIMonitor.Presentation.Wpf.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly LatestRefreshCoordinator _refreshCoordinator;
    private readonly SettingsSession _settingsSession;
    private ITrayReadingsSink? _traySink;
    private IReadOnlyList<TrayReading>? _latestTrayReadings;

    private bool _isRefreshing;
    private bool _pendingRefresh;
    private string _lastRefreshStatus = "Ready";
    private ProviderTabViewModel _selectedTab;
    private DashboardOption<string> _selectedMetric;
    private DashboardOption<int> _selectedRange;
    private DashboardOption<int> _selectedInterval;
    private bool _syncingFromSettings;
    private DateTimeOffset? _lastSuccess;
    private bool _showComposedStatus;

    public MainWindowViewModel(
        LatestRefreshCoordinator refreshCoordinator,
        SettingsSession settingsSession,
        ITrayReadingsSink? trayHost = null)
    {
        _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
        _settingsSession = settingsSession ?? throw new ArgumentNullException(nameof(settingsSession));
        _traySink = trayHost;

        ClaudeTab = new ProviderTabViewModel("claude", "Claude");
        OpenAiTab = new ProviderTabViewModel("openai", "OpenAI / Codex");
        GeminiTab = new ProviderTabViewModel("gemini", "Gemini");

        ProviderTabs = [ClaudeTab, OpenAiTab, GeminiTab];
        var settings = _settingsSession.Current;
        _selectedTab = ProviderTabs.FirstOrDefault(t => t.ProviderId == settings.ActiveProvider) ?? ClaudeTab;
        _selectedMetric = DashboardOptions.Find(DashboardOptions.Metrics, settings.ChartMetric, 0);
        _selectedRange = DashboardOptions.Find(DashboardOptions.Ranges, settings.ChartRangeDays, 1);
        _selectedInterval = DashboardOptions.Find(DashboardOptions.Intervals, settings.RefreshIntervalSeconds, 2);
        _settingsSession.Changed += OnSettingsChanged;

        RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsRefreshing);
        OpenSettingsCommand = new RelayCommand(() => RequestOpenSettings?.Invoke());
        SwitchToWidgetCommand = new RelayCommand(() => RequestSwitchToWidget?.Invoke());
        OpenLogCommand = new RelayCommand(() => RequestOpenLog?.Invoke());
        OpenAboutCommand = new RelayCommand(() => RequestOpenAbout?.Invoke());

        Connections = new ConnectionsViewModel(_settingsSession);
        Connections.ConnectRequested += id => RequestConnect?.Invoke(id);
        Connections.RedetectRequested += async _ => await RefreshAsync();
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
    public ProviderTabViewModel GeminiTab { get; }
    public IReadOnlyList<ProviderTabViewModel> ProviderTabs { get; }

    public IReadOnlyDictionary<string, Domain.ProviderSnapshot> LatestSnapshots => _latestSnapshots;
    private IReadOnlyDictionary<string, Domain.ProviderSnapshot> _latestSnapshots = new Dictionary<string, Domain.ProviderSnapshot>();

    public ProviderTabViewModel SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null || !SetProperty(ref _selectedTab, value))
            {
                return;
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
            if (value is not null && SetProperty(ref _selectedMetric, value) && !_syncingFromSettings)
            {
                _ = ApplyViewChangeAsync(s => s with { ChartMetric = value.Value });
            }
        }
    }

    /// <summary>History range in days; changing it saves the setting and refreshes straight away.</summary>
    public DashboardOption<int> SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (value is not null && SetProperty(ref _selectedRange, value) && !_syncingFromSettings)
            {
                _ = ApplyViewChangeAsync(s => s with { ChartRangeDays = value.Value });
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
    public event Action? RequestOpenAbout;
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

    private long _requestIdCounter;

    public async Task RefreshAsync()
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
            var req = new ProviderSnapshotRequest(
                historyDays: settings.ChartRangeDays,
                metric: settings.ChartMetric,
                includeHistory: true);
            var requestId = Interlocked.Increment(ref _requestIdCounter);
            var result = await _refreshCoordinator.QueueAsync(requestId, req).ConfigureAwait(true);
            _latestSnapshots = result.Snapshots;

            // Update tab ViewModels
            if (result.Snapshots.TryGetValue("claude", out var claudeSnap)) ClaudeTab.UpdateFromSnapshot(claudeSnap);
            if (result.Snapshots.TryGetValue("openai", out var openAiSnap)) OpenAiTab.UpdateFromSnapshot(openAiSnap);
            if (result.Snapshots.TryGetValue("gemini", out var geminiSnap)) GeminiTab.UpdateFromSnapshot(geminiSnap);

            // Update connections page view model with latest snapshot detections.
            // In AIMonitor, re-detect triggers a unified refresh across all providers in a single pass.
            Connections.Update(result.Snapshots);

            // Update tray readings
            var trayReadings = new List<TrayReading>();
            foreach (var tab in ProviderTabs)
            {
                var highestMeter = tab.Meters.MaxBy(m => m.Value);
                var pct = highestMeter?.Value ?? 0.0;
                var sev = highestMeter?.Severity ?? Domain.Severity.Normal;
                var detail = highestMeter?.Title ?? tab.Status;
                var code = tab.ProviderId switch
                {
                    "claude" => "CL",
                    "openai" => "OA",
                    "gemini" => "GE",
                    _ => "AI"
                };

                trayReadings.Add(new TrayReading(
                    tab.ProviderId,
                    tab.DisplayName,
                    code,
                    pct,
                    sev,
                    detail,
                    tab.Status is "Connected" or "Limited"));
            }

            if (trayReadings.Any(r => r.HasData))
            {
                _latestTrayReadings = trayReadings;
            }

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
    }
}
