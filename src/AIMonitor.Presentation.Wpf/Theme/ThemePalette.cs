using AIMonitor.Domain;
using Color = System.Windows.Media.Color;

namespace AIMonitor.Presentation.Wpf.Theme;

/// <summary>
/// Colour tokens for one resolved scheme. Values mirror 1.3.3 <c>theme.py</c> (LIGHT / DARK / STATUS)
/// so the two apps read the same; no WPF resources are touched here, which keeps it unit-testable.
/// </summary>
public sealed record ThemePalette(
    bool IsDark,
    Color Plane,
    Color Surface,
    Color SurfaceHover,
    Color Ink,
    Color InkSecondary,
    Color InkMuted,
    Color Grid,
    Color Baseline,
    Color Border,
    Color Track,
    Color Accent,
    Color AccentHover,
    IReadOnlyList<Color> Categorical)
{
    /// <summary>Status colours are fixed and never themed (1.3.3 <c>STATUS</c>).</summary>
    public static Color StatusGood { get; } = Color.FromRgb(0x0c, 0xa3, 0x0c);
    public static Color StatusWarning { get; } = Color.FromRgb(0xfa, 0xb2, 0x19);
    public static Color StatusSerious { get; } = Color.FromRgb(0xec, 0x83, 0x5a);
    public static Color StatusCritical { get; } = Color.FromRgb(0xd0, 0x3b, 0x3b);

    public static ThemePalette Light { get; } = new(
        IsDark: false,
        Plane: Rgb("#f9f9f7"),
        Surface: Rgb("#fcfcfb"),
        SurfaceHover: Rgb("#f1f1ee"),
        Ink: Rgb("#0b0b0b"),
        InkSecondary: Rgb("#52514e"),
        InkMuted: Rgb("#898781"),
        Grid: Rgb("#e1e0d9"),
        Baseline: Rgb("#c3c2b7"),
        Border: Color.FromArgb(0x1a, 0x0b, 0x0b, 0x0b),
        Track: Rgb("#dcdbd4"),
        Accent: Rgb("#2a78d6"),
        AccentHover: Rgb("#1f65bb"),
        Categorical: new[]
        {
            Rgb("#2a78d6"), Rgb("#eb6834"), Rgb("#1baf7a"), Rgb("#eda100"),
            Rgb("#e87ba4"), Rgb("#008300"), Rgb("#4a3aa7"), Rgb("#e34948"),
        });

    public static ThemePalette Dark { get; } = new(
        IsDark: true,
        Plane: Rgb("#0d0d0d"),
        Surface: Rgb("#1a1a19"),
        SurfaceHover: Rgb("#242422"),
        Ink: Rgb("#ffffff"),
        InkSecondary: Rgb("#c3c2b7"),
        InkMuted: Rgb("#898781"),
        Grid: Rgb("#2c2c2a"),
        Baseline: Rgb("#383835"),
        Border: Color.FromArgb(0x1a, 0xff, 0xff, 0xff),
        Track: Rgb("#3a3a37"),
        Accent: Rgb("#3987e5"),
        AccentHover: Rgb("#5b9ee9"),
        Categorical: new[]
        {
            Rgb("#3987e5"), Rgb("#d95926"), Rgb("#199e70"), Rgb("#c98500"),
            Rgb("#d55181"), Rgb("#008300"), Rgb("#9085e9"), Rgb("#e66767"),
        });

    public static ThemePalette For(bool isDark) => isDark ? Dark : Light;

    /// <summary>Traffic-light fill for a severity: green, yellow, orange ("serious"), red.</summary>
    public static Color StatusFor(Severity severity) => severity switch
    {
        Severity.High => StatusWarning,
        Severity.VeryHigh => StatusSerious,
        Severity.Critical => StatusCritical,
        _ => StatusGood,
    };

    /// <summary>Categorical hue for slot <paramref name="index"/>, folded to the last slot past the end.</summary>
    public Color Series(int index) => Categorical[Math.Clamp(index, 0, Categorical.Count - 1)];

    private static Color Rgb(string hex) =>
        Color.FromRgb(
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
}
