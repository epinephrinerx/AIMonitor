using AIMonitor.Application.Settings;
using AIMonitor.Application.Windows;
using Avalonia;
using Avalonia.Controls;

namespace AIMonitor.Presentation.Avalonia;

/// <summary>
/// Remembers where the dashboard and the widget were, per arrangement of displays, through the same
/// <see cref="WindowGeometryManager"/> rules the Windows build uses (PAR-021): the last eight
/// layouts are kept, and a remembered spot is fitted back onto a screen that can still be seen.
///
/// Everything is kept in device-independent units — the window's position and the screens' working
/// areas are both divided by the same desktop scaling — so the numbers mean the same thing as the
/// window's own Width and Height.
/// </summary>
internal static class WindowGeometry
{
    public static IReadOnlyList<DisplayArea> Displays(Window window)
    {
        var scale = Scale(window);
        return [.. window.Screens.All.Select(screen => new DisplayArea(
            screen.WorkingArea.X / scale,
            screen.WorkingArea.Y / scale,
            screen.WorkingArea.Width / scale,
            screen.WorkingArea.Height / scale,
            screen.Scaling))];
    }

    /// <summary>Puts the window where it was last time on this arrangement of displays.</summary>
    /// <param name="clamp">Holds the remembered size to what the window allows, for the widget.</param>
    public static bool TryRestore(
        Window window,
        SettingsSession session,
        string mode,
        Func<double, double, (double Width, double Height)>? clamp = null)
    {
        var displays = Displays(window);
        if (displays.Count == 0) return false;

        var topologyId = WindowGeometryManager.GenerateTopologyId(displays);
        if (!WindowGeometryManager.TryRestorePlacement(session.Current, topologyId, mode, displays, out var placement))
        {
            return false;
        }

        var (width, height) = clamp?.Invoke(placement.Width, placement.Height) ?? (placement.Width, placement.Height);
        var scale = Scale(window);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint((int)Math.Round(placement.Left * scale), (int)Math.Round(placement.Top * scale));
        window.Width = width;
        window.Height = height;
        if (placement.IsMaximized) window.WindowState = WindowState.Maximized;
        return true;
    }

    /// <summary>Records where the window is now. Failures are swallowed: a lost position is not worth an error.</summary>
    public static async Task SaveAsync(Window window, SettingsSession session, string mode)
    {
        try
        {
            var displays = Displays(window);
            if (displays.Count == 0) return;

            var scale = Scale(window);
            var width = window.Bounds.Width > 0 ? window.Bounds.Width : window.Width;
            var height = window.Bounds.Height > 0 ? window.Bounds.Height : window.Height;
            var placement = new WindowPlacement(
                window.Position.X / scale,
                window.Position.Y / scale,
                width,
                height,
                window.WindowState == WindowState.Maximized);
            if (!placement.IsValid) return;

            await WindowPlacementRecorder
                .RecordAsync(session, mode, placement, displays, DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ObjectDisposedException)
        {
            // Non-fatal, as on Windows.
        }
    }

    private static double Scale(Window window) => window.DesktopScaling > 0 ? window.DesktopScaling : 1;
}
