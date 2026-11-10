using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Theme;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace AIMonitor.Presentation.Wpf.Controls;

/// <summary>
/// Per-day usage stacked by model, custom-drawn (ADR-0001). Follows 1.3.3 <c>StackedColumnChart</c>:
/// bars capped at 24 px with a 4 px rounded data end, hairline gridlines on "nice" ticks, a 2 px surface
/// gap between touching segments, value labels on the caps, thinned day labels, a legend when two or more
/// series exist, and a tooltip with the per-model breakdown.
/// </summary>
public sealed class StackedColumnChartControl : FrameworkElement
{
    private const double BarMaxThickness = 24.0;
    private const double DataEndRadius = 4.0;
    private const double SurfaceGap = 2.0;
    private const int GridDivisions = 4;
    private const string OtherSeries = "Other";
    private const string CostSeries = "Cost ($)";
    private const double CostDotRadius = 2.5;
    private const double CostUnpricedDotRadius = 3.0;

    public static readonly DependencyProperty BucketsProperty =
        DependencyProperty.Register(nameof(Buckets), typeof(IReadOnlyList<UsageHistoryBucket>), typeof(StackedColumnChartControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeriesProperty =
        DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<string>), typeof(StackedColumnChartControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MetricProperty =
        DependencyProperty.Register(nameof(Metric), typeof(string), typeof(StackedColumnChartControl),
            new FrameworkPropertyMetadata("Total tokens", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CostsProperty =
        DependencyProperty.Register(nameof(Costs), typeof(IReadOnlyList<DailyCost>), typeof(StackedColumnChartControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly List<(double Start, double End)> _bands = [];
    private int _hoverIndex = -1;

    public StackedColumnChartControl()
    {
        Loaded += (_, _) => ThemeManager.Instance.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
    }

    public IReadOnlyList<UsageHistoryBucket>? Buckets
    {
        get => (IReadOnlyList<UsageHistoryBucket>?)GetValue(BucketsProperty);
        set => SetValue(BucketsProperty, value);
    }

    public IReadOnlyList<string>? Series
    {
        get => (IReadOnlyList<string>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string Metric
    {
        get => (string)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    /// <summary>Optional per-day priced costs; null/empty leaves the chart exactly as before.</summary>
    public IReadOnlyList<DailyCost>? Costs
    {
        get => (IReadOnlyList<DailyCost>?)GetValue(CostsProperty);
        set => SetValue(CostsProperty, value);
    }

    /// <summary>
    /// True when the cost line and its right-hand axis should be drawn: there is at least one cost
    /// entry and at least one of them has a positive priced value (F2).
    /// </summary>
    internal static bool ShowsCostAxis(IReadOnlyList<DailyCost>? costs) =>
        costs is { Count: > 0 } && costs.Any(cost => cost.PricedUsd > 0);

    /// <summary>
    /// Legend entries for the chart: the model series unchanged when there is no cost axis,
    /// otherwise the model series followed by "Cost ($)" (F2).
    /// </summary>
    internal static IReadOnlyList<string> LegendEntries(
        IReadOnlyList<string> series,
        IReadOnlyList<DailyCost>? costs)
    {
        if (!ShowsCostAxis(costs))
        {
            return series;
        }

        var entries = new List<string>(series.Count + 1);
        entries.AddRange(series);
        entries.Add(CostSeries);
        return entries;
    }

    /// <summary>Colour for a series: categorical hue by position, muted ink for the "Other" spill.</summary>
    internal static Color ColourFor(ThemePalette palette, IReadOnlyList<string> series, string name)
    {
        if (name == OtherSeries)
        {
            return palette.InkMuted;
        }

        var index = -1;
        for (var i = 0; i < series.Count; i++)
        {
            if (series[i] == name)
            {
                index = i;
                break;
            }
        }

        return index < 0 ? palette.Accent : palette.Series(index);
    }

    /// <summary>How many labels to skip between drawn day labels so they never collide.</summary>
    internal static int LabelStride(double labelWidth, double bandWidth) =>
        Math.Max(1, (int)Math.Ceiling((labelWidth + 14) / Math.Max(bandWidth, 1.0)));

    private void OnThemeChanged() => Dispatcher.InvokeAsync(InvalidateVisual);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 50 || height < 50)
        {
            return;
        }

        // Transparent fill so the whole area takes part in hit testing (tooltip).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var p = ThemePalette.For(ThemeManager.Instance.IsDark);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var buckets = Buckets ?? [];
        var series = Series ?? [];
        var costs = Costs;
        var showCosts = ShowsCostAxis(costs);
        _bands.Clear();

        if (buckets.Count == 0 || (series.Count == 0 && !showCosts))
        {
            var empty = Text("No usage recorded in this range", 13, p.InkMuted, dpi, FontWeights.Normal);
            dc.DrawText(empty, new Point((width - empty.Width) / 2.0, (height - empty.Height) / 2.0));
            return;
        }

        var legendEntries = LegendEntries(series, costs);
        var legendHeight = legendEntries.Count >= 2 ? DrawLegend(dc, p, series, legendEntries, width, dpi) : 0.0;

        var peak = buckets.Max(b => b.Total);
        var axisMax = ChartFormat.NiceAxisMax(peak, GridDivisions);
        var tickTexts = Enumerable.Range(0, GridDivisions + 1)
            .Select(i => ChartFormat.AxisTick(axisMax * i / GridDivisions, Metric))
            .ToList();

        const double fontSize = 11;
        var lineHeight = Text("0", fontSize, p.InkMuted, dpi, FontWeights.Normal).Height;
        var left = tickTexts.Max(t => Text(t, fontSize, p.InkMuted, dpi, FontWeights.Normal).Width) + 12;

        double right = 6.0;
        double costAxisMax = 0;
        List<string>? costTickTexts = null;
        if (showCosts)
        {
            costAxisMax = ChartFormat.NiceAxisMax(costs!.Max(cost => cost.PricedUsd), GridDivisions);
            costTickTexts = Enumerable.Range(0, GridDivisions + 1)
                .Select(i => ChartFormat.Money(costAxisMax * i / GridDivisions))
                .ToList();
            var widest = costTickTexts.Max(t => Text(t, fontSize, p.InkMuted, dpi, FontWeights.Normal).Width);
            right = Math.Max(right, widest + 10.0);
        }

        var top = legendHeight + 8.0;
        var bottom = lineHeight + 10.0;
        var plot = new Rect(left, top, Math.Max(1.0, width - left - right), Math.Max(1.0, height - top - bottom));

        var gridPen = new Pen(Solid(p.Grid), 1);
        var basePen = new Pen(Solid(p.Baseline), 1);
        for (var i = 0; i <= GridDivisions; i++)
        {
            var y = plot.Bottom - plot.Height * i / GridDivisions;
            dc.DrawLine(i == 0 ? basePen : gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var tick = Text(tickTexts[i], fontSize, p.InkMuted, dpi, FontWeights.Normal);
            dc.DrawText(tick, new Point(left - 8 - tick.Width, y - tick.Height / 2));
        }

        if (costTickTexts is not null)
        {
            for (var i = 0; i <= GridDivisions; i++)
            {
                var y = plot.Bottom - plot.Height * i / GridDivisions;
                var tick = Text(costTickTexts[i], fontSize, p.InkMuted, dpi, FontWeights.Normal);
                dc.DrawText(tick, new Point(plot.Right + 8, y - tick.Height / 2));
            }
        }

        var bandWidth = plot.Width / buckets.Count;
        var thickness = Math.Max(3.0, Math.Min(BarMaxThickness, bandWidth - 4.0));

        for (var index = 0; index < buckets.Count; index++)
        {
            var bucket = buckets[index];
            var centre = plot.Left + bandWidth * (index + 0.5);
            _bands.Add((centre - bandWidth / 2, centre + bandWidth / 2));
            if (bucket.Total <= 0)
            {
                continue;
            }

            var stack = series.Where(s => bucket.PerModel.GetValueOrDefault(s) > 0).ToList();
            var cursor = plot.Bottom;
            for (var position = 0; position < stack.Count; position++)
            {
                var name = stack[position];
                var segmentHeight = plot.Height * (bucket.PerModel[name] / axisMax);
                var rect = new Rect(centre - thickness / 2, cursor - segmentHeight, thickness, segmentHeight);
                cursor -= segmentHeight;

                // A 2 px surface gap separates touching segments; the bottom one keeps its edge on the baseline.
                if (position > 0 && rect.Height > SurfaceGap)
                {
                    rect = new Rect(rect.X, rect.Y, rect.Width, rect.Height - SurfaceGap);
                }

                if (rect.Height <= 0.4)
                {
                    continue;
                }

                dc.DrawGeometry(Solid(ColourFor(p, series, name)), null,
                    BarGeometry(rect, DataEndRadius, roundedTop: position == stack.Count - 1));
            }
        }

        // Optional cost line over the bars, on its own right-hand axis (F5).
        if (showCosts && costs is not null)
        {
            var costByDay = new Dictionary<DateOnly, DailyCost>();
            foreach (var cost in costs)
            {
                costByDay[cost.Day] = cost;
            }

            var linePen = new Pen(Solid(p.Ink), 1.5);
            var unpricedPen = new Pen(Solid(p.Ink), 1.5);
            var points = new List<(Point Point, bool Unpriced)>();
            for (var index = 0; index < buckets.Count; index++)
            {
                if (!costByDay.TryGetValue(buckets[index].Day, out var cost))
                {
                    continue;
                }

                var centre = plot.Left + bandWidth * (index + 0.5);
                var y = plot.Bottom - plot.Height * (cost.PricedUsd / costAxisMax);
                points.Add((new Point(centre, y), cost.HasUnpricedModels));
            }

            if (points.Count > 1)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(points[0].Point, isFilled: false, isClosed: false);
                    for (var i = 1; i < points.Count; i++)
                    {
                        ctx.LineTo(points[i].Point, isStroked: true, isSmoothJoin: false);
                    }
                }

                geometry.Freeze();
                dc.DrawGeometry(null, linePen, geometry);
            }

            foreach (var (point, unpriced) in points)
            {
                if (unpriced)
                {
                    dc.DrawEllipse(null, unpricedPen, point, CostUnpricedDotRadius, CostUnpricedDotRadius);
                }
                else
                {
                    dc.DrawEllipse(Solid(p.Ink), null, point, CostDotRadius, CostDotRadius);
                }
            }
        }

        // Value labels on the caps - only where the text fits the band and does not touch its neighbour.
        var drawnUntil = double.MinValue;
        for (var index = 0; index < buckets.Count; index++)
        {
            var bucket = buckets[index];
            if (bucket.Total <= 0)
            {
                continue;
            }

            var label = Text(ChartFormat.MetricLabel(bucket.Total, Metric), 10, p.InkSecondary, dpi, FontWeights.SemiBold);
            if (label.Width > bandWidth - 2)
            {
                continue;
            }

            var centre = plot.Left + bandWidth * (index + 0.5);
            if (centre - label.Width / 2 < drawnUntil + 4)
            {
                continue;
            }

            var capTop = plot.Bottom - plot.Height * (bucket.Total / axisMax);
            var y = capTop - label.Height - 2;
            if (y < plot.Top)
            {
                y = capTop + 2; // no room above the cap; sit just inside it
            }

            dc.DrawText(label, new Point(centre - label.Width / 2, y));
            drawnUntil = centre + label.Width / 2;
        }

        // X labels, thinned so they never collide.
        var sample = Text(ChartFormat.DayLabel(buckets[0].Day), fontSize, p.InkMuted, dpi, FontWeights.Normal);
        var stride = LabelStride(sample.Width, bandWidth);
        for (var index = 0; index < buckets.Count; index++)
        {
            if (index % stride != 0 && index != buckets.Count - 1)
            {
                continue;
            }

            var day = Text(ChartFormat.DayLabel(buckets[index].Day), fontSize, p.InkMuted, dpi, FontWeights.Normal);
            var centre = plot.Left + bandWidth * (index + 0.5);
            dc.DrawText(day, new Point(centre - day.Width / 2, plot.Bottom + 4));
        }
    }

    private double DrawLegend(
        DrawingContext dc,
        ThemePalette p,
        IReadOnlyList<string> series,
        IReadOnlyList<string> entries,
        double width,
        double dpi)
    {
        const double swatch = 9.0;
        var x = 0.0;
        var y = 4.0;
        var rowHeight = Text("Ag", 11, p.InkSecondary, dpi, FontWeights.Normal).Height + 4;
        foreach (var name in entries)
        {
            var label = Text(name, 11, p.InkSecondary, dpi, FontWeights.Normal);
            var itemWidth = swatch + 6 + label.Width + 16;
            if (x + itemWidth > width && x > 0)
            {
                x = 0;
                y += rowHeight;
            }

            var colour = name == CostSeries ? p.Ink : ColourFor(p, series, name);
            dc.DrawRoundedRectangle(Solid(colour), null,
                new Rect(x, y + (label.Height - swatch) / 2, swatch, swatch), 2, 2);
            dc.DrawText(label, new Point(x + swatch + 6, y));
            x += itemWidth;
        }

        return y + rowHeight;
    }

    /// <summary>A bar with an optionally rounded data end and a square baseline.</summary>
    private static Geometry BarGeometry(Rect rect, double radius, bool roundedTop)
    {
        radius = Math.Min(radius, Math.Min(rect.Width / 2, rect.Height));
        if (!roundedTop || radius <= 0.5)
        {
            return new RectangleGeometry(rect);
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(rect.Left, rect.Bottom), true, true);
            ctx.LineTo(new Point(rect.Left, rect.Top + radius), true, false);
            ctx.QuadraticBezierTo(new Point(rect.Left, rect.Top), new Point(rect.Left + radius, rect.Top), true, false);
            ctx.LineTo(new Point(rect.Right - radius, rect.Top), true, false);
            ctx.QuadraticBezierTo(new Point(rect.Right, rect.Top), new Point(rect.Right, rect.Top + radius), true, false);
            ctx.LineTo(new Point(rect.Right, rect.Bottom), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static FormattedText Text(string text, double size, Color colour, double dpi, FontWeight weight) =>
        new(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(System.Windows.SystemFonts.MessageFontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            size, Solid(colour), dpi);

    private static Brush Solid(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var x = e.GetPosition(this).X;
        var index = _bands.FindIndex(b => x >= b.Start && x <= b.End);
        if (index == _hoverIndex)
        {
            return;
        }

        _hoverIndex = index;
        ToolTip = index >= 0 && Buckets is { } buckets && index < buckets.Count
            ? TooltipText(buckets[index], Series ?? [], Metric, Costs, index)
            : null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        ToolTip = null;
    }

    internal static string TooltipText(UsageHistoryBucket bucket, IReadOnlyList<string> series, string metric) =>
        TooltipText(bucket, series, metric, costs: null, index: -1);

    /// <summary>
    /// The tooltip text. When <paramref name="costs"/> has an entry for the bucket's day, a cost
    /// line is appended (F3); the entry is matched by <see cref="DailyCost.Day"/> (F4), so
    /// <paramref name="index"/> is only carried for the chart's call site.
    /// </summary>
    internal static string TooltipText(
        UsageHistoryBucket bucket,
        IReadOnlyList<string> series,
        string metric,
        IReadOnlyList<DailyCost>? costs,
        int index)
    {
        _ = index; // the cost is matched by Day, not by position (F4)
        var lines = new List<string> { ChartFormat.DayLabel(bucket.Day) };
        if (bucket.Total <= 0)
        {
            lines.Add("No usage");
        }
        else
        {
            foreach (var name in series)
            {
                var value = bucket.PerModel.GetValueOrDefault(name);
                if (value > 0)
                {
                    lines.Add($"{name}: {ChartFormat.MetricLabel(value, metric)}");
                }
            }

            lines.Add($"Total: {ChartFormat.MetricLabel(bucket.Total, metric)}");
        }

        if (costs is { Count: > 0 })
        {
            DailyCost? cost = null;
            foreach (var entry in costs)
            {
                if (entry.Day == bucket.Day)
                {
                    cost = entry;
                    break;
                }
            }

            if (cost is not null)
            {
                var line = $"Cost: {ChartFormat.Money(cost.PricedUsd)}";
                if (cost.HasUnpricedModels)
                {
                    line += " + unpriced";
                }

                lines.Add(line);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
