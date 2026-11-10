using System.Globalization;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-015 Q2.2 — <c>OpenCodeSnapshotBuilder.Build(lastGood, lastError, budgets, today, now, zone)</c>
/// written from Docs/SPEC_R015.md only (§5.6): the three-state table, the meter fields, oracles
/// O1/O2/O3/O4/O5/O7/E12 (subtitle and detail texts are pinned by E26: spent goes into the subtitle,
/// <c>detail</c> keeps the same spent text; O8 is superseded by E10) and decisions E1 (resetsAt day 1
/// 00:00 in the caller's zone with zone.GetUtcOffset of THAT local instant), E10 (the error-first
/// snapshot is configured TRUE — it overrides the "configured: false" wording of the state table and
/// of O8), E11 (120 % renders Critical through the existing local thresholds, no new code) and E12
/// (money(x) is ChartFormat.Money((double)x), also for the budget in the subtitle).
/// E23 pins the return type: <c>ProviderSnapshot?</c> — <c>null</c> means "no ccusage result yet"
/// (lastGood AND lastError are null) and the view model then pushes nothing; the snapshot it does
/// return carries <c>ProviderId == "opencode"</c>. The Q2 branch has merged (handoff), so these
/// tests compile and run against the real builder.
/// </summary>
public sealed class OpenCodeSnapshotBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 7, 3, 22, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 10);

    private static readonly IReadOnlyDictionary<string, decimal> NoBudgets = new Dictionary<string, decimal>();

    private static AgentUsage Usage(string agent, params (string Model, decimal? Cost)[] models) =>
        new(
            agent,
            new TokenCounts(0, 0, 0, 0, 100L * models.Length),
            models.Select(m => new ModelUsage(m.Model, new TokenCounts(100, 0, 0, 0, 100), m.Cost)).ToList());

    private static AgentUsageReport ReportWithMonth(DateOnly day, AgentUsage monthAgent) =>
        new(
            daily: [],
            monthly: [new MonthlyAgentUsage(day.Year, day.Month, [monthAgent])],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: Now,
            skippedRows: 0);

    private static IReadOnlyDictionary<string, decimal> Budget(params (string Agent, decimal Usd)[] entries) =>
        entries.ToDictionary(e => e.Agent, e => e.Usd);

    private static AgentUsage OpenCode(decimal cost) => Usage("opencode", ("glm-4.6", cost));

    // Build returns ProviderSnapshot? (E23). The raw call, for the one case that expects null.
    private static ProviderSnapshot? BuildRaw(
        AgentUsageReport? lastGood,
        AgentUsageUnavailableException? lastError,
        IReadOnlyDictionary<string, decimal> budgets,
        DateOnly today,
        TimeZoneInfo? zone = null) =>
        OpenCodeSnapshotBuilder.Build(lastGood, lastError, budgets, today, Now, zone ?? TimeZoneInfo.Utc);

    // Every state that has something to show: unwrap once here so the tests read against a
    // non-null snapshot (the repo style is Assert.NotNull + `!`, CS8602 under warnings-as-errors).
    private static ProviderSnapshot Build(
        AgentUsageReport? lastGood,
        AgentUsageUnavailableException? lastError,
        IReadOnlyDictionary<string, decimal> budgets,
        DateOnly today,
        TimeZoneInfo? zone = null)
    {
        var snapshot = BuildRaw(lastGood, lastError, budgets, today, zone);
        Assert.NotNull(snapshot); // a state with a report or an error always yields a snapshot
        return snapshot!;
    }

    // ── The three states of the Q2.2 table (E23 return type) ───────────────────

    [Fact]
    public void Build_E23_FirstRunStillGoing_ReturnsNullSoNothingIsPushed()
    {
        // E23 / Q2.2 state 1: lastGood == null && lastError == null → Build returns null, and "no
        // snapshot is pushed; the tab keeps its initial 'Checking...' state".
        Assert.Null(BuildRaw(null, null, NoBudgets, Today));
    }

    [Fact]
    public void Build_E10_ErrorBeforeAnyGoodReport_IsConfiguredWithErrorBannerAndNoMeters()
    {
        // E10 (OVERRIDES the "configured: false" cell of the state table and of O8, which E23 also
        // marks superseded): the error-first snapshot carries configured: true so the tab shows the
        // error banner — not the setup card, which would have an empty hint — with no meters/stats.
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "ccusage is not installed");

        var snapshot = Build(null, failure, NoBudgets, Today);

        Assert.Equal("opencode", snapshot.ProviderId); // E23: the snapshot is the OpenCode tab's
        Assert.True(snapshot.Configured); // E10, not O8's stale "configured false"
        Assert.Equal(AgentUsagePresenter.ErrorText(AgentUsageErrorKind.NotInstalled, "ccusage is not installed"), snapshot.Error);
        Assert.Empty(snapshot.Meters);
        Assert.Empty(snapshot.Stats);
    }

    [Fact]
    public void Build_Q22_GoodReportWithLaterError_IgnoresErrorLikeAllAgentsTab()
    {
        // Q2.2 state 3: lastGood != null → configured true, one meter, error: null — "a later
        // lastError while lastGood exists is ignored here, matching the All agents tab".
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.Failed, "ccusage failed this time");

        var snapshot = Build(ReportWithMonth(Today, OpenCode(30m)), failure, NoBudgets, Today);

        Assert.Equal("opencode", snapshot.ProviderId); // E23
        Assert.True(snapshot.Configured);
        Assert.Null(snapshot.Error);
        var meter = Assert.Single(snapshot.Meters); // one meter (below), no stats
        Assert.Equal("month_budget", meter.Kind); // Q2.2 meter fields
        Assert.Equal("month_budget", meter.Key);
        Assert.Equal("Month to date", meter.Title);
        Assert.Empty(snapshot.Stats);
    }

    // ── O1–O5: the meter values (subtitle texts pinned by E26) ────────────────

    [Fact]
    public void Build_O1_CostAgainstBudget_SubtitleCarriesBothMoneyTexts()
    {
        // O1 (E26): cost 12.345, budget 50 → Percent 24.69 and subtitle
        // "$12.35 of $50.00 budget" — money(12.345) = ChartFormat.Money((double)12.345) = "$12.35"
        // (the double sits above the 12.345 midpoint), money(50) = "$50.00". The calculator side of
        // O1 lives in Q1's AgentBudgetComputeTests; here the texts and the uncapped ratio.
        var snapshot = Build(ReportWithMonth(Today, OpenCode(12.345m)), null, Budget(("opencode", 50m)), Today);

        var meter = Assert.Single(snapshot.Meters);
        Assert.Equal(24.69, meter.Percent); // (12.345 / 50) × 100 hand-derived
        Assert.Equal("$12.35 of $50.00 budget", meter.Subtitle); // E26 O1
        Assert.Equal("$12.35", meter.Detail); // E26: detail = the spent text
    }

    [Fact]
    public void Build_O2_OverBudget_PercentIsNotCapped_AndBudgetUsesMoneyFormatter()
    {
        // O2: cost 60, budget 50 → Percent 120, not capped. E26 subtitle: "$60.00 of $50.00 budget";
        // money = ChartFormat.Money (E12), detail keeps the spent text.
        var snapshot = Build(ReportWithMonth(Today, OpenCode(60m)), null, Budget(("opencode", 50m)), Today);

        var meter = Assert.Single(snapshot.Meters);
        Assert.Equal(120.0, meter.Percent); // E12/O2: (60/50)*100 uncapped
        Assert.Equal("$60.00 of $50.00 budget", meter.Subtitle); // E26 O2
        Assert.Equal("$60.00", meter.Detail);
        Assert.Equal(Severity.Normal, meter.ServerSeverity); // Q2.2: colour comes from the local thresholds
    }

    [Fact]
    public void Build_O3_NoBudget_PercentNullAndNoBudgetSubtitle()
    {
        // O3 (E26): cost 7.5, no budget → Percent null, subtitle
        // "$7.50 · No budget set — add one in Settings", detail "$7.50". The gauge text "--" is
        // pinned in Build_O3_GaugeTextIsDashesWhenNoBudget.
        var snapshot = Build(ReportWithMonth(Today, OpenCode(7.5m)), null, NoBudgets, Today);

        var meter = Assert.Single(snapshot.Meters);
        Assert.Null(meter.Percent);
        Assert.Equal("$7.50 · No budget set — add one in Settings", meter.Subtitle); // E26 O3
        Assert.Equal("$7.50", meter.Detail);
        Assert.NotNull(meter.ResetsAt); // the month window is shown regardless of the budget
    }

    [Theory]
    // O4 (E26 variants): the report has an opencode row for LAST month only → MonthCostUsd 0,
    // HasUsageThisMonth false. Budget 10 → Percent 0 and subtitle "$0.00 of $10.00 budget"; no
    // budget → subtitle "$0.00 · No budget set — add one in Settings". Detail is the spent text.
    [InlineData("10", 0.0, "$0.00 of $10.00 budget")]
    [InlineData(null, null, "$0.00 · No budget set — add one in Settings")]
    public void Build_O4_NoRowThisMonth_SpendsZero(
        string? budgetText,
        double? expectedPercent,
        string expectedSubtitle)
    {
        var lastMonth = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-1);
        var budgets = budgetText is null
            ? NoBudgets
            : Budget(("opencode", decimal.Parse(budgetText, CultureInfo.InvariantCulture)));

        var snapshot = Build(ReportWithMonth(lastMonth, OpenCode(99m)), null, budgets, Today);

        var meter = Assert.Single(snapshot.Meters);
        Assert.Equal(expectedPercent, meter.Percent);
        Assert.Equal(expectedSubtitle, meter.Subtitle);
        Assert.Equal("$0.00", meter.Detail);
    }

    [Fact]
    public void Build_O5_UnpricedModels_AppendsUnpricedToSpentText()
    {
        // O5 (E26): model A cost 3, model B unpriced → spent = "$3.00 + unpriced" (cost not counted
        // as $0); detail keeps that text and the no-budget subtitle carries it too.
        var monthRow = Usage("opencode", ("model-a", 3m), ("model-b", null));

        var snapshot = Build(ReportWithMonth(Today, monthRow), null, NoBudgets, Today);

        var meter = Assert.Single(snapshot.Meters);
        Assert.Equal("$3.00 + unpriced", meter.Detail); // money(MonthCostUsd) + " + unpriced"
        Assert.Equal("$3.00 + unpriced · No budget set — add one in Settings", meter.Subtitle); // E26 O5
    }

    [Theory]
    // E12: money(x) is ChartFormat.Money((double)x) — the AgentUsagePresenter priced-cost text.
    // E26: the same text is the detail AND the leading spent part of the no-budget subtitle.
    [InlineData("0.005", "<$0.01")]
    [InlineData("0", "$0.00")]
    [InlineData("12.34", "$12.34")]
    public void Build_E12_MoneyFormatMatchesChartFormatMoney(string costText, string expectedDetail)
    {
        var cost = decimal.Parse(costText, CultureInfo.InvariantCulture);

        var snapshot = Build(ReportWithMonth(Today, OpenCode(cost)), null, NoBudgets, Today);

        var meter = Assert.Single(snapshot.Meters);
        Assert.Equal(expectedDetail, meter.Detail);
        Assert.Equal($"{expectedDetail} · No budget set — add one in Settings", meter.Subtitle);
    }

    [Fact]
    public void Build_E12_BudgetInSubtitleIsTheSameMoneyFormatter()
    {
        // E12: "the budget in the subtitle uses it too" — money(20.01) = "$20.01"; E26 puts the
        // spend in front: money(1) = "$1.00".
        var snapshot = Build(ReportWithMonth(Today, OpenCode(1m)), null, Budget(("opencode", 20.01m)), Today);

        Assert.Equal("$1.00 of $20.01 budget", Assert.Single(snapshot.Meters).Subtitle);
    }

    // ── O7 / E1: resetsAt — first day of the next local month, in the given zone ──

    [Theory]
    // O7: today 2026-11-30 → 2026-12-01T00:00. E1: today.AddMonths(1) → day 1 at 00:00 in `zone`,
    // offset = zone.GetUtcOffset(that local DateTime) — also 2026-01-31 → 2026-02-01 and
    // 2024-02-29 → 2024-03-01; last row: in "Eastern Standard Time" Nov 1 2026 00:00 local is still
    // DST (EDT, −04:00: the clock changes at 02:00 that morning), so a fixed-standard-offset or
    // a "offset of now" implementation fails.
    [InlineData("20261130", "UTC", 2026, 12, 1, 0)]
    [InlineData("20260131", "UTC", 2026, 2, 1, 0)]
    [InlineData("20240229", "UTC", 2024, 3, 1, 0)]
    [InlineData("20261031", "Eastern Standard Time", 2026, 11, 1, -4)]
    public void Build_E1_ResetsAtIsNextLocalMonthStartInZone(
        string todayText,
        string zoneId,
        int year,
        int month,
        int day,
        int utcOffsetHours)
    {
        var today = DateOnly.ParseExact(todayText, "yyyyMMdd", CultureInfo.InvariantCulture);
        var expected = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.FromHours(utcOffsetHours));

        var snapshot = Build(ReportWithMonth(today, OpenCode(1m)), null, NoBudgets, today, TimeZoneInfo.FindSystemTimeZoneById(zoneId));

        Assert.Equal(expected, Assert.Single(snapshot.Meters).ResetsAt);
    }

    // ── O2 / E11: the gauge the tab renders ────────────────────────────────────

    [Fact]
    public void Build_O2_E11_GaugeTextIs120Percent_AndSeverityIsCriticalViaLocalThresholds()
    {
        // O2: gauge text "120%" (ProviderTabViewModel: Percent F0 + '%'), E11: 120 % ≥ 90 % critical
        // threshold → EffectiveSeverity is Critical through the existing Meter.cs / SeverityRules
        // Combine — no new severity code; 120 must still not be capped.
        var snapshot = Build(ReportWithMonth(Today, OpenCode(60m)), null, Budget(("opencode", 50m)), Today);

        Assert.Equal(Severity.Critical, Assert.Single(snapshot.Meters).EffectiveSeverity);

        var tab = new ProviderTabViewModel("opencode", "OpenCode");
        tab.UpdateFromSnapshot(snapshot, Now);

        var gauge = Assert.Single(tab.Meters);
        Assert.Equal("120%", gauge.ValueText);
        Assert.Equal(Severity.Critical, gauge.Severity);
    }

    [Fact]
    public void Build_O3_GaugeTextIsDashesWhenNoBudget()
    {
        // O3: gauge text "--" when Percent is null.
        var snapshot = Build(ReportWithMonth(Today, OpenCode(7.5m)), null, NoBudgets, Today);

        var tab = new ProviderTabViewModel("opencode", "OpenCode");
        tab.UpdateFromSnapshot(snapshot, Now);

        var gauge = Assert.Single(tab.Meters);
        Assert.Equal("--", gauge.ValueText);
        Assert.False(gauge.HasPercent); // "--" meters report no denominator
        Assert.Equal(Severity.Normal, gauge.Severity); // no percent implies nothing (SeverityRules)
    }

    [Fact]
    public void Build_E10_GaugeTabDrawsNoCardsForTheErrorSnapshot()
    {
        // E10 tail: "no empty gauge card is drawn when there are no meters" — tab level.
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "ccusage is not installed");
        var snapshot = Build(null, failure, NoBudgets, Today);

        var tab = new ProviderTabViewModel("opencode", "OpenCode");
        tab.UpdateFromSnapshot(snapshot, Now);

        Assert.Equal(AgentUsagePresenter.ErrorText(AgentUsageErrorKind.NotInstalled, "ccusage is not installed"), tab.ErrorMessage);
        Assert.True(tab.HasError);
        Assert.Empty(tab.Meters);
        Assert.Empty(tab.MeterSections); // no gauge card, empty or otherwise
    }
}
