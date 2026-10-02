using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Reports;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Version;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.Infrastructure.Providers.Gemini;
using AIMonitor.Infrastructure.Providers.OpenAi;
using AIMonitor.Infrastructure.Storage;
using AIMonitor.Infrastructure.Time;
using AIMonitor.Infrastructure.Windows;
using AIMonitor.Presentation.Wpf.Theme;
using AIMonitor.Presentation.Wpf.Tray;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

/// <summary>
/// Composition Root and application lifecycle coordinator for AIMonitor 2.0.
/// Conforms to Docs/ARCHITECTURE.md Section 6 and ADR-0001, ADR-0002, ADR-0003.
/// </summary>
public partial class App : System.Windows.Application
{
    internal static readonly Type ApplicationLayerReference = typeof(AIMonitor.Application.AssemblyMarker);
    internal static readonly Type InfrastructureReference = typeof(AIMonitor.Infrastructure.AssemblyMarker);

    private NamedMutexSingleInstance? _singleInstanceCoordinator;
    private HttpClient? _httpClient;
    private LatestRefreshCoordinator? _refreshCoordinator;
    private JsonSettingsStore? _settingsStore;
    private SettingsSession? _settingsSession;
    private DpapiSecretStore? _secretStore;
    private WindowsStartupRegistrar? _startupRegistrar;
    private LiveSettingsApplier? _liveSettingsApplier;
    private DispatcherTimer? _refreshTimer;

    private MainWindow? _mainWindow;
    private WidgetWindow? _widgetWindow;
    private MainWindowViewModel? _mainViewModel;
    private WidgetViewModel? _widgetViewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Single-Instance Check (PAR-023, ADR-0003)
        _singleInstanceCoordinator = new NamedMutexSingleInstance();
        var acquired = await _singleInstanceCoordinator.TryAcquireAsync();
        if (!acquired)
        {
            // Secondary instance: signal running primary instance, then terminate cleanly
            await _singleInstanceCoordinator.NotifyExistingInstanceAsync(e.Args);
            Shutdown(0);
            return;
        }

        // Primary instance: listen for activation requests from secondary launches
        _singleInstanceCoordinator.Activated += OnSingleInstanceActivated;

        // 2. Storage & One-Time Migration (ADR-0002, PAR-026, PAR-029)
        var localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageMonitor");
        Directory.CreateDirectory(localAppData);

        var settingsPath = Path.Combine(localAppData, "settings.json");
        var secretsPath = Path.Combine(localAppData, "secrets.dat");

        _settingsStore = new JsonSettingsStore(settingsPath);
        _secretStore = new DpapiSecretStore(secretsPath);
        _startupRegistrar = new WindowsStartupRegistrar();

        if (!_settingsStore.Exists)
        {
            var migrationUseCase = new MigrateLegacySettingsUseCase(
                _settingsStore,
                _secretStore,
                new LegacyRegistrySettingsReader(),
                new LegacyDpapiSecretUnsealer());

            await migrationUseCase.ExecuteAsync();
        }

        _settingsSession = await SettingsSession.CreateAsync(_settingsStore);

        // 3. Theme Application (PAR-028)
        ThemeManager.Instance.ApplyTheme(_settingsSession.Current.Theme);

        // 4. Provider Clients & Refresh Orchestration (PAR-001, PAR-002, PAR-003, PAR-004, PAR-029)
        _httpClient = new HttpClient();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var clock = SystemClock.Instance;

        var openAiKey = await _secretStore.GetAsync("providers/openai/key").ConfigureAwait(true);
        var geminiKey = await _secretStore.GetAsync("providers/gemini/key").ConfigureAwait(true);

        _settingsSession.Current.Providers.TryGetValue("openai", out var openAiPref);
        _settingsSession.Current.Providers.TryGetValue("gemini", out var geminiPref);

        double? monthlyBudget = double.TryParse(openAiPref?.Extra, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedBudget) && parsedBudget > 0
            ? parsedBudget
            : null;

        var claudeClient = new ClaudeLiveQuotaClient(_httpClient, Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), userProfile, clock);
        var openAiClient = new OpenAiLiveQuotaClient(_httpClient, userProfile, clock, savedAdminKey: openAiKey, monthlyBudgetUsd: monthlyBudget);
        var geminiClient = new GeminiLiveQuotaClient(_httpClient, userProfile, clock, savedServiceAccountPath: geminiKey, projectOverride: string.IsNullOrWhiteSpace(geminiPref?.Extra) ? null : geminiPref.Extra);

        var registrations = new[]
        {
            new ProviderClientRegistration("claude", claudeClient),
            new ProviderClientRegistration("openai", openAiClient),
            new ProviderClientRegistration("gemini", geminiClient)
        };

        var refreshUseCase = new RefreshProvidersUseCase(registrations);
        _refreshCoordinator = new LatestRefreshCoordinator(refreshUseCase);

        // 5. ViewModels
        _mainViewModel = new MainWindowViewModel(_refreshCoordinator, _settingsSession);
        _mainViewModel.RequestOpenLog += OpenUsageLogDialog;
        _mainViewModel.RequestOpenAbout += OpenAboutDialog;
        _widgetViewModel = new WidgetViewModel(_mainViewModel.ProviderTabs);

        // 6. Periodic Refresh Timer
        _refreshTimer = new DispatcherTimer();
        _refreshTimer.Tick += async (s, args) => await _mainViewModel.RefreshAsync();
        _refreshTimer.Start();

        // 7. Live Settings & Tray Lifecycle Coordinator
        _liveSettingsApplier = new LiveSettingsApplier(
            _widgetViewModel,
            _mainViewModel,
            CreateTrayHost,
            interval =>
            {
                if (_refreshTimer is not null)
                {
                    _refreshTimer.Interval = interval;
                }
            },
            () => (_mainWindow?.IsVisible ?? false) || (_widgetWindow?.IsVisible ?? false),
            SwitchToDashboardMode);
        _liveSettingsApplier.Apply(_settingsSession.Current);

        // 8. Initial Window
        var startMinimizedRequested = e.Args.Any(a => a is "--minimized" or "--tray" or "-m");
        if (!TrayPolicy.ShouldStartHidden(_settingsSession.Current, startMinimizedRequested))
        {
            SwitchToDashboardMode();
        }

        // Initial background fetch
        _ = _mainViewModel.RefreshAsync();

        _settingsSession.Changed += OnSettingsChanged;
    }

    private ITrayHost CreateTrayHost()
    {
        var host = new TrayIconHost();
        host.OpenDashboardRequested += SwitchToDashboardMode;
        host.OpenWidgetRequested += SwitchToWidgetMode;
        host.OpenLogRequested += OpenUsageLogDialog;
        host.RefreshRequested += async () =>
        {
            if (_mainViewModel is not null) await _mainViewModel.RefreshAsync();
        };
        host.OpenSettingsRequested += OpenSettingsDialog;
        host.OpenAboutRequested += OpenAboutDialog;
        host.ExitRequested += ShutdownApp;
        return host;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _liveSettingsApplier?.Apply(settings);
        });
    }

    public void SwitchToDashboardMode()
    {
        Dispatcher.Invoke(() =>
        {
            if (_widgetWindow is not null && _widgetWindow.IsVisible)
            {
                _widgetWindow.Hide();
            }

            if (_mainWindow is null && _settingsSession is not null)
            {
                var window = new MainWindow(_mainViewModel!, _settingsSession);
                window.Closed += (s, e) =>
                {
                    if (ReferenceEquals(_mainWindow, window))
                    {
                        _mainWindow = null;
                    }
                };
                _mainWindow = window;
            }

            _mainWindow?.Show();
            if (_mainWindow?.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            _mainWindow?.Activate();
        });
    }

    public void SwitchToWidgetMode()
    {
        Dispatcher.Invoke(() =>
        {
            if (_mainWindow is not null && _mainWindow.IsVisible)
            {
                _mainWindow.Hide();
            }

            if (_widgetWindow is null && _settingsSession is not null)
            {
                var window = new WidgetWindow(_widgetViewModel!, _settingsSession);
                window.Closed += (s, e) =>
                {
                    if (ReferenceEquals(_widgetWindow, window))
                    {
                        _widgetWindow = null;
                    }
                };
                _widgetWindow = window;
            }

            _widgetWindow?.Show();
            _widgetWindow?.Activate();
        });
    }

    public void OpenSettingsDialog()
    {
        Dispatcher.Invoke(() =>
        {
            if (_settingsSession is null) return;

            var vm = new SettingsViewModel(_settingsSession, _startupRegistrar!);
            var dialog = new SettingsDialog(vm)
            {
                Owner = _mainWindow?.IsVisible == true ? _mainWindow : null
            };

            dialog.ShowDialog();
        });
    }

    public void OpenUsageLogDialog()
    {
        Dispatcher.Invoke(() =>
        {
            var providers = new (string, string)[]
            {
                ("claude", "Claude"),
                ("openai", "OpenAI / Codex"),
                ("gemini", "Gemini")
            };

            var snapshots = _mainViewModel?.LatestSnapshots ?? new Dictionary<string, Domain.ProviderSnapshot>();
            var chartRangeDays = _settingsSession?.Current.ChartRangeDays ?? 14;
            var report = UsageReportGenerator.Build(providers, snapshots, chartRangeDays, "Total tokens");
            var vm = new UsageLogViewModel(report);
            var dialog = new UsageLogDialog(vm)
            {
                Owner = _mainWindow?.IsVisible == true ? _mainWindow : null
            };
            dialog.ShowDialog();
        });
    }

    public void OpenAboutDialog()
    {
        Dispatcher.Invoke(() =>
        {
            var vm = new AboutViewModel(new GitHubVersionChecker(_httpClient));
            var dialog = new AboutDialog(vm)
            {
                Owner = _mainWindow?.IsVisible == true ? _mainWindow : null
            };
            dialog.ShowDialog();
        });
    }

    private void OnSingleInstanceActivated(string[] args)
    {
        Dispatcher.InvokeAsync(SwitchToDashboardMode);
    }

    public void ShutdownApp()
    {
        Dispatcher.Invoke(() =>
        {
            _mainWindow?.RequestExplicitExit();
            _widgetWindow?.Close();
            Shutdown();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Flush pending settings FIRST (before disposing the single-instance coordinator/mutex, tray,
        // HTTP client, and theme) because releasing the mutex allows a relaunched process to load settings
        // before this process has finished writing.
        // OnExit is a void lifecycle method that cannot be async-awaited; we block with a bounded timeout
        // to prevent process termination before the atomic file write completes.
        // Because SettingsSession uses ConfigureAwait(false) internally, this will not deadlock.
        var flushed = false;
        try
        {
            if (_settingsSession is not null)
            {
                flushed = _settingsSession.FlushAsync().Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Non-fatal during application exit
        }

        // If flush timed out, do NOT dispose SettingsSession: the process is exiting, and disposing
        // the session's underlying gate would throw ObjectDisposedException and strand queued waiters.
        if (flushed)
        {
            _settingsSession?.Dispose();
        }

        _refreshTimer?.Stop();
        _liveSettingsApplier?.Shutdown();
        _ = _refreshCoordinator?.DisposeAsync();
        _httpClient?.Dispose();
        _singleInstanceCoordinator?.Dispose();
        ThemeManager.Instance.Dispose();

        base.OnExit(e);
    }
}
