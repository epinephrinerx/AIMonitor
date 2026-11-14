using System.Linq;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>
/// Predefined dashboard window size option conforming to PAR-027 and 1.3.3 parity.
/// </summary>
public sealed record WindowSizeOption(string DisplayName, int Width, int Height)
{
    public string Label => DisplayName;

    public static readonly WindowSizeOption Compact = new("Compact · 960 × 680", 960, 680);
    public static readonly WindowSizeOption Standard = new("Standard · 1120 × 820 · default", 1120, 820);
    public static readonly WindowSizeOption Wide = new("Wide · 1400 × 900", 1400, 900);
    public static readonly WindowSizeOption RememberLastSize = new("Remember last size", 0, 0);

    public static IReadOnlyList<WindowSizeOption> All { get; } = [Compact, Standard, Wide, RememberLastSize];

    public static WindowSizeOption FromSize(int width, int height)
    {
        return All.FirstOrDefault(opt => opt.Width == width && opt.Height == height) ?? Standard;
    }

    public static WindowSizeOption FromDimensions(int width, int height) => FromSize(width, height);

    public override string ToString() => DisplayName;
}
