using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using AIMonitor.Presentation.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AIMonitor.Presentation.Avalonia;

public sealed partial class MainWindow : Window
{
    // Keeps "Updated 12s ago" and the memory figure true, as the Windows footer's one-second tick
    // does; it only runs while the window can be seen.
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _statusTicks;
    private bool _placed;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _statusTimer.Tick += (_, _) => TickStatus();
        Opened += (_, _) =>
        {
            _statusTimer.Start();
            RestorePlacement();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible) _statusTimer.Start(); else _statusTimer.Stop();
        };
        Closed += (_, _) => _statusTimer.Stop();
        // Hidden into the menu bar or closed, the spot is kept either way.
        Closing += (_, _) => SavePlacement();
    }

    /// <summary>Set by the app before the window opens; geometry is remembered through it.</summary>
    public SettingsSession? Session { get; set; }

    /// <summary>
    /// The first time the window opens it goes where it was last time on this arrangement of
    /// displays (PAR-021); with nothing remembered it takes the default size from Settings, unless
    /// that is "Remember last size".
    /// </summary>
    private void RestorePlacement()
    {
        if (_placed || Session is null) return;
        _placed = true;

        if (WindowGeometry.TryRestore(this, Session, WindowGeometryManager.DashboardMode)) return;

        var settings = Session.Current;
        if (settings.DashboardWidth > 0 && settings.DashboardHeight > 0)
        {
            Width = settings.DashboardWidth;
            Height = settings.DashboardHeight;
        }
    }

    public void SavePlacement()
    {
        if (Session is null || !IsVisible) return;
        _ = WindowGeometry.SaveAsync(this, Session, WindowGeometryManager.DashboardMode);
    }

    private void TickStatus()
    {
        if (DataContext is not DashboardViewModel dashboard) return;

        double? workingSet = null;
        // Reading the process is not free; every fifth tick is often enough for a memory figure.
        if (_statusTicks++ % 5 == 0)
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            workingSet = process.WorkingSet64 / (1024.0 * 1024.0);
        }

        dashboard.RefreshStatusText(DateTimeOffset.UtcNow, workingSet);
    }
}
