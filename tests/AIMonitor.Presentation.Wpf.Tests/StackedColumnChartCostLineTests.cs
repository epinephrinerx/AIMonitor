using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Controls;

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
/// axis) — pinned here by comparing whole bitmap renders. Written against Docs/SPEC_R017.md; the
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
}
