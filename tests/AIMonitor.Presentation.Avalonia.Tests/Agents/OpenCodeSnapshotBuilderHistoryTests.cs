using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.Agents;

/// <summary>
/// R-017 G1.3 — <c>OpenCodeSnapshotBuilder.Build</c> "gains parameters <c>int rangeDays, string
/// metric</c> and, when <c>lastGood != null</c>, sets <c>history: OpenCodeHistoryBuilder.Build(
/// lastGood, today, rangeDays, metric)</c> on the snapshot. Error-only and 'no result' states
/// (E10/E23) are unchanged (no history)." Written from Docs/SPEC_R017.md only (§5.6); the
/// per-value expectations are the SPEC oracle table (fixture today 2026-11-10), the H-shape
/// hand-derived there. Later review decisions covered here: F11 (fetchedAt is the report time —
/// the rebuild must not move LastUpdated), F15 (the primary Build overload keeps rangeDays and
/// metric required — every call here passes both) and F17 (the R-015 helper now passes them too,
/// so WO3 can drop the temporary 6-argument shim). New arguments are passed by NAME so the tests
/// bind regardless of where rangeDays/metric sit in the signature.
/// </summary>
public sealed class OpenCodeSnapshotBuilderHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 7, 3, 22, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 10);
    private static readonly DateTimeOffset RetrievedAt = new(2026, 11, 10, 7, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, decimal> NoBudgets = new Dictionary<string, decimal>();

    private const string Kimi = "kimi-k2.7-code";
    private const string Glm = "glm-5.3-flash";
    private const string Deepseek = "deepseek-v4.1-flash";

    private static ModelUsage Model(string name, long total, long output, decimal? costUsd) =>
        new(name, new TokenCounts(total - output, output, 0, 0, total), costUsd);

    private static AgentUsageReport OracleReport() => new(
        daily:
        [
            new DailyAgentUsage(new DateOnly(2026, 11, 1),
                [new AgentUsage("opencode", new TokenCounts(9999, 0, 0, 0, 9999), [Model(Kimi, 9999, 99, 9.99m)])]),
            new DailyAgentUsage(new DateOnly(2026, 11, 8),
                [new AgentUsage("opencode", new TokenCounts(1310, 90, 0, 0, 1400),
                    [Model(Kimi, 1000, 50, 0.50m), Model(Glm, 400, 40, 0.10m)])]),
            new DailyAgentUsage(new DateOnly(2026, 11, 9),
                [new AgentUsage("claude", new TokenCounts(5000, 0, 0, 0, 5000), [Model("claude-opus-5-5", 5000, 0, null)])]),
            new DailyAgentUsage(new DateOnly(2026, 11, 10),
                [new AgentUsage("opencode", new TokenCounts(2170, 130, 0, 0, 2300),
                    [Model(Kimi, 2000, 100, 1.25m), Model(Deepseek, 300, 30, null)])]),
        ],
        monthly: [], // no monthly row: the gauge takes the O4 "$0.00" path; the history is what this file tests
        sessions: [],
        unpricedModels: [],
        pricing: AgentUsagePricing.OnlineRequested,
        retrievedAt: RetrievedAt,
        skippedRows: 0);

    private static ProviderSnapshot? BuildRaw(
        AgentUsageReport? lastGood,
        AgentUsageUnavailableException? lastError,
        int rangeDays,
        string metric) =>
        OpenCodeSnapshotBuilder.Build(
            lastGood: lastGood,
            lastError: lastError,
            budgets: NoBudgets,
            today: Today,
            now: Now,
            zone: TimeZoneInfo.Utc,
            rangeDays: rangeDays,
            metric: metric);

    private static ProviderSnapshot Build(int rangeDays, string metric)
    {
        var snapshot = BuildRaw(OracleReport(), null, rangeDays, metric);
        Assert.NotNull(snapshot); // lastGood != null always yields the good snapshot
        return snapshot!;
    }

    // ── G1.3: lastGood != null → history attached, built from the same oracle values ──

    [Fact]
    public void Build_GoodReport_AttachesSevenDayHistoryOnTheSnapshot()
    {
        var snapshot = Build(rangeDays: 7, metric: UsageMetric.TotalTokens);

        var history = snapshot.History; // G1.3: "sets history: OpenCodeHistoryBuilder.Build(...) on the snapshot"
        Assert.NotNull(history);
        Assert.Equal(7, history!.Days);
        Assert.Equal(UsageMetric.TotalTokens, history.Metric);
        Assert.Equal(Enumerable.Range(4, 7).Select(d => new DateOnly(2026, 11, d)), history.Buckets.Select(b => b.Day));
        Assert.Equal([Kimi, Glm, Deepseek], history.Series); // H1: window totals 3000, 400, 300
        Assert.Equal(
            [new UsageHistoryBreakdown(Kimi, 3000), new UsageHistoryBreakdown(Glm, 400), new UsageHistoryBreakdown(Deepseek, 300)],
            history.ByModel);
        Assert.Empty(history.ByProject);

        // H4 through the same window: 11-08 $0.60, 11-10 $1.25 + unpriced, everything else (0, false).
        Assert.Equal(7, history.DailyCosts.Count);
        var byDay = history.DailyCosts.ToDictionary(c => c.Day);
        Assert.Equal(0.60, byDay[new DateOnly(2026, 11, 8)].PricedUsd, 10);
        Assert.Equal(1.25, byDay[new DateOnly(2026, 11, 10)].PricedUsd, 10);
        Assert.True(byDay[new DateOnly(2026, 11, 10)].HasUnpricedModels);
        Assert.Equal(0.0, byDay[new DateOnly(2026, 11, 9)].PricedUsd);
        Assert.False(byDay[new DateOnly(2026, 11, 9)].HasUnpricedModels);

        // The R-015 shape of the snapshot is unchanged (same meter, configured, no error).
        Assert.Equal("opencode", snapshot.ProviderId);
        Assert.True(snapshot.Configured);
        Assert.Null(snapshot.Error);
        Assert.Single(snapshot.Meters);
    }

    [Fact]
    public void Build_F11_GoodSnapshot_FetchedAtIsTheReportTimeNotNow()
    {
        // F11 (R1 m2): "Every OpenCode snapshot uses fetchedAt: lastGood.RetrievedAt ... so a
        // range/metric/budget rebuild does not move LastUpdated." `now` (07:03:22) differs from
        // the report's RetrievedAt (07:00:00) — passing `now` through must not win.
        var snapshot = Build(rangeDays: 7, metric: UsageMetric.TotalTokens);

        Assert.Equal(RetrievedAt, snapshot.FetchedAt);
    }

    [Fact]
    public void Build_F11_ErrorOnlySnapshot_FetchedAtIsNow()
    {
        // F11: "(or lastError's time if no lastGood — use now only then)" — the exception carries
        // no time, so the error-only snapshot keeps R-015's `now`.
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.Failed, "ccusage failed");

        var snapshot = BuildRaw(null, failure, rangeDays: 7, metric: UsageMetric.TotalTokens);

        Assert.NotNull(snapshot);
        Assert.Equal(Now, snapshot!.FetchedAt);
        Assert.Null(snapshot.History);
    }

    [Fact]
    public void Build_GoodReport_ForwardsRangeDaysAndMetricToTheBuilder()
    {
        // rangeDays 30 widens the window so 11-01's 9999/9.99 enters:
        // kimi = 9999 + 1000 + 2000 = 12999; the window starts today.AddDays(-29) = 2026-10-12.
        var history = Build(rangeDays: 30, metric: UsageMetric.EquivalentValue).History;

        Assert.NotNull(history);
        Assert.Equal(30, history!.Days);
        Assert.Equal(UsageMetric.EquivalentValue, history.Metric);
        Assert.Equal(30, history.Buckets.Count);
        Assert.Equal(new DateOnly(2026, 10, 12), history.Buckets[0].Day);
        Assert.Equal(
            new Dictionary<string, double> { [Kimi] = 0.50, [Glm] = 0.10 },
            history.Buckets.First(b => b.Day == new DateOnly(2026, 11, 8)).PerModel);
        Assert.Equal([Kimi, Glm], history.Series); // H3 under 30 days: kimi 0.50 + 1.25 + 9.99 = 11.74, glm 0.10
        Assert.Equal(9.99, history.DailyCosts.Single(c => c.Day == new DateOnly(2026, 11, 1)).PricedUsd, 10);
    }

    [Fact]
    public void Build_GoodReportWithNoUsageInTheWindow_StillAttachesTheEmptyHistory()
    {
        // G1.2/H6 through G1.3: the history is still returned when lastGood exists — an empty
        // window attaches 7 empty buckets, not a missing history.
        var report = new AgentUsageReport(
            daily: [new DailyAgentUsage(new DateOnly(2026, 11, 1),
                [new AgentUsage("opencode", new TokenCounts(9999, 0, 0, 0, 9999), [Model(Kimi, 9999, 99, 9.99m)])])],
            monthly: [],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.Offline,
            retrievedAt: RetrievedAt,
            skippedRows: 0);

        var snapshot = OpenCodeSnapshotBuilder.Build(
            lastGood: report, lastError: null, budgets: NoBudgets, today: Today, now: Now,
            zone: TimeZoneInfo.Utc, rangeDays: 7, metric: UsageMetric.TotalTokens);

        Assert.NotNull(snapshot);
        var history = snapshot!.History;
        Assert.NotNull(history);
        Assert.Equal(7, history!.Buckets.Count);
        Assert.All(history.Buckets, b => Assert.Empty(b.PerModel));
        Assert.Empty(history.Series);
        Assert.All(history.DailyCosts, c => Assert.Equal((0.0, false), (c.PricedUsd, c.HasUnpricedModels)));
    }

    // ── G1.3: the E10/E23 states are unchanged — no history ────────────────────

    [Fact]
    public void Build_E10_ErrorOnlySnapshot_HasNoHistory()
    {
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "ccusage is not installed");

        var snapshot = BuildRaw(null, failure, rangeDays: 7, metric: UsageMetric.TotalTokens);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.History); // G1.3: "Error-only ... states are unchanged (no history)"
        Assert.Equal(AgentUsagePresenter.ErrorText(AgentUsageErrorKind.NotInstalled, "ccusage is not installed"), snapshot.Error);
        Assert.Empty(snapshot.Meters);
    }

    [Fact]
    public void Build_E23_NoResultState_StillReturnsNullSnapshot()
    {
        // G1.3: "'no result' states (E10/E23) are unchanged" — E23 keeps its null return, so the
        // new parameters do not turn "nothing known yet" into a pushed snapshot.
        Assert.Null(BuildRaw(null, null, rangeDays: 7, metric: UsageMetric.TotalTokens));
    }

    [Fact]
    public void Build_GoodReportWithLaterError_IgnoresErrorAndKeepsHistory()
    {
        // Q2.2 state 3 + G1.3: lastGood != null → the good snapshot with the attached history,
        // the later error ignored.
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.Failed, "ccusage failed this time");

        var snapshot = BuildRaw(OracleReport(), failure, rangeDays: 7, metric: UsageMetric.TotalTokens);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Error);
        Assert.NotNull(snapshot.History);
        Assert.Equal(7, snapshot.History!.Buckets.Count);
    }
}
