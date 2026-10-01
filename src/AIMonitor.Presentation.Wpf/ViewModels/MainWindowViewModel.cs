using System.Windows.Input;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Wpf.Tray;

namespace AIMonitor.Presentation.Wpf.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly LatestRefreshCoordinator _refreshCoordinator;
    private readonly SettingsSession _settingsSession;
    private readonly TrayIconHost? _trayHost;

    private bool _isRefreshing;
    private string _lastRefreshStatus = "Ready";
    private ProviderTabViewModel _selectedTab;

    public MainWindowViewModel(
        LatestRefreshCoordinator refreshCoordinator,
        SettingsSession settingsSession,
        TrayIconHost? trayHost = null)
    {
        _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
        _settingsSession = settingsSession ?? throw new ArgumentNullException(nameof(settingsSession));
        _trayHost = trayHost;

        ClaudeTab = new ProviderTabViewModel("claude", "Claude");
        OpenAiTab = new ProviderTabViewModel("openai", "OpenAI / Codex");
        GeminiTab = new ProviderTabViewModel("gemini", "Gemini");

        ProviderTabs = [ClaudeTab, OpenAiTab, GeminiTab];
        _selectedTab = ClaudeTab;

        RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsRefreshing);
        OpenSettingsCommand = new RelayCommand(() => RequestOpenSettings?.Invoke());
        SwitchToWidgetCommand = new RelayCommand(() => RequestSwitchToWidget?.Invoke());
        OpenLogCommand = new RelayCommand(() => RequestOpenLog?.Invoke());
        OpenAboutCommand = new RelayCommand(() => RequestOpenAbout?.Invoke());
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
        set => SetProperty(ref _selectedTab, value);
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

    public ICommand RefreshCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand SwitchToWidgetCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenAboutCommand { get; }

    public event Action? RequestOpenSettings;
    public event Action? RequestSwitchToWidget;
    public event Action? RequestOpenLog;
    public event Action? RequestOpenAbout;

    private long _requestIdCounter;

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
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

            // Update tray readings
            if (_trayHost is not null)
            {
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
                _trayHost.UpdateReadings(trayReadings);
            }

            LastRefreshStatus = $"Updated at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            LastRefreshStatus = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }
}
