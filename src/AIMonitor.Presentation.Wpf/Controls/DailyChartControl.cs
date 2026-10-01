using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace AIMonitor.Presentation.Wpf.Controls;

public sealed record DailyChartBar(string DateLabel, double Value, string FormattedValue);

/// <summary>
/// A custom-drawn vector bar chart for daily token/cost usage.
/// Conforms to ADR-0001: custom vector-drawn control with hardware acceleration.
/// </summary>
public sealed class DailyChartControl : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<DailyChartBar>), typeof(DailyChartControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(DailyChartControl),
            new FrameworkPropertyMetadata(Brushes.RoyalBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<DailyChartBar>? Items
    {
        get => (IReadOnlyList<DailyChartBar>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 50 || height < 50) return;

        var items = Items ?? [];
        if (items.Count == 0)
        {
            var textMuted = (System.Windows.Application.Current?.Resources["TextMutedBrush"] as Brush) ?? Brushes.Gray;
            var emptyText = new FormattedText(
                "No daily history recorded yet",
                CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(System.Windows.SystemFonts.MessageFontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal),
                13,
                textMuted,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(emptyText, new Point((width - emptyText.Width) / 2.0, (height - emptyText.Height) / 2.0));
            return;
        }

        const double bottomLabelHeight = 24.0;
        const double topPadding = 16.0;
        var chartHeight = Math.Max(20, height - bottomLabelHeight - topPadding);

        var maxValue = items.Max(i => i.Value);
        if (maxValue <= 0) maxValue = 1.0;

        var barSpacing = 4.0;
        var totalSpacing = barSpacing * (items.Count + 1);
        var barWidth = Math.Max(6, (width - totalSpacing) / items.Count);

        var textSecondary = (System.Windows.Application.Current?.Resources["TextSecondaryBrush"] as Brush) ?? Brushes.Gray;
        var labelTypeface = new Typeface(System.Windows.SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Draw baseline
        var borderBrush = (System.Windows.Application.Current?.Resources["BorderBrush"] as Brush) ?? Brushes.LightGray;
        dc.DrawLine(new Pen(borderBrush, 1), new Point(0, topPadding + chartHeight), new Point(width, topPadding + chartHeight));

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var barHeight = Math.Max(2, (item.Value / maxValue) * chartHeight);
            var x = barSpacing + i * (barWidth + barSpacing);
            var y = topPadding + chartHeight - barHeight;

            var rect = new Rect(x, y, barWidth, barHeight);
            dc.DrawRoundedRectangle(BarBrush ?? Brushes.RoyalBlue, null, rect, 2, 2);

            // Draw date label on alternating or sparse bars to prevent overlapping
            var showLabel = items.Count <= 14 || (i % 2 == 0);
            if (showLabel)
            {
                var label = new FormattedText(
                    item.DateLabel,
                    CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    labelTypeface,
                    9,
                    textSecondary,
                    dpi);

                var labelX = x + (barWidth - label.Width) / 2.0;
                dc.DrawText(label, new Point(labelX, topPadding + chartHeight + 4));
            }
        }
    }
}
