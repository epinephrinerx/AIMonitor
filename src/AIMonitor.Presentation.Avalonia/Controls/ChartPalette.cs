using Avalonia.Media;
using Avalonia.Styling;

namespace AIMonitor.Presentation.Avalonia.Controls;

/// <summary>
/// The ink, grid and series colours the history charts draw with, in both themes. Fixed values
/// rather than theme resources: these are the same eight categorical colours the Windows build
/// uses, so one model keeps one colour across both platforms.
/// </summary>
public sealed record ChartPalette(
    Color Ink,
    Color InkSecondary,
    Color InkMuted,
    Color Grid,
    Color Baseline,
    Color Accent,
    IReadOnlyList<Color> Categorical)
{
    public static ChartPalette Light { get; } = new(
        Ink: Rgb("#0b0b0b"),
        InkSecondary: Rgb("#52514e"),
        InkMuted: Rgb("#898781"),
        Grid: Rgb("#e1e0d9"),
        Baseline: Rgb("#c3c2b7"),
        Accent: Rgb("#2a78d6"),
        Categorical:
        [
            Rgb("#2a78d6"), Rgb("#eb6834"), Rgb("#1baf7a"), Rgb("#eda100"),
            Rgb("#e87ba4"), Rgb("#008300"), Rgb("#4a3aa7"), Rgb("#e34948"),
        ]);

    public static ChartPalette Dark { get; } = new(
        Ink: Rgb("#ffffff"),
        InkSecondary: Rgb("#c3c2b7"),
        InkMuted: Rgb("#898781"),
        Grid: Rgb("#2c2c2a"),
        Baseline: Rgb("#383835"),
        Accent: Rgb("#3987e5"),
        Categorical:
        [
            Rgb("#3987e5"), Rgb("#d95926"), Rgb("#199e70"), Rgb("#c98500"),
            Rgb("#d55181"), Rgb("#008300"), Rgb("#9085e9"), Rgb("#e66767"),
        ]);

    /// <summary>The palette for whichever theme the app is showing right now.</summary>
    public static ChartPalette Current
    {
        get
        {
            // Fully qualified: AIMonitor.Application is a namespace of this solution, so a plain
            // "Application" here binds to it rather than to the toolkit's class.
            var application = global::Avalonia.Application.Current;
            return application?.ActualThemeVariant == ThemeVariant.Dark ? Dark : Light;
        }
    }

    public Color Series(int index) => Categorical[Math.Clamp(index, 0, Categorical.Count - 1)];

    private static Color Rgb(string hex) => Color.Parse(hex);
}
