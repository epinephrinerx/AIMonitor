using System.Globalization;
using AIMonitor.Domain;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AIMonitor.Presentation.Avalonia.Controls;

/// <summary>
/// The dial the dashboard reads quota from: a track arc with the used portion drawn over it in the
/// severity colour, the percentage in the middle, and the window's name underneath.
///
/// Drawn rather than composed from shapes, for the same reason the Windows build draws it
/// (ADR-0001): it scales to any size and any display density without a pile of bitmaps, and it
/// follows the theme because every brush is looked up as it draws.
///
/// The geometry matches the Windows gauge exactly — a 260° sweep starting at 140° — so the same
/// reading looks the same on both platforms.
/// </summary>
public sealed class GaugeControl : Control
{
    /// <summary>Leaves the bottom of the dial open, which is what makes it read as a gauge.</summary>
    private const double StartAngleDegrees = 140.0;
    private const double SweepDegrees = 260.0;

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<GaugeControl, double>(nameof(Value));

    public static readonly StyledProperty<string> ValueTextProperty =
        AvaloniaProperty.Register<GaugeControl, string>(nameof(ValueText), "0%");

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<GaugeControl, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<GaugeControl, string>(nameof(Subtitle), string.Empty);

    public static readonly StyledProperty<Severity> SeverityProperty =
        AvaloniaProperty.Register<GaugeControl, Severity>(nameof(Severity));

    public static readonly StyledProperty<bool> HasValueProperty =
        AvaloniaProperty.Register<GaugeControl, bool>(nameof(HasValue), true);

    static GaugeControl() =>
        AffectsRender<GaugeControl>(
            ValueProperty, ValueTextProperty, TitleProperty, SubtitleProperty, SeverityProperty, HasValueProperty);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string ValueText
    {
        get => GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public Severity Severity
    {
        get => GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    /// <summary>
    /// False when the service reports no denominator. The dial then shows an empty track and a dash
    /// rather than a confident nought, because no percentage is ever invented.
    /// </summary>
    public bool HasValue
    {
        get => GetValue(HasValueProperty);
        set => SetValue(HasValueProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 20 || height < 20) return;

        var center = new Point(width / 2.0, height * 0.48);
        var radius = Math.Min(width, height) * 0.38;
        var thickness = Math.Max(4, radius * 0.16);

        var track = Resource("GaugeTrackBrush", Brushes.Gainsboro);
        var fill = SeverityBrush(Severity);
        var ink = Resource("SystemControlForegroundBaseHighBrush", Brushes.Black);
        var quiet = Resource("SystemControlForegroundBaseMediumBrush", Brushes.Gray);

        DrawArc(context, center, radius, StartAngleDegrees, SweepDegrees, Stroke(track, thickness));

        var fraction = HasValue ? Math.Clamp(Value, 0, 100) / 100.0 : 0;
        if (fraction > 0.001)
        {
            DrawArc(context, center, radius, StartAngleDegrees, SweepDegrees * fraction, Stroke(fill, thickness));
        }

        var value = Text(HasValue ? ValueText : "—", Math.Max(12, radius * 0.46), FontWeight.Bold, ink);
        context.DrawText(value, new Point(center.X - (value.Width / 2.0), center.Y - (value.Height / 2.0)));

        var subtitleTop = center.Y + (radius * 0.75);
        if (Title.Length > 0)
        {
            var titleTop = center.Y + (radius * TitleOffset);
            var title = FitText(Title, Math.Max(10, radius * 0.24), FontWeight.SemiBold, ink, TitleSpan(radius, thickness));
            context.DrawText(title, new Point(Left(title, center.X), titleTop));
            // A title that had to wrap pushes the subtitle down instead of drawing over it.
            subtitleTop = Math.Max(subtitleTop, titleTop + title.Height + 1);
        }

        if (Subtitle.Length > 0)
        {
            var subtitle = FitText(Subtitle, Math.Max(9, radius * 0.19), FontWeight.Normal, quiet, width);
            context.DrawText(subtitle, new Point(Left(subtitle, center.X), subtitleTop));
        }
    }

    /// <summary>How far below the centre the title starts, as a fraction of the radius.</summary>
    internal const double TitleOffset = 0.45;

    /// <summary>Smallest a label is shrunk to, as a fraction of its usual size, before it wraps.</summary>
    internal const double MinimumScale = 0.8;

    /// <summary>
    /// The width between the inner edges of the ring at the height the title sits at. Anything
    /// wider runs across the arc, which is what a long name such as "Gemini 4 Argon · Weekly" did.
    /// </summary>
    internal static double TitleSpan(double radius, double thickness)
    {
        var halfChord = radius * Math.Sqrt(1 - (TitleOffset * TitleOffset));
        return Math.Max(0, (2 * (halfChord - (thickness / 2.0))) - 4);
    }

    /// <summary>
    /// The size a label is drawn at so it fits the room it has: its usual size when it fits, shrunk
    /// in proportion when it does not, but never below <see cref="MinimumScale"/> of the usual size
    /// — past that it wraps onto a second line instead of turning unreadable.
    /// </summary>
    internal static double FittedSize(double size, double naturalWidth, double available)
    {
        if (naturalWidth <= available || naturalWidth <= 0) return size;
        return Math.Max(size * MinimumScale, size * available / naturalWidth);
    }

    private static FormattedText FitText(string text, double size, FontWeight weight, IBrush brush, double available)
    {
        var natural = Text(text, size, weight, brush);
        var fitted = FittedSize(size, natural.Width, available);
        if (fitted >= size) return natural;

        var shrunk = Text(text, fitted, weight, brush);
        if (shrunk.Width <= available) return shrunk;

        // Still too wide at the smallest size: wrap at a word, and only trim what a second line
        // cannot hold.
        shrunk.MaxTextWidth = available;
        shrunk.MaxLineCount = 2;
        shrunk.Trimming = TextTrimming.CharacterEllipsis;
        shrunk.TextAlignment = TextAlignment.Center;
        return shrunk;
    }

    /// <summary>
    /// Where a label starts so it is centred. A wrapped label centres its lines inside its own
    /// maximum width, so it is that box which is centred rather than the widest line.
    /// </summary>
    private static double Left(FormattedText text, double centerX) =>
        centerX - ((text.TextAlignment == TextAlignment.Center ? text.MaxTextWidth : text.Width) / 2.0);

    /// <summary>Round caps, so the arc ends read as a dial rather than as a cut pipe.</summary>
    private static Pen Stroke(IBrush brush, double thickness) =>
        new(brush, thickness, lineCap: PenLineCap.Round);

    private static void DrawArc(
        DrawingContext context, Point center, double radius, double startDegrees, double sweepDegrees, IPen pen)
    {
        if (sweepDegrees <= 0) return;

        var start = Polar(center, radius, startDegrees);
        var end = Polar(center, radius, startDegrees + sweepDegrees);

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(start, isFilled: false);
            sink.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: sweepDegrees > 180.0,
                sweepDirection: SweepDirection.Clockwise);
            sink.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static Point Polar(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }

    private static FormattedText Text(string text, double size, FontWeight weight, IBrush brush) =>
        new(text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            // No font is named: macOS has no Segoe UI, and naming a font that is not installed makes
            // the toolkit substitute one glyph run at a time.
            new Typeface(FontFamily.Default, FontStyle.Normal, weight),
            size,
            brush);

    private static IBrush SeverityBrush(Severity severity) => Resource(
        severity switch
        {
            Severity.High => "SeverityHighBrush",
            Severity.VeryHigh => "SeverityVeryHighBrush",
            Severity.Critical => "SeverityCriticalBrush",
            _ => "SeverityNormalBrush",
        },
        Brushes.Teal);

    /// <summary>
    /// Resolves a brush the application defines, looked up on every draw so a theme change is
    /// picked up without the control subscribing to anything.
    /// </summary>
    private static IBrush Resource(string key, IBrush fallback)
    {
        // Fully qualified: AIMonitor.Application is a namespace of this solution, so a plain
        // "Application" here binds to it rather than to the toolkit's class.
        var application = global::Avalonia.Application.Current;
        if (application is not null
            && application.TryGetResource(key, application.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }
}
