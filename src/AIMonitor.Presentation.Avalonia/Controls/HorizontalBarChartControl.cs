using System.Globalization;
using AIMonitor.Domain;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AIMonitor.Presentation.Avalonia.Controls;

/// <summary>
/// A ranked breakdown with the value labelled at each bar tip, custom-drawn (ADR-0001). The same
/// chart the Windows build draws: 30px rows, 14px bars rounded at the tip, at most eight rows with
/// the remainder folded into "Other". "By model" is coloured per row; "By project" uses the accent.
/// </summary>
public sealed class HorizontalBarChartControl : Control
{
    internal const double RowHeight = 30.0;
    private const double BarThickness = 14.0;
    private const double DataEndRadius = 4.0;
    internal const int MaxRows = 8;
    private const string OtherLabel = "Other";

    public static readonly StyledProperty<IReadOnlyList<UsageHistoryBreakdown>?> RowsProperty =
        AvaloniaProperty.Register<HorizontalBarChartControl, IReadOnlyList<UsageHistoryBreakdown>?>(nameof(Rows));

    public static readonly StyledProperty<string> MetricProperty =
        AvaloniaProperty.Register<HorizontalBarChartControl, string>(nameof(Metric), "Total tokens");

    public static readonly StyledProperty<bool> ColouredProperty =
        AvaloniaProperty.Register<HorizontalBarChartControl, bool>(nameof(Coloured));

    static HorizontalBarChartControl()
    {
        AffectsRender<HorizontalBarChartControl>(RowsProperty, MetricProperty, ColouredProperty);
        AffectsMeasure<HorizontalBarChartControl>(RowsProperty);
    }

    public IReadOnlyList<UsageHistoryBreakdown>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public string Metric
    {
        get => GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    /// <summary>One colour per row ("By model") rather than the accent throughout ("By project").</summary>
    public bool Coloured
    {
        get => GetValue(ColouredProperty);
        set => SetValue(ColouredProperty, value);
    }

    /// <summary>
    /// At most <see cref="MaxRows"/> rows; everything past that is summed into one "Other" row, so
    /// a service with forty models still produces a chart somebody can read.
    /// </summary>
    internal static IReadOnlyList<UsageHistoryBreakdown> Fold(IReadOnlyList<UsageHistoryBreakdown>? rows)
    {
        if (rows is null || rows.Count <= MaxRows)
        {
            return rows ?? [];
        }

        var spill = rows.Skip(MaxRows - 1).Sum(row => row.Value);
        return [.. rows.Take(MaxRows - 1), new UsageHistoryBreakdown(OtherLabel, spill)];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Math.Max(Fold(Rows).Count, 3);
        var width = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        return new Size(width, (RowHeight * count) + 8);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var palette = ChartPalette.Current;
        var rows = Fold(Rows);
        var width = Bounds.Width;
        var height = Bounds.Height;

        if (rows.Count == 0)
        {
            var empty = Text("Nothing recorded yet", 12, palette.InkMuted);
            context.DrawText(empty, new Point((width - empty.Width) / 2.0, (height - empty.Height) / 2.0));
            return;
        }

        var labelWidth = Math.Min(140.0, rows.Max(row => Text(row.Label, 12, palette.InkSecondary).Width) + 10.0);
        var valueTexts = rows.Select(row => ChartFormat.MetricLabel(row.Value, Metric)).ToList();
        var valueWidth = valueTexts.Max(text => Text(text, 12, palette.Ink).Width) + 10;
        var trackLeft = labelWidth + 10;
        var trackWidth = Math.Max(20.0, width - trackLeft - valueWidth);
        var peak = rows.Max(row => row.Value);
        if (peak <= 0)
        {
            peak = 1.0;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var centre = (index * RowHeight) + (RowHeight / 2);

            var name = Text(row.Label, 12, palette.InkSecondary);
            name.MaxTextWidth = Math.Max(1.0, labelWidth);
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(name, new Point(0, centre - (name.Height / 2)));

            var colour = row.Label == OtherLabel
                ? palette.InkMuted
                : Coloured ? palette.Series(index) : palette.Accent;
            var barWidth = Math.Max(2.0, trackWidth * (row.Value / peak));
            var rect = new Rect(trackLeft, centre - (BarThickness / 2), barWidth, BarThickness);
            context.DrawGeometry(new SolidColorBrush(colour), null, BarGeometry(rect, DataEndRadius));

            var value = Text(valueTexts[index], 12, palette.Ink);
            context.DrawText(value, new Point(rect.Right + 6, centre - (value.Height / 2)));
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
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(new Point(rect.Left, rect.Top), isFilled: true);
            sink.LineTo(new Point(rect.Right - radius, rect.Top));
            sink.QuadraticBezierTo(new Point(rect.Right, rect.Top), new Point(rect.Right, rect.Top + radius));
            sink.LineTo(new Point(rect.Right, rect.Bottom - radius));
            sink.QuadraticBezierTo(new Point(rect.Right, rect.Bottom), new Point(rect.Right - radius, rect.Bottom));
            sink.LineTo(new Point(rect.Left, rect.Bottom));
            sink.EndFigure(isClosed: true);
        }

        return geometry;
    }

    private static FormattedText Text(string text, double size, Color colour) =>
        new(text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            size,
            new SolidColorBrush(colour));
}
