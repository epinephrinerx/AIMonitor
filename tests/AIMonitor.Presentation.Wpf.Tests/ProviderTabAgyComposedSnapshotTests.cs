using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-018 A4/D5 (tab half) — <see cref="ProviderTabViewModel"/> rules that must let the composed AGY
/// snapshot render: the four new stats ("Total tokens", "Output tokens", "Cache reads", "Equivalent
/// API value") are not supplementary stats, so they stay as tiles and never enter a Plan/Credits/
/// Limit-style supplementary row; a snapshot carrying history shows the chart cards on the AGY tab;
/// and when the snapshot is unconfigured or has an error, the setup card / error banner stay while
/// the stats row and chart still show (D5). Written from Docs/SPEC_R018.md — the snapshots below are
/// hand-built in exactly the shape <c>AgyUsageComposer</c> returns (spec oracles 40K / 2,000 / 34K /
/// "$1.50 + unpriced"), so these pins hold independent of the composer's own tests. This file uses
/// only existing APIs and is expected to pass at the base commit: it verifies A4's "check the rules"
/// promise and Claude/OpenAI/OpenCode/Overview behaviour is left unchanged (the "Not included" list);
/// if a check finds a rule gap, the Writing Owner changes the source to satisfy these pins.
/// </summary>
public sealed class ProviderTabAgyComposedSnapshotTests
{
    private static readonly DateOnly Today = new(2026, 5, 1);

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 7, 0, 0, TimeSpan.Zero);

    private const string ModelA = "Model A";
    private const string ModelX = "Model X";

    private static UsageHistoryBucket Bucket(DateOnly day, Dictionary<string, double>? perModel = null) =>
        new(day, perModel);

    /// <summary>The range-7 history the composer produces for the spec oracle fixture: buckets
    /// Apr 25 … May 1 with Apr 30 = 10K and May 1 = 30K (A 20K + X 10K).</summary>
    private static UsageHistory OracleHistory() => new(
        buckets:
        [
            Bucket(new DateOnly(2026, 4, 25)),
            Bucket(new DateOnly(2026, 4, 26)),
            Bucket(new DateOnly(2026, 4, 27)),
            Bucket(new DateOnly(2026, 4, 28)),
            Bucket(new DateOnly(2026, 4, 29)),
            Bucket(new DateOnly(2026, 4, 30), new Dictionary<string, double> { [ModelA] = 10_000 }),
            Bucket(new DateOnly(2026, 5, 1), new Dictionary<string, double> { [ModelA] = 20_000, [ModelX] = 10_000 }),
        ],
        series: [ModelA, ModelX],
        byModel: [new UsageHistoryBreakdown(ModelA, 30_000), new UsageHistoryBreakdown(ModelX, 10_000)],
        byProject: [],
        days: 7,
        metric: UsageMetric.TotalTokens,
        dailyCosts:
        [
            new DailyCost(new DateOnly(2026, 4, 25), 0.0, false),
            new DailyCost(new DateOnly(2026, 4, 26), 0.0, false),
            new DailyCost(new DateOnly(2026, 4, 27), 0.0, false),
            new DailyCost(new DateOnly(2026, 4, 28), 0.0, false),
            new DailyCost(new DateOnly(2026, 4, 29), 0.0, false),
            new DailyCost(new DateOnly(2026, 4, 30), 0.50, false),
            new DailyCost(new DateOnly(2026, 5, 1), 1.00, true),
        ]);

    private static Stat[] ComposedStats() =>
    [
        new Stat("Requests today", "128"),
        new Stat("Total tokens", "40K"),
        new Stat("Output tokens", "2,000"),
        new Stat("Cache reads", "34K"),
        new Stat("Equivalent API value", "$1.50 + unpriced", "ccusage · equivalent API price"),
    ];

    // ── A4: stats row + chart visible on the AGY tab ────────────────────────────

    [Fact]
    public void UpdateFromSnapshot_ComposedAgy_ShowsFourCcusageTilesAndTheChart()
    {
        var tab = new ProviderTabViewModel("agy", "Gemini / AGY");

        tab.UpdateFromSnapshot(new ProviderSnapshot(
            "agy",
            configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 40.0)],
            stats: ComposedStats(),
            history: OracleHistory(),
            account: "paul@google.example",
            valueNote: "Quota from AGY CLI · tokens and cost from ccusage",
            fetchedAt: Now), Now);

        // The chart cards: HasHistory plus the seven buckets and their costs.
        Assert.True(tab.HasHistory);
        Assert.Equal(7, tab.ChartBuckets.Count);
        Assert.Equal(
            Enumerable.Range(25, 6).Select(d => new DateOnly(2026, 4, d)).Append(new DateOnly(2026, 5, 1)),
            tab.ChartBuckets.Select(b => b.Day));
        Assert.Equal(10_000.0, tab.ChartBuckets[5].Total);
        Assert.Equal(30_000.0, tab.ChartBuckets[6].Total);
        Assert.Equal([ModelA, ModelX], tab.ChartSeries);
        Assert.Equal("Usage per day · last 7 days", tab.ChartTitle);
        Assert.Equal(7, tab.ChartCosts.Count); // costs aligned onto the chart days
        Assert.Equal(0.50, tab.ChartCosts[5].PricedUsd, 10);
        Assert.True(tab.ChartCosts[6].HasUnpricedModels);
        Assert.True(tab.HasModelRows);
        Assert.False(tab.HasProjectRows); // ByProject empty — the "By project" card stays hidden

        // A4: none of the four new labels is a supplementary stat — every tile shows, in order,
        // and the value tile keeps its Detail.
        Assert.Equal(
            ["Requests today", "Total tokens", "Output tokens", "Cache reads", "Equivalent API value"],
            tab.StatTiles.Select(t => t.Label));
        Assert.Equal(["128", "40K", "2,000", "34K", "$1.50 + unpriced"],
            tab.StatTiles.Select(t => t.Value));
        Assert.Equal("ccusage · equivalent API price", tab.StatTiles[^1].Detail);

        // The ccusage stats never move into the supplementary rows on AGY (that section is the
        // R-014 rows area only); account/note pass through.
        Assert.Equal("paul@google.example", tab.AccountLabel);
        Assert.Equal("Quota from AGY CLI · tokens and cost from ccusage", tab.ValueNote);
        Assert.False(tab.HasError);
        Assert.False(tab.ShowSetupCard);
    }

    [Fact]
    public void UpdateFromSnapshot_UnconfiguredError_KeepsSetupCardAndBanner_StatsAndChartStillShow()
    {
        // D5 (tab half): when antigravity data exists, the stats row and chart show even when the
        // agy snapshot is unconfigured or has an error — and the setup card / error banner stay.
        var tab = new ProviderTabViewModel("agy", "Gemini / AGY");

        tab.UpdateFromSnapshot(new ProviderSnapshot(
            "agy",
            configured: false,
            stats: ComposedStats()[1..], // the four ccusage stats only
            history: OracleHistory(),
            error: "agy CLI exited with code 1",
            setupHint: "Install or sign in to AGY CLI, then refresh.",
            valueNote: "Quota from AGY CLI · tokens and cost from ccusage"), Now);

        Assert.True(tab.ShowSetupCard);          // as they are today
        Assert.True(tab.HasError);               // error banner stays
        Assert.Equal("agy CLI exited with code 1", tab.ErrorMessage);
        Assert.True(tab.HasHistory);             // ...and the chart shows alongside them
        Assert.Equal(7, tab.ChartBuckets.Count);
        Assert.Equal(["Total tokens", "Output tokens", "Cache reads", "Equivalent API value"],
            tab.StatTiles.Select(t => t.Label)); // stats row shows too
        Assert.Equal("Gemini / AGY", tab.TabTitle); // no meters → plain display name
    }

    [Fact]
    public void UpdateFromSnapshot_TodayLikeSnapshot_NoStatsNoHistory()
    {
        // "return agy unchanged (no stats, no history — today's look)" — what the tab shows when the
        // composer found nothing: the plain quota tab.
        var tab = new ProviderTabViewModel("agy", "Gemini / AGY");

        tab.UpdateFromSnapshot(new ProviderSnapshot(
            "agy",
            configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 40.0)],
            account: "paul@google.example",
            valueNote: "AGY shared model-pool quotas; used percentage.",
            fetchedAt: Now), Now);

        Assert.False(tab.HasHistory);
        Assert.Empty(tab.StatTiles);
        Assert.Empty(tab.ChartBuckets);
        Assert.Empty(tab.ChartCosts);
        Assert.False(tab.ShowSetupCard);
    }
}
