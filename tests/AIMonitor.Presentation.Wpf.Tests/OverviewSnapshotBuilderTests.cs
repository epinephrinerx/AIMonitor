using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-016 H1.1 — <c>OverviewSnapshotBuilder.Build(lastGood, lastError, budgets, today, now, zone)</c>
/// and <c>public const string ProviderId = "overview"</c>, written from Docs/SPEC_R016.md only (§5.6):
/// the three states (same as R-015 E10/E23) with oracles O7 + decision I7's concrete message, the
/// meters of state 3 (H1.1: one per current-month ccusage agent, reading = AgentBudget.Compute,
/// ordered MonthCostUsd desc with StringComparer.Ordinal name tie-break, kind "month_budget",
/// key "agent:" + normalised agent, title = ccusage name as-is, percent = reading.Percent,
/// resetsAt = first day of the next local month 00:00 in zone — "same helper/semantics as R-015 E1",
/// detail = spent, subtitles identical to R-015 E26), the per-oracle fixture values O1–O6, O8,
/// I1 (Monthly section authoritative — the builder reads report.Monthly only), I2 (duplicate names
/// in any case → one meter, first row's values, never throw) and I6/O11 (budgets for agents outside
/// the current month, or absent from the report entirely, create no meter).
/// The fixture is the spec's oracle fixture: fixture month 2026-11, today = 2026-11-10, zone UTC,
/// monthly 2026-11 agents claude $12.50 priced · codex $3.20 + unpriced model · copilot $0.00 with
/// usage · newagent $3.20; monthly 2026-10 has zcode $9.99 only; budgets { "claude": 50, "NewAgent": 10 }.
/// Expected values are the spec's oracles with the hand calculations shown; nothing here is derived
/// from the implementation (the code is written in parallel — these tests are expected not to compile
/// until the R-016 H1 branch merges; I8: the builder is pure, so these tests are clock-free and fully
/// deterministic).
/// </summary>
public sealed class OverviewSnapshotBuilderTests
{
    // The spec's pinned oracle fixture (SPEC_R016.md, "Oracles" section).
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 7, 3, 22, TimeSpan.Zero);
    private static readonly DateTimeOffset RetrievedAt = new(2026, 11, 10, 6, 45, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 10);

    private static readonly IReadOnlyDictionary<string, decimal> NoBudgets = new Dictionary<string, decimal>();

    private static AgentUsage Usage(string agent, params (string Model, decimal? Cost)[] models) =>
        new(
            agent,
            new TokenCounts(0, 0, 0, 0, 100L * models.Length),
            models.Select(m => new ModelUsage(m.Model, new TokenCounts(100, 0, 0, 0, 100), m.Cost)).ToList());

    /// <summary>
    /// The oracle fixture's report: monthly 2026-11 holds claude $12.50, codex $3.20 + an unpriced
    /// model, copilot $0.00 (usage but zero cost) and newagent $3.20; monthly 2026-10 holds zcode
    /// $9.99 only. I1: the Monthly section is authoritative — no daily rows in the fixture at all.
    /// </summary>
    private static AgentUsageReport SpecReport() =>
        new(
            daily: [],
            monthly:
            [
                new MonthlyAgentUsage(2026, 10, [Usage("zcode", ("z-code-model", 9.99m))]),
                new MonthlyAgentUsage(2026, 11,
                [
                    Usage("claude", ("claude-model", 12.50m)),
                    Usage("codex", ("gpt-5", 3.20m), ("mystery-model", null)),
                    Usage("copilot", ("copilot-model", 0.00m)),
                    Usage("newagent", ("new-model", 3.20m)),
                ]),
            ],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: RetrievedAt,
            skippedRows: 0);

    /// <summary>Fixture budgets: { "claude": 50, "NewAgent": 10 } — "NewAgent" exercises the
    /// case-insensitive lookup the O4 oracle pins.</summary>
    private static IReadOnlyDictionary<string, decimal> SpecBudgets() =>
        new Dictionary<string, decimal> { ["claude"] = 50m, ["NewAgent"] = 10m };

    private static AgentUsageReport ReportWithAgents(int year, int month, params AgentUsage[] agents) =>
        new(
            daily: [],
            monthly: [new MonthlyAgentUsage(year, month, agents.ToList())],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: RetrievedAt,
            skippedRows: 0);

    // H1.1: Build is the 6-parameter pure function (no rangeDays/metric — Overview has no chart).
    private static ProviderSnapshot? BuildRaw(
        AgentUsageReport? lastGood,
        AgentUsageUnavailableException? lastError,
        IReadOnlyDictionary<string, decimal> budgets,
        DateOnly today,
        DateTimeOffset? now = null,
        TimeZoneInfo? zone = null) =>
        OverviewSnapshotBuilder.Build(lastGood, lastError, budgets, today, now ?? Now, zone ?? TimeZoneInfo.Utc);

    // Every state that has something to show: unwrap once so tests read a non-null snapshot
    // (repo style, CS8602 under warnings-as-errors).
    private static ProviderSnapshot Build(
        AgentUsageReport? lastGood,
        AgentUsageUnavailableException? lastError,
        IReadOnlyDictionary<string, decimal> budgets,
        DateOnly today)
    {
        var snapshot = BuildRaw(lastGood, lastError, budgets, today);
        Assert.NotNull(snapshot); // states 2 and 3 always yield a snapshot (H1.1)
        return snapshot!;
    }

    // ── H1.1 states 1/2/3 (same as R-015 E10/E23) + O7/I7 ─────────────────────

    [Fact]
    public void Build_H11_State1_NoResultYet_ReturnsNull()
    {
        // H1.1 state 1: lastGood == null && lastError == null → null, "the tab keeps its initial
        // 'Checking...' state" (the "Checking ccusage..." card text is I3's, tested at the view-model
        // level in OverviewTabViewModelTests).
        Assert.Null(BuildRaw(null, null, SpecBudgets(), Today));
    }

    [Fact]
    public void Build_O7_I7_State2_NotInstalledBeforeAnyGoodReport_IsErrorOnlySnapshot()
    {
        // O7 state 2 with I7's concrete inputs: lastGood = null,
        // lastError = AgentUsageUnavailableException(NotInstalled, "ccusage was not found on PATH.")
        // → configured: true, error == AgentUsagePresenter.ErrorText(NotInstalled, msg), zero meters,
        // fetchedAt: now. I7 also pins that the presenter's current text is that same message — this
        // is the R-014 install guidance for NotInstalled.
        var notFound = "ccusage was not found on PATH.";
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, notFound);

        var snapshot = Build(null, failure, SpecBudgets(), Today);

        Assert.Equal("overview", OverviewSnapshotBuilder.ProviderId); // H1.1 const
        Assert.Equal("overview", snapshot.ProviderId);
        Assert.True(snapshot.Configured); // state 2 / R-015 E10 shape: configured TRUE so the setup card stays away
        Assert.Equal(AgentUsagePresenter.ErrorText(AgentUsageErrorKind.NotInstalled, notFound), snapshot.Error); // O7 oracle
        Assert.Equal(notFound, snapshot.Error); // I7: "which is that same message per the current presenter"
        Assert.Empty(snapshot.Meters); // I7: "and zero meters"
        Assert.Empty(snapshot.Stats);
        Assert.Null(snapshot.History);
        Assert.Equal(Now, snapshot.FetchedAt); // state 2: fetchedAt = now
    }

    [Theory]
    // O7 generic: state 2's error is exactly AgentUsagePresenter.ErrorText(kind, message) for any
    // kind (NotInstalled is the concrete I7 case above).
    [InlineData(AgentUsageErrorKind.Timeout)]
    [InlineData(AgentUsageErrorKind.OutputTooLarge)]
    [InlineData(AgentUsageErrorKind.Failed)]
    [InlineData(AgentUsageErrorKind.InvalidResponse)]
    public void Build_O7_State2_AnyErrorKind_ErrorComesFromThePresenter(AgentUsageErrorKind kind)
    {
        var message = "ccusage went wrong this way";
        var failure = new AgentUsageUnavailableException(kind, message);

        var snapshot = Build(null, failure, NoBudgets, Today);

        Assert.True(snapshot.Configured);
        Assert.Equal(AgentUsagePresenter.ErrorText(kind, message), snapshot.Error);
        Assert.Empty(snapshot.Meters);
        Assert.Equal(Now, snapshot.FetchedAt);
    }

    [Fact]
    public void Build_H11_State3_GoodReportWithLaterError_IgnoresError_StaleButGoodWins()
    {
        // H1.1 state 3 tail: "lastError is ignored when lastGood exists (stale-but-good wins, as for
        // OpenCode)". State 3 also pins fetchedAt = lastGood.RetrievedAt (not now) and history: null.
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.Failed, "ccusage failed this time");

        var snapshot = Build(SpecReport(), failure, SpecBudgets(), Today);

        Assert.Equal("overview", snapshot.ProviderId);
        Assert.True(snapshot.Configured);
        Assert.Null(snapshot.Error);
        Assert.Equal(4, snapshot.Meters.Count); // the four 2026-11 agents (below)
        Assert.Null(snapshot.History); // state 3: history: null — no chart on Overview (H1.2)
        Assert.Equal(RetrievedAt, snapshot.FetchedAt); // state 3 / O6: fetchedAt = lastGood.RetrievedAt
        Assert.NotEqual(Now, snapshot.FetchedAt);
    }

    // ── State 3 meters: O1 order + the H1.1 meter fields ───────────────────────

    [Fact]
    public void Build_O1_MetersAreOrderedCostDescendingThenOrdinalName_AndZcodeIsAbsent()
    {
        // O1 order: claude ($12.50), codex ($3.20), newagent ($3.20), copilot ($0.00).
        // Hand check: 12.50 > 3.20 = 3.20 > 0.00; the codex/newagent tie breaks by agent name with
        // StringComparer.Ordinal ascending — 'c' = U+0063 < 'n' = U+006E, so "codex" < "newagent".
        // zcode lives only in 2026-10 → no meter (H1.1: "Agents in the report but not in the current
        // month ... get no meter").
        var snapshot = Build(SpecReport(), null, SpecBudgets(), Today);

        Assert.Equal(
            ["claude", "codex", "newagent", "copilot"],
            snapshot.Meters.Select(m => m.Title)); // title = the ccusage name as-is
        Assert.Equal(
            ["agent:claude", "agent:codex", "agent:newagent", "agent:copilot"],
            snapshot.Meters.Select(m => m.Key)); // key = "agent:" + normalised (lower-case) agent
        Assert.All(snapshot.Meters, m => Assert.Equal("month_budget", m.Kind));
    }

    [Fact]
    public void Build_O2_Claude_Percent25AndBudgetSubtitle()
    {
        // O2: claude $12.50 against budget 50 → percent = 12.50 ÷ 50 × 100 = 25 (hand-derived);
        // subtitle "$12.50 of $50.00 budget" (identical text to R-015 E26), detail = spent "$12.50".
        var meter = Build(SpecReport(), null, SpecBudgets(), Today).Meters[0];

        Assert.Equal("claude", meter.Title);
        Assert.Equal(25.0, meter.Percent);
        Assert.Equal("$12.50 of $50.00 budget", meter.Subtitle);
        Assert.Equal("$12.50", meter.Detail);
    }

    [Fact]
    public void Build_O3_Codex_UnpricedSpentTextWithNoBudget()
    {
        // O3: codex $3.20 priced + one unpriced model, no budget → percent null; spent = "$3.20" plus
        // " + unpriced" when HasUnpricedModels (H1.1) → subtitle
        // "$3.20 + unpriced · No budget set — add one in Settings" (R-015 E26 text).
        var meter = Build(SpecReport(), null, SpecBudgets(), Today).Meters[1];

        Assert.Equal("codex", meter.Title);
        Assert.Null(meter.Percent);
        Assert.Equal("$3.20 + unpriced", meter.Detail);
        Assert.Equal("$3.20 + unpriced · No budget set — add one in Settings", meter.Subtitle);
    }

    [Fact]
    public void Build_O4_NewAgent_BudgetFoundCaseInsensitively_Percent32()
    {
        // O4: the report's agent is "newagent"; the settings budget is keyed "NewAgent".
        // AgentBudget.Compute finds it case-insensitively (R-015 E21) → percent = 3.20 ÷ 10 × 100 = 32
        // (hand-derived), subtitle "$3.20 of $10.00 budget". A brand-new ccusage agent name gets its
        // gauge with no code change — H1.1 puts no agent names in this tab's code.
        var snapshot = Build(SpecReport(), null, SpecBudgets(), Today);

        var meter = snapshot.Meters[2];
        Assert.Equal("newagent", meter.Title);
        Assert.Equal("agent:newagent", meter.Key);
        Assert.Equal(32.0, meter.Percent);
        Assert.Equal("$3.20 of $10.00 budget", meter.Subtitle);
        Assert.Equal("$3.20", meter.Detail);
    }

    [Fact]
    public void Build_O5_Copilot_ZeroCostUsage_PercentNullNoBudgetSubtitle()
    {
        // O5: copilot used the model but costs $0.00, no budget → percent null, subtitle
        // "$0.00 · No budget set — add one in Settings", detail "$0.00" (ChartFormat.Money(0) per E26/E12).
        var meter = Build(SpecReport(), null, SpecBudgets(), Today).Meters[3];

        Assert.Equal("copilot", meter.Title);
        Assert.Null(meter.Percent);
        Assert.Equal("$0.00", meter.Detail);
        Assert.Equal("$0.00 · No budget set — add one in Settings", meter.Subtitle);
    }

    [Fact]
    public void Build_O6_EveryMeterResetsAtFirstOfNextMonth_FetchedAtIsRetrievedAt()
    {
        // O6: resetsAt = 2026-12-01T00:00:00+00:00 for EVERY meter (today 2026-11-10, zone UTC:
        // first day of the next local month at 00:00 in the zone, R-015 E1 semantics), and
        // fetchedAt = lastGood.RetrievedAt, not now (H1.1 state 3).
        var expected = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

        var snapshot = Build(SpecReport(), null, SpecBudgets(), Today);

        Assert.All(snapshot.Meters, m => Assert.Equal(expected, m.ResetsAt));
        Assert.Equal(RetrievedAt, snapshot.FetchedAt);
    }

    [Theory]
    // H1.1: resetsAt uses the "same helper/semantics as R-015 E1" — day 1 of the NEXT month at
    // 00:00 in the supplied zone with zone.GetUtcOffset of THAT local instant (not of now, not the
    // zone's standard offset). Eastern row hand-checked in OpenCodeSnapshotBuilderTests' E1 theory:
    // 2026-11-01 00:00 in "Eastern Standard Time" is still daylight time (EDT, −04:00; the clock
    // changes at 02:00 that morning).
    [InlineData("20261110", "UTC", 2026, 12, 1, 0)]
    [InlineData("20261031", "Eastern Standard Time", 2026, 11, 1, -4)]
    public void Build_H11_ResetsAtHonoursTheZoneOfTheRebuild(
        string todayText,
        string zoneId,
        int year,
        int month,
        int day,
        int utcOffsetHours)
    {
        var today = DateOnly.ParseExact(todayText, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var expected = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.FromHours(utcOffsetHours));
        var report = ReportWithAgents(today.Year, today.Month, Usage("claude", ("claude-model", 1m)));

        var snapshot = BuildRaw(report, null, NoBudgets, today, zone: TimeZoneInfo.FindSystemTimeZoneById(zoneId));

        Assert.NotNull(snapshot);
        Assert.Equal(expected, Assert.Single(snapshot!.Meters).ResetsAt);
    }

    // ── O8 / I1: agents come from THIS month's Monthly entry only ───────────────

    [Fact]
    public void Build_O8_NoEntryForCurrentMonth_State3WithZeroMeters()
    {
        // O8: a report with no 2026-11 month entry (zcode's 2026-10 only) → state 3 with zero meters
        // (configured true, error null, fetchedAt = RetrievedAt). The "No agent usage recorded this
        // month" text is the tab-level half of O8, asserted in OverviewTabViewModelTests and
        // MainWindowViewModelOverviewTabTests. I1: only Monthly is read — the Daily rows are absent
        // from this fixture entirely, so the meters can only come from Monthly.
        var report = ReportWithAgents(2026, 10, Usage("zcode", ("z-code-model", 9.99m)));

        var snapshot = Build(report, null, SpecBudgets(), Today);

        Assert.True(snapshot.Configured);
        Assert.Null(snapshot.Error);
        Assert.Empty(snapshot.Meters);
        Assert.Null(snapshot.History);
        Assert.Equal(RetrievedAt, snapshot.FetchedAt);
    }

    [Fact]
    public void Build_H11_CurrentMonthEntryWithNoAgents_IsZeroMeters()
    {
        // H1.1: "No such month, or no agents in it → zero meters."
        var report = ReportWithAgents(2026, 11);

        var snapshot = Build(report, null, SpecBudgets(), Today);

        Assert.Empty(snapshot.Meters);
    }

    // ── I2: duplicate names in any case → one meter, first row wins, never throws ──

    [Fact]
    public void Build_I2_DuplicateAgentNames_ProduceOneMeterCarryingTheFirstRowsValues()
    {
        // I2 oracle: rows `Codex` $1.00 then `codex` $2.00 → ONE meter named "codex" (normalised
        // lower-case) whose values are the first row's, "i.e. exactly what AgentBudget.Compute
        // returns" (its name-matching FirstOrDefault picks row 1) — subtitle
        // "$1.00 · No budget set — add one in Settings". Never throw: a builder that emitted both
        // rows would die in ProviderSnapshot's duplicate-key guard ("agent:codex"), and this test
        // failing with that exception is I2's "Never throw" being violated.
        var report = ReportWithAgents(
            2026,
            11,
            Usage("Codex", ("first-model", 1.00m)),
            Usage("codex", ("second-model", 2.00m)));

        var snapshot = Build(report, null, NoBudgets, Today);

        var meter = Assert.Single(snapshot.Meters);
        Assert.Equal("codex", meter.Title); // normalised lower-case name (I2)
        Assert.Equal("agent:codex", meter.Key);
        Assert.Equal("$1.00", meter.Detail); // first row's values — not $2.00, not $3.00 summed
        Assert.Equal("$1.00 · No budget set — add one in Settings", meter.Subtitle);
        Assert.Null(meter.Percent);
    }

    [Fact]
    public void Build_I2_DuplicateNamesWithBudget_ValuesAreExactlyAgentBudgetComputeFirstRow()
    {
        // I2 tail: the reading is exactly AgentBudget.Compute's — with budget 4 on the duplicated
        // name, Compute matches the FIRST row ($1.00): percent = 1.00 ÷ 4 × 100 = 25 (hand-derived),
        // subtitle "$1.00 of $4.00 budget". If a meter were built from the second row this would be
        // $2.00 / 50 %.
        var report = ReportWithAgents(
            2026,
            11,
            Usage("Codex", ("first-model", 1.00m)),
            Usage("codex", ("second-model", 2.00m)));
        var budgets = new Dictionary<string, decimal> { ["codex"] = 4m };

        var meter = Assert.Single(Build(report, null, budgets, Today).Meters);

        Assert.Equal(25.0, meter.Percent);
        Assert.Equal("$1.00 of $4.00 budget", meter.Subtitle);
    }

    // ── I6 / O11: budgets never create meters ───────────────────────────────────

    [Fact]
    public void Build_O11_I6_BudgetsForZcodeAndGhost_CreateNoMeter()
    {
        // O11 (I6): a budget for `zcode` (present only in 2026-10) and for `ghost` (never in the
        // report at all) creates no meter — meters are agents of the CURRENT month, budgets only
        // decorate them. The four fixture meters must be exactly the 2026-11 agents.
        var budgets = new Dictionary<string, decimal> { ["zcode"] = 5m, ["ghost"] = 100m };

        var snapshot = Build(SpecReport(), null, budgets, Today);

        Assert.Equal(
            ["claude", "codex", "newagent", "copilot"],
            snapshot.Meters.Select(m => m.Title));
        Assert.DoesNotContain(snapshot.Meters, m => m.Key == "agent:zcode");
        Assert.DoesNotContain(snapshot.Meters, m => m.Key == "agent:ghost");
    }
}
