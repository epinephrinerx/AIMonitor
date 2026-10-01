using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIMonitor.Presentation.Wpf.Controls;

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
            new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public Brush GaugeBrush
    {
        get => (Brush)GetValue(GaugeBrushProperty);
        set => SetValue(GaugeBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(side) || side <= 0) side = 140;
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20) return;

        var center = new Point(width / 2.0, height * 0.48);
        var radius = Math.Min(width, height) * 0.38;
        var strokeThickness = Math.Max(4, radius * 0.16);

        var trackBrush = (System.Windows.Application.Current?.Resources["BorderBrush"] as Brush) ?? Brushes.LightGray;
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
            var fillPen = new Pen(GaugeBrush ?? Brushes.Teal, strokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            DrawArc(dc, center, radius, startAngle, fillSweep, fillPen);
        }

        // Draw text: Value, Title, Subtitle
        var textPrimaryBrush = (System.Windows.Application.Current?.Resources["TextPrimaryBrush"] as Brush) ?? Brushes.Black;
        var textSecondaryBrush = (System.Windows.Application.Current?.Resources["TextSecondaryBrush"] as Brush) ?? Brushes.Gray;
        var fontFamily = System.Windows.SystemFonts.MessageFontFamily;
        var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var subTypeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // Value text (e.g. 45%)
        var valFormatted = new FormattedText(
            ValueText,
            CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            Math.Max(12, radius * 0.46),
            textPrimaryBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(valFormatted, new Point(center.X - valFormatted.Width / 2.0, center.Y - valFormatted.Height / 2.0));

        // Title text (e.g. Session)
        if (!string.IsNullOrEmpty(Title))
        {
            var titleFormatted = new FormattedText(
                Title,
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                Math.Max(10, radius * 0.24),
                textPrimaryBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(titleFormatted, new Point(center.X - titleFormatted.Width / 2.0, center.Y + radius * 0.45));
        }

        // Subtitle text (e.g. Resets in 2h)
        if (!string.IsNullOrEmpty(Subtitle))
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
