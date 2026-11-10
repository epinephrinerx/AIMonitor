using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-017 G1.2 — <c>OpenCodeHistoryBuilder.Build(report, today, rangeDays, metric)</c>, written from
/// Docs/SPEC_R017.md only (§5.6): the fixture and oracles H1–H8, F8's tie-break oracle H10, and the
/// G1.2 rules (window = the rangeDays local dates ending at today inclusive, oldest first, one
/// bucket per day; only the <c>AgentUsage</c> with <c>Agent == "opencode"</c> (case-insensitive);
/// per-metric values; a model with value 0 on a day is left out of that day's PerModel; Series =
/// models with window total &gt; 0 ordered by total descending, ties by StringComparer.Ordinal;
/// ByModel = the same models/totals in the same order; ByProject empty; DailyCosts one entry per
/// window day — PricedUsd the sum of priced opencode CostUsd that day, HasUnpricedModels any
/// opencode model that day with CostUsd null; unknown metric → "Total tokens"; no usage in the
/// window still returns the history). Every expected number is hand-derived from the oracle
/// fixture: today 2026-11-10, rangeDays 7 → window 2026-11-04 … 2026-11-10. The G1
/// implementation has since merged into this branch (041433a), so these tests compile and run
/// against it.
/// </summary>
public sealed class OpenCodeHistoryBuilderTests
{
    // The SPEC fixture clock (today 2026-11-10). Pure builder — it receives `today`, no clock read.
    private static readonly DateOnly Today = new(2026, 11, 10);

    private static readonly DateTimeOffset RetrievedAt = new(2026, 11, 10, 7, 0, 0, TimeSpan.Zero);

    private const string Kimi = "kimi-k2.7-code";
    private const string Glm = "glm-5.3-flash";
    private const string Deepseek = "deepseek-v4.1-flash";
    private const string ClaudeOpus = "claude-opus-5-5";

    private static TokenCounts Sum(IReadOnlyList<ModelUsage> models) => new(
        models.Sum(m => m.Tokens.Input),
        models.Sum(m => m.Tokens.Output),
        models.Sum(m => m.Tokens.CacheCreation),
        models.Sum(m => m.Tokens.CacheRead),
        models.Sum(m => m.Tokens.Total));

    /// <summary>One oracle model row. <c>total</c> is the input+output+cache sum, so Total tokens and
    /// Output tokens read straight off the fixture table (input = total − output).</summary>
    private static ModelUsage Model(string name, long total, long output, decimal? costUsd) =>
        new(name, new TokenCounts(total - output, output, 0, 0, total), costUsd);

    private static AgentUsage Agent(string id, params ModelUsage[] models) =>
        new(id, Sum(models), models);

    private static AgentUsageReport Report(params (DateOnly Day, AgentUsage[] Agents)[] days) => new(
        daily: days.Select(d => new DailyAgentUsage(d.Day, d.Agents)).ToList(),
        monthly: [],
        sessions: [],
        unpricedModels: [],
        pricing: AgentUsagePricing.OnlineRequested,
        retrievedAt: RetrievedAt,
        skippedRows: 0);

    /// <summary>The oracle fixture verbatim (SPEC "Oracles"): 11-01 opencode kimi 9999/99/$9.99
    /// (outside the 7-day window), 11-08 opencode kimi total 1000 output 50 $0.50 + glm total 400
    /// output 40 $0.10, 11-09 claude only 5000 with a null cost (must be ignored — the null also
    /// pins that H4's 11-09 entry is (0, false): other agents' unpriced models never flag a day),
    /// 11-10 opencode kimi 2000/100/$1.25 + deepseek 300/30/unpriced.</summary>
    private static AgentUsageReport OracleReport() => Report(
        (new DateOnly(2026, 11, 1), [Agent("opencode", Model(Kimi, 9999, 99, 9.99m))]),
        (new DateOnly(2026, 11, 8), [Agent("opencode", Model(Kimi, 1000, 50, 0.50m), Model(Glm, 400, 40, 0.10m))]),
        (new DateOnly(2026, 11, 9), [Agent("claude", Model(ClaudeOpus, 5000, 0, null))]),
        (new DateOnly(2026, 11, 10), [Agent("opencode", Model(Kimi, 2000, 100, 1.25m), Model(Deepseek, 300, 30, null))]));

    private static UsageHistory Build(
        AgentUsageReport report,
        int rangeDays = 7,
        string metric = UsageMetric.TotalTokens,
        DateOnly? today = null)
    {
        var history = OpenCodeHistoryBuilder.Build(
            report: report, today: today ?? Today, rangeDays: rangeDays, metric: metric);
        Assert.NotNull(history); // only H7 expects null; every other oracle returns a history
        return history!;
    }

    /// <summary>H1 + H4 together: the whole default-window shape for "Total tokens".</summary>
    private static void AssertOracleH1(UsageHistory h)
    {
        Assert.Equal(7, h.Days);
        Assert.Equal("By project", h.ProjectLabel); // G1.2: "ProjectLabel default"
        Assert.Empty(h.ByProject);                  // G1.2: "ByProject = empty"

        // H1: 7 buckets 11-04 … 11-10, oldest first, one per day even without data (G1.2).
        Assert.Equal(Enumerable.Range(4, 7).Select(d => new DateOnly(2026, 11, d)), h.Buckets.Select(b => b.Day));

        var byDay = h.Buckets.ToDictionary(b => b.Day);
        Assert.Empty(byDay[new DateOnly(2026, 11, 4)].PerModel); // days without data: empty PerModel
        Assert.Empty(byDay[new DateOnly(2026, 11, 5)].PerModel);
        Assert.Empty(byDay[new DateOnly(2026, 11, 6)].PerModel);
        Assert.Empty(byDay[new DateOnly(2026, 11, 7)].PerModel);
        Assert.Equal(
            new Dictionary<string, double> { [Kimi] = 1000, [Glm] = 400 },
            byDay[new DateOnly(2026, 11, 8)].PerModel); // tokens from the fixture rows
        Assert.Empty(byDay[new DateOnly(2026, 11, 9)].PerModel); // claude-only day ignored (H1)
        Assert.Equal(
            new Dictionary<string, double> { [Kimi] = 2000, [Deepseek] = 300 },
            byDay[new DateOnly(2026, 11, 10)].PerModel);

        // H1: Series ordered by window total descending — kimi 1000+2000=3000 (the 11-01 9999 is
        // outside the window), glm 400, deepseek 300.
        Assert.Equal([Kimi, Glm, Deepseek], h.Series);
        Assert.Equal(
            [new UsageHistoryBreakdown(Kimi, 3000), new UsageHistoryBreakdown(Glm, 400), new UsageHistoryBreakdown(Deepseek, 300)],
            h.ByModel); // G1.2: ByModel = same models/totals, same order

        // H4: DailyCosts 11-04…11-07 and 11-09 (0, false) — 11-09's unpriced model is CLAUDE, not
        // opencode; 11-08 (0.50 + 0.10 = 0.60, false); 11-10 (1.25, true) deepseek unpriced.
        Assert.Equal(h.Buckets.Select(b => b.Day), h.DailyCosts.Select(c => c.Day)); // oldest first, one per day
        var byCostDay = h.DailyCosts.ToDictionary(c => c.Day);
        foreach (var day in new[] { 4, 5, 6, 7, 9 })
        {
            Assert.Equal((0.0, false), (byCostDay[new DateOnly(2026, 11, day)].PricedUsd,
                                        byCostDay[new DateOnly(2026, 11, day)].HasUnpricedModels));
        }

        Assert.Equal(0.60, byCostDay[new DateOnly(2026, 11, 8)].PricedUsd, 10);
        Assert.False(byCostDay[new DateOnly(2026, 11, 8)].HasUnpricedModels);
        Assert.Equal(1.25, byCostDay[new DateOnly(2026, 11, 10)].PricedUsd, 10);
        Assert.True(byCostDay[new DateOnly(2026, 11, 10)].HasUnpricedModels);
    }

    // ── The oracle table H1–H8 ─────────────────────────────────────────────────

    [Fact]
    public void Build_H1_TotalTokens_WindowBucketsSeriesAndByModel()
    {
        var h = Build(OracleReport(), metric: UsageMetric.TotalTokens);

        Assert.Equal(UsageMetric.TotalTokens, h.Metric); // G1.2: Metric as normalised — passes through
        AssertOracleH1(h);
    }

    [Fact]
    public void Build_H2_OutputTokens_SwitchesTheValueSource()
    {
        var h = Build(OracleReport(), metric: UsageMetric.OutputTokens);

        Assert.Equal(UsageMetric.OutputTokens, h.Metric);
        var byDay = h.Buckets.ToDictionary(b => b.Day);
        Assert.Equal(
            new Dictionary<string, double> { [Kimi] = 50, [Glm] = 40 },
            byDay[new DateOnly(2026, 11, 8)].PerModel); // H2: 11-08 {kimi:50, glm:40}
        Assert.Equal(
            new Dictionary<string, double> { [Kimi] = 100, [Deepseek] = 30 },
            byDay[new DateOnly(2026, 11, 10)].PerModel); // H2: 11-10 {kimi:100, deepseek:30}
        Assert.Equal([Kimi, Glm, Deepseek], h.Series);   // H2: totals 150 (50+100), 40, 30 descending
        Assert.Equal(
            [new UsageHistoryBreakdown(Kimi, 150), new UsageHistoryBreakdown(Glm, 40), new UsageHistoryBreakdown(Deepseek, 30)],
            h.ByModel);
    }

    [Fact]
    public void Build_H3_EquivalentValue_PricedCostsOnly_UnpricedModelContributesNothing()
    {
        var h = Build(OracleReport(), metric: UsageMetric.EquivalentValue);

        Assert.Equal(UsageMetric.EquivalentValue, h.Metric);
        var byDay = h.Buckets.ToDictionary(b => b.Day);
        Assert.Equal(
            new Dictionary<string, double> { [Kimi] = 0.50, [Glm] = 0.10 },
            byDay[new DateOnly(2026, 11, 8)].PerModel); // H3
        Assert.Equal(
            new Dictionary<string, double> { [Kimi] = 1.25 },
            byDay[new DateOnly(2026, 11, 10)].PerModel); // H3: deepseek left out (unpriced → nothing)
        Assert.Equal([Kimi, Glm], h.Series);             // H3: deepseek's window total is 0 → no series
        Assert.Equal(
            [new UsageHistoryBreakdown(Kimi, 1.75), new UsageHistoryBreakdown(Glm, 0.10)], // 0.50 + 1.25 = 1.75
            h.ByModel);
    }

    [Fact]
    public void Build_H4_DailyCosts_AreIndependentOfTheMetric()
    {
        // H4 values, asserted under "Equivalent value" — G1.2 defines DailyCosts once (sum of priced
        // CostUsd per day + any-unpriced flag), not per metric. Full H4 sweep for "Total tokens"
        // lives in AssertOracleH1 (Build_H1).
        var h = Build(OracleReport(), metric: UsageMetric.EquivalentValue);

        Assert.Equal(7, h.DailyCosts.Count);
        Assert.Equal(new DateOnly(2026, 11, 4), h.DailyCosts[0].Day); // oldest first
        Assert.Equal(0.60, h.DailyCosts[4].PricedUsd, 10);            // 11-08 = 0.50 + 0.10
        Assert.False(h.DailyCosts[4].HasUnpricedModels);
        Assert.Equal(0.0, h.DailyCosts[5].PricedUsd);                 // 11-09: claude-only day
        Assert.False(h.DailyCosts[5].HasUnpricedModels);              // ...and its unpriced model is not opencode
        Assert.Equal(1.25, h.DailyCosts[6].PricedUsd, 10);            // 11-10 priced sum
        Assert.True(h.DailyCosts[6].HasUnpricedModels);               // ...deepseek has CostUsd null
    }

    [Fact]
    public void Build_H5_UnknownMetric_IsTreatedAsTotalTokens()
    {
        var h = Build(OracleReport(), metric: "Bogus");

        Assert.Equal(UsageMetric.TotalTokens, h.Metric); // H5: Metric is the NORMALISED name
        AssertOracleH1(h);                      // H5: "same as H1"
    }

    [Fact]
    public void Build_H6_NoOpencodeRowsInTheWindow_StillReturnsEmptyHistory()
    {
        // H6: opencode data only on 11-01 (outside the window), claude rows inside it — G1.2: "No
        // usage in the window → buckets all empty, Series/ByModel empty, DailyCosts all zero (the
        // history is still returned)." The claude models carry a null cost: they must not flag a
        // day that has no opencode models at all.
        var report = Report(
            (new DateOnly(2026, 11, 1), [Agent("opencode", Model(Kimi, 9999, 99, 9.99m))]),
            (new DateOnly(2026, 11, 8), [Agent("claude", Model(ClaudeOpus, 5000, 0, null))]),
            (new DateOnly(2026, 11, 9), [Agent("claude", Model(ClaudeOpus, 5000, 0, null))]));

        var h = Build(report, metric: UsageMetric.TotalTokens);

        Assert.Equal(7, h.Days);
        Assert.Equal(Enumerable.Range(4, 7).Select(d => new DateOnly(2026, 11, d)), h.Buckets.Select(b => b.Day));
        Assert.All(h.Buckets, b => Assert.Empty(b.PerModel));
        Assert.Empty(h.Series);
        Assert.Empty(h.ByModel);
        Assert.Empty(h.ByProject);
        Assert.Equal(7, h.DailyCosts.Count);
        Assert.All(h.DailyCosts, c => Assert.Equal((0.0, false), (c.PricedUsd, c.HasUnpricedModels)));
    }

    [Fact]
    public void Build_H7_NullReport_ReturnsNull()
    {
        Assert.Null(OpenCodeHistoryBuilder.Build(report: null!, today: Today, rangeDays: 7, metric: UsageMetric.TotalTokens));
    }

    [Fact]
    public void Build_H8_RangeDaysOne_SingleBucketForToday()
    {
        var h = Build(OracleReport(), rangeDays: 1);

        Assert.Equal(1, h.Days);
        var bucket = Assert.Single(h.Buckets);
        Assert.Equal(new DateOnly(2026, 11, 10), bucket.Day); // the window ends at today inclusive
        Assert.Equal(new Dictionary<string, double> { [Kimi] = 2000, [Deepseek] = 300 }, bucket.PerModel);
        Assert.Equal([Kimi, Deepseek], h.Series); // 2000 > 300; glm has no bucket in a 1-day window
        Assert.Equal(
            [new UsageHistoryBreakdown(Kimi, 2000), new UsageHistoryBreakdown(Deepseek, 300)],
            h.ByModel);
        var cost = Assert.Single(h.DailyCosts);
        Assert.Equal((new DateOnly(2026, 11, 10), 1.25, true), (cost.Day, cost.PricedUsd, cost.HasUnpricedModels));
    }

    [Fact]
    public void Build_H10_F8_EqualWindowTotals_OrderedByStringComparerOrdinal()
    {
        // F8 (m8 → oracle H10): two models with equal window totals sort by StringComparer.Ordinal —
        // the spec example "glm before kimi", plus a case pair that separates Ordinal from a
        // culture-aware comparer ('B' = 0x42 < 'a' = 0x61, so Ordinal puts "Beta" first where
        // culture-sensitive ordering would put "alpha" first).
        var report = Report(
            (new DateOnly(2026, 11, 10),
                [Agent("opencode",
                    Model("Beta", 1000, 10, 1.00m),
                    Model("alpha", 1000, 10, 1.00m),
                    Model("glm", 500, 5, 0.50m),
                    Model("kimi", 500, 5, 0.50m))]));

        var h = Build(report, metric: UsageMetric.TotalTokens);

        Assert.Equal(["Beta", "alpha", "glm", "kimi"], h.Series);
        Assert.Equal(
            [new UsageHistoryBreakdown("Beta", 1000), new UsageHistoryBreakdown("alpha", 1000),
             new UsageHistoryBreakdown("glm", 500), new UsageHistoryBreakdown("kimi", 500)],
            h.ByModel); // ByModel keeps the same models AND order (G1.2)
    }

    // ── G1.2 rule details beyond the table ─────────────────────────────────────

    [Fact]
    public void Build_WindowIsInclusive_DayBeforeTheWindowIsExcluded()
    {
        // Off-by-one guard for "the rangeDays local dates ending at today inclusive": with rangeDays
        // 7 the window starts 11-04, so a 11-03 row must not appear anywhere (buckets, Series sums,
        // DailyCosts), and the first window day (11-04) must.
        var report = Report(
            (new DateOnly(2026, 11, 3), [Agent("opencode", Model(Kimi, 7777, 77, 7.77m))]),
            (new DateOnly(2026, 11, 4), [Agent("opencode", Model(Glm, 111, 11, 0.11m))]));

        var h = Build(report, rangeDays: 7);

        Assert.Equal(new DateOnly(2026, 11, 4), h.Buckets[0].Day);
        Assert.DoesNotContain(new DateOnly(2026, 11, 3), h.Buckets.Select(b => b.Day));
        Assert.Equal([Glm], h.Series);                                 // kimi (only on 11-03) is out
        Assert.Equal([new UsageHistoryBreakdown(Glm, 111)], h.ByModel);
        Assert.Equal(new DateOnly(2026, 11, 4), h.DailyCosts[0].Day);
        Assert.Equal(0.11, h.DailyCosts[0].PricedUsd, 10);              // the first window day is in
        Assert.All(h.DailyCosts.Skip(1), c => Assert.Equal((0.0, false), (c.PricedUsd, c.HasUnpricedModels))); // the 11-03 $7.77 is out
    }

    [Fact]
    public void Build_WindowCrossesTheMonthBoundary()
    {
        // today.AddDays(-(rangeDays-1)) … today spans months: today 2026-01-02, rangeDays 4 →
        // window 2025-12-30 … 2026-01-02.
        var today = new DateOnly(2026, 1, 2);
        var report = Report(
            (new DateOnly(2025, 12, 29), [Agent("opencode", Model("m29", 500, 5, 5.00m))]),   // just outside
            (new DateOnly(2025, 12, 30), [Agent("opencode", Model("m30", 10, 1, 0.01m))]),
            (new DateOnly(2026, 1, 2), [Agent("opencode", Model("m02", 20, 2, 0.02m))]));

        var h = Build(report, rangeDays: 4, today: today);

        Assert.Equal([new DateOnly(2025, 12, 30), new DateOnly(2025, 12, 31), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2)],
            h.Buckets.Select(b => b.Day));
        Assert.DoesNotContain("m29", h.Series);
        Assert.Equal(["m02", "m30"], h.Series); // 20 > 10
        Assert.Equal([new UsageHistoryBreakdown("m02", 20), new UsageHistoryBreakdown("m30", 10)], h.ByModel);
        Assert.Equal(0.01, h.DailyCosts[0].PricedUsd, 10); // 12-30 = $0.01
        Assert.Equal(0.0, h.DailyCosts[1].PricedUsd);      // 12-31 and 01-01 carry no rows
        Assert.Equal(0.0, h.DailyCosts[2].PricedUsd);
        Assert.Equal(0.02, h.DailyCosts[3].PricedUsd, 10); // 01-02 = $0.02
    }

    [Fact]
    public void Build_AgentMatchIsCaseInsensitive_OpencodeRowSpelledOpenCodeIsCounted()
    {
        // G1.2: "the AgentUsage with Agent == \"opencode\" (case-insensitive)". Per F20 (Inspector B
        // minor 3) this test covers the DOMAIN normalisation: the AgentUsage record lower-cases and
        // trims its agent at construction, so the builder already receives "opencode" here and its
        // own OrdinalIgnoreCase comparison never sees mixed case through this public path — the
        // builder-comparison mutant is an equivalent mutant (no production seam exists to feed it
        // mixed case, and F22 forbids adding one). The observable contract that stays pinned: a row
        // spelled "OpenCode" in the report is counted by the pipeline regardless of spelling.
        var report = Report(
            (new DateOnly(2026, 11, 10), [Agent("OpenCode", Model(Kimi, 1000, 10, 1.00m))]));

        var h = Build(report);

        Assert.Equal([Kimi], h.Series);
        Assert.Equal(1000.0, h.Buckets[^1].PerModel[Kimi]);
    }

    [Fact]
    public void Build_OnlyTheOpencodeAgentOnAMixedDayIsCounted()
    {
        // G1.2 source rows: inside each in-window day, ONLY the AgentUsage whose Agent is opencode.
        var report = Report(
            (new DateOnly(2026, 11, 10),
                [Agent("opencode", Model(Kimi, 1000, 50, 0.50m)),
                 Agent("claude", Model(ClaudeOpus, 5000, 4000, 9.99m))]));

        var h = Build(report);

        Assert.Equal(new Dictionary<string, double> { [Kimi] = 1000 }, h.Buckets[^1].PerModel);
        Assert.DoesNotContain(ClaudeOpus, h.Series);
        Assert.Equal(0.50, h.DailyCosts[6].PricedUsd, 10); // claude's $9.99 never enters the cost line
        Assert.False(h.DailyCosts[6].HasUnpricedModels);
    }

    [Fact]
    public void Build_ModelWithZeroValueOnADay_IsLeftOutOfThatDayPerModel()
    {
        // G1.2: "A model with value 0 on a day is left out of that day's PerModel." — but its positive
        // day still carries it into Series/ByModel with the window total.
        var report = Report(
            (new DateOnly(2026, 11, 8), [Agent("opencode", Model(Kimi, 1000, 50, 0.50m), Model("zeta", 0, 0, 0.00m))]),
            (new DateOnly(2026, 11, 10), [Agent("opencode", Model("zeta", 500, 5, 0.30m))]));

        var h = Build(report, metric: UsageMetric.TotalTokens);

        Assert.Equal(new Dictionary<string, double> { [Kimi] = 1000 }, h.Buckets[4].PerModel); // 11-08: zeta (0) left out
        Assert.Equal(new Dictionary<string, double> { ["zeta"] = 500 }, h.Buckets[6].PerModel);
        Assert.Equal([Kimi, "zeta"], h.Series); // window totals 1000 > 500
        Assert.Equal([new UsageHistoryBreakdown(Kimi, 1000), new UsageHistoryBreakdown("zeta", 500)], h.ByModel);
        Assert.Equal(0.50, h.DailyCosts[4].PricedUsd, 10); // zeta's $0.00 changes nothing on 11-08
    }

    [Fact]
    public void Build_EquivalentValue_PricedZeroCostModelContributesNothing_ButTheDayIsNotUnpriced()
    {
        // G1.2 metric rule + "a model with value 0 on a day is left out": a priced-at-$0.00 model
        // has value 0 under "Equivalent value" → out of PerModel and out of Series — while the day's
        // DailyCosts entry is (0, false): it IS priced, just zero.
        var report = Report(
            (new DateOnly(2026, 11, 10), [Agent("opencode", Model("freeby", 900, 90, 0.00m))]));

        var h = Build(report, metric: UsageMetric.EquivalentValue);

        Assert.Empty(h.Buckets[6].PerModel);
        Assert.Empty(h.Series);
        Assert.Empty(h.ByModel);
        Assert.Equal((0.0, false), (h.DailyCosts[6].PricedUsd, h.DailyCosts[6].HasUnpricedModels));
    }

    [Fact]
    public void Build_UnpricedModelWithNoTokensStillFlagsItsDayInDailyCosts()
    {
        // G1.2: "HasUnpricedModels = any opencode model that day has CostUsd == null" — by price,
        // independent of any token value. A zero-token unpriced model leaves the bucket empty but
        // flags the day, and the day's PricedUsd stays 0.
        var report = Report(
            (new DateOnly(2026, 11, 10), [Agent("opencode", new ModelUsage("ghost", new TokenCounts(0, 0, 0, 0, 0), null))]));

        var h = Build(report, metric: UsageMetric.TotalTokens);

        Assert.Empty(h.Buckets[6].PerModel);
        Assert.Empty(h.Series);
        Assert.Equal((0.0, true), (h.DailyCosts[6].PricedUsd, h.DailyCosts[6].HasUnpricedModels));
    }
}
