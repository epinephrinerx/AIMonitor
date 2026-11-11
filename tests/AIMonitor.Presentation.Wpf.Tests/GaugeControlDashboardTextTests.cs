using AIMonitor.Presentation.Wpf.Controls;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-018 G3/D8 — <c>GaugeControl.ComputeDashboardText(width, height, captionLineHeight,
/// subtitleHeight, hasSubtitle)</c>, the pure helper that decides the dashboard (non-compact)
/// caption layout without rendering, written from Docs/SPEC_R018.md only.
/// ORACLE: max text width = width − 8, never below 1 (G1). Caption max lines: 2 unless a subtitle is
/// drawn AND the caption's bottom would overlap it — overlap when
/// <c>captionTop + lines × captionLineHeight > height − subtitleHeight − 2</c> (D12: the gap is
/// 2 px, so long captions take two lines on the 190 px dashboard gauge) with
/// <c>captionTop = height × 0.48 + min(width, height) × 0.38 × 0.45</c> (D8: the check runs for the
/// 2-line candidate; on overlap the caption is reduced to 1 line, never 0 — "never hide caption or
/// subtitle"). No subtitle → always 2 (G2 checks overlap only when a subtitle is drawn).
/// Signature note (handoff: name it as the spec suggests, keep guessed-signature tests in one file):
/// the spec fixes the name, the five parameters and "returning max text width and caption max
/// lines"; the return shape is not stated, so these call sites destructure positionally as
/// <c>(maxTextWidth, captionMaxLines)</c> — this compiles against a <c>(double, int)</c> tuple or any
/// positional record/struct with the double first; align the helper (or these lines) if the chosen
/// shape differs. "A caption that fits on one line renders exactly as before" is pinned in
/// <see cref="GaugeControlCaptionFitTests"/>; the compact path is unchanged and stays pinned by the
/// existing GaugeControlTests.
/// </summary>
public sealed class GaugeControlDashboardTextTests
{
    private static (double MaxWidth, int MaxLines) Compute(
        double width, double height, double captionLineHeight, double subtitleHeight, bool hasSubtitle)
    {
        // One adapter, one place to align: positional deconstruction binds to a (double, int) tuple
        // or to any positional record/struct (its generated Deconstruct) that carries the max text
        // width first and the caption max lines second.
        var (maxWidth, maxLines) = GaugeControl.ComputeDashboardText(width, height, captionLineHeight, subtitleHeight, hasSubtitle);
        return (maxWidth, maxLines);
    }

    [Fact]
    public void WideCardWithRoom_AllowsTwoLines_AndWidthMinusEight()
    {
        // captionTop = 200*0.48 + 200*0.38*0.45 = 130.2; two lines reach 162.2, the subtitle top is
        // 200 − 12 − 2 = 186 → no overlap (D12: the 2 px gap).
        var (maxWidth, maxLines) = Compute(width: 200, height: 200, captionLineHeight: 16, subtitleHeight: 12, hasSubtitle: true);

        Assert.Equal(192.0, maxWidth); // G1/G2: ActualWidth − 8
        Assert.Equal(2, maxLines);
    }

    [Fact]
    public void CaptionWouldOverlapSubtitle_FallsBackToOneLine_StillTrimmed()
    {
        // captionTop = 120*0.48 + 120*0.38*0.45 = 78.12; two lines reach 110.12 > 120 − 12 − 2 = 106
        // (D12 gap) → overlap → one line (G2/D8). Trimming (CharacterEllipsis) is the render-side
        // half of
        // "1 line, still trimmed" — pinned by GaugeControlCaptionFitTests.
        var (maxWidth, maxLines) = Compute(200, 120, 16, 12, hasSubtitle: true);

        Assert.Equal(192.0, maxWidth);
        Assert.Equal(1, maxLines);
        Assert.True(maxLines >= 1); // "never hide caption or subtitle" (D8)
    }

    [Fact]
    public void D12_TwoPixelGap_Boundary_KeepsTwoLinesThatTheOldFourPixelGapWouldCut()
    {
        // D12: overlap when captionTop + lines × lineHeight > height − subtitleHeight − 2 (gap 2,
        // not 4). Height 135 (width 200 → min = 135): captionTop = 135*0.48 + 135*0.38*0.45 =
        // 87.885; two lines reach 119.885. The D12 threshold is 135 − 12 − 2 = 121 → 119.885 is
        // NOT over it → two lines — while the old 4 px threshold 119 would have cut the caption
        // to one line. This is the 190 px-class dashboard gauge case D12 exists for.
        Assert.Equal(2, Compute(200, 135, 16, 12, hasSubtitle: true).MaxLines);

        // One pixel of height less (131): captionTop = 62.88 + 22.401 = 85.281, reach 117.281 >
        // 131 − 12 − 2 = 117 → still overlap → one line, so the gap is 2 px, not "effectively 0".
        Assert.Equal(1, Compute(200, 131, 16, 12, hasSubtitle: true).MaxLines);
    }

    [Fact]
    public void WidthAtOrBelowEight_FloorsMaxWidthToOne()
    {
        // Handoff/spec: width ≤ 8 → max width 1 ("never below 1", G1).
        foreach (var width in new[] { 8.0, 7.5, 4.0, 0.0, -50.0 })
        {
            var (maxWidth, maxLines) = Compute(width, 140, 16, 12, hasSubtitle: true);

            Assert.Equal(1.0, maxWidth);
            Assert.InRange(maxLines, 1, 2); // the line decision is about overlap, not width
        }
    }

    [Fact]
    public void NoSubtitle_AllowsTwoLines_EvenWhenTooTallForTwoWithOne()
    {
        // G2: the overlap check applies only when a subtitle is drawn. For height 100 two lines
        // (65.1 + 32 = 97.1) would pass 100 − 12 − 2 = 86 — yet with hasSubtitle = false the
        // caption keeps its two-line budget.
        Assert.Equal(2, Compute(200, 100, 16, 12, hasSubtitle: false).MaxLines);
        Assert.Equal(1, Compute(200, 100, 16, 12, hasSubtitle: true).MaxLines); // same numbers, subtitle drawn
    }

    [Fact]
    public void TallDashboardCard_NeverReducesBelowOneLine_EvenWhenSubtitlesDominate()
    {
        // Extreme short card (height 40, huge line heights): overlap is certain, the answer is
        // still 1 — the caption is never hidden (D8).
        var (maxWidth, maxLines) = Compute(200, 40, 30, 20, hasSubtitle: true);

        Assert.Equal(192.0, maxWidth);
        Assert.Equal(1, maxLines);
    }

    [Fact]
    public void OneLineCaptionIsUnaffected_AllRoomCasesReturnTheTwoLineBudget()
    {
        // G3 "a caption that fits on one line renders exactly as before": the helper can only allow
        // 2 lines when there is room, and the single line it uses is drawn at the unchanged point
        // (D8 — pinned by GaugeControlCaptionFitTests's pixel comparisons). The decision grid below
        // spans realistic dashboard sizes.
        foreach (var (width, height) in new[] { (1120.0, 240.0), (560.0, 220.0), (300.0, 200.0), (240.0, 180.0) })
        {
            var (_, maxLines) = Compute(width, height, 16, 12, hasSubtitle: true);

            // captionTop + 2 × 16 always clears these cards' subtitle top: two lines stay allowed.
            Assert.Equal(2, maxLines);
        }
    }
}
