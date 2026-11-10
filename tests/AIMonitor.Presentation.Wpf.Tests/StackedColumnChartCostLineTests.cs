using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Controls;
using AIMonitor.Presentation.Wpf.Theme;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-017 G1.4 + F2/H9 + F3/F4 — the cost-line seams of <c>StackedColumnChartControl</c>, written
/// from Docs/SPEC_R017.md only (§5.6). F2 defines the testable seams and H9 verbatim:
/// <c>ShowsCostAxis(costs)</c> is true only when non-empty and some <c>PricedUsd &gt; 0</c>;
/// <c>LegendEntries(series, costs)</c> is <c>series</c> when <c>ShowsCostAxis</c> is false, else
/// <c>series + "Cost ($)"</c>; H9 = ShowsCostAxis(null/empty) == false, LegendEntries(series, null)
/// == series, and the tooltip with no costs equals the existing text. F4 adds the overload
/// <c>TooltipText(bucket, series, metric, costs, index)</c> — "the existing 3-argument method
/// forwards with costs: null and its output is unchanged", the cost entry matched by Day, no
/// entry → no cost line. F3: with Costs non-empty EVERY day's tooltip appends
/// <c>Cost: {ChartFormat.Money(PricedUsd)}</c> plus <c>" + unpriced"</c> iff that day has
/// <c>HasUnpricedModels</c> — also when all costs are 0 (then no line/right axis per G1.4, but the
/// tooltip still shows Cost: $0.00). Later review decisions: F10 adds <c>internal static string
/// CostTickLabel(double v)</c> with its tick oracles, and F12 the empty-state guard
/// (<c>series.Count == 0</c> → the existing "No usage recorded" render, no cost line, no right
/// axis) — pinned here by comparing whole bitmap renders. Inspector B decisions: F18 adds a pixel
/// probe on the cost line itself (ink within 5×5 of a segment midpoint between day-column centres,
/// and no ink anywhere in the plot with Costs empty), and F19 pins F14/F5 dot rendering (priced dot
/// centre = ink; unpriced dot centre = card surface with an ink ring ≈3 px out) and the line-shaped
/// "Cost ($)" legend swatch (ink pixels wider than tall) — both in light AND dark themes. The
/// probes read their geometry FROM the bitmap (day centres from the bar fills, dot rows from the
/// ink runs), so no production seam was needed (F22). Written against Docs/SPEC_R017.md; the
/// G1 implementation has since merged into this branch (041433a).
/// </summary>
[Collection("Wpf")]
public sealed class StackedColumnChartCostLineTests
{
    private const string Kimi = "kimi-k2.7-code";
    private const string Glm = "glm-5.3-flash";
    private const string Deepseek = "deepseek-v4.1-flash";

    private static readonly DateOnly D4 = new(2026, 11, 4);
    private static readonly DateOnly D8 = new(2026, 11, 8);
    private static readonly DateOnly D9 = new(2026, 11, 9);
    private static readonly DateOnly D10 = new(2026, 11, 10);

    private static DailyCost Cost(DateOnly day, double pricedUsd, bool unpriced) => new(day, pricedUsd, unpriced);

    private static UsageHistoryBucket Empty(DateOnly day) => new(day);

    private static UsageHistoryBucket Day8() => new(
        D8, new Dictionary<string, double> { [Kimi] = 1000, [Glm] = 400 });

    private static UsageHistoryBucket Day10() => new(
        D10, new Dictionary<string, double> { [Kimi] = 2000, [Deepseek] = 300 });

    private static string Join(params string[] lines) => string.Join(Environment.NewLine, lines);

    // ── F2 seam: ShowsCostAxis ─────────────────────────────────────────────────

    [Fact]
    public void ShowsCostAxis_H9_NullAndEmpty_ShowNoAxis()
    {
        // F2 (H9): ShowsCostAxis(null/empty) == false — G1.4 "Null or empty → the control renders
        // exactly as today (no line, no right axis...)".
        Assert.False(StackedColumnChartControl.ShowsCostAxis(null));
        Assert.False(StackedColumnChartControl.ShowsCostAxis([]));
    }

    [Fact]
    public void ShowsCostAxis_AllCostsZero_ShowNoAxis()
    {
        // G1.4 "When all PricedUsd == 0 → no line and no right axis"; F2 "true only when non-empty
        // and some PricedUsd > 0". HasUnpricedModels is irrelevant to the axis.
        Assert.False(StackedColumnChartControl.ShowsCostAxis([Cost(D8, 0.0, false), Cost(D10, 0.0, true)]));
    }

    [Fact]
    public void ShowsCostAxis_AnyPositiveCost_ShowsAxis()
    {
        Assert.True(StackedColumnChartControl.ShowsCostAxis([Cost(D8, 0.0, false), Cost(D10, 1.25, true)]));
    }

    // ── F2 seam: LegendEntries ─────────────────────────────────────────────────

    [Fact]
    public void LegendEntries_H9_NullAndEmpty_Costs_LeaveTheSeriesLegendUntouched()
    {
        var series = new[] { Kimi, Glm, Deepseek };

        // F2 (H9): LegendEntries(series, null) == series.
        Assert.Equal(series, StackedColumnChartControl.LegendEntries(series, null));
        Assert.Equal(series, StackedColumnChartControl.LegendEntries(series, []));
    }

    [Fact]
    public void LegendEntries_AllCostsZero_NoCostEntry()
    {
        // F2: series unchanged whenever ShowsCostAxis is false — the zero-cost case gets no entry
        // even though its tooltip still shows the cost line (F3; see Tooltip tests).
        var series = new[] { Kimi, Glm };

        Assert.Equal(series, StackedColumnChartControl.LegendEntries(series, [Cost(D8, 0.0, false)]));
    }

    [Fact]
    public void LegendEntries_WithCostLine_AppendsCostEntryAfterTheModelSeries()
    {
        // F2 + G1.4 "legend gets one extra entry "Cost ($)" after the model series".
        var series = new[] { Kimi, Glm, Deepseek };

        Assert.Equal(
            [Kimi, Glm, Deepseek, "Cost ($)"],
            StackedColumnChartControl.LegendEntries(series, [Cost(D8, 0.60, false), Cost(D10, 1.25, true)]));
    }

    // ── F4: the existing 3-argument tooltip is unchanged ───────────────────────

    [Fact]
    public void TooltipText_ThreeArgumentOutput_IsUnchanged()
    {
        // F4: "the existing 3-argument method forwards with costs: null and its output is
        // unchanged" — pinned against the R-014 text shape (day label, per-model lines in series
        // order, total line; "No usage" for an empty day). Compact("Total tokens") values:
        // 1000 → "1,000", 400 → "400", total 1400 → "1,400".
        Assert.Equal(
            Join("Nov 8", $"{Kimi}: 1,000", $"{Glm}: 400", "Total: 1,400"),
            StackedColumnChartControl.TooltipText(Day8(), [Kimi, Glm], UsageMetric.TotalTokens));
        Assert.Equal(
            Join("Nov 4", "No usage"),
            StackedColumnChartControl.TooltipText(Empty(D4), [Kimi, Glm], UsageMetric.TotalTokens));
    }

    [Fact]
    public void TooltipText_H9_OverloadWithNullAndEmptyCosts_MatchesTheThreeArgumentText()
    {
        // F2 (H9) "the tooltip with no costs equals the existing text" + F4's forwarding.
        foreach (var bucket in new[] { Day8(), Day10(), Empty(D9) })
        {
            var plain = StackedColumnChartControl.TooltipText(bucket, [Kimi, Glm, Deepseek], UsageMetric.TotalTokens);
            Assert.Equal(plain, StackedColumnChartControl.TooltipText(bucket, [Kimi, Glm, Deepseek], UsageMetric.TotalTokens, null, 0));
            Assert.Equal(plain, StackedColumnChartControl.TooltipText(bucket, [Kimi, Glm, Deepseek], UsageMetric.TotalTokens, [], 0));
        }
    }

    // ── F3/F4: the cost line on the tooltip ────────────────────────────────────

    [Fact]
    public void Tooltip_CostEntryForTheDay_AppendsMoneyLine()
    {
        // G1.4/F3: "the hover tooltip for a day adds a line Cost: {ChartFormat.Money(PricedUsd)}".
        // Money(0.60) = "$0.60" (ChartFormat pins the formatter; see ChartTests.Money_*).
        var costs = new[] { Cost(D8, 0.60, false), Cost(D10, 1.25, true) };

        var text = StackedColumnChartControl.TooltipText(Day8(), [Kimi, Glm, Deepseek], UsageMetric.TotalTokens, costs, index: 0);

        Assert.Equal(
            Join("Nov 8", $"{Kimi}: 1,000", $"{Glm}: 400", "Total: 1,400", "Cost: $0.60"),
            text);
    }

    [Fact]
    public void Tooltip_UnpricedDay_AppendsUnpricedSuffix()
    {
        // G1.4: 'plus " + unpriced" when HasUnpricedModels'. The 11-10 bucket has no glm value, so
        // the glm series line is skipped by the unchanged rule (only values > 0 are listed);
        // Total = 2300 from the bucket itself.
        var costs = new[] { Cost(D8, 0.60, false), Cost(D10, 1.25, true) };

        var text = StackedColumnChartControl.TooltipText(Day10(), [Kimi, Glm, Deepseek], UsageMetric.TotalTokens, costs, index: 1);

        Assert.Equal(
            Join("Nov 10", $"{Kimi}: 2,000", $"{Deepseek}: 300", "Total: 2,300", "Cost: $1.25 + unpriced"),
            text);
    }

    [Fact]
    public void Tooltip_F3_AllZeroCosts_StillShowTheCostLine_ButNoAxisNoLegendEntry()
    {
        // F3 (m3): "the tooltip of EVERY day appends Cost: ... also when all costs are 0", and
        // G1.4: "but tooltip still shows Cost: $0.00 for days with costs entries" — while the axis
        // and the legend entry stay away (F2 seam). Money(0) = "$0.00"; an empty day keeps "No
        // usage" and still gets the cost line.
        var costs = new[] { Cost(D4, 0.0, false), Cost(D9, 0.0, true) };

        Assert.False(StackedColumnChartControl.ShowsCostAxis(costs)); // no line, no right axis
        Assert.Equal(
            Join("Nov 4", "No usage", "Cost: $0.00"),
            StackedColumnChartControl.TooltipText(Empty(D4), [Kimi, Glm], UsageMetric.TotalTokens, costs, index: 0));
        Assert.Equal(
            Join("Nov 9", "No usage", "Cost: $0.00 + unpriced"),
            StackedColumnChartControl.TooltipText(Empty(D9), [Kimi, Glm], UsageMetric.TotalTokens, costs, index: 1));
    }

    [Fact]
    public void Tooltip_F4_DayWithoutACostEntry_HasNoCostLine()
    {
        // F4 (m4): "the cost entry is matched by Day (no entry for that day → no cost line)" —
        // costs covers 11-08 and 11-10; the hovered bucket is 11-09 (bucket index 1). An
        // implementation that took costs[index] positionally would borrow 11-10's $1.25 line here;
        // matching by Day finds no entry and draws none.
        var costs = new[] { Cost(D8, 0.60, false), Cost(D10, 1.25, true) };

        var text = StackedColumnChartControl.TooltipText(Empty(D9), [Kimi, Glm, Deepseek], UsageMetric.TotalTokens, costs, index: 1);

        Assert.Equal(Join("Nov 9", "No usage"), text); // byte-identical to the no-costs text
        Assert.DoesNotContain("Cost:", text);
    }

    // ── F10: right-axis tick labels ────────────────────────────────────────────

    [Theory]
    // F10 (G1 code review): below $1,000 the tick is "$" + v.ToString("#,##0.00##") (up to four
    // decimals, at least two); from $1,000 it is "$" + ChartFormat.Compact(v) — which still
    // spells 1,000–9,999 with a thousands separator (Compact only abbreviates from 10K, pinned
    // in ChartTests.Compact_MatchesLegacyFormat), then $12K. The spec's own
    // oracles: ticks under max 0.10 read $0.00, $0.025, $0.05, $0.075, $0.10; under max 0.02 —
    // $0.00, $0.005, $0.01, $0.015, $0.02. The tooltip keeps ChartFormat.Money (pinned above).
    [InlineData(0.0, "$0.00")]
    [InlineData(0.005, "$0.005")]
    [InlineData(0.01, "$0.01")]
    [InlineData(0.015, "$0.015")]
    [InlineData(0.02, "$0.02")]
    [InlineData(0.025, "$0.025")]
    [InlineData(0.05, "$0.05")]
    [InlineData(0.075, "$0.075")]
    [InlineData(0.1, "$0.10")]
    [InlineData(12.34, "$12.34")]
    [InlineData(999.0, "$999.00")]
    // From $1,000 the label is "$" + ChartFormat.Compact(v) — Compact only abbreviates at 10,000+.
    [InlineData(1000.0, "$1,000")]
    [InlineData(1500.0, "$1,500")]
    [InlineData(10000.0, "$10K")]
    [InlineData(12345.0, "$12.3K")]
    [InlineData(2500000.0, "$2.5M")]
    public void CostTickLabel_F10_FollowsTheDecimalThenCompactRule(double value, string expected) =>
        Assert.Equal(expected, StackedColumnChartControl.CostTickLabel(value));

    [Fact]
    public void CostTickLabel_F10_TheSpecTickSequences()
    {
        // The full right-axis sweep exactly as OnRender computes it: NiceAxisMax over the same
        // GridDivisions (4) as the left axis, ticks at max*i/4 (G1.4, F10).
        var max010 = ChartFormat.NiceAxisMax(0.10, 4);
        Assert.Equal(
            ["$0.00", "$0.025", "$0.05", "$0.075", "$0.10"],
            Enumerable.Range(0, 5).Select(i => StackedColumnChartControl.CostTickLabel(max010 * i / 4)));

        var max002 = ChartFormat.NiceAxisMax(0.02, 4);
        Assert.Equal(
            ["$0.00", "$0.005", "$0.01", "$0.015", "$0.02"],
            Enumerable.Range(0, 5).Select(i => StackedColumnChartControl.CostTickLabel(max002 * i / 4)));
    }

    // ── F12/H9: whole-control render comparisons ───────────────────────────────

    [Fact]
    public void F12_EmptySeries_WithPositiveCosts_RendersExactlyTheOldEmptyState()
    {
        // F12 (R1 m3): "the empty-state guard back to the original series.Count == 0 → the existing
        // 'No usage recorded' state, no cost line, no right axis". OnRender early-returns before
        // the legend and the cost axis, so with no series even positive costs (with an unpriced
        // flag) must not move a single pixel; the two contrast renders prove the comparison is
        // not vacuous (a populated series changes it, and with a series the costs DO draw).
        WpfTestHost.Run(() =>
        {
            var buckets = new[] { Day8(), Day10() };
            var series = new[] { Kimi, Glm, Deepseek };
            var costs = new[] { Cost(D8, 0.60, false), Cost(D10, 1.25, true) };

            var emptyNoCosts = Render(buckets, [], UsageMetric.TotalTokens, null);
            Assert.True(emptyNoCosts.SequenceEqual(Render(buckets, [], UsageMetric.TotalTokens, costs)));

            var withSeries = Render(buckets, series, UsageMetric.TotalTokens, null);
            Assert.False(withSeries.SequenceEqual(emptyNoCosts));
            Assert.False(Render(buckets, series, UsageMetric.TotalTokens, costs).SequenceEqual(withSeries));
        });
    }

    [Fact]
    public void H9_NullEmptyOrAllZeroCosts_RenderIdenticallyToBefore()
    {
        // H9 (F2): "chart control with Costs null/empty — rendering identical to before (no right
        // axis, no 'Cost ($)' legend)" — and the all-zero case draws no line either (G1.4), so it
        // renders like the pre-R-017 control too (the unpriced flag alone never draws).
        WpfTestHost.Run(() =>
        {
            var buckets = new[] { Day8(), Day10() };
            var series = new[] { Kimi, Glm, Deepseek };
            var none = Render(buckets, series, UsageMetric.TotalTokens, null);

            Assert.True(none.SequenceEqual(Render(buckets, series, UsageMetric.TotalTokens, [])));
            Assert.True(none.SequenceEqual(Render(
                buckets, series, UsageMetric.TotalTokens, [Cost(D8, 0.0, false), Cost(D10, 0.0, true)])));
        });
    }

    /// <summary>One deterministic 320×160 render of the control (STA via WpfTestHost); F12/H9
    /// compare the whole pixel buffer, so "no cost line, no right axis" means "byte-identical".</summary>
    private static byte[] Render(
        IReadOnlyList<UsageHistoryBucket> buckets,
        IReadOnlyList<string> series,
        string metric,
        IReadOnlyList<DailyCost>? costs)
    {
        var chart = new StackedColumnChartControl
        {
            Buckets = buckets,
            Series = series,
            Metric = metric,
            Costs = costs,
        };
        chart.Measure(new Size(320.0, 160.0));
        chart.Arrange(new Rect(0.0, 0.0, 320.0, 160.0));
        chart.UpdateLayout();

        var target = new RenderTargetBitmap(320, 160, 96.0, 96.0, PixelFormats.Pbgra32);
        target.Render(chart);
        var pixels = new byte[320 * 160 * 4];
        target.CopyPixels(pixels, 320 * 4, 0);
        return pixels;
    }

    // ── G1.4: the Costs dependency property itself ─────────────────────────────

    [Fact]
    public void Costs_DependencyProperty_IsAnOptionalAffectsRenderList()
    {
        // G1.4: "New dependency property Costs (IReadOnlyList<DailyCost>?, AffectsRender). Null or
        // empty → the control renders exactly as today" — the property type and the null default
        // are observable here. The AffectsRender FLAG is not introspectable: WPF returns
        // DefaultMetadata as a base PropertyMetadata for every registration on this control
        // (verified against the pre-existing Buckets/Series/Metric DPs too), so asserting the
        // framework type would pin WPF behaviour, not this control's. The render effect the flag
        // drives is verified through the seams OnRender consumes (F2: ShowsCostAxis /
        // LegendEntries) plus the manual smoke checklist.
        Assert.Equal(typeof(IReadOnlyList<DailyCost>), StackedColumnChartControl.CostsProperty.PropertyType);
        Assert.Equal(
            StackedColumnChartControl.BucketsProperty.DefaultMetadata.GetType(),
            StackedColumnChartControl.CostsProperty.DefaultMetadata.GetType()); // registered in the same style as the existing chart DPs
        Assert.Null(StackedColumnChartControl.CostsProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void Costs_CanBeSetAndReadBack()
    {
        // The XAML binding Costs="{Binding SelectedTab.ChartCosts}" (G1.3) targets this CLR
        // property; a real instance is needed, so run on the WPF test host.
        WpfTestHost.Run(() =>
        {
            var chart = new StackedColumnChartControl();
            Assert.Null(chart.Costs); // default: no line, renders exactly as today (G1.4)

            var costs = new[] { Cost(D8, 0.60, false) };
            chart.Costs = costs;
            Assert.Equal(costs, chart.Costs);

            chart.Costs = null;
            Assert.Null(chart.Costs);
        });
    }

    [Fact]
    public void MainWindowXaml_UsagePerDayChart_BindsCostsToTheSelectedTab()
    {
        // G1.3: "the existing 'Usage per day' StackedColumnChartControl binds
        // Costs=\"{Binding SelectedTab.ChartCosts}\". No other layout change." — a text-level
        // contract in the style of ConnectionsSourceContractTests.
        var root = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(root) && !File.Exists(Path.Combine(root, "AIMonitor.sln")))
        {
            root = Directory.GetParent(root)?.FullName;
        }

        Assert.NotNull(root);
        var xaml = File.ReadAllText(Path.Combine(root!, "src", "AIMonitor.Presentation.Wpf", "MainWindow.xaml"));

        Assert.Matches(
            @"<controls:StackedColumnChartControl\s+[^>]*Costs=""\{\s*Binding\s+SelectedTab\.ChartCosts\s*\}""",
            xaml);
    }

    // ── F18/F19: pixel probes on the rendered cost line ─────────────────────────
    //
    // Everything below reads its geometry FROM the 320×160 bitmap, so no production seam is needed
    // (F22): the day-column centres are the x-runs of series-coloured pixels in the plot area, the
    // dot centres are the vertical ink runs at those columns, and "ink" is a colour-distance test
    // against the palette (F5: line and dots use p.Ink; nothing else on the control draws ink — the
    // texts are InkMuted/InkSecondary, the bars are categorical, the grid/baseline are their own
    // tokens). Anti-aliasing is tolerated by the threshold, never by exact-pixel equality (F18).

    private const int RenderW = 320; // the fixed size Render() draws at
    private const int RenderH = 160;
    private const double InkTolerance = 70.0;   // RGB distance from the palette's Ink
    private const double SeriesTolerance = 48.0; // RGB distance from a categorical fill
    private const double SurfaceTolerance = 60.0; // card-background fill of the hollow dot
    private const int MinAlpha = 50; // anti-aliased coverage still carries the ink hue
    private const int PlotInkTop = 45; // below the two legend rows (≈41 px), above all plot ink

    [Fact]
    public void F18_CostLine_SegmentMidpointProbeFindsInk_AndNothingWithoutCosts()
    {
        // F18 (IB major 1): "render with ≥2 priced days and assert ink-coloured pixels in a small
        // neighbourhood (e.g. 3×3) around the midpoint of a segment between two adjacent day-column
        // centres, away from any dot; the same probe finds no ink with Costs empty." Killing
        // mutant: omitting the polyline leaves only the ≈3 px dots — the segment midpoint sits
        // ≈55 px from both, so no ink is found there and the whole plot-area ink list is empty.
        WpfTestHost.Run(() =>
        {
            var p = ApplyTheme("dark"); // pin the host default rather than inherit another test's theme
            try
            {
                var buckets = new[] { Day8(), Day10() };
                var series = new[] { Kimi, Glm, Deepseek };
                var costs = new[] { Cost(D8, 0.60, false), Cost(D10, 1.25, true) };

                var withLine = Render(buckets, series, UsageMetric.TotalTokens, costs);
                var without = Render(buckets, series, UsageMetric.TotalTokens, null);

                var centres = BarCentres(withLine, p, series);
                Assert.Equal(2, centres.Count); // the day-8 and day-10 columns, left to right

                var ink = InkPixels(withLine, p, PlotInkTop, RenderH);
                Assert.True(ink.Count >= 30, $"expected a drawn cost line, found {ink.Count} plot ink pixels");
                var y8 = DotCentreY(ink, centres[0]); // the priced dot (Nov 8, $0.60)
                var y10 = DotCentreY(ink, centres[1]); // the unpriced-flagged dot (Nov 10, $1.25)
                var xm = (centres[0] + centres[1]) / 2.0;
                var ym = (y8 + y10) / 2.0;

                Assert.True(Math.Abs(xm - centres[0]) >= 15.0 && Math.Abs(xm - centres[1]) >= 15.0,
                    "the probe must sit away from both dots");
                Assert.True(InkNear(ink, xm, ym, 2), "ink expected within 5×5 of the segment midpoint");

                // "the same probe finds no ink with Costs empty" — pinned at its strongest form:
                // with Costs null nothing outside the legend is ink at all.
                Assert.Empty(InkPixels(without, p, PlotInkTop, RenderH));
            }
            finally
            {
                ApplyTheme("dark"); // leave the host as WpfTestHost initialised it
            }
        });
    }

    [Theory]
    [InlineData(false)] // light
    [InlineData(true)]  // dark
    public void F19_UnpricedDotIsHollowInkRingAroundSurfaceCentre_PricedDotCentreIsInk(bool dark)
    {
        // F19 (IB major 2) over F5/F14: "an unpriced day's dot centre is the card background colour
        // and its ring is ink; a priced dot centre is ink ... in both light and dark themes." The
        // surface fill must be OPAQUE (F14: "so the line does not show through"). Killing mutant:
        // filling the unpriced dot solid paints ink at its centre → the surface check fails (and
        // the ring checks find no gap-free ink annulus at the nominal radius).
        WpfTestHost.Run(() =>
        {
            var p = ApplyTheme(dark ? "dark" : "light");
            try
            {
                var px = Render(
                    [Day8(), Day10()],
                    [Kimi, Glm, Deepseek],
                    UsageMetric.TotalTokens,
                    [Cost(D8, 0.60, false), Cost(D10, 1.25, true)]);
                var centres = BarCentres(px, p, [Kimi, Glm, Deepseek]);
                Assert.Equal(2, centres.Count);
                var ink = InkPixels(px, p, PlotInkTop, RenderH);

                // Priced dot (Nov 8): a solid ink disc (r = 2.5) — its centre pixel is ink.
                var cx1 = (int)Math.Round(centres[0]);
                var cy1 = (int)Math.Round(DotCentreY(ink, centres[0]));
                AssertInkAt(px, cx1, cy1, p, "priced dot centre");

                // Unpriced dot (Nov 10): hollow (r = 3) — centre is the opaque card surface…
                var cx2 = (int)Math.Round(centres[1]);
                var cy2 = (int)Math.Round(DotCentreY(ink, centres[1]));
                AssertSurfaceAt(px, cx2, cy2, p, "unpriced dot centre");

                // …and its ring is ink above, below, left and right (≈3 px out, ±1 px probe window).
                AssertInkNear(ink, cx2, cy2 - 3.0, "ring above");
                AssertInkNear(ink, cx2, cy2 + 3.0, "ring below");
                AssertInkNear(ink, cx2 - 3.0, cy2, "ring left");
                AssertInkNear(ink, cx2 + 3.0, cy2, "ring right");
            }
            finally
            {
                ApplyTheme("dark");
            }
        });
    }

    [Theory]
    [InlineData(false)] // light
    [InlineData(true)]  // dark
    public void F19_CostLegendSwatch_IsAnInkLineWiderThanTall(bool dark)
    {
        // F19 (IB major 2) over F14: "the 'Cost ($)' legend swatch's ink pixels are wider than
        // tall", in both themes. In the legend band the only ink on the control is this swatch —
        // model swatches are categorical, legend text is InkSecondary — so the ink bounding box
        // IS the swatch: ≈9 px wide, 2 px + anti-aliasing tall. Killing mutant: the old square
        // swatch (9×9) has width == height → fails; no swatch at all → fails on Assert.NotEmpty.
        WpfTestHost.Run(() =>
        {
            var p = ApplyTheme(dark ? "dark" : "light");
            try
            {
                var px = Render(
                    [Day8(), Day10()],
                    [Kimi, Glm, Deepseek],
                    UsageMetric.TotalTokens,
                    [Cost(D8, 0.60, false), Cost(D10, 1.25, true)]);

                var legendInk = InkPixels(px, p, 0, PlotInkTop);
                Assert.NotEmpty(legendInk);
                var (w, h) = Bounds(legendInk);
                Assert.True(w >= 6, $"swatch should span ≈9 px, measured width {w}");
                Assert.True(h <= 6, $"a 2 px line plus anti-aliasing expected, measured height {h}");
                Assert.True(w > h, $"legend swatch must be wider than tall (w={w}, h={h})");
            }
            finally
            {
                ApplyTheme("dark");
            }
        });
    }

    // ── Pixel-probe helpers (STA: call inside WpfTestHost.Run) ───────────────────

    /// <summary>Switches the shared ThemeManager (the control reads it in OnRender) and returns
    /// the matching palette. Always restore "dark" — the WpfTestHost default — in a finally.</summary>
    private static ThemePalette ApplyTheme(string preference)
    {
        ThemeManager.Instance.ApplyTheme(preference);
        return ThemePalette.For(ThemeManager.Instance.IsDark);
    }

    /// <summary>Pbgra32 is premultiplied; divide the stored channels back to the source colour so
    /// anti-aliased coverage pixels keep the palette hue they were drawn with.</summary>
    private static Color PixelColor(byte[] px, int x, int y)
    {
        var i = (y * RenderW + x) * 4;
        byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
        if (a is > 0 and < 255)
        {
            r = (byte)Math.Min(255, r * 255 / a);
            g = (byte)Math.Min(255, g * 255 / a);
            b = (byte)Math.Min(255, b * 255 / a);
        }

        return Color.FromRgb(r, g, b);
    }

    private static byte PixelAlpha(byte[] px, int x, int y) => px[(y * RenderW + x) * 4 + 3];

    private static bool Close(Color a, Color b, double tolerance)
    {
        var dr = (double)a.R - b.R;
        var dg = (double)a.G - b.G;
        var db = (double)a.B - b.B;
        return dr * dr + dg * dg + db * db <= tolerance * tolerance;
    }

    /// <summary>Every ink-coloured pixel of the render inside the [yMin, yMax) band.</summary>
    private static List<(int X, int Y)> InkPixels(byte[] px, ThemePalette p, int yMin, int yMax)
    {
        var hits = new List<(int, int)>();
        for (var y = Math.Max(0, yMin); y < Math.Min(RenderH, yMax); y++)
        {
            for (var x = 0; x < RenderW; x++)
            {
                if (PixelAlpha(px, x, y) >= MinAlpha && Close(PixelColor(px, x, y), p.Ink, InkTolerance))
                {
                    hits.Add((x, y));
                }
            }
        }

        return hits;
    }

    /// <summary>Centre x of each solid bar column: runs of ≥6 consecutive x's that carry opaque
    /// series-coloured pixels at or below <see cref="PlotInkTop"/>. The bars are exactly
    /// <c>thickness</c> wide around each day centre, so the run midpoint is that centre.</summary>
    private static List<double> BarCentres(byte[] px, ThemePalette p, IReadOnlyList<string> series)
    {
        var colours = Enumerable.Range(0, series.Count).Select(i => p.Series(i)).ToList();
        var centres = new List<double>();
        var start = -1;
        for (var x = 0; x <= RenderW; x++)
        {
            var hit = x < RenderW && HasSeriesPixel(x, colours);
            if (hit && start < 0)
            {
                start = x;
            }
            else if (!hit && start >= 0)
            {
                if (x - start >= 6)
                {
                    centres.Add((start + x - 1) / 2.0);
                }

                start = -1;
            }
        }

        return centres;

        bool HasSeriesPixel(int col, List<Color> palette)
        {
            for (var y = PlotInkTop; y < RenderH; y++)
            {
                if (PixelAlpha(px, col, y) >= 200)
                {
                    var c = PixelColor(px, col, y);
                    for (var i = 0; i < palette.Count; i++)
                    {
                        if (Close(c, palette[i], SeriesTolerance))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }

    /// <summary>Centre y of the dot at <paramref name="centreX"/>: the mid of the vertical ink run
    /// within ±1.5 px of the column. Solid dots and ink rings are both symmetric about the centre,
    /// and the line meets the dot at that same point, so min/max of the run brackets the dot.</summary>
    private static double DotCentreY(List<(int X, int Y)> ink, double centreX)
    {
        var ys = ink.Where(pt => Math.Abs(pt.X - centreX) <= 1.5).Select(pt => pt.Y).ToList();
        Assert.NotEmpty(ys);
        return (ys.Min() + ys.Max()) / 2.0;
    }

    private static bool InkNear(List<(int X, int Y)> ink, double x, double y, int radius) =>
        ink.Any(pt => Math.Abs(pt.X - x) <= radius && Math.Abs(pt.Y - y) <= radius);

    private static void AssertInkNear(List<(int X, int Y)> ink, double x, double y, string what) =>
        Assert.True(InkNear(ink, x, y, 1), $"{what}: expected ink near ({x:0.0}, {y:0.0})");

    private static void AssertInkAt(byte[] px, int x, int y, ThemePalette p, string what)
    {
        var a = PixelAlpha(px, x, y);
        var c = PixelColor(px, x, y);
        Assert.True(a >= 200 && Close(c, p.Ink, InkTolerance),
            $"{what}: expected ink at ({x}, {y}), found rgb({c.R},{c.G},{c.B}) a={a}");
    }

    private static void AssertSurfaceAt(byte[] px, int x, int y, ThemePalette p, string what)
    {
        var a = PixelAlpha(px, x, y);
        var c = PixelColor(px, x, y);
        Assert.True(a >= 200, $"{what}: expected an OPAQUE card-background fill at ({x}, {y}), found a={a}");
        Assert.True(Close(c, p.Surface, SurfaceTolerance),
            $"{what}: expected the surface colour at ({x}, {y}), found rgb({c.R},{c.G},{c.B})");
        Assert.False(Close(c, p.Ink, InkTolerance),
            $"{what}: the dot centre must not be solid ink at ({x}, {y})");
    }

    private static (int Width, int Height) Bounds(List<(int X, int Y)> pts) =>
        (pts.Max(pt => pt.X) - pts.Min(pt => pt.X) + 1, pts.Max(pt => pt.Y) - pts.Min(pt => pt.Y) + 1);
}
