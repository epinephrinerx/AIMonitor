using System.Globalization;
using AIMonitor.Domain;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AIMonitor.Presentation.Avalonia.Controls;

/// <summary>
/// Per-day usage stacked by model, custom-drawn (ADR-0001). The same chart the Windows build draws:
/// bars capped at 24px with a 4px rounded data end, hairline gridlines on "nice" ticks, a 2px gap
/// between touching segments, value labels on the caps, day labels thinned so they never collide,
/// and a legend once there are two or more series.
/// </summary>
public sealed class StackedColumnChartControl : Control
{
    private const double BarMaxThickness = 24.0;
    private const double DataEndRadius = 4.0;
    private const double SurfaceGap = 2.0;
    private const int GridDivisions = 4;
    private const string OtherSeries = "Other";

    public static readonly StyledProperty<IReadOnlyList<UsageHistoryBucket>?> BucketsProperty =
        AvaloniaProperty.Register<StackedColumnChartControl, IReadOnlyList<UsageHistoryBucket>?>(nameof(Buckets));

    public static readonly StyledProperty<IReadOnlyList<string>?> SeriesProperty =
        AvaloniaProperty.Register<StackedColumnChartControl, IReadOnlyList<string>?>(nameof(Series));

    public static readonly StyledProperty<string> MetricProperty =
        AvaloniaProperty.Register<StackedColumnChartControl, string>(nameof(Metric), "Total tokens");

    static StackedColumnChartControl() =>
        AffectsRender<StackedColumnChartControl>(BucketsProperty, SeriesProperty, MetricProperty);

    public IReadOnlyList<UsageHistoryBucket>? Buckets
    {
        get => GetValue(BucketsProperty);
        set => SetValue(BucketsProperty, value);
    }

    public IReadOnlyList<string>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string Metric
    {
        get => GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    /// <summary>How many labels to skip between drawn day labels so they never collide.</summary>
    internal static int LabelStride(double labelWidth, double bandWidth) =>
        Math.Max(1, (int)Math.Ceiling((labelWidth + 14) / Math.Max(bandWidth, 1.0)));

    internal static Color ColourFor(ChartPalette palette, IReadOnlyList<string> series, string name)
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

        return index < 0 ? palette.InkMuted : palette.Series(index);
    }

    /// <summary>What the pointer shows for one day: the per-model split and the total.</summary>
    internal static string TooltipText(UsageHistoryBucket bucket, IReadOnlyList<string> series, string metric)
    {
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

        return string.Join('\n', lines);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 50 || height < 50) return;

        var palette = ChartPalette.Current;
        var buckets = Buckets ?? [];
        var series = Series ?? [];

        if (buckets.Count == 0 || series.Count == 0)
        {
            var empty = Text("No usage recorded in this range", 13, palette.InkMuted);
            context.DrawText(empty, new Point((width - empty.Width) / 2.0, (height - empty.Height) / 2.0));
            return;
        }

        var legendHeight = series.Count >= 2 ? DrawLegend(context, palette, series, width) : 0.0;

        var peak = buckets.Max(bucket => bucket.Total);
        var axisMax = ChartFormat.NiceAxisMax(peak, GridDivisions);
        var tickTexts = Enumerable.Range(0, GridDivisions + 1)
            .Select(i => ChartFormat.AxisTick(axisMax * i / GridDivisions, Metric))
            .ToList();

        const double fontSize = 11;
        var lineHeight = Text("0", fontSize, palette.InkMuted).Height;
        var left = tickTexts.Max(tick => Text(tick, fontSize, palette.InkMuted).Width) + 12;
        const double right = 6.0;
        var top = legendHeight + 8.0;
        var bottom = lineHeight + 10.0;
        var plot = new Rect(left, top, Math.Max(1.0, width - left - right), Math.Max(1.0, height - top - bottom));

        var gridPen = new Pen(new SolidColorBrush(palette.Grid), 1);
        var basePen = new Pen(new SolidColorBrush(palette.Baseline), 1);
        for (var i = 0; i <= GridDivisions; i++)
        {
            var y = plot.Bottom - (plot.Height * i / GridDivisions);
            context.DrawLine(i == 0 ? basePen : gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var tick = Text(tickTexts[i], fontSize, palette.InkMuted);
            context.DrawText(tick, new Point(left - 8 - tick.Width, y - (tick.Height / 2)));
        }

        var bandWidth = plot.Width / buckets.Count;
        var thickness = Math.Max(3.0, Math.Min(BarMaxThickness, bandWidth - 4.0));

        for (var index = 0; index < buckets.Count; index++)
        {
            var bucket = buckets[index];
            if (bucket.Total <= 0) continue;

            var centre = plot.Left + (bandWidth * (index + 0.5));
            var stack = series.Where(name => bucket.PerModel.GetValueOrDefault(name) > 0).ToList();
            var cursor = plot.Bottom;
            for (var position = 0; position < stack.Count; position++)
            {
                var name = stack[position];
                var segmentHeight = plot.Height * (bucket.PerModel[name] / axisMax);
                var rect = new Rect(centre - (thickness / 2), cursor - segmentHeight, thickness, segmentHeight);
                cursor -= segmentHeight;

                // A 2px surface gap separates touching segments; the bottom one keeps its edge on
                // the baseline.
                if (position > 0 && rect.Height > SurfaceGap)
                {
                    rect = new Rect(rect.X, rect.Y, rect.Width, rect.Height - SurfaceGap);
                }

                if (rect.Height <= 0.4) continue;

                context.DrawGeometry(
                    new SolidColorBrush(ColourFor(palette, series, name)),
                    null,
                    BarGeometry(rect, DataEndRadius, roundedTop: position == stack.Count - 1));
            }
        }

        // Value labels on the caps, only where the text fits the band and clears its neighbour.
        var drawnUntil = double.MinValue;
        for (var index = 0; index < buckets.Count; index++)
        {
            var bucket = buckets[index];
            if (bucket.Total <= 0) continue;

            var label = Text(ChartFormat.MetricLabel(bucket.Total, Metric), 10, palette.InkSecondary, FontWeight.SemiBold);
            if (label.Width > bandWidth - 2) continue;

            var centre = plot.Left + (bandWidth * (index + 0.5));
            if (centre - (label.Width / 2) < drawnUntil + 4) continue;

            var capTop = plot.Bottom - (plot.Height * (bucket.Total / axisMax));
            var y = capTop - label.Height - 2;
            if (y < plot.Top)
            {
                y = capTop + 2; // no room above the cap; sit just inside it
            }

            context.DrawText(label, new Point(centre - (label.Width / 2), y));
            drawnUntil = centre + (label.Width / 2);
        }

        var sample = Text(ChartFormat.DayLabel(buckets[0].Day), fontSize, palette.InkMuted);
        var stride = LabelStride(sample.Width, bandWidth);
        for (var index = 0; index < buckets.Count; index++)
        {
            if (index % stride != 0 && index != buckets.Count - 1) continue;

            var day = Text(ChartFormat.DayLabel(buckets[index].Day), fontSize, palette.InkMuted);
            var centre = plot.Left + (bandWidth * (index + 0.5));
            context.DrawText(day, new Point(centre - (day.Width / 2), plot.Bottom + 4));
        }
    }

    private static double DrawLegend(DrawingContext context, ChartPalette palette, IReadOnlyList<string> series, double width)
    {
        const double swatch = 9.0;
        var x = 0.0;
        var y = 4.0;
        var rowHeight = Text("Ag", 11, palette.InkSecondary).Height + 4;
        foreach (var name in series)
        {
            var label = Text(name, 11, palette.InkSecondary);
            var itemWidth = swatch + 6 + label.Width + 16;
            if (x + itemWidth > width && x > 0)
            {
                x = 0;
                y += rowHeight;
            }

            context.DrawRectangle(
                new SolidColorBrush(ColourFor(palette, series, name)),
                null,
                new RoundedRect(new Rect(x, y + ((label.Height - swatch) / 2), swatch, swatch), 2));
            context.DrawText(label, new Point(x + swatch + 6, y));
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
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(new Point(rect.Left, rect.Bottom), isFilled: true);
            sink.LineTo(new Point(rect.Left, rect.Top + radius));
            sink.QuadraticBezierTo(new Point(rect.Left, rect.Top), new Point(rect.Left + radius, rect.Top));
            sink.LineTo(new Point(rect.Right - radius, rect.Top));
            sink.QuadraticBezierTo(new Point(rect.Right, rect.Top), new Point(rect.Right, rect.Top + radius));
            sink.LineTo(new Point(rect.Right, rect.Bottom));
            sink.EndFigure(isClosed: true);
        }

        return geometry;
    }

    private static FormattedText Text(string text, double size, Color colour, FontWeight weight = FontWeight.Normal) =>
        new(text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            // No font is named on purpose: macOS has no Segoe UI.
            new Typeface(FontFamily.Default, FontStyle.Normal, weight),
            size,
            new SolidColorBrush(colour));
}
