using System.Windows;
using System.Windows.Input;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

public partial class WidgetWindow : Window
{
    private readonly ISettingsStore? _settingsStore;
    private AppSettings? _currentSettings;

    public WidgetWindow()
    {
        InitializeComponent();
    }

    public WidgetWindow(WidgetViewModel viewModel, ISettingsStore settingsStore, AppSettings settings)
        : this()
    {
        DataContext = viewModel;
        _settingsStore = settingsStore;
        _currentSettings = settings;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_currentSettings is not null)
        {
            var displays = GetDisplayAreas();
            var topologyId = WindowGeometryManager.GenerateTopologyId(displays);

            if (WindowGeometryManager.TryRestorePlacement(_currentSettings, topologyId, WindowGeometryManager.WidgetMode, displays, out var placement))
            {
                Left = placement.Left;
                Top = placement.Top;
            }
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveGeometry();
    }

    private void SaveGeometry()
    {
        if (_settingsStore is null || _currentSettings is null) return;

        try
        {
            var displays = GetDisplayAreas();
            var topologyId = WindowGeometryManager.GenerateTopologyId(displays);
            var placement = new WindowPlacement(Left, Top, ActualWidth, ActualHeight, false);

            var updated = WindowGeometryManager.SavePlacement(_currentSettings, topologyId, WindowGeometryManager.WidgetMode, placement, DateTimeOffset.UtcNow);
            _currentSettings = updated;
            _ = _settingsStore.SaveAsync(updated);
        }
        catch
        {
        }
    }

    private static IReadOnlyList<DisplayArea> GetDisplayAreas()
    {
        return System.Windows.Forms.Screen.AllScreens
            .Select(s => new DisplayArea(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height))
            .ToList();
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            (System.Windows.Application.Current as App)?.SwitchToDashboardMode();
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnOpenDashboardClick(object sender, RoutedEventArgs e)
    {
        (System.Windows.Application.Current as App)?.SwitchToDashboardMode();
    }
}
