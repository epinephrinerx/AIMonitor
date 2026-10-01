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
    private DpapiSecretStore? _secretStore;
    private WindowsStartupRegistrar? _startupRegistrar;
    private TrayIconHost? _trayHost;
    private DispatcherTimer? _refreshTimer;

    private AppSettings _currentSettings = new();
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

        _currentSettings = await _settingsStore.LoadAsync();

        // 3. Theme Application (PAR-028)
        ThemeManager.Instance.ApplyTheme(_currentSettings.Theme);

        // 4. Provider Clients & Refresh Orchestration (PAR-001, PAR-002, PAR-003, PAR-004, PAR-029)
        _httpClient = new HttpClient();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var clock = SystemClock.Instance;

        var openAiKey = await _secretStore.GetAsync("providers/openai/key").ConfigureAwait(true);
        var geminiKey = await _secretStore.GetAsync("providers/gemini/key").ConfigureAwait(true);

        _currentSettings.Providers.TryGetValue("openai", out var openAiPref);
        _currentSettings.Providers.TryGetValue("gemini", out var geminiPref);

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

        // 5. System Tray Host (PAR-022, PAR-025)
        if (_currentSettings.ShowTrayIcon)
        {
            _trayHost = new TrayIconHost();
            _trayHost.OpenDashboardRequested += SwitchToDashboardMode;
            _trayHost.OpenWidgetRequested += SwitchToWidgetMode;
            _trayHost.OpenLogRequested += OpenUsageLogDialog;
            _trayHost.RefreshRequested += async () =>
            {
                if (_mainViewModel is not null) await _mainViewModel.RefreshAsync();
            };
            _trayHost.OpenSettingsRequested += OpenSettingsDialog;
            _trayHost.OpenAboutRequested += OpenAboutDialog;
            _trayHost.ExitRequested += ShutdownApp;
        }

        // 6. ViewModels
        _mainViewModel = new MainWindowViewModel(_refreshCoordinator, _settingsStore, _trayHost);
        _mainViewModel.RequestOpenLog += OpenUsageLogDialog;
        _mainViewModel.RequestOpenAbout += OpenAboutDialog;
        _widgetViewModel = new WidgetViewModel(_mainViewModel.ProviderTabs);

        // 7. Initial Window
        var startMinimized = e.Args.Any(a => a is "--minimized" or "--tray" or "-m");
        if (!startMinimized)
        {
            SwitchToDashboardMode();
        }

        // Initial background fetch
        _ = _mainViewModel.RefreshAsync();

        // 8. Periodic Refresh Timer
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_currentSettings.RefreshIntervalSeconds)
        };
        _refreshTimer.Tick += async (s, args) => await _mainViewModel.RefreshAsync();
        _refreshTimer.Start();
    }

    public void SwitchToDashboardMode()
    {
        Dispatcher.Invoke(() =>
        {
            if (_widgetWindow is not null && _widgetWindow.IsVisible)
            {
                _widgetWindow.Hide();
            }

            if (_mainWindow is null)
            {
                _mainWindow = new MainWindow(_mainViewModel!, _settingsStore!, _currentSettings);
            }

            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            _mainWindow.Activate();
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

            if (_widgetWindow is null)
            {
                _widgetWindow = new WidgetWindow(_widgetViewModel!, _settingsStore!, _currentSettings);
            }

            _widgetWindow.Show();
            _widgetWindow.Activate();
        });
    }

    public void OpenSettingsDialog()
    {
        Dispatcher.Invoke(() =>
        {
            var vm = new SettingsViewModel(_currentSettings, _settingsStore!, _startupRegistrar!);
            var dialog = new SettingsDialog(vm)
            {
                Owner = _mainWindow?.IsVisible == true ? _mainWindow : null
            };

            if (dialog.ShowDialog() == true)
            {
                _ = Task.Run(async () =>
                {
                    _currentSettings = await _settingsStore!.LoadAsync();
                    Dispatcher.Invoke(() =>
                    {
                        if (_refreshTimer is not null)
                        {
                            _refreshTimer.Interval = TimeSpan.FromSeconds(_currentSettings.RefreshIntervalSeconds);
                        }
                    });
                });
            }
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
            var report = UsageReportGenerator.Build(providers, snapshots, _currentSettings.ChartRangeDays, "Total tokens");
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
        _refreshTimer?.Stop();
        _trayHost?.Dispose();
        _ = _refreshCoordinator?.DisposeAsync();
        _httpClient?.Dispose();
        _singleInstanceCoordinator?.Dispose();
        ThemeManager.Instance.Dispose();

        base.OnExit(e);
    }
}
