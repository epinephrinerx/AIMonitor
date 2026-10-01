using System.Windows;
using System.Windows.Input;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf;

public partial class WidgetWindow : Window
{
    private readonly SettingsSession? _session;

    public WidgetWindow()
    {
        InitializeComponent();
    }

    public WidgetWindow(WidgetViewModel viewModel, SettingsSession session)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            var displays = GetDisplayAreas();
            var topologyId = WindowGeometryManager.GenerateTopologyId(displays);

            if (WindowGeometryManager.TryRestorePlacement(_session.Current, topologyId, WindowGeometryManager.WidgetMode, displays, out var placement))
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

    public Task SaveGeometryAsync()
    {
        if (_session is null) return Task.CompletedTask;

        try
        {
            var displays = GetDisplayAreas();
            var placement = new WindowPlacement(Left, Top, ActualWidth, ActualHeight, false);
            return WindowPlacementRecorder.RecordAsync(_session, WindowGeometryManager.WidgetMode, placement, displays, DateTimeOffset.UtcNow);
        }
        catch
        {
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
