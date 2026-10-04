using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Theme;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIMonitor.Presentation.Wpf.Controls;

internal readonly record struct CompactGaugeLayout(Rect Ring, Rect Title, Rect Subtitle, double Height);

/// <summary>
/// A lightweight, custom-drawn vector gauge control implemented via DrawingContext.
/// Satisfies ADR-0001: custom-drawn gauge control with native DPI scaling and theme awareness.
/// </summary>
public sealed class GaugeControl : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(GaugeControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueTextProperty =
        DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(GaugeControl),
            new FrameworkPropertyMetadata("0%", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(GaugeControl),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(GaugeControl),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GaugeBrushProperty =
        DependencyProperty.Register(nameof(GaugeBrush), typeof(Brush), typeof(GaugeControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeverityProperty =
        DependencyProperty.Register(nameof(Severity), typeof(Severity), typeof(GaugeControl),
            new FrameworkPropertyMetadata(Severity.Normal, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowSubtitleProperty =
        DependencyProperty.Register(nameof(ShowSubtitle), typeof(bool), typeof(GaugeControl),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowInlineValueProperty =
        DependencyProperty.Register(nameof(ShowInlineValue), typeof(bool), typeof(GaugeControl),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionLinesProperty =
        DependencyProperty.Register(nameof(CaptionLines), typeof(int), typeof(GaugeControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ArcSizeProperty =
        DependencyProperty.Register(nameof(ArcSize), typeof(double), typeof(GaugeControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionLineHeightProperty =
        DependencyProperty.Register(nameof(CaptionLineHeight), typeof(double), typeof(GaugeControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Explicit fill override. When null the fill follows <see cref="Severity"/> and the active theme.</summary>
    public Brush? GaugeBrush
    {
        get => (Brush?)GetValue(GaugeBrushProperty);
        set => SetValue(GaugeBrushProperty, value);
    }

    public Severity Severity
    {
        get => (Severity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    private Brush FillBrush => GaugeBrush ?? ThemeManager.SeverityBrush(Severity);

    public GaugeControl()
    {
        Loaded += (_, _) => ThemeManager.Instance.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged() => Dispatcher.InvokeAsync(InvalidateVisual);

    public bool ShowSubtitle
    {
        get => (bool)GetValue(ShowSubtitleProperty);
        set => SetValue(ShowSubtitleProperty, value);
    }

    public bool ShowInlineValue
    {
        get => (bool)GetValue(ShowInlineValueProperty);
        set => SetValue(ShowInlineValueProperty, value);
    }

    public int CaptionLines
    {
        get => (int)GetValue(CaptionLinesProperty);
        set => SetValue(CaptionLinesProperty, value);
    }

    public double ArcSize
    {
        get => (double)GetValue(ArcSizeProperty);
        set => SetValue(ArcSizeProperty, value);
    }

    public double CaptionLineHeight
    {
        get => (double)GetValue(CaptionLineHeightProperty);
        set => SetValue(CaptionLineHeightProperty, value);
    }

    private static double NonNegative(double value) =>
        double.IsFinite(value) && value > 0.0 ? value : 0.0;

    internal static CompactGaugeLayout ComputeCompactLayout(double width, double arc, int captionLines, double lineHeight)
    {
        double safeWidth = NonNegative(width);
        double safeArc = NonNegative(arc);
        int safeLines = captionLines < 0 ? 0 : captionLines;
        double safeLineHeight = NonNegative(lineHeight);

        var ring = new Rect((safeWidth / 2.0) - (safeArc / 2.0), 0.0, safeArc, safeArc);
        var title = new Rect(0.0, safeArc + 2.0, safeWidth, safeLineHeight);
        var subtitle = safeLines >= 2
            ? new Rect(0.0, safeArc + 2.0 + safeLineHeight, safeWidth, safeLineHeight)
            : Rect.Empty;
        var height = safeArc + 2.0 + (safeLines * safeLineHeight);
        return new CompactGaugeLayout(ring, title, subtitle, height);
    }

    internal static string FormatCaptionText(string title, string valueText, bool showInlineValue)
    {
        if (showInlineValue)
        {
            return title;
        }

        if (string.IsNullOrWhiteSpace(valueText))
        {
            return title;
        }

        return string.IsNullOrWhiteSpace(title) ? valueText : $"{title} {valueText}";
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (CaptionLines > 0)
        {
            double safeArc = NonNegative(ArcSize);
            double safeLineHeight = NonNegative(CaptionLineHeight);
            int safeLines = CaptionLines < 0 ? 0 : CaptionLines;

            bool isFiniteW = double.IsFinite(availableSize.Width);
            double widthForLayout = isFiniteW ? NonNegative(availableSize.Width) : safeArc;
            var layout = ComputeCompactLayout(widthForLayout, safeArc, safeLines, safeLineHeight);
            double desiredW = (isFiniteW && availableSize.Width > 0) ? availableSize.Width : safeArc;
            return new Size(NonNegative(desiredW), layout.Height);
        }

        double w = !double.IsNaN(Width) ? Width : availableSize.Width;
        double h = !double.IsNaN(Height) ? Height : availableSize.Height;
        var side = Math.Min(w, h);
        if (double.IsInfinity(side) || side <= 0) side = 140;
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (CaptionLines > 0)
        {
            RenderCompact(dc);
            return;
        }

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20) return;

        var center = new Point(width / 2.0, height * 0.48);
        var radius = Math.Min(width, height) * 0.38;
        var strokeThickness = Math.Max(4, radius * 0.16);

        var trackBrush = (System.Windows.Application.Current?.Resources["TrackBrush"] as Brush) ?? Brushes.LightGray;
        var trackPen = new Pen(trackBrush, strokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        // Gauge arc spans from 140° to 400° (260° sweep)
        const double startAngle = 140.0;
        const double totalSweep = 260.0;

        // Draw track arc
        DrawArc(dc, center, radius, startAngle, totalSweep, trackPen);

        // Draw progress arc
        var pct = Math.Clamp(Value, 0.0, 100.0) / 100.0;
        if (pct > 0.001)
        {
            var fillSweep = totalSweep * pct;
            var fillPen = new Pen(FillBrush, strokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            DrawArc(dc, center, radius, startAngle, fillSweep, fillPen);
        }

        // Draw text: Value, Title, Subtitle
        var textPrimaryBrush = (System.Windows.Application.Current?.Resources["TextPrimaryBrush"] as Brush) ?? Brushes.Black;
        var textSecondaryBrush = (System.Windows.Application.Current?.Resources["TextSecondaryBrush"] as Brush) ?? Brushes.Gray;
        var fontFamily = System.Windows.SystemFonts.MessageFontFamily;
        var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var subTypeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // Value text (e.g. 45% in center) - only if ShowInlineValue is true
        if (ShowInlineValue)
        {
            var valFormatted = new FormattedText(
                ValueText,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                Math.Max(12, radius * 0.46),
                textPrimaryBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(valFormatted, new Point(center.X - valFormatted.Width / 2.0, center.Y - valFormatted.Height / 2.0));
        }

        // Title text (e.g. Session or Session 45% when !ShowInlineValue)
        var captionText = FormatCaptionText(Title, ValueText, ShowInlineValue);
        if (!string.IsNullOrEmpty(captionText))
        {
            var titleFormatted = new FormattedText(
                captionText,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                Math.Max(10, radius * 0.24),
                textPrimaryBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(titleFormatted, new Point(center.X - titleFormatted.Width / 2.0, center.Y + radius * 0.45));
        }

        // Subtitle text (e.g. Resets in 2h) - only if ShowSubtitle is true
        if (ShowSubtitle && !string.IsNullOrEmpty(Subtitle))
        {
            var subFormatted = new FormattedText(
                Subtitle,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                subTypeface,
                Math.Max(9, radius * 0.18),
                textSecondaryBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(subFormatted, new Point(center.X - subFormatted.Width / 2.0, height - subFormatted.Height - 4));
        }
    }

    private void RenderCompact(DrawingContext dc)
    {
        double width = NonNegative(ActualWidth);
        double arc = NonNegative(ArcSize);
        if (width <= 0.0 || arc <= 0.0) return;

        int captionLines = CaptionLines < 0 ? 0 : CaptionLines;
        double lineHeight = NonNegative(CaptionLineHeight);

        var layout = ComputeCompactLayout(width, arc, captionLines, lineHeight);

        double thickness = Math.Max(4.0, arc * 0.11);
        double inset = (thickness / 2.0) + 1.0;
        var box = new Rect(
            layout.Ring.X + inset,
            layout.Ring.Y + inset,
            Math.Max(0.0, layout.Ring.Width - (2.0 * inset)),
            Math.Max(0.0, layout.Ring.Height - (2.0 * inset)));
        double radius = box.Width / 2.0;
        var center = new Point(layout.Ring.X + (layout.Ring.Width / 2.0), layout.Ring.Y + (layout.Ring.Height / 2.0));

        const double startAngle = 135.0;
        const double totalSweep = 270.0;

        var trackBrush = (System.Windows.Application.Current?.Resources["TrackBrush"] as Brush) ?? Brushes.LightGray;
        var trackPen = new Pen(trackBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        DrawArc(dc, center, radius, startAngle, totalSweep, trackPen);

        var pct = Math.Clamp(Value, 0.0, 100.0) / 100.0;
        if (pct > 0.001)
        {
            var fillSweep = totalSweep * pct;
            var fillPen = new Pen(FillBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            DrawArc(dc, center, radius, startAngle, fillSweep, fillPen);
        }

        double ppd = 1.0;
        try
        {
            ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }
        catch
        {
            // Fall back when visual is not attached to a presentation source
        }

        var textPrimaryBrush = (System.Windows.Application.Current?.Resources["TextPrimaryBrush"] as Brush) ?? Brushes.Black;
        var textSecondaryBrush = (System.Windows.Application.Current?.Resources["TextSecondaryBrush"] as Brush) ?? Brushes.Gray;
        var fontFamily = System.Windows.SystemFonts.MessageFontFamily;

        if (ShowInlineValue && !string.IsNullOrEmpty(ValueText))
        {
            var valueTypeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var valFormatted = new FormattedText(
                ValueText,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                valueTypeface,
                Math.Max(8.0, arc * 0.267),
                textPrimaryBrush,
                ppd);

            dc.DrawText(valFormatted, new Point(center.X - (valFormatted.Width / 2.0), center.Y - (valFormatted.Height / 2.0)));
        }

        var captionTypeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double maxTextWidth = Math.Max(1.0, width);

        var captionText = FormatCaptionText(Title, ValueText, ShowInlineValue);
        if (!string.IsNullOrEmpty(captionText))
        {
            var titleFormatted = new FormattedText(
                captionText,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                captionTypeface,
                10.0,
                textPrimaryBrush,
                ppd)
            {
                MaxTextWidth = maxTextWidth,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center
            };

            dc.DrawText(titleFormatted, new Point(0.0, layout.Title.Top));
        }

        if (ShowSubtitle && captionLines >= 2 && !string.IsNullOrEmpty(Subtitle))
        {
            var subFormatted = new FormattedText(
                Subtitle,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                captionTypeface,
                10.0,
                textSecondaryBrush,
                ppd)
            {
                MaxTextWidth = maxTextWidth,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center
            };

            dc.DrawText(subFormatted, new Point(0.0, layout.Subtitle.Top));
        }
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius, double startAngleDeg, double sweepAngleDeg, Pen pen)
    {
        if (sweepAngleDeg <= 0) return;

        var startRad = startAngleDeg * Math.PI / 180.0;
        var endRad = (startAngleDeg + sweepAngleDeg) * Math.PI / 180.0;

        var pStart = new Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
        var pEnd = new Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));

        var isLargeArc = sweepAngleDeg > 180.0;

        var figure = new PathFigure { StartPoint = pStart, IsClosed = false };
        figure.Segments.Add(new ArcSegment(pEnd, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        dc.DrawGeometry(null, pen, geometry);
    }
}
