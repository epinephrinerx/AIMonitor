using System.Globalization;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-015 Q2.2 — <c>OpenCodeSnapshotBuilder.Build(lastGood, lastError, budgets, today, now, zone)</c>
/// written from Docs/SPEC_R015.md only (§5.6): the three-state table, the meter fields, oracles
/// O2/O3/O5/O7/O8 and decisions E1 (resetsAt day 1 00:00 in the caller's zone with
/// zone.GetUtcOffset of THAT local instant), E10 (error-first snapshot is configured TRUE — E10
/// overrides the "configured: false" wording of the state table and of O8), E11 (120 % renders
/// Critical through the existing local thresholds, no new code) and E12 (money(x) is
/// ChartFormat.Money((double)x), also for the budget in the subtitle). Q2 tests: expected not to
/// compile until the Q2 branch merges (handoff note, E16).
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

    // ── The three states of the Q2.2 table ─────────────────────────────────────

    [Fact]
    public void Build_Q22_FirstRunStillGoing_ReturnsNullSoNothingIsPushed()
    {
        // Q2.2: lastGood == null && lastError == null → "no snapshot is pushed; the tab keeps its
        // initial 'Checking...' state".
        Assert.Null(OpenCodeSnapshotBuilder.Build(null, null, NoBudgets, Today, Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Build_E10_ErrorBeforeAnyGoodReport_IsConfiguredWithErrorBannerAndNoMeters()
    {
        // E10 (OVERRIDES the "configured: false" cell of the state table and of O8): the error-first
        // snapshot carries configured: true so the tab shows the error banner — not the setup card,
        // which would have an empty hint — with no meters and no stats.
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "ccusage is not installed");

        var snapshot = OpenCodeSnapshotBuilder.Build(null, failure, NoBudgets, Today, Now, TimeZoneInfo.Utc);

        Assert.NotNull(snapshot);
        Assert.Equal("opencode", snapshot.ProviderId);
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

        var snapshot = OpenCodeSnapshotBuilder.Build(
            ReportWithMonth(Today, OpenCode(30m)), failure, NoBudgets, Today, Now, TimeZoneInfo.Utc);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Configured);
        Assert.Null(snapshot.Error);
        var meter = Assert.Single(snapshot.Meters); // one meter (below), no stats
        Assert.Equal("month_budget", meter.Kind); // Q2.2 meter fields
        Assert.Equal("month_budget", meter.Key);
        Assert.Equal("Month to date", meter.Title);
        Assert.Empty(snapshot.Stats);
    }

    // ── O2 / O3 / O5: the meter values ─────────────────────────────────────────

    [Fact]
    public void Build_O2_OverBudget_PercentIsNotCapped_AndBudgetUsesMoneyFormatter()
    {
        // O2: cost 60, budget 50 → Percent 120, not capped. Subtitle text pinned by O1/E12:
        // money(50) = ChartFormat.Money(50.0) = "$50.00". Detail = money(60) = "$60.00".
        var snapshot = OpenCodeSnapshotBuilder.Build(
            ReportWithMonth(Today, OpenCode(60m)), null, Budget(("opencode", 50m)), Today, Now, TimeZoneInfo.Utc);

        Assert.NotNull(snapshot);
        var meter = Assert.Single(snapshot.Meters);
        Assert.Equal(120.0, meter.Percent); // E12/O2: (60/50)*100 uncapped
        Assert.Equal("of your $50.00 budget", meter.Subtitle);
        Assert.Equal("$60.00", meter.Detail);
        Assert.Equal(Severity.Normal, meter.ServerSeverity); // Q2.2: colour comes from the local thresholds
    }

    [Fact]
    public void Build_O3_NoBudget_PercentNullAndNoBudgetSubtitle()
    {
        // O3: cost 7.5, no budget → Percent null, subtitle "No budget set — add one in Settings",
        // detail "$7.50".
        var snapshot = OpenCodeSnapshotBuilder.Build(
            ReportWithMonth(Today, OpenCode(7.5m)), null, NoBudgets, Today, Now, TimeZoneInfo.Utc);

        Assert.NotNull(snapshot);
        var meter = Assert.Single(snapshot.Meters);
        Assert.Null(meter.Percent);
        Assert.Equal("No budget set — add one in Settings", meter.Subtitle);
        Assert.Equal("$7.50", meter.Detail);
        Assert.NotNull(meter.ResetsAt); // the month window is shown regardless of the budget
    }

    [Fact]
    public void Build_O5_UnpricedModels_AppendsUnpricedToTheDetail()
    {
        // O5: model A cost 3, model B unpriced → detail "$3.00 + unpriced" (cost not counted as $0).
        var monthRow = Usage("opencode", ("model-a", 3m), ("model-b", null));

        var snapshot = OpenCodeSnapshotBuilder.Build(
            ReportWithMonth(Today, monthRow), null, NoBudgets, Today, Now, TimeZoneInfo.Utc);

        Assert.NotNull(snapshot);
        var meter = Assert.Single(snapshot.Meters);
        Assert.Equal("$3.00 + unpriced", meter.Detail); // Q2.2: money(MonthCostUsd) + " + unpriced"
    }

    [Theory]
    // E12: money(x) is ChartFormat.Money((double)x) — the AgentUsagePresenter priced-cost text.
    [InlineData("0.005", "<$0.01")]
    [InlineData("0", "$0.00")]
    [InlineData("12.34", "$12.34")]
    public void Build_E12_MoneyFormatMatchesChartFormatMoney(string costText, string expectedDetail)
    {
        var cost = decimal.Parse(costText, CultureInfo.InvariantCulture);

        var snapshot = OpenCodeSnapshotBuilder.Build(
            ReportWithMonth(Today, OpenCode(cost)), null, NoBudgets, Today, Now, TimeZoneInfo.Utc);

        Assert.NotNull(snapshot);
        Assert.Equal(expectedDetail, Assert.Single(snapshot.Meters).Detail);
    }

    [Fact]
    public void Build_E12_BudgetInSubtitleIsTheSameMoneyFormatter()
    {
        // E12: "the budget in the subtitle uses it too" — money(20.01) = "$20.01".
        var snapshot = OpenCodeSnapshotBuilder.Build(
            ReportWithMonth(Today, OpenCode(1m)), null, Budget(("opencode", 20.01m)), Today, Now, TimeZoneInfo.Utc);

        Assert.NotNull(snapshot);
        Assert.Equal("of your $20.01 budget", Assert.Single(snapshot.Meters).Subtitle);
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
        string todayText, string zoneId, int year, int month, int day, int utcOffsetHours)
    {
        var day2 = DateOnly.ParseExact(todayText, "yyyyMMdd", CultureInfo.InvariantCulture);
        var expected = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.FromHours(utcOffsetHours));

        var snapshot = OpenCodeSnapshotBuilder.Build(
            ReportWithMonth(day2, OpenCode(1m)), null, NoBudgets, day2, Now, TimeZoneInfo.FindSystemTimeZoneById(zoneId));

        Assert.NotNull(snapshot);
        Assert.Equal(expected, Assert.Single(snapshot.Meters).ResetsAt);
    }

    // ── O2 / E11: the gauge the tab renders ────────────────────────────────────

    [Fact]
    public void Build_O2_E11_GaugeTextIs120Percent_AndSeverityIsCriticalViaLocalThresholds()
    {
        // O2: gauge text "120%" (ProviderTabViewModel: Percent F0 + '%'), E11: 120 % ≥ 90 % critical
        // threshold → EffectiveSeverity is Critical through the existing Meter.cs / SeverityRules
        // Combine — no new severity code; 120 must still not be capped.
        var snapshot = OpenCodeSnapshotBuilder.Build(
            ReportWithMonth(Today, OpenCode(60m)), null, Budget(("opencode", 50m)), Today, Now, TimeZoneInfo.Utc);

        Assert.NotNull(snapshot);
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
        var snapshot = OpenCodeSnapshotBuilder.Build(
            ReportWithMonth(Today, OpenCode(7.5m)), null, NoBudgets, Today, Now, TimeZoneInfo.Utc);

        Assert.NotNull(snapshot);
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
        var snapshot = OpenCodeSnapshotBuilder.Build(null, failure, NoBudgets, Today, Now, TimeZoneInfo.Utc);

        Assert.NotNull(snapshot);
        var tab = new ProviderTabViewModel("opencode", "OpenCode");
        tab.UpdateFromSnapshot(snapshot, Now);

        Assert.Equal(AgentUsagePresenter.ErrorText(AgentUsageErrorKind.NotInstalled, "ccusage is not installed"), tab.ErrorMessage);
        Assert.True(tab.HasError);
        Assert.Empty(tab.Meters);
        Assert.Empty(tab.MeterSections); // no gauge card, empty or otherwise
    }
}
