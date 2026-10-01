namespace AIMonitor.Application.Windows;

/// <summary>
/// Represents the placement, bounds, and state of a window.
/// </summary>
public sealed record WindowPlacement(
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsMaximized = false)
{
    public bool IsValid =>
        double.IsFinite(Left) &&
        double.IsFinite(Top) &&
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width > 0 &&
        Height > 0;
}

/// <summary>
/// Represents the work area bounds and DPI scale factor of a display monitor.
/// </summary>
public sealed record DisplayArea(
    double Left,
    double Top,
    double Width,
    double Height,
    double DpiScale = 1.0)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    public bool Intersects(WindowPlacement placement) =>
        placement.Left < Right &&
        placement.Left + placement.Width > Left &&
        placement.Top < Bottom &&
        placement.Top + placement.Height > Top;

    public double OverlapArea(WindowPlacement placement)
    {
        var xOverlap = Math.Max(0, Math.Min(Right, placement.Left + placement.Width) - Math.Max(Left, placement.Left));
        var yOverlap = Math.Max(0, Math.Min(Bottom, placement.Top + placement.Height) - Math.Max(Top, placement.Top));
        return xOverlap * yOverlap;
    }
}
