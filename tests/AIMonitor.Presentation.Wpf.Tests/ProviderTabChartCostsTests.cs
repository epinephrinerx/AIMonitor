using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-017 G1.3 — <c>ProviderTabViewModel.UpdateFromSnapshot</c> maps <c>history.DailyCosts</c> to
/// the new property <c>ChartCosts</c>: "empty when no history, aligned with <c>ChartBuckets</c> by
/// <c>Day</c> (only the days present in <c>ChartBuckets</c>, same order). Other providers get an
/// empty list." F7 (the Coding Owner's decision after Inspector A) adds the second half: "
/// <c>ChartCosts = history.DailyCosts</c> when there is history, and is cleared to empty in the
/// no-history branch (so an error snapshot after a good one leaves no stale costs)." Written from
/// Docs/SPEC_R017.md only (§5.6). The implementation is written in parallel; these tests compile
/// only after it merges.
/// </summary>
public sealed class ProviderTabChartCostsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 7, 3, 22, TimeSpan.Zero);

    private static readonly DateOnly D8 = new(2026, 11, 8);
    private static readonly DateOnly D9 = new(2026, 11, 9);
    private static readonly DateOnly D10 = new(2026, 11, 10);

    /// <summary>One usable history: <c>days</c> buckets each with model "m", plus the given
    /// per-day costs (none when the array is empty — the pre-R-017 shape every other producer makes).</summary>
    private static UsageHistory History(int days, params (DateOnly Day, double PricedUsd, bool Unpriced)[] costs) => new(
        buckets: Enumerable.Range(0, days)
            .Select(i => new UsageHistoryBucket(D10.AddDays(days * -1 + i + 1), new Dictionary<string, double> { ["m"] = 1.0 }))
            .ToList(),
        series: ["m"],
        byModel: [new UsageHistoryBreakdown("m", days)],
        days: days,
        dailyCosts: costs.Select(c => new DailyCost(c.Day, c.PricedUsd, c.Unpriced)).ToList());

    private static ProviderTabViewModel OpenCodeTabWith(UsageHistory? history)
    {
        var tab = new ProviderTabViewModel("opencode", "OpenCode");
        var snapshot = history is null
            ? new ProviderSnapshot("opencode", configured: true, error: "ccusage is not installed", fetchedAt: Now)
            : new ProviderSnapshot("opencode", configured: true, history: history, fetchedAt: Now);
        tab.UpdateFromSnapshot(snapshot, Now);
        return tab;
    }

    [Fact]
    public void ChartCosts_InitialTab_IsEmptyNotNull()
    {
        // G1.3: the tab's ChartCosts starts empty so the chart renders exactly as before any snapshot.
        var tab = new ProviderTabViewModel("opencode", "OpenCode");

        Assert.Empty(tab.ChartCosts);
    }

    [Fact]
    public void UpdateFromSnapshot_WithHistory_ChartCostsMirrorsDailyCostsInOrder()
    {
        // F7: "ChartCosts = history.DailyCosts when there is history" — aligned with ChartBuckets by
        // Day, same order (G1.3).
        var costs = new[] { (Day: D8, PricedUsd: 0.60, Unpriced: false), (Day: D9, PricedUsd: 0.0, Unpriced: false), (Day: D10, PricedUsd: 1.25, Unpriced: true) };
        var history = History(3, costs);

        var tab = OpenCodeTabWith(history);

        Assert.Equal(3, tab.ChartBuckets.Count);
        Assert.Equal(costs.Select(c => c.Day), tab.ChartCosts.Select(c => c.Day));
        Assert.Equal(0.60, tab.ChartCosts[0].PricedUsd, 10);
        Assert.Equal(0.0, tab.ChartCosts[1].PricedUsd);
        Assert.Equal(1.25, tab.ChartCosts[2].PricedUsd, 10);
        Assert.True(tab.ChartCosts[2].HasUnpricedModels);
    }

    [Fact]
    public void UpdateFromSnapshot_ChartCosts_CarryOnlyDaysPresentInChartBucketsSameOrder()
    {
        // G1.3 alignment: UpdateFromSnapshot keeps TakeLast(history.Days) buckets (here 2 of 3 days),
        // so ChartCosts must drop 11-08 and hold [11-09, 11-10] in the buckets' order.
        var history = History(3, (D8, 0.60, false), (D9, 0.0, false), (D10, 1.25, true));
        var shortHistory = new UsageHistory(
            buckets: history.Buckets,
            series: history.Series,
            byModel: history.ByModel,
            days: 2, // ChartBuckets = the last two days
            dailyCosts: history.DailyCosts);

        var tab = OpenCodeTabWith(shortHistory);

        Assert.Equal([D9, D10], tab.ChartBuckets.Select(b => b.Day));
        Assert.Equal([D9, D10], tab.ChartCosts.Select(c => c.Day));
        Assert.Equal(1.25, tab.ChartCosts[1].PricedUsd, 10);
        Assert.True(tab.ChartCosts[1].HasUnpricedModels);
    }

    [Fact]
    public void UpdateFromSnapshot_NoHistory_ChartCostsEmpty()
    {
        // G1.3 "(empty when no history)" / F7's no-history branch.
        var tab = OpenCodeTabWith(history: null);

        Assert.False(tab.HasHistory);
        Assert.Empty(tab.ChartBuckets);
        Assert.Empty(tab.ChartCosts);
    }

    [Fact]
    public void UpdateFromSnapshot_HistoryWithoutBuckets_ChartCostsEmptyToo()
    {
        // The no-history branch is the `history.Buckets.Count > 0` test already used for
        // HasHistory/ChartBuckets: a zero-bucket history takes it, so its costs are cleared as well
        // ("only the days present in ChartBuckets" — none).
        var history = new UsageHistory(days: 7, dailyCosts: [new DailyCost(D8, 0.60, false)]);

        var tab = OpenCodeTabWith(history);

        Assert.False(tab.HasHistory);
        Assert.Empty(tab.ChartBuckets);
        Assert.Empty(tab.ChartCosts);
    }

    [Fact]
    public void UpdateFromSnapshot_F7_ErrorSnapshotAfterAGoodOne_ClearsStaleCosts()
    {
        // F7 (m5/m6): "cleared to empty in the no-history branch (so an error snapshot after a good
        // one leaves no stale costs)" — the chart would otherwise draw the last good day's line.
        var tab = OpenCodeTabWith(History(3, (D8, 0.60, false), (D9, 0.0, false), (D10, 1.25, true)));
        Assert.NotEmpty(tab.ChartCosts);

        tab.UpdateFromSnapshot(
            new ProviderSnapshot("opencode", configured: true, error: "ccusage failed this time", fetchedAt: Now),
            Now);

        Assert.Empty(tab.ChartCosts);
        Assert.Empty(tab.ChartBuckets);
        Assert.False(tab.HasHistory);
    }

    [Fact]
    public void UpdateFromSnapshot_GoodAfterError_RepopulatesCosts()
    {
        // The pair of F7's rule: clearing must not poison the next good snapshot.
        var tab = OpenCodeTabWith(history: null);
        Assert.Empty(tab.ChartCosts);

        tab.UpdateFromSnapshot(
            new ProviderSnapshot("opencode", configured: true, history: History(3, (D8, 0.60, false)), fetchedAt: Now),
            Now);

        Assert.Single(tab.ChartCosts);
        Assert.Equal(new DateOnly(2026, 11, 8), tab.ChartCosts[0].Day);
    }

    [Theory]
    // G1.3: "Other providers get an empty list." Their snapshots still carry the pre-R-017 history
    // shape — a UsageHistory with no DailyCosts — and F7's mapping is unconditioned on the provider,
    // so those tabs land on the empty default. (No out-of-scope provider ever builds DailyCosts:
    // the cost line is OpenCode-only per the SPEC's Out of scope.)
    [InlineData("claude", "Claude")]
    [InlineData("openai", "OpenAI / Codex")]
    [InlineData("agy", "Gemini / AGY")]
    public void UpdateFromSnapshot_OtherProviders_HistoryWithoutCosts_ChartCostsEmpty(string providerId, string displayName)
    {
        var tab = new ProviderTabViewModel(providerId, displayName);
        var history = new UsageHistory(
            buckets: [new UsageHistoryBucket(D8, new Dictionary<string, double> { ["opus"] = 10 })],
            series: ["opus"],
            byModel: [new UsageHistoryBreakdown("opus", 10)],
            days: 14);

        tab.UpdateFromSnapshot(new ProviderSnapshot(providerId, configured: true, history: history, fetchedAt: Now), Now);

        Assert.True(tab.HasHistory); // the charts themselves are unchanged for these tabs
        Assert.Empty(tab.ChartCosts);
    }
}
