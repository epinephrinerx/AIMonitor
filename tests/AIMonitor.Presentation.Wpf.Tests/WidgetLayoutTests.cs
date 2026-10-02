namespace AIMonitor.Presentation.Wpf.Tests;

public class WidgetLayoutTests
{
    [Fact]
    public void Compute_WideEnoughForThreeGauges_ReturnsCountThree()
    {
        // Width 230 is ample for 3 meters (contentWidth = 208, cell = 65.33 >= ArcMin 44)
        var result = WidgetLayout.Compute(
            width: 230.0,
            height: 175.0,
            meterCount: 3,
            headerHeight: 15.0,
            lineHeight: 12.0,
            hasStatus: false);

        Assert.Equal(3, result.Count);
        Assert.False(result.TooSmall);
        Assert.True(result.Arc >= WidgetLayout.ArcMin);
    }

    [Fact]
    public void Compute_NarrowWidth_DropsCountToOne()
    {
        // Width 100 has contentWidth = 78.
        // For count=3: (78 - 12)/3 = 22 < 44
        // For count=2: (78 - 6)/2 = 36 < 44
        // For count=1: 78 >= 44 -> Count drops to 1
        var result = WidgetLayout.Compute(
            width: 100.0,
            height: 175.0,
            meterCount: 3,
            headerHeight: 15.0,
            lineHeight: 12.0,
            hasStatus: false);

        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void Compute_InsufficientHeight_DoesNotReduceGaugeCount()
    {
        // Height is only 30px (insufficient height, room is negative / TooSmall),
        // but width 230px is ample for 3 meters. Height must NEVER reduce count.
        var result = WidgetLayout.Compute(
            width: 230.0,
            height: 30.0,
            meterCount: 3,
            headerHeight: 15.0,
            lineHeight: 12.0,
            hasStatus: false);

        Assert.Equal(3, result.Count);
        Assert.True(result.TooSmall);
    }

    [Fact]
    public void Compute_DraggingWider_RestoresGaugeCount()
    {
        // At width 100, count is 1
        var narrow = WidgetLayout.Compute(100.0, 175.0, 3, 15.0, 12.0, false);
        Assert.Equal(1, narrow.Count);

        // Dragging wider to width 230 restores count to 3
        var wide = WidgetLayout.Compute(230.0, 175.0, 3, 15.0, 12.0, false);
        Assert.Equal(3, wide.Count);
    }

    [Fact]
    public void Compute_TightVerticalRoom_SacrificesSubtitleFirst()
    {
        // At height 107 (y=31, margin=11 -> room=65):
        // 2 lines: labelBlock = 12*2 + 3 = 27 -> arc = 65 - 27 = 38 < 44 (ArcMin)
        // Subtitle sacrificed -> captionLines=1, labelBlock = 15 -> arc = 65 - 15 = 50 >= 44
        var tightResult = WidgetLayout.Compute(
            width: 230.0,
            height: 107.0,
            meterCount: 3,
            headerHeight: 15.0,
            lineHeight: 12.0,
            hasStatus: false);

        Assert.Equal(1, tightResult.CaptionLines);
        Assert.Equal(50.0, tightResult.Arc);
        Assert.False(tightResult.TooSmall);

        // When height is 130 (ample vertical room, room=88), 2 caption lines fit:
        var ampleResult = WidgetLayout.Compute(
            width: 230.0,
            height: 130.0,
            meterCount: 3,
            headerHeight: 15.0,
            lineHeight: 12.0,
            hasStatus: false);

        Assert.Equal(2, ampleResult.CaptionLines);
    }

    [Fact]
    public void Compute_ArcBelowFloorWithSufficientRoom_ClampsArcToArcFloor()
    {
        // width = 45 -> contentWidth = 23 -> byWidth = 23 < ArcFloor (28)
        // height = 175 -> room = 133 >= ArcFloor + labelBlock (28 + 15 = 43)
        // Since room is sufficient, arc must be clamped to ArcFloor (28) and TooSmall is false
        var result = WidgetLayout.Compute(
            width: 45.0,
            height: 175.0,
            meterCount: 1,
            headerHeight: 15.0,
            lineHeight: 12.0,
            hasStatus: false);

        Assert.Equal(WidgetLayout.ArcFloor, result.Arc);
        Assert.False(result.TooSmall);
    }

    [Fact]
    public void Compute_InsufficientRoom_SetsTooSmallTrue()
    {
        // height = 82 (y=31, margin=11 -> room=40)
        // captionLines=1, labelBlock=15. ArcFloor + labelBlock = 28 + 15 = 43.
        // room (40) < 43 -> TooSmall must be true
        var result = WidgetLayout.Compute(
            width: 230.0,
            height: 82.0,
            meterCount: 3,
            headerHeight: 15.0,
            lineHeight: 12.0,
            hasStatus: false);

        Assert.True(result.TooSmall);
    }

    [Fact]
    public void Compute_InlineValueBoundaryAt46_ReturnsTrueAtOrAboveAndFalseBelow()
    {
        // At height 103 (room=61, captionLines=1, labelBlock=15 -> arc = 46.0):
        // 46.0 >= InlineValueMin (46.0) is true
        var atBoundary = WidgetLayout.Compute(
            width: 230.0,
            height: 103.0,
            meterCount: 3,
            headerHeight: 15.0,
            lineHeight: 12.0,
            hasStatus: false);

        Assert.Equal(46.0, atBoundary.Arc);
        Assert.True(atBoundary.InlineValue);

        // At height 102 (room=60, captionLines=1, labelBlock=15 -> arc = 45.0):
        // 45.0 < 46.0 -> InlineValue is false
        var belowBoundary = WidgetLayout.Compute(
            width: 230.0,
            height: 102.0,
            meterCount: 3,
            headerHeight: 15.0,
            lineHeight: 12.0,
            hasStatus: false);

        Assert.Equal(45.0, belowBoundary.Arc);
        Assert.False(belowBoundary.InlineValue);
    }

    [Fact]
    public void Compute_HasStatusTrue_ReducesVerticalRoomWithoutAffectingGaugeCount()
    {
        var withoutStatus = WidgetLayout.Compute(230.0, 120.0, 3, 15.0, 12.0, hasStatus: false);
        var withStatus = WidgetLayout.Compute(230.0, 120.0, 3, 15.0, 12.0, hasStatus: true);

        // Status line reservation (lineHeight + 4 = 16px) does NOT reduce count.
        // Vertical room is reduced (room=62 vs 78); 2-line caption gives arc < 44 so subtitle
        // is sacrificed (labelBlock=15, room=62, arc=47).
        Assert.Equal(3, withoutStatus.Count);
        Assert.Equal(3, withStatus.Count);
        Assert.Equal(2, withoutStatus.CaptionLines);
        Assert.Equal(1, withStatus.CaptionLines);
        Assert.Equal(51.0, withoutStatus.Arc);
        Assert.Equal(47.0, withStatus.Arc);
    }

    [Fact]
    public void Compute_ZeroOrNegativeMeterCount_ReturnsZeroResult()
    {
        var zero = WidgetLayout.Compute(230.0, 175.0, 0, 15.0, 12.0, false);
        Assert.Equal(0, zero.Count);
        Assert.Equal(0.0, zero.Arc);
        Assert.False(zero.TooSmall);

        var negative = WidgetLayout.Compute(230.0, 175.0, -1, 15.0, 12.0, false);
        Assert.Equal(0, negative.Count);
    }

    [Fact]
    public void MinimumUsefulHeight_MatchesExactFormulaFromMetrics()
    {
        const double header = 15.0;
        const double line = 12.0;

        // Margin (11) + header (15) + 5 + ArcFloor (28) + line (12) + 3 + line (12) + 4 + Margin (11) + 1 = 102.0
        const double expected = 11.0 + header + 5.0 + 28.0 + line + 3.0 + line + 4.0 + 11.0 + 1.0;
        var actual = WidgetLayout.MinimumUsefulHeight(header, line);

        Assert.Equal(expected, actual);
        Assert.Equal(102.0, actual);
    }

    [Fact]
    public void Clamp_ClampsDimensionsWithinBounds_AndPreventsMinGreaterThanMax()
    {
        // 1. Below minimums
        var (w1, h1) = WidgetLayout.Clamp(100.0, 50.0, 80.0);
        Assert.Equal(WidgetLayout.MinWidth, w1);
        Assert.Equal(WidgetLayout.MinHeight, h1);

        // 2. Above maximums
        var (w2, h2) = WidgetLayout.Clamp(400.0, 500.0, 80.0);
        Assert.Equal(WidgetLayout.MaxEdge, w2);
        Assert.Equal(WidgetLayout.MaxEdge, h2);

        // 3. Elevated minHeight
        var (w3, h3) = WidgetLayout.Clamp(200.0, 100.0, 120.0);
        Assert.Equal(200.0, w3);
        Assert.Equal(120.0, h3);

        // 4. Extreme minHeight exceeding MaxEdge: min > max must be prevented
        var (w4, h4) = WidgetLayout.Clamp(200.0, 250.0, 450.0);
        Assert.Equal(200.0, w4);
        Assert.Equal(WidgetLayout.MaxEdge, h4);
    }
}
