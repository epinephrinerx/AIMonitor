using System.ComponentModel;
using System.Windows;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

public partial class MainWindow : Window
{
    private readonly SettingsSession? _session;
    private bool _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel, SettingsSession session)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        viewModel.RequestOpenSettings += () => (System.Windows.Application.Current as App)?.OpenSettingsDialog();
        viewModel.RequestSwitchToWidget += () => (System.Windows.Application.Current as App)?.SwitchToWidgetMode();
        viewModel.RequestOpenLog += () => (System.Windows.Application.Current as App)?.OpenUsageLogDialog();
        viewModel.RequestOpenAbout += () => (System.Windows.Application.Current as App)?.OpenAboutDialog();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public void RequestExplicitExit()
    {
        _isExplicitExit = true;
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Restore geometry if available (PAR-021)
        if (_session is not null)
        {
            var displays = GetDisplayAreas();
            var topologyId = WindowGeometryManager.GenerateTopologyId(displays);

            if (WindowGeometryManager.TryRestorePlacement(_session.Current, topologyId, WindowGeometryManager.DashboardMode, displays, out var placement))
            {
                Left = placement.Left;
                Top = placement.Top;
                Width = placement.Width;
                Height = placement.Height;
                if (placement.IsMaximized) WindowState = WindowState.Maximized;
            }
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExplicitExit)
        {
            SaveGeometry();
            return;
        }

        // Minimize to tray if configured and tray is available (PAR-025)
        if (_session is not null && TrayPolicy.ShouldHideOnClose(_session.Current))
        {
            e.Cancel = true;
            Hide();
            SaveGeometry();
            return;
        }

        SaveGeometry();
    }

    public Task SaveGeometryAsync()
    {
        if (_session is null) return Task.CompletedTask;

        try
        {
            var displays = GetDisplayAreas();
            var placement = new WindowPlacement(Left, Top, ActualWidth, ActualHeight, WindowState == WindowState.Maximized);
            return WindowPlacementRecorder.RecordAsync(_session, WindowGeometryManager.DashboardMode, placement, displays, DateTimeOffset.UtcNow);
        }
        catch
        {
            // Non-fatal if saving geometry fails on exit
            return Task.CompletedTask;
        }
    }

    public void SaveGeometry()
    {
        // Fire-and-forget helper observes and catches any task exception to avoid unobserved task exceptions on exit.
        _ = FireAndForgetAsync(SaveGeometryAsync());
    }

    private static async Task FireAndForgetAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal: ignored to prevent unobserved task exceptions
        }
    }

    private static IReadOnlyList<DisplayArea> GetDisplayAreas()
    {
        return System.Windows.Forms.Screen.AllScreens
            .Select(s => new DisplayArea(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height))
            .ToList();
    }
}