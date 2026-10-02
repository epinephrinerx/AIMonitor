namespace AIMonitor.Presentation.Wpf;

/// <summary>
/// Layout calculation result for the compact floating widget.
/// </summary>
public readonly record struct WidgetLayoutResult(
    int Count,
    double Arc,
    int CaptionLines,
    bool InlineValue,
    bool TooSmall,
    double Cell);

/// <summary>
/// Pure layout calculation logic for the compact floating widget,
/// faithfully ported from Python 1.3.3 compact.py.
/// Contains no WPF dependencies beyond standard primitives (double, int, bool).
/// </summary>
public static class WidgetLayout
{
    public const double Margin = 11.0;
    public const double ArcMax = 76.0;
    public const double ArcMin = 44.0;
    public const double ArcFloor = 28.0;
    public const double Gap = 6.0;
    public const double InlineValueMin = 46.0;
    public const double MinWidth = 150.0;
    public const double MinHeight = 96.0;
    public const double MaxEdge = 300.0;
    public const double DefaultWidth = 230.0;
    public const double DefaultHeight = 175.0;
    public const double ResizeMargin = 7.0;

    /// <summary>
    /// Computes gauge layout based on widget dimensions, available meters, and font metrics.
    /// Following 1.3.3 paintEvent strictly: gauge count is determined solely by width.
    /// </summary>
    public static WidgetLayoutResult Compute(
        double width,
        double height,
        int meterCount,
        double headerHeight,
        double lineHeight,
        bool hasStatus)
    {
        if (meterCount <= 0)
        {
            return new WidgetLayoutResult(0, 0.0, 0, false, false, 0.0);
        }

        double y = Margin + headerHeight + 5.0;
        double contentWidth = width - (2.0 * Margin);
        double footer = hasStatus ? lineHeight + 4.0 : 0.0;

        // Gauge count is decided by width alone; height must NEVER reduce count.
        int count = meterCount;
        while (count > 1 && (contentWidth - (Gap * (count - 1))) / count < ArcMin)
        {
            count--;
        }

        double byWidth = (contentWidth - (Gap * (count - 1))) / count;
        double room = height - y - Margin - footer;
        int captionLines = 2;
        double labelBlock = (lineHeight * captionLines) + 3.0;
        double arc = Math.Min(ArcMax, Math.Min(byWidth, room - labelBlock));

        // Subtitle is sacrificed first if vertical room is tight; title is never dropped.
        if (arc < ArcMin && captionLines > 1)
        {
            captionLines = 1;
            labelBlock = (lineHeight * captionLines) + 3.0;
            arc = Math.Min(ArcMax, Math.Min(byWidth, room - labelBlock));
        }

        bool tooSmall = false;
        if (arc < ArcFloor)
        {
            if (room < ArcFloor + labelBlock)
            {
                tooSmall = true;
            }
            else
            {
                arc = ArcFloor;
            }
        }

        bool inlineValue = arc >= InlineValueMin;
        double cell = (contentWidth - (Gap * (count - 1))) / count;

        return new WidgetLayoutResult(count, arc, captionLines, inlineValue, tooSmall, cell);
    }

    /// <summary>
    /// Calculates the minimum useful height for the widget based on font metrics.
    /// Margin + headerHeight + 5 + ArcFloor + lineHeight + 3 + lineHeight + 4 + Margin + 1.
    /// </summary>
    public static double MinimumUsefulHeight(double headerHeight, double lineHeight) =>
        Margin + headerHeight + 5.0 + ArcFloor + lineHeight + 3.0 + lineHeight + 4.0 + Margin + 1.0;

    /// <summary>
    /// Clamps widget dimensions to [MinWidth, MaxEdge] and [effectiveMinH, MaxEdge]
    /// where effectiveMinH = min(MaxEdge, max(MinHeight, minHeight)), preventing min > max.
    /// </summary>
    public static (double W, double H) Clamp(double w, double h, double minHeight)
    {
        double clampedW = Math.Clamp(w, MinWidth, MaxEdge);
        double effectiveMinH = Math.Min(MaxEdge, Math.Max(MinHeight, minHeight));
        double clampedH = Math.Clamp(h, effectiveMinH, MaxEdge);
        return (clampedW, clampedH);
    }
}
