using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-018 A1/D4 — <c>AgentHistoryBuilder.Build(report, agentId, today, rangeDays, metric)</c>,
/// written from Docs/SPEC_R018.md only (§5.6 Tests + decisions): the builder is the generalisation of
/// <c>OpenCodeHistoryBuilder.Build</c> with identical behaviour for an explicit agent id; the window is
/// the inclusive local days <c>today − (rangeDays − 1) … today</c> (D4 — month boundaries do not
/// matter); only the named agent's models contribute to buckets, series and the cost line; unknown
/// metric strings normalise to "Total tokens"; a null report returns null and rangeDays ≤ 0 throws,
/// as before. <c>OpenCodeHistoryBuilder</c> must keep its public API and delegate with "opencode"
/// (A1) — pinned here by content equality against the same report (D11: record equality cannot
/// hold across two builds — the history's lists and per-model dictionaries are defensive copies).
/// Hand-computed oracles from the
/// spec: today 2026-05-01; Apr 30 <c>antigravity</c> Model A 10,000 total / 500 output / 8,500 cache
/// reads / $0.50; May 1 Model A 20,000 / 1,000 / 17,000 / $1.00 and Model X 10,000 / 500 / 8,500 /
/// null (unpriced). Range 7 → seven buckets with Apr 30 = 10K, May 1 = 30K (A 20K + X 10K) and five
/// empty. Pure builder: no I/O, no clock; every date comes from the test. The new type does not exist
/// at the base commit, so this file is the R-018 "expected red" against the base.
/// </summary>
public sealed class AgentHistoryBuilderTests
{
    private static readonly DateOnly Today = new(2026, 5, 1);

    private static readonly DateTimeOffset RetrievedAt = new(2026, 5, 1, 7, 0, 0, TimeSpan.Zero);

    private const string ModelA = "Model A";   // priced: $0.50 Apr 30, $1.00 May 1
    private const string ModelX = "Model X";   // unpriced (CostUsd null) on May 1
    private const string Opus = "claude-opus-5-5";
    private const string Gpt = "gpt-5.1-codex";

    private static ModelUsage Model(string name, long total, long output, long cacheRead, decimal? costUsd)
    {
        // input = total − output − cacheRead so the hand fixture (total/output/cache reads) maps
        // exactly onto TokenCounts(input, output, cacheCreation=0, cacheRead, total).
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

    private static AgentUsageReport Report(params (DateOnly Day, AgentUsage[] Agents)[] days) => new(
        daily: days.Select(d => new DailyAgentUsage(d.Day, d.Agents)).ToList(),
        monthly: [],
        sessions: [],
        unpricedModels: [],
        pricing: AgentUsagePricing.OnlineRequested,
        retrievedAt: RetrievedAt,
        skippedRows: 0);

    /// <summary>The spec oracle report (hand-computed in SPEC_R018): Apr 30 and May 1 antigravity
    /// rows, plus noise that must never leak — a claude row on both days, a codex row on May 1, and
    /// an unpriced claude model on Apr 26 (an antigravity-empty day: other agents' null costs must
    /// not flag it, the H4 rule generalised).</summary>
    private static AgentUsageReport OracleReport() => Report(
        (new DateOnly(2026, 4, 26), [Agent("claude", Model(Opus, 5_000, 400, 0, null))]),
        (new DateOnly(2026, 4, 30), [
            Agent("antigravity", Model(ModelA, 10_000, 500, 8_500, 0.50m)),
            Agent("claude", Model(Opus, 7_777, 777, 0, 7.77m))]),
        (new DateOnly(2026, 5, 1), [
            Agent("antigravity", Model(ModelA, 20_000, 1_000, 17_000, 1.00m), Model(ModelX, 10_000, 500, 8_500, null)),
            Agent("codex", Model(Gpt, 999_999, 9_999, 0, 99.99m)),
            Agent("claude", Model(Opus, 6_000, 600, 0, 6.00m))]));

    private static UsageHistory Build(
        AgentUsageReport report,
        string agentId = "antigravity",
        int rangeDays = 7,
        string metric = UsageMetric.TotalTokens,
        DateOnly? today = null)
    {
        var history = AgentHistoryBuilder.Build(
            report: report, agentId: agentId, today: today ?? Today, rangeDays: rangeDays, metric: metric);
        Assert.NotNull(history);
        return history!;
    }

    // ── The spec oracle: antigravity, range 7 ──────────────────────────────────

    [Fact]
    public void Build_Antigravity_Range7_SevenBucketsWithHandOracleValues()
    {
        var h = Build(OracleReport());

        // Seven buckets Apr 25 … May 1 (window = today − 6 … today inclusive, D4; crosses the month
        // boundary — month edges do not matter), oldest first.
        Assert.Equal(7, h.Days);
        Assert.Equal(Enumerable.Range(25, 6).Select(d => new DateOnly(2026, 4, d)).Append(new DateOnly(2026, 5, 1)),
            h.Buckets.Select(b => b.Day));

        var byDay = h.Buckets.ToDictionary(b => b.Day);
        Assert.Empty(byDay[new DateOnly(2026, 4, 25)].PerModel); // five empty days ...
        Assert.Empty(byDay[new DateOnly(2026, 4, 27)].PerModel);
        Assert.Empty(byDay[new DateOnly(2026, 4, 28)].PerModel);
        Assert.Empty(byDay[new DateOnly(2026, 4, 29)].PerModel);
        Assert.Empty(byDay[new DateOnly(2026, 4, 26)].PerModel); // the claude-only day stays empty (A1)
        Assert.Equal(new Dictionary<string, double> { [ModelA] = 10_000 }, byDay[new DateOnly(2026, 4, 30)].PerModel);
        Assert.Equal(
            new Dictionary<string, double> { [ModelA] = 20_000, [ModelX] = 10_000 },
            byDay[new DateOnly(2026, 5, 1)].PerModel); // May 1 = 30K over two models

        // Series/ByModel: window totals — Model A 10,000 + 20,000 = 30,000 > Model X 10,000.
        Assert.Equal([ModelA, ModelX], h.Series);
        Assert.Equal(
            [new UsageHistoryBreakdown(ModelA, 30_000), new UsageHistoryBreakdown(ModelX, 10_000)],
            h.ByModel);
        Assert.Empty(h.ByProject);
        Assert.Equal(UsageMetric.TotalTokens, h.Metric);

        // DailyCosts follow the antigravity rows only: Apr 26's unpriced claude model must NOT flag
        // the day (the OpenCode H4 rule generalised to every agentId).
        Assert.Equal(h.Buckets.Select(b => b.Day), h.DailyCosts.Select(c => c.Day));
        var byCostDay = h.DailyCosts.ToDictionary(c => c.Day);
        foreach (var day in new[]
                 {
                     new DateOnly(2026, 4, 25), new DateOnly(2026, 4, 26), new DateOnly(2026, 4, 27),
                     new DateOnly(2026, 4, 28), new DateOnly(2026, 4, 29),
                 })
        {
            Assert.Equal((0.0, false), (byCostDay[day].PricedUsd, byCostDay[day].HasUnpricedModels));
        }

        Assert.Equal(0.50, byCostDay[new DateOnly(2026, 4, 30)].PricedUsd, 10); // claude's $7.77 excluded
        Assert.False(byCostDay[new DateOnly(2026, 4, 30)].HasUnpricedModels);
        Assert.Equal(1.00, byCostDay[new DateOnly(2026, 5, 1)].PricedUsd, 10);  // codex $99.99, claude $6.00 excluded
        Assert.True(byCostDay[new DateOnly(2026, 5, 1)].HasUnpricedModels);     // Model X has no price
    }

    [Fact]
    public void Build_Antigravity_Range1_OnlyTodayCounts()
    {
        var h = Build(OracleReport(), rangeDays: 1);

        // Range 1 → the single window day is today; the spec oracle: May 1 = A 20K + X 10K.
        var bucket = Assert.Single(h.Buckets);
        Assert.Equal(new DateOnly(2026, 5, 1), bucket.Day);
        Assert.Equal(new Dictionary<string, double> { [ModelA] = 20_000, [ModelX] = 10_000 }, bucket.PerModel);
        Assert.Equal([ModelA, ModelX], h.Series); // 20,000 > 10,000
        Assert.Equal(1.00, Assert.Single(h.DailyCosts).PricedUsd, 10);
        Assert.True(Assert.Single(h.DailyCosts).HasUnpricedModels);
    }

    [Fact]
    public void Build_Antigravity_OutputTokens_SwitchesValues_KeepsAgentIsolation()
    {
        var h = Build(OracleReport(), metric: UsageMetric.OutputTokens);

        Assert.Equal(UsageMetric.OutputTokens, h.Metric);
        var byDay = h.Buckets.ToDictionary(b => b.Day);
        Assert.Equal(new Dictionary<string, double> { [ModelA] = 500 }, byDay[new DateOnly(2026, 4, 30)].PerModel);
        Assert.Equal(
            new Dictionary<string, double> { [ModelA] = 1_000, [ModelX] = 500 },
            byDay[new DateOnly(2026, 5, 1)].PerModel);
        Assert.Equal([ModelA, ModelX], h.Series); // output totals: A 1,500 > X 500
        Assert.Equal(
            [new UsageHistoryBreakdown(ModelA, 1_500), new UsageHistoryBreakdown(ModelX, 500)],
            h.ByModel);
    }

    [Fact]
    public void Build_Antigravity_UnknownMetric_NormalisesToTotalTokens()
    {
        var h = Build(OracleReport(), metric: "Bogus");

        Assert.Equal(UsageMetric.TotalTokens, h.Metric); // identical behaviour to OpenCode (A1):
        var byDay = h.Buckets.ToDictionary(b => b.Day);  // unknown metric reads as total tokens
        Assert.Equal(new Dictionary<string, double> { [ModelA] = 10_000 }, byDay[new DateOnly(2026, 4, 30)].PerModel);
        Assert.Equal([ModelA, ModelX], h.Series);
    }

    [Fact]
    public void Build_WindowIsInclusive_DayBeforeTheWindowIsExcluded()
    {
        // D4 off-by-one guard with an explicit agent: window 2026-04-25 … 05-01 — an Apr 24
        // antigravity row appears nowhere, while the first window day (Apr 25) does.
        var report = Report(
            (new DateOnly(2026, 4, 24), [Agent("antigravity", Model(ModelA, 60_000, 3_000, 0, 60.00m))]),
            (new DateOnly(2026, 4, 25), [Agent("antigravity", Model(ModelX, 111, 11, 0, 0.11m))]));

        var h = Build(report);

        Assert.Equal(new DateOnly(2026, 4, 25), h.Buckets[0].Day);
        Assert.DoesNotContain(new DateOnly(2026, 4, 24), h.Buckets.Select(b => b.Day));
        Assert.Equal(new Dictionary<string, double> { [ModelX] = 111 }, h.Buckets[0].PerModel);
        Assert.Equal([ModelX], h.Series); // Model A's 60,000 is outside
        Assert.Equal(0.0, h.DailyCosts[^1].PricedUsd);            // the Apr 24 $60.00 never enters the cost line
        Assert.False(h.DailyCosts[^1].HasUnpricedModels);
        Assert.Equal(0.11, h.DailyCosts[0].PricedUsd, 10);
        Assert.All(h.DailyCosts.Skip(1), c => Assert.Equal((0.0, false), (c.PricedUsd, c.HasUnpricedModels)));
    }

    // ── A1: OpenCodeHistoryBuilder keeps its API, delegates with "opencode" ────

    [Fact]
    public void Build_OpencodeAgent_IdenticalToOpenCodeHistoryBuilder()
    {
        // A1: OpenCodeHistoryBuilder.Build delegates to AgentHistoryBuilder.Build with "opencode" —
        // both public methods return the same history CONTENT for the same report. D11: content, not
        // record equality: UsageHistory is a record whose Buckets/Series/ByModel/DailyCosts and each
        // bucket's PerModel are defensive copies, so two builds over one report never match as
        // records even when every value agrees.
        var report = Report(
            (new DateOnly(2026, 4, 30), [
                Agent("antigravity", Model(ModelA, 10_000, 500, 8_500, 0.50m)),
                Agent("opencode", Model("deepseek-v4.1-flash", 4_000, 400, 0, 0.40m))]),
            (new DateOnly(2026, 5, 1), [
                Agent("opencode", Model("deepseek-v4.1-flash", 4_000, 400, 0, 0.40m), Model(ModelX, 10_000, 500, 8_500, null)),
                Agent("antigravity", Model(ModelA, 20_000, 1_000, 17_000, 1.00m))]));

        var generalised = AgentHistoryBuilder.Build(report, "opencode", Today, 7, UsageMetric.TotalTokens);
        var dedicated = OpenCodeHistoryBuilder.Build(report, Today, 7, UsageMetric.TotalTokens);

        Assert.NotNull(dedicated);
        Assert.NotNull(generalised);
        AssertSameHistoryContent(dedicated!, generalised!);

        // The opencode path never sees the antigravity rows (existing behaviour unchanged).
        // Series by descending window totals: Model X 10,000 (May 1) > deepseek 8,000 (4,000 + 4,000).
        Assert.Equal([ModelX, "deepseek-v4.1-flash"], generalised!.Series);
        Assert.DoesNotContain(ModelA, generalised!.Series);
    }

    /// <summary>D11: element-wise history comparison — buckets with their per-model values, series,
    /// by-model, daily costs, days and metric — the content the chart consumes.</summary>
    private static void AssertSameHistoryContent(UsageHistory expected, UsageHistory actual)
    {
        Assert.Equal(expected.Days, actual.Days);
        Assert.Equal(expected.Metric, actual.Metric);
        Assert.Equal(expected.Series, actual.Series);
        Assert.Equal(expected.ByModel, actual.ByModel);   // value-equal records
        Assert.Equal(expected.ByProject, actual.ByProject);

        Assert.Equal(expected.Buckets.Count, actual.Buckets.Count);
        for (var i = 0; i < expected.Buckets.Count; i++)
        {
            Assert.Equal(expected.Buckets[i].Day, actual.Buckets[i].Day);
            Assert.Equal(expected.Buckets[i].PerModel, actual.Buckets[i].PerModel); // by content, not reference
            Assert.Equal(expected.Buckets[i].Total, actual.Buckets[i].Total);
        }

        Assert.Equal(expected.DailyCosts.Count, actual.DailyCosts.Count);
        for (var i = 0; i < expected.DailyCosts.Count; i++)
        {
            Assert.Equal(
                (expected.DailyCosts[i].Day, expected.DailyCosts[i].PricedUsd, expected.DailyCosts[i].HasUnpricedModels),
                (actual.DailyCosts[i].Day, actual.DailyCosts[i].PricedUsd, actual.DailyCosts[i].HasUnpricedModels));
        }
    }

    // ── Identical edge behaviour to OpenCode (A1) ───────────────────────────────

    [Fact]
    public void Build_NullReport_ReturnsNull()
    {
        Assert.Null(AgentHistoryBuilder.Build(null!, "antigravity", Today, 7, UsageMetric.TotalTokens));
    }

    [Fact]
    public void Build_RangeDaysNotPositive_Throws()
    {
        var report = OracleReport();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AgentHistoryBuilder.Build(report, "antigravity", Today, 0, UsageMetric.TotalTokens));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AgentHistoryBuilder.Build(report, "antigravity", Today, -1, UsageMetric.TotalTokens));
    }
}
