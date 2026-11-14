using AIMonitor.Application.OpenCode;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Reports;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Version;
using AIMonitor.Presentation.Avalonia.MenuBar;
using AIMonitor.Presentation.Avalonia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
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
    private IOpenCodeAuthReader? _openCodeAuthReader;
    private OpenCodeAuthStatus? _openCodeStatus;
    private Task? _openCodeCheck;
    private ConnectionsViewModel? _connectionsPage;
    private HttpClient? _updateHttpClient;
    private MenuBarHost? _menuBar;
    private MainWindow? _window;
    private WidgetWindow? _widgetWindow;
    // One of each at a time, as on Windows: asking again brings the open one forward.
    private SettingsWindow? _settingsWindow;
    private ConnectionsWindow? _connectionsWindow;
    private AboutWindow? _aboutWindow;
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
        _openCodeAuthReader = startup.OpenCodeAuthReader;
        ApplyTheme(_settings.Current.Theme);

        _dashboard = new DashboardViewModel(startup.RefreshCoordinator, _settings, startup.AgentUsage);
        _dashboard.ThemeChanged += ApplyTheme;
        _dashboard.ExitRequested += Quit;
        _dashboard.Refreshed += OnRefreshed;
        _dashboard.AgentUsageUpdated += (lastGood, lastError) => _connectionsPage?.UpdateOtherAgents(lastGood, lastError);
        _dashboard.OpenSettingsRequested += OpenSettings;
        _dashboard.OpenConnectionsRequested += OpenConnections;
        _dashboard.OpenAboutRequested += OpenAbout;
        _dashboard.OpenUsageLogRequested += OpenUsageLog;
        _dashboard.PrintUsageLogRequested += PrintUsageLog;
        _dashboard.SwitchToWidgetRequested += ShowWidget;

        _widget = new WidgetViewModel([.. _dashboard.Providers]);
        _widget.ExpandRequested += ShowDashboard;
        _widget.RefreshRequested += () => _ = _dashboard.RefreshAsync(manual: true);
        _widget.QuitRequested += Quit;
        // With rotation off and nothing pinned, the widget follows the dashboard's tab.
        _widget.ActiveProviderSource = () => _dashboard.SelectedProvider.ProviderId;
        _dashboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DashboardViewModel.SelectedProvider)) _widget.OnActiveProviderChanged();
        };

        _window = new MainWindow { DataContext = _dashboard, Session = _settings };
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
        CheckOpenCode();
        _ = CheckForUpdatesQuietlyAsync();

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
        ApplyTheme(settings.Theme);
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
                _menuBar.OpenSettingsRequested += OpenSettings;
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
        _connectionsPage?.Update(_dashboard.LatestSnapshots);
    }

    /// <summary>
    /// The menu macOS shows along the top of the screen. Separate from the menu bar extra: this one
    /// belongs to the app while it is in front, that one is there whether it is or not.
    /// </summary>
    private NativeMenu BuildApplicationMenu()
    {
        // Shortcuts: see AppShortcuts (PAR-032).
        var file = new NativeMenuItem("File") { Menu = new NativeMenu() };
        file.Menu!.Add(Item("Refresh now", () => _ = _dashboard?.RefreshAsync(manual: true), AppShortcuts.Refresh));
        file.Menu.Add(Item("Sign-in / Connections…", OpenConnections, AppShortcuts.Connections));
        file.Menu.Add(new NativeMenuItemSeparator());
        file.Menu.Add(Item("Usage log…", OpenUsageLog, AppShortcuts.UsageLog));
        file.Menu.Add(Item("Print usage log…", PrintUsageLog, AppShortcuts.PrintUsageLog));
        file.Menu.Add(new NativeMenuItemSeparator());
        file.Menu.Add(Item("Settings…", OpenSettings, AppShortcuts.Settings));

        var window = new NativeMenuItem("Window") { Menu = new NativeMenu() };
        window.Menu!.Add(Item("Dashboard", ShowDashboard, AppShortcuts.Dashboard));
        window.Menu.Add(Item("Widget", ShowWidget, AppShortcuts.Widget));

        var help = new NativeMenuItem("Help") { Menu = new NativeMenu() };
        help.Menu!.Add(Item("About AI Usage Monitor", OpenAbout, AppShortcuts.About));
        help.Menu.Add(Item("Read me", () => OpenAbout(AboutPage.Readme)));
        help.Menu.Add(Item("Licence", () => OpenAbout(AboutPage.License)));
        help.Menu.Add(Item("Third-party notices", () => OpenAbout(AboutPage.Notices)));
        help.Menu.Add(Item("Developer", () => OpenAbout(AboutPage.Developer)));
        help.Menu.Add(new NativeMenuItemSeparator());
        help.Menu.Add(Item("Check for updates…", () => OpenAbout(AboutPage.About, checkNow: true)));

        return [file, window, help];
    }

    private static NativeMenuItem Item(string header, Action action, string? gesture = null)
    {
        var item = new NativeMenuItem(header);
        if (gesture is not null) item.Gesture = KeyGesture.Parse(gesture);
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenSettings()
    {
        if (_settings is null) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        // The live preview (PAR-027): theme, widget look, window size and budgets show at once and
        // are put back by Cancel. Only the appearance is touched; nothing is saved.
        var resizer = new DashboardPreviewResizer(() => _window);
        var lastTheme = _settings.Current.Theme;
        var lastSize = (_settings.Current.DashboardWidth, _settings.Current.DashboardHeight);
        void Preview(AppSettings preview)
        {
            if (!string.Equals(preview.Theme, lastTheme, StringComparison.Ordinal))
            {
                lastTheme = preview.Theme;
                ApplyTheme(preview.Theme);
            }

            _widget?.ApplySettings(preview);
            if ((preview.DashboardWidth, preview.DashboardHeight) != lastSize)
            {
                lastSize = (preview.DashboardWidth, preview.DashboardHeight);
                resizer.Resize(preview.DashboardWidth, preview.DashboardHeight);
            }

            _dashboard?.ApplyAgentBudgets(preview.AgentBudgets);
        }

        _settingsWindow = new SettingsWindow { DataContext = new SettingsViewModel(_settings, KnownAgentNames(), Preview) };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        Show(_settingsWindow);
    }

    /// <summary>Every agent ccusage has reported, so Settings can offer each one a budget row.</summary>
    private IReadOnlyList<string>? KnownAgentNames()
    {
        if (_dashboard?.LatestAgentUsage is not { } report) return null;

        var agents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in report.Daily.SelectMany(day => day.Agents)) agents.Add(agent.Agent);
        foreach (var agent in report.Monthly.SelectMany(month => month.Agents)) agents.Add(agent.Agent);
        foreach (var session in report.Sessions) agents.Add(session.Usage.Agent);
        return agents.Count == 0 ? null : [.. agents];
    }

    private void OpenConnections()
    {
        if (_settings is null || _connections is null || _dashboard is null) return;
        if (_connectionsWindow is not null)
        {
            _connectionsWindow.Activate();
            return;
        }

        _connectionsPage = new ConnectionsViewModel(_settings, _connections);
        // "Check again" on the OpenCode card asks OpenCode only; the services are refreshed for the rest.
        _connectionsPage.RedetectRequested += providerId =>
        {
            if (providerId is null or "opencode") CheckOpenCode();
            if (providerId != "opencode") _ = _dashboard.RefreshAsync();
        };
        _connectionsPage.Update(_dashboard.LatestSnapshots);
        _connectionsPage.UpdateOpenCode(_openCodeStatus);
        _connectionsPage.UpdateOtherAgents(_dashboard.LatestAgentUsage, null);

        _connectionsWindow = new ConnectionsWindow { DataContext = _connectionsPage };
        _connectionsWindow.Closed += (_, _) =>
        {
            _connectionsPage = null;
            _connectionsWindow = null;
        };
        Show(_connectionsWindow);
    }

    /// <summary>
    /// Reads OpenCode's sign-ins off the UI thread, since it may run the OpenCode CLI. One check at a
    /// time; the result is kept so the connections page shows it as soon as it opens.
    /// </summary>
    private void CheckOpenCode()
    {
        if (_openCodeAuthReader is not { } reader || _openCodeCheck is { IsCompleted: false }) return;

        _openCodeCheck = Task.Run(async () =>
        {
            OpenCodeAuthStatus status;
            try
            {
                status = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                status = new OpenCodeAuthStatus(true, "", [], false,
                    "Could not read OpenCode sign-ins. Run `opencode auth list` in a terminal to check.");
            }

            Dispatcher.UIThread.Post(() =>
            {
                _openCodeStatus = status;
                _connectionsPage?.UpdateOpenCode(status);
            });
        });
    }

    private void OpenAbout() => OpenAbout(AboutPage.About);

    /// <summary>Opens About on <paramref name="page"/>; one About window at a time.</summary>
    private void OpenAbout(AboutPage page, bool checkNow = false, AboutViewModel? model = null)
    {
        if (_aboutWindow is { DataContext: AboutViewModel open })
        {
            open.SelectedPage = page;
            if (checkNow) _ = open.CheckUpdatesAsync();
            _aboutWindow.Activate();
            return;
        }

        model ??= new AboutViewModel(CreateVersionChecker());
        model.SelectedPage = page;
        _aboutWindow = new AboutWindow { DataContext = model };
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        Show(_aboutWindow);
        if (checkNow) _ = model.CheckUpdatesAsync();
    }

    private GitHubVersionChecker CreateVersionChecker()
    {
        _updateHttpClient ??= new HttpClient();
        return new GitHubVersionChecker(_updateHttpClient, branch: AboutViewModel.ReleaseBranch);
    }

    /// <summary>
    /// Asked on every launch, as on Windows, and quiet unless there is something newer: then About
    /// opens on the news with the button that goes to the release. There is no installer to run on
    /// the Mac (R-023), so this is where the Windows "install now?" question ends up.
    /// </summary>
    private async Task CheckForUpdatesQuietlyAsync()
    {
        var model = new AboutViewModel(CreateVersionChecker());
        try
        {
            await model.CheckUpdatesAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return;
        }

        if (model.IsUpdateAvailable && _aboutWindow is null)
        {
            OpenAbout(AboutPage.About, model: model);
        }
    }

    private void OpenUsageLog() => OpenUsageLog(print: false);

    /// <summary>⌘P: the log, with its print page on the way, as Ctrl+P opens the log to print on Windows.</summary>
    private void PrintUsageLog() => OpenUsageLog(print: true);

    private void OpenUsageLog(bool print)
    {
        if (_dashboard is null) return;

        var report = UsageReportGenerator.Build(
            _dashboard.Providers.Select(provider => (provider.ProviderId, provider.Title)),
            _dashboard.LatestSnapshots,
            _dashboard.ChartRangeDays,
            _dashboard.ChartMetric);

        var model = new UsageLogViewModel(report);
        Show(new UsageLogWindow { DataContext = model });
        if (print) model.PrintCommand.Execute(null);
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

    /// <summary>Dashboard mode: the widget goes away and the dashboard comes back, as on Windows.</summary>
    private void ShowDashboard()
    {
        if (_window is null) return;
        if (_widgetWindow?.IsVisible == true)
        {
            _widgetWindow.SavePlacement();
            _widgetWindow.Hide();
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Widget mode: the dashboard is put away while the widget is up (PAR-019).</summary>
    private void ShowWidget()
    {
        if (_widget is null) return;
        _widgetWindow ??= new WidgetWindow { DataContext = _widget, Session = _settings };
        if (_window?.IsVisible == true)
        {
            _window.SavePlacement();
            _window.Hide();
        }

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
        NotifyParkedInMenuBar();
    }

    /// <summary>
    /// The window was just put away into the menu bar. The first time ever, say so, so closing the
    /// window does not look like the app quit (the Windows tray balloon, 1.3.3 <c>mark_once("trayHint")</c>).
    /// </summary>
    private void NotifyParkedInMenuBar()
    {
        if (_settings is null || _settings.Current.TrayHintShown) return;

        _ = _settings.UpdateAsync(s => s with { TrayHintShown = true });
        var notice = new NoticeWindow(
            "Still watching",
            "AI Usage Monitor is still running in the menu bar. Click its icon to reopen the dashboard, or choose Quit AI Usage Monitor there to stop it.");
        notice.Show();
        notice.Activate();
    }

    private void Quit()
    {
        // Shutting down does not ask the windows whether they may close, so their spots are
        // recorded here, before the settings are flushed on the way out.
        _window?.SavePlacement();
        _widgetWindow?.SavePlacement();

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
