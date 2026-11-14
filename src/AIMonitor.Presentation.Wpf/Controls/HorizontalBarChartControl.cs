using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Theme;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIMonitor.Presentation.Wpf.Controls;

/// <summary>
/// Ranked breakdown with the value labelled at each bar tip, custom-drawn (ADR-0001). Follows 1.3.3
/// <c>HorizontalBarChart</c>: 30 px rows, 14 px bars rounded at the tip, at most eight rows with the
/// remainder folded into "Other". "By model" is coloured per row; "By project" uses the accent.
/// </summary>
public sealed class HorizontalBarChartControl : FrameworkElement
{
    internal const double RowHeight = 30.0;
    private const double BarThickness = 14.0;
    private const double DataEndRadius = 4.0;
    internal const int MaxRows = 8;
    private const string OtherLabel = "Other";

    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.Register(nameof(Rows), typeof(IReadOnlyList<UsageHistoryBreakdown>), typeof(HorizontalBarChartControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MetricProperty =
        DependencyProperty.Register(nameof(Metric), typeof(string), typeof(HorizontalBarChartControl),
            new FrameworkPropertyMetadata("Total tokens", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColouredProperty =
        DependencyProperty.Register(nameof(Coloured), typeof(bool), typeof(HorizontalBarChartControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public HorizontalBarChartControl()
    {
        Loaded += (_, _) => ThemeManager.Instance.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
    }

    public IReadOnlyList<UsageHistoryBreakdown>? Rows
    {
        get => (IReadOnlyList<UsageHistoryBreakdown>?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public string Metric
    {
        get => (string)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public bool Coloured
    {
        get => (bool)GetValue(ColouredProperty);
        set => SetValue(ColouredProperty, value);
    }

    /// <summary>The rows as drawn: capped at <see cref="MaxRows"/>, the tail summed into "Other".</summary>
    internal static IReadOnlyList<UsageHistoryBreakdown> Fold(IReadOnlyList<UsageHistoryBreakdown>? rows)
    {
        if (rows is null || rows.Count <= MaxRows)
        {
            return rows ?? [];
        }

        var spill = rows.Skip(MaxRows - 1).Sum(r => r.Value);
        return [.. rows.Take(MaxRows - 1), new UsageHistoryBreakdown(OtherLabel, spill)];
    }

    private void OnThemeChanged() => Dispatcher.InvokeAsync(InvalidateVisual);

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Math.Max(Fold(Rows).Count, 3);
        var width = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        return new Size(width, RowHeight * count + 8);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var p = ThemePalette.For(ThemeManager.Instance.IsDark);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var rows = Fold(Rows);
        var width = ActualWidth;
        var height = ActualHeight;

        if (rows.Count == 0)
        {
            var empty = Text("Nothing recorded yet", 12, p.InkMuted, dpi);
            dc.DrawText(empty, new Point((width - empty.Width) / 2.0, (height - empty.Height) / 2.0));
            return;
        }

        var labelWidth = Math.Min(140.0, rows.Max(r => Text(r.Label, 12, p.InkSecondary, dpi).Width) + 10.0);
        var valueTexts = rows.Select(r => ChartFormat.MetricLabel(r.Value, Metric)).ToList();
        var valueWidth = valueTexts.Max(t => Text(t, 12, p.Ink, dpi).Width) + 10;
        var trackLeft = labelWidth + 10;
        var trackWidth = Math.Max(20.0, width - trackLeft - valueWidth);
        var peak = rows.Max(r => r.Value);
        if (peak <= 0)
        {
            peak = 1.0;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var y = index * RowHeight;
            var centre = y + RowHeight / 2;

            var name = Text(row.Label, 12, p.InkSecondary, dpi);
            name.MaxTextWidth = Math.Max(1.0, labelWidth);
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(name, new Point(0, centre - name.Height / 2));

            var colour = row.Label == OtherLabel ? p.InkMuted : Coloured ? p.Series(index) : p.Accent;
            var barWidth = Math.Max(2.0, trackWidth * (row.Value / peak));
            var rect = new Rect(trackLeft, centre - BarThickness / 2, barWidth, BarThickness);
            dc.DrawGeometry(Solid(colour), null, BarGeometry(rect, DataEndRadius));

            var value = Text(valueTexts[index], 12, p.Ink, dpi);
            dc.DrawText(value, new Point(rect.Right + 6, centre - value.Height / 2));
        }
    }

    /// <summary>A horizontal bar: rounded at the tip, square at the baseline.</summary>
    private static Geometry BarGeometry(Rect rect, double radius)
    {
        radius = Math.Min(radius, Math.Min(rect.Height / 2, rect.Width));
        if (radius <= 0.5)
        {
            return new RectangleGeometry(rect);
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(rect.Left, rect.Top), true, true);
            ctx.LineTo(new Point(rect.Right - radius, rect.Top), true, false);
            ctx.QuadraticBezierTo(new Point(rect.Right, rect.Top), new Point(rect.Right, rect.Top + radius), true, false);
            ctx.LineTo(new Point(rect.Right, rect.Bottom - radius), true, false);
            ctx.QuadraticBezierTo(new Point(rect.Right, rect.Bottom), new Point(rect.Right - radius, rect.Bottom), true, false);
            ctx.LineTo(new Point(rect.Left, rect.Bottom), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static FormattedText Text(string text, double size, Color colour, double dpi) =>
        new(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(System.Windows.SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, Solid(colour), dpi);

    private static Brush Solid(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
