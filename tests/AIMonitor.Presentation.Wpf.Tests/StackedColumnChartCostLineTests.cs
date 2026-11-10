using System.Windows;
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
/// tooltip still shows Cost: $0.00). The implementation is written in parallel; these tests
/// compile only after it merges.
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
            StackedColumnChartControl.TooltipText(Day8(), [Kimi, Glm], "Total tokens"));
        Assert.Equal(
            Join("Nov 4", "No usage"),
            StackedColumnChartControl.TooltipText(Empty(D4), [Kimi, Glm], "Total tokens"));
    }

    [Fact]
    public void TooltipText_H9_OverloadWithNullAndEmptyCosts_MatchesTheThreeArgumentText()
    {
        // F2 (H9) "the tooltip with no costs equals the existing text" + F4's forwarding.
        foreach (var bucket in new[] { Day8(), Day10(), Empty(D9) })
        {
            var plain = StackedColumnChartControl.TooltipText(bucket, [Kimi, Glm, Deepseek], "Total tokens");
            Assert.Equal(plain, StackedColumnChartControl.TooltipText(bucket, [Kimi, Glm, Deepseek], "Total tokens", null, 0));
            Assert.Equal(plain, StackedColumnChartControl.TooltipText(bucket, [Kimi, Glm, Deepseek], "Total tokens", [], 0));
        }
    }

    // ── F3/F4: the cost line on the tooltip ────────────────────────────────────

    [Fact]
    public void Tooltip_CostEntryForTheDay_AppendsMoneyLine()
    {
        // G1.4/F3: "the hover tooltip for a day adds a line Cost: {ChartFormat.Money(PricedUsd)}".
        // Money(0.60) = "$0.60" (ChartFormat pins the formatter; see ChartTests.Money_*).
        var costs = new[] { Cost(D8, 0.60, false), Cost(D10, 1.25, true) };

        var text = StackedColumnChartControl.TooltipText(Day8(), [Kimi, Glm, Deepseek], "Total tokens", costs, index: 0);

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

        var text = StackedColumnChartControl.TooltipText(Day10(), [Kimi, Glm, Deepseek], "Total tokens", costs, index: 1);

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
            StackedColumnChartControl.TooltipText(Empty(D4), [Kimi, Glm], "Total tokens", costs, index: 0));
        Assert.Equal(
            Join("Nov 9", "No usage", "Cost: $0.00 + unpriced"),
            StackedColumnChartControl.TooltipText(Empty(D9), [Kimi, Glm], "Total tokens", costs, index: 1));
    }

    [Fact]
    public void Tooltip_F4_DayWithoutACostEntry_HasNoCostLine()
    {
        // F4 (m4): "the cost entry is matched by Day (no entry for that day → no cost line)" —
        // costs covers 11-08 and 11-10; the hovered bucket is 11-09 (bucket index 1). An
        // implementation that took costs[index] positionally would borrow 11-10's $1.25 line here;
        // matching by Day finds no entry and draws none.
        var costs = new[] { Cost(D8, 0.60, false), Cost(D10, 1.25, true) };

        var text = StackedColumnChartControl.TooltipText(Empty(D9), [Kimi, Glm, Deepseek], "Total tokens", costs, index: 1);

        Assert.Equal(Join("Nov 9", "No usage"), text); // byte-identical to the no-costs text
        Assert.DoesNotContain("Cost:", text);
    }

    // ── G1.4: the Costs dependency property itself ─────────────────────────────

    [Fact]
    public void Costs_DependencyProperty_IsAnOptionalAffectsRenderList()
    {
        // G1.4: "New dependency property Costs (IReadOnlyList<DailyCost>?, AffectsRender). Null or
        // empty → the control renders exactly as today" — the default is therefore null, and a
        // change re-renders.
        Assert.Equal(typeof(IReadOnlyList<DailyCost>), StackedColumnChartControl.CostsProperty.PropertyType);
        Assert.Null(StackedColumnChartControl.CostsProperty.DefaultValue);
        var metadata = Assert.IsType<FrameworkPropertyMetadata>(StackedColumnChartControl.CostsProperty.DefaultMetadata);
        Assert.True(metadata.Options.HasFlag(FrameworkPropertyMetadataOptions.AffectsRender));
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
}
