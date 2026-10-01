using System.ComponentModel;
using System.Windows;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

public partial class MainWindow : Window
{
    private readonly ISettingsStore? _settingsStore;
    private AppSettings? _currentSettings;
    private bool _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel, ISettingsStore settingsStore, AppSettings settings)
        : this()
    {
        DataContext = viewModel;
        _settingsStore = settingsStore;
        _currentSettings = settings;

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
        if (_currentSettings is not null)
        {
            var displays = GetDisplayAreas();
            var topologyId = WindowGeometryManager.GenerateTopologyId(displays);

            if (WindowGeometryManager.TryRestorePlacement(_currentSettings, topologyId, WindowGeometryManager.DashboardMode, displays, out var placement))
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

        // Minimize to tray if configured (PAR-025)
        if (_currentSettings?.MinimizeToTray == true)
        {
            e.Cancel = true;
            Hide();
            SaveGeometry();
            return;
        }

        SaveGeometry();
    }

    private void SaveGeometry()
    {
        if (_settingsStore is null || _currentSettings is null) return;

        try
        {
            var displays = GetDisplayAreas();
            var topologyId = WindowGeometryManager.GenerateTopologyId(displays);
            var placement = new WindowPlacement(Left, Top, ActualWidth, ActualHeight, WindowState == WindowState.Maximized);

            var updated = WindowGeometryManager.SavePlacement(_currentSettings, topologyId, WindowGeometryManager.DashboardMode, placement, DateTimeOffset.UtcNow);
            _currentSettings = updated;
            _ = _settingsStore.SaveAsync(updated);
        }
        catch
        {
            // Non-fatal if saving geometry fails on exit
        }
    }

    private static IReadOnlyList<DisplayArea> GetDisplayAreas()
    {
        return System.Windows.Forms.Screen.AllScreens
            .Select(s => new DisplayArea(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height))
            .ToList();
    }
}