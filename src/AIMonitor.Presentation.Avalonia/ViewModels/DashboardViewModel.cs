using System.Collections.ObjectModel;
using System.Globalization;
using AIMonitor.Domain;
using System.Windows.Input;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;

namespace AIMonitor.Presentation.Avalonia.ViewModels;

/// <summary>
/// The dashboard. It talks to <c>AIMonitor.Application</c> through the refresh coordinator and the
/// settings session only — never to <c>AIMonitor.Infrastructure</c> — per <c>Docs/ARCHITECTURE.md</c> §5.
/// </summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly LatestRefreshCoordinator _refreshCoordinator;
    private readonly SettingsSession _settings;
    private long _requestId;
    private string _status = "Ready";
    private bool _isRefreshing;
    private ProviderViewModel _selectedProvider;

    public DashboardViewModel(LatestRefreshCoordinator refreshCoordinator, SettingsSession settings)
    {
        _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        Providers =
        [
            new ProviderViewModel("claude", "Claude"),
            new ProviderViewModel("openai", "OpenAI / Codex"),
            new ProviderViewModel("agy", "Gemini / AGY"),
        ];

        var current = _settings.Current;
        _selectedProvider = Providers.FirstOrDefault(p => p.ProviderId == current.ActiveProvider) ?? Providers[0];

        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsRefreshing);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke());
        OpenConnectionsCommand = new RelayCommand(() => OpenConnectionsRequested?.Invoke());
        OpenAboutCommand = new RelayCommand(() => OpenAboutRequested?.Invoke());
        OpenUsageLogCommand = new RelayCommand(() => OpenUsageLogRequested?.Invoke());
        SetThemeCommand = new SetThemeCommandImplementation(this);

        _settings.Changed += OnSettingsChanged;
    }

    public ObservableCollection<ProviderViewModel> Providers { get; }

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

    /// <summary>Zero means "Manual only", which the caller reads as "do not run a timer".</summary>
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Max(0, _settings.Current.RefreshIntervalSeconds));

    public ProviderViewModel SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (value is null || !Set(ref _selectedProvider, value)) return;
            _ = _settings.UpdateAsync(s => s with { ActiveProvider = value.ProviderId });
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

    public async Task RefreshAsync()
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
                provider.Apply(result.Snapshots.GetValueOrDefault(provider.ProviderId));
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
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        Raise(nameof(Theme));
        Raise(nameof(RefreshInterval));
        RefreshIntervalChanged?.Invoke();
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
