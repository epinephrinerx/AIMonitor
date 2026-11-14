using AIMonitor.Application.Providers;
using AIMonitor.Application.Reports;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Version;
using AIMonitor.Presentation.Avalonia.MenuBar;
using AIMonitor.Presentation.Avalonia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AIMonitor.Presentation.Avalonia;

/// <summary>
/// The composition root. This is the only place that names anything from
/// <c>AIMonitor.Infrastructure</c>, per <c>Docs/ARCHITECTURE.md</c> §5, and it goes through
/// <c>PlatformAdapters</c> so the choice of adapter per operating system lives in one place.
/// </summary>
public sealed partial class App : global::Avalonia.Application
{
    private readonly PlatformHost? _platform;
    private readonly string[] _arguments;
    private DashboardViewModel? _dashboard;
    private WidgetViewModel? _widget;
    private SettingsSession? _settings;
    private ProviderConnectionStore? _connections;
    private ConnectionsViewModel? _connectionsPage;
    private HttpClient? _updateHttpClient;
    private MenuBarHost? _menuBar;
    private MainWindow? _window;
    private WidgetWindow? _widgetWindow;
    private DispatcherTimer? _refreshTimer;

    public App()
        : this(null, [])
    {
    }

    internal App(PlatformHost? platform, string[] arguments)
    {
        _platform = platform;
        _arguments = arguments;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || _platform is null)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // Shutting down only when the user asks keeps the app alive behind a closed dashboard,
        // which is the whole point of the menu bar extra.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var startup = _platform.StartAsync().GetAwaiter().GetResult();
        _settings = startup.Settings;
        _connections = startup.Connections;
        ApplyTheme(_settings.Current.Theme);

        _dashboard = new DashboardViewModel(startup.RefreshCoordinator, _settings, startup.AgentUsage);
        _dashboard.ThemeChanged += ApplyTheme;
        _dashboard.ExitRequested += Quit;
        _dashboard.Refreshed += OnRefreshed;
        _dashboard.OpenSettingsRequested += OpenSettings;
        _dashboard.OpenConnectionsRequested += OpenConnections;
        _dashboard.OpenAboutRequested += OpenAbout;
        _dashboard.OpenUsageLogRequested += OpenUsageLog;

        _widget = new WidgetViewModel([.. _dashboard.Providers]);
        _widget.ExpandRequested += ShowDashboard;
        _widget.RefreshRequested += () => _ = _dashboard.RefreshAsync(manual: true);
        _widget.QuitRequested += Quit;

        _window = new MainWindow { DataContext = _dashboard };
        _window.Closing += OnDashboardClosing;
        desktop.MainWindow = _window;

        _settings.Changed += OnSettingsChanged;
        ApplySettings(_settings.Current);

        _platform.Activated += arguments => Dispatcher.UIThread.Post(() => ShowDashboard());

        // --minimized/--tray only takes effect when something is left to restore the app from.
        var hidden = TrayPolicy.ShouldStartHidden(_settings.Current, WantsHiddenStart(_arguments));
        if (!hidden)
        {
            _window.Show();
        }

        NativeMenu.SetMenu(this, BuildApplicationMenu());

        StartRefreshTimer();
        _ = _dashboard.RefreshAsync();

        // The connections page is where a brand-new profile starts, because nothing can be shown
        // until at least one service is signed in.
        if (_settings.Current.ShowConnectionsAtStartup && !hidden)
        {
            OpenConnections();
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static bool WantsHiddenStart(IEnumerable<string> arguments) =>
        arguments.Any(argument => argument is "--minimized" or "--tray" or "-m");

    private void ApplySettings(AppSettings settings)
    {
        _widget?.ApplySettings(settings);
        ApplyMenuBar(TrayPolicy.ShouldShowTray(settings));
        ApplyRefreshInterval();
    }

    private void ApplyMenuBar(bool wanted)
    {
        if (wanted)
        {
            if (_menuBar is null)
            {
                _menuBar = new MenuBarHost();
                _menuBar.OpenDashboardRequested += ShowDashboard;
                _menuBar.OpenWidgetRequested += ShowWidget;
                _menuBar.RefreshRequested += () => _ = _dashboard?.RefreshAsync(manual: true);
                _menuBar.QuitRequested += Quit;
                if (_dashboard is not null)
                {
                    _menuBar.UpdateReadings(_dashboard.Providers);
                }
            }

            _menuBar.IsVisible = true;
            return;
        }

        _menuBar?.Dispose();
        _menuBar = null;
    }

    private void OnRefreshed()
    {
        if (_dashboard is null) return;
        _menuBar?.UpdateReadings(_dashboard.Providers);
        _widget?.Show(_dashboard.SelectedProvider.ProviderId);
        _connectionsPage?.Update(_dashboard.LatestSnapshots);
    }

    /// <summary>
    /// The menu macOS shows along the top of the screen. Separate from the menu bar extra: this one
    /// belongs to the app while it is in front, that one is there whether it is or not.
    /// </summary>
    private NativeMenu BuildApplicationMenu()
    {
        var file = new NativeMenuItem("File") { Menu = new NativeMenu() };
        file.Menu!.Add(Item("Refresh now", () => _ = _dashboard?.RefreshAsync(manual: true)));
        file.Menu.Add(Item("Usage log", OpenUsageLog));
        file.Menu.Add(new NativeMenuItemSeparator());
        file.Menu.Add(Item("Settings…", OpenSettings));
        file.Menu.Add(Item("Connections…", OpenConnections));

        var window = new NativeMenuItem("Window") { Menu = new NativeMenu() };
        window.Menu!.Add(Item("Dashboard", ShowDashboard));
        window.Menu.Add(Item("Widget", ShowWidget));

        var help = new NativeMenuItem("Help") { Menu = new NativeMenu() };
        help.Menu!.Add(Item("About AI Usage Monitor", OpenAbout));

        return [file, window, help];
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenSettings()
    {
        if (_settings is null) return;
        Show(new SettingsWindow { DataContext = new SettingsViewModel(_settings) });
    }

    private void OpenConnections()
    {
        if (_settings is null || _connections is null || _dashboard is null) return;

        _connectionsPage = new ConnectionsViewModel(_settings, _connections);
        // Named rather than discarded: a discard here would shadow the lambda's own parameter.
        _connectionsPage.RedetectRequested += providerId => _ = _dashboard.RefreshAsync();
        _connectionsPage.Update(_dashboard.LatestSnapshots);

        var window = new ConnectionsWindow { DataContext = _connectionsPage };
        window.Closed += (_, _) => _connectionsPage = null;
        Show(window);
    }

    private void OpenAbout()
    {
        _updateHttpClient ??= new HttpClient();
        var checker = new GitHubVersionChecker(_updateHttpClient, branch: AboutViewModel.ReleaseBranch);
        Show(new AboutWindow { DataContext = new AboutViewModel(checker) });
    }

    private void OpenUsageLog()
    {
        if (_dashboard is null) return;

        var report = UsageReportGenerator.Build(
            _dashboard.Providers.Select(provider => (provider.ProviderId, provider.Title)),
            _dashboard.LatestSnapshots,
            _dashboard.ChartRangeDays,
            _dashboard.ChartMetric);

        Show(new UsageLogWindow { DataContext = new UsageLogViewModel(report) });
    }

    /// <summary>
    /// Shown beside the dashboard rather than modal over it: on macOS a modal sheet would block the
    /// window the user is reading the numbers in.
    /// </summary>
    private void Show(Window window)
    {
        if (_window?.IsVisible == true)
        {
            window.Show(_window);
        }
        else
        {
            window.Show();
        }

        window.Activate();
    }

    private void OnSettingsChanged(AppSettings settings) =>
        Dispatcher.UIThread.Post(() => ApplySettings(settings));

    private void ShowDashboard()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ShowWidget()
    {
        if (_widget is null) return;
        _widgetWindow ??= new WidgetWindow { DataContext = _widget };
        _widgetWindow.Show();
        _widgetWindow.Activate();
    }

    /// <summary>
    /// Closing the dashboard parks the app in the menu bar when the user asked for that, and only
    /// when there is a menu bar extra to bring it back from.
    /// </summary>
    private void OnDashboardClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_settings is null || !TrayPolicy.ShouldHideOnClose(_settings.Current)) return;

        e.Cancel = true;
        _window?.Hide();
    }

    private void Quit()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void StartRefreshTimer()
    {
        if (_dashboard is null) return;

        _refreshTimer = new DispatcherTimer();
        _refreshTimer.Tick += (_, _) => _ = _dashboard.RefreshAsync();
        ApplyRefreshInterval();
    }

    private void ApplyRefreshInterval()
    {
        if (_refreshTimer is null || _dashboard is null) return;

        var interval = _dashboard.RefreshInterval;
        // Zero is "Manual only": no periodic refresh at all.
        if (interval <= TimeSpan.Zero)
        {
            _refreshTimer.Stop();
            return;
        }

        _refreshTimer.Interval = interval;
        _refreshTimer.Start();
    }

    /// <summary>
    /// "System" is handled by the toolkit's own variant, which follows the macOS appearance setting
    /// live. The 1.x port learned this the hard way: a hand-rolled "does the system prefer dark"
    /// check answered false everywhere but Windows, so the system theme stuck on light.
    /// </summary>
    private void ApplyTheme(string theme) =>
        RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
}
