using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.Agents;

/// <summary>
/// R-018 A2/D4/D5/D7 — <c>AgyUsageComposer.Compose(agy, report, today, rangeDays, metric)</c>,
/// written from Docs/SPEC_R018.md only (§5.6 Tests + decisions). Returns a copy of the agy snapshot
/// (meters, account, error, detection, setup hint always preserved — D5) with
/// <c>History = AgentHistoryBuilder.Build(report, "antigravity", …)</c> (only when the snapshot has
/// no history — A2) and <c>Stats</c> = the agy stats followed by four stats over the same inclusive
/// range (D4): "Total tokens", "Output tokens", "Cache reads" (sums of the agent-level
/// <c>AgentUsage.Tokens</c> of the in-range antigravity daily rows, formatted like the chart labels —
/// ChartFormat.Compact), and "Equivalent API value" (D7: priced-only <c>$X.XX</c>, priced+unpriced
/// <c>$X.XX + unpriced</c>, all-unpriced <c>Unknown price</c>, no models <c>$0.00</c>, via
/// ChartFormat.Money; <c>Stat.Detail</c> = "ccusage · equivalent API price"). Stats and history are
/// added only when at least one antigravity daily row lies in the range; null report or no in-range
/// rows returns agy unchanged (same instance or field-equal). D10: the same-instance rule also
/// covers a snapshot that ALREADY has history — nothing is composed at all. Hand-computed oracles
/// (spec test oracles): today 2026-05-01; Apr 30 Model A 10,000/500/8,500/$0.50; May 1 Model A
/// 20,000/1,000/17,000/$1.00 + Model X 10,000/500/8,500/null. Range 7 → 40K / 2,000 / 34K /
/// "$1.50 + unpriced"; range 1 → 30K / 1,500 / 25.5K / "$1.00 + unpriced". Pure composer; dates from
/// the test. The new type does not exist at the base commit — this file is R-018 "expected red".
/// The composer's ValueNote behaviour (D9) is pinned at the tab level in the view-model tests, not
/// here, so A2's "every field kept" and D9's note override can meet at the call site.
/// </summary>
public sealed class AgyUsageComposerTests
{
    private static readonly DateOnly Today = new(2026, 5, 1);

    private static readonly DateTimeOffset FetchedAt = new(2026, 5, 1, 6, 30, 0, TimeSpan.Zero);

    private const string ModelA = "Model A"; // priced
    private const string ModelX = "Model X"; // unpriced

    private static ModelUsage Model(string name, long total, long output, long cacheRead, decimal? costUsd)
    {
        var input = total - output - cacheRead;
        return new ModelUsage(name, new TokenCounts(input, output, 0, cacheRead, total), costUsd);
    }

    private static AgentUsage Agent(string id, params ModelUsage[] models) =>
        new(id, new TokenCounts(
            models.Sum(m => m.Tokens.Input),
            models.Sum(m => m.Tokens.Output),
            models.Sum(m => m.Tokens.CacheCreation),
            models.Sum(m => m.Tokens.CacheRead),
            models.Sum(m => m.Tokens.Total)),
            models);

    private static AgentUsage AgentWithTokens(string id, TokenCounts tokens) => new(id, tokens, []);

    private static AgentUsageReport Report(params (DateOnly Day, AgentUsage[] Agents)[] days) => new(
        daily: days.Select(d => new DailyAgentUsage(d.Day, d.Agents)).ToList(),
        monthly: [],
        sessions: [],
        unpricedModels: [],
        pricing: AgentUsagePricing.OnlineRequested,
        retrievedAt: FetchedAt,
        skippedRows: 0);

    /// <summary>The spec oracle report: Apr 30 and May 1 antigravity rows plus other-agent noise that
    /// must never enter the tiles, the chart or the "+ unpriced" flag.</summary>
    private static AgentUsageReport OracleReport() => Report(
        (new DateOnly(2026, 4, 30), [
            Agent("antigravity", Model(ModelA, 10_000, 500, 8_500, 0.50m)),
            Agent("claude", Model("claude-opus-5-5", 7_777, 777, 0, 7.77m))]),
        (new DateOnly(2026, 5, 1), [
            Agent("antigravity", Model(ModelA, 20_000, 1_000, 17_000, 1.00m), Model(ModelX, 10_000, 500, 8_500, null)),
            Agent("codex", Model("gpt-5.1-codex", 999_999, 9_999, 0, null))])); // other agents' null cost must not flag

    private static ProviderSnapshot AgySnapshot(
        IReadOnlyList<Stat>? stats = null,
        UsageHistory? history = null,
        bool configured = true,
        string? error = null,
        IReadOnlyList<Meter>? meters = null) => new(
            "agy",
            configured: configured,
            meters: meters ?? [
                new Meter("session", "session", "Session", "5-hour window", 40.0),
                new Meter("weekly", "weekly", "Weekly", "7-day window", 61.0, section: "Gemini"),
            ],
            stats: stats ?? [new Stat("Requests today", "128"), new Stat("Pool", "shared")],
            history: history,
            account: "paul@google.example",
            error: error,
            setupHint: "Install or sign in to AGY CLI, then refresh.",
            valueNote: "AGY shared model-pool quotas; used percentage.",
            fetchedAt: FetchedAt,
            detection: new DetectionInfo("agy", DetectionState.Connected, sourceId: "agy-cli", sourceLabel: "AGY CLI"));

    private static ProviderSnapshot Compose(
        ProviderSnapshot agy,
        AgentUsageReport? report,
        int rangeDays = 7,
        string metric = UsageMetric.TotalTokens) =>
        AgyUsageComposer.Compose(agy, report, Today, rangeDays, metric);

    /// <summary>D4/D7 tile oracle check: the agy stats stay first, then the four new stats with the
    /// hand-computed values. Detail is pinned only on "Equivalent API value" (D7).</summary>
    private static void AssertNewTiles(ProviderSnapshot composed, int existingCount, string total, string output, string cacheReads, string value)
    {
        Assert.Equal(existingCount + 4, composed.Stats.Count);
        var added = composed.Stats.Skip(existingCount).ToList();
        Assert.Equal(["Total tokens", "Output tokens", "Cache reads", "Equivalent API value"],
            added.Select(s => s.Label));
        Assert.Equal([total, output, cacheReads, value], added.Select(s => s.Value));
        Assert.Equal("ccusage · equivalent API price", added[3].Detail);
    }

    private static void AssertAgyFieldsPreserved(ProviderSnapshot agy, ProviderSnapshot composed)
    {
        // A2 "every field kept" + D5 "Quota meters, account, error, detection, setup hint are
        // always preserved". ValueNote is deliberately not pinned here (D9 — see class summary).
        Assert.Equal("agy", composed.ProviderId);
        Assert.Equal(agy.Configured, composed.Configured);
        Assert.Equal(agy.Meters, composed.Meters);
        Assert.Equal(agy.Account, composed.Account);
        Assert.Equal(agy.Error, composed.Error);
        Assert.Equal(agy.HistoryError, composed.HistoryError);
        Assert.Equal(agy.Unauthorized, composed.Unauthorized);
        Assert.Equal(agy.SetupHint, composed.SetupHint);
        Assert.Equal(agy.FetchedAt, composed.FetchedAt);
        Assert.Equal(agy.Detection, composed.Detection);
    }

    // ── The hand oracle — range 7 ───────────────────────────────────────────────

    [Fact]
    public void Compose_Range7_Oracle_AppendsFourStatsAndBuildsHistory()
    {
        var agy = AgySnapshot();

        var composed = Compose(agy, OracleReport(), rangeDays: 7);

        AssertAgyFieldsPreserved(agy, composed);

        // Existing stats kept, in order, then the four new ones: 40K / 2,000 / 34K / $1.50 + unpriced.
        Assert.Equal(agy.Stats, composed.Stats.Take(2));
        AssertNewTiles(composed, 2, "40K", "2,000", "34K", "$1.50 + unpriced");

        // History = AgentHistoryBuilder over the same range/metric: seven buckets Apr 25 … May 1,
        // Apr 30 = 10K, May 1 = 30K, five empty (spec oracle).
        var history = composed.History;
        Assert.NotNull(history);
        Assert.Equal(7, history!.Days);
        Assert.Equal(
            Enumerable.Range(25, 6).Select(d => new DateOnly(2026, 4, d)).Append(new DateOnly(2026, 5, 1)),
            history.Buckets.Select(b => b.Day));
        Assert.Equal(10_000.0, history.Buckets[5].Total); // Apr 30
        Assert.Equal(30_000.0, history.Buckets[6].Total); // May 1 (Model A 20K + Model X 10K)
        Assert.All(history.Buckets.Where((_, i) => i is not 5 and not 6), b => Assert.Empty(b.PerModel));
        Assert.Equal([ModelA, ModelX], history.Series);
        Assert.Equal(UsageMetric.TotalTokens, history.Metric);
    }

    [Fact]
    public void Compose_Range1_Oracle_OnlyTodayCounts()
    {
        var composed = Compose(AgySnapshot(), OracleReport(), rangeDays: 1);

        // 30,000 → "30K"; 1,500 → "1,500"; 25,500 → "25.5K"; priced $1.00 with unpriced Model X.
        AssertNewTiles(composed, 2, "30K", "1,500", "25.5K", "$1.00 + unpriced");
        var bucket = Assert.Single(composed.History!.Buckets);
        Assert.Equal(Today, bucket.Day);
        Assert.Equal(30_000.0, bucket.Total);
    }

    [Fact]
    public void Compose_MetricChange_TilesStayFixed_ChartFollowsMetric()
    {
        // D4: the tiles always sum Total/Output/CacheRead of the agent-level tokens — independent of
        // the chart metric; the chart (A1 history) follows it.
        var composed = Compose(AgySnapshot(), OracleReport(), rangeDays: 7, metric: UsageMetric.OutputTokens);

        AssertNewTiles(composed, 2, "40K", "2,000", "34K", "$1.50 + unpriced");
        Assert.Equal(UsageMetric.OutputTokens, composed.History!.Metric);
        Assert.Equal(new Dictionary<string, double> { [ModelA] = 1_000, [ModelX] = 500 },
            composed.History.Buckets[6].PerModel);
    }

    // ── D4 — the gate, the inclusive window, and rows without models ────────────

    [Fact]
    public void Compose_NullReport_ReturnsAgyUnchanged()
    {
        var agy = AgySnapshot();

        var composed = Compose(agy, report: null);

        Assert.Equal(agy, composed); // same instance or field-equal — nothing added
    }

    [Fact]
    public void Compose_AntigravityMissingFromReport_ReturnsAgyUnchanged()
    {
        var agy = AgySnapshot();
        var report = Report(
            (new DateOnly(2026, 4, 30), [Agent("claude", Model("claude-opus-5-5", 7_777, 777, 0, 7.77m))]),
            (Today, [Agent("opencode", Model("glm-5.3-flash", 300, 30, 0, 0.03m))]));

        var composed = Compose(agy, report);

        Assert.Equal(agy, composed); // no stats, no history — today's look (A2)
    }

    [Fact]
    public void Compose_AntigravityRowsOnlyOutsideWindow_ReturnsAgyUnchanged()
    {
        // D4: stats/history are added only when at least one antigravity daily row lies in the
        // inclusive range — Apr 24 is one day before the range-7 window start (Apr 25).
        var agy = AgySnapshot();
        var report = Report(
            (new DateOnly(2026, 4, 24), [Agent("antigravity", Model(ModelA, 60_000, 3_000, 0, 60.00m))]));

        Assert.Equal(agy, Compose(agy, report, rangeDays: 7));
    }

    [Fact]
    public void Compose_AntigravityRowOnFirstWindowDay_IsAdded()
    {
        // The window's first day is inclusive: range 7 from May 1 starts Apr 25 — exactly the day
        // that the previous test's Apr 24 row misses by one.
        var report = Report(
            (new DateOnly(2026, 4, 25), [Agent("antigravity", Model(ModelA, 1_000, 100, 0, 0.10m))]));

        var composed = Compose(AgySnapshot(), report, rangeDays: 7);

        AssertNewTiles(composed, 2, "1,000", "100", "0", "$0.10");
        Assert.Equal(1_000.0, Assert.Single(composed.History!.Buckets[0].PerModel).Value);
    }

    [Fact]
    public void Compose_RowWithoutModels_CountsInTiles_ShowsEmptyChartDay_ValueZeroDollars()
    {
        // D4: "A row without models counts in the tiles and shows an empty chart day" + D7: "no
        // models in range → $0.00". Agent-level tokens (500 total / 100 output / 400 cache reads)
        // with an empty Models list.
        var report = Report((Today, [AgentWithTokens("antigravity", new TokenCounts(0, 100, 0, 400, 500))]));

        var composed = Compose(AgySnapshot(), report, rangeDays: 1);

        AssertNewTiles(composed, 2, "500", "100", "400", "$0.00");
        var bucket = Assert.Single(composed.History!.Buckets);
        Assert.Empty(bucket.PerModel); // empty chart day
        Assert.Empty(composed.History.Series);
        Assert.Equal((0.0, false), (composed.History.DailyCosts[0].PricedUsd,
                                    composed.History.DailyCosts[0].HasUnpricedModels));
    }

    // ── D7 — the "Equivalent API value" tile ────────────────────────────────────

    [Fact]
    public void Compose_AllModelsUnpriced_ValueIsUnknownPrice()
    {
        var report = Report((Today, [Agent("antigravity", Model(ModelX, 10_000, 500, 8_500, null))]));

        var composed = Compose(AgySnapshot(), report, rangeDays: 1);

        // D7: all models unpriced → "Unknown price" (not "$0.00 + unpriced").
        Assert.Equal("Unknown price", composed.Stats.Last(s => s.Label == "Equivalent API value").Value);
        Assert.True(composed.History!.DailyCosts[0].HasUnpricedModels);
        Assert.Equal(0.0, composed.History.DailyCosts[0].PricedUsd);
    }

    [Fact]
    public void Compose_PricedOnly_ValueIsPlainMoney()
    {
        var report = Report((Today, [Agent("antigravity", Model(ModelA, 20_000, 1_000, 17_000, 1.00m))]));

        var composed = Compose(AgySnapshot(), report, rangeDays: 1);

        Assert.Equal("$1.00", composed.Stats.Last(s => s.Label == "Equivalent API value").Value);
    }

    [Fact]
    public void Compose_UnpricedModelOutsideWindow_DoesNotFlagTheValue()
    {
        // "+ unpriced" applies only when some model IN RANGE has no cost (A2/D4): the unpriced row
        // sits on Apr 24, one day outside the range-7 window; May 1 is priced $1.00.
        var report = Report(
            (new DateOnly(2026, 4, 24), [Agent("antigravity", Model(ModelX, 10_000, 500, 8_500, null))]),
            (Today, [Agent("antigravity", Model(ModelA, 20_000, 1_000, 17_000, 1.00m))]));

        var composed = Compose(AgySnapshot(), report, rangeDays: 7);

        Assert.Equal("$1.00", composed.Stats.Last(s => s.Label == "Equivalent API value").Value);
        Assert.Equal("20K", composed.Stats.Single(s => s.Label == "Total tokens").Value); // only May 1 (20,000) is in range
    }

    // ── A2/D10 — existing agy history short-circuits the whole compose ──────────

    [Fact]
    public void Compose_AgySnapshotAlreadyHasHistory_ReturnsTheSameInstanceUnchanged()
    {
        // D10: A2's last sentence is literal — stats, history and note are composed ONLY when
        // agy.History is null; a snapshot that already carries history is returned unchanged,
        // the same instance, with nothing appended even when in-range antigravity data exists.
        var ownHistory = new UsageHistory(
            buckets: [new UsageHistoryBucket(Today, new Dictionary<string, double> { ["agy-model"] = 42 })],
            series: ["agy-model"],
            days: 1);
        var agy = AgySnapshot(history: ownHistory);

        var composed = Compose(agy, OracleReport(), rangeDays: 1);

        Assert.Same(agy, composed);
        Assert.Same(ownHistory, composed.History);            // kept as it was, not rebuilt
        Assert.Equal(2, composed.Stats.Count);                // no tiles appended
        Assert.DoesNotContain(composed.Stats, s => s.Label == "Total tokens");
        Assert.Equal("AGY shared model-pool quotas; used percentage.", composed.ValueNote); // note not overridden
    }

    // ── D5 — unconfigured / error snapshots still get stats and chart ───────────

    [Fact]
    public void Compose_UnconfiguredWithErrorSnapshot_KeepsErrorFields_AndAddsStatsAndHistory()
    {
        // D5: antigravity data exists → stats row and chart show even when the snapshot is
        // unconfigured and carries an error; the error/setup fields stay exactly as they were.
        var agy = AgySnapshot(
            configured: false,
            error: "agy CLI exited with code 1",
            meters: []);

        var composed = Compose(agy, OracleReport(), rangeDays: 1);

        Assert.False(composed.Configured);
        Assert.Equal("agy CLI exited with code 1", composed.Error);
        Assert.Empty(composed.Meters);
        Assert.Equal("Install or sign in to AGY CLI, then refresh.", composed.SetupHint);
        Assert.NotNull(composed.History);
        AssertNewTiles(composed, 2, "30K", "1,500", "25.5K", "$1.00 + unpriced");
    }

    [Fact]
    public void Compose_ErrorSnapshotWithInRangeData_StillAdds_AndEmptyReportReturnsUnchanged()
    {
        var agy = AgySnapshot(configured: false, error: "agy CLI exited with code 1", meters: []);

        var composed = Compose(agy, OracleReport(), rangeDays: 7, metric: UsageMetric.TotalTokens);

        // Sanity that range 7 still adds (Apr 30 + May 1 in window) — and an out-of-window-only
        // variant returns unchanged:
        Assert.NotNull(composed.History);

        var missing = Compose(agy, Report(), rangeDays: 7);
        Assert.Equal(agy, missing); // empty report → no rows at all → unchanged
    }
}
