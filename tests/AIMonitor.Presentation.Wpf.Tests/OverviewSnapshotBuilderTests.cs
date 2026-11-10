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
/// in any case → one meter, first row's values, never throw), I6/O11 (budgets for agents outside
/// the current month, or absent from the report entirely, create no meter) and — added in round 2
/// per I12 — <c>public static string? EmptyMessage(lastGood, lastError, today, zone)</c> tested
/// directly with explicit today/zone: the I3/I10 ASCII texts, the I4 rollover trigger (I12: both
/// halves, plus a current-month entry with zero agents and a month boundary in a non-local zone),
/// and — round 3 per I17 — the complete EmptyMessage table: state 1 → "Checking ccusage...",
/// state 2 → null, state 3 with ≥1 meter → null (I17's new row, the O1 fixture), state 3 with a
/// current-month entry and zero agents → "No agent usage recorded this month", state 3 with no
/// current-month entry → the I4 waiting text when the retrieved month differs and I3's recorded
/// text when it does not. I17's row is checked against Build's own meters, because the decision
/// says "null whenever state 3 produces at least one meter". Round 4 adds I20: the negative half of
/// I1 — current-month DAILY rows with no current-month Monthly entry still give zero meters.
/// The fixture is the spec's oracle fixture: fixture month 2026-11, today = 2026-11-10, zone UTC,
/// monthly 2026-11 agents claude $12.50 priced · codex $3.20 + unpriced model · copilot $0.00 with
/// usage · newagent $3.20; monthly 2026-10 has zcode $9.99 only; budgets { "claude": 50, "NewAgent": 10 }.
/// Expected values are the spec's oracles with the hand calculations shown; nothing here is derived
/// from the implementation (the code is written in parallel — these tests are expected not to compile
/// until the R-016 H1 branch merges; I8: the builder is pure, so these tests are clock-free and fully
/// deterministic). The branch has since merged (5167681 + c02da4e + efa9051) and the tests run
/// against it.
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
        MonthlyReport(year, month, agents.ToList(), RetrievedAt);

    private static AgentUsageReport MonthlyReport(
        int year,
        int month,
        IReadOnlyList<AgentUsage> agents,
        DateTimeOffset retrievedAt) =>
        new(
            daily: [],
            monthly: [new MonthlyAgentUsage(year, month, agents)],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: retrievedAt,
            skippedRows: 0);

    // I12 seam: the builder exposes the resulting empty-state text for MainWindowViewModel to hand
    // to the Overview tab — tested directly here with an explicit today and zone, so nothing reads
    // the machine clock.
    private static string? Message(
        AgentUsageReport? lastGood,
        AgentUsageUnavailableException? lastError,
        DateOnly today,
        TimeZoneInfo zone) =>
        OverviewSnapshotBuilder.EmptyMessage(lastGood, lastError, today, zone);

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

    [Theory]
    // I20 (Inspector B, major 2): the negative half of I1 that O8's fixture cannot kill — Daily
    // DOES hold current-month rows with costs, while Monthly has no 2026-11 entry at all. A builder
    // that also reads Daily would synthesise a "claude" meter (or worse, sum the rows) instead of
    // the zero meters H1.1 mandates. The expected text follows I17's last two rows, split by
    // whether RetrievedAt's month (in zone) differs from today's: retrieved 2026-10-20 -> the I4
    // waiting text; retrieved 2026-11-05 (this month) -> I3's recorded text. Hand check: today =
    // 2026-11-10, zone UTC; both retrieval instants are UTC so their local months are 10 and 11.
    [InlineData(2026, 10, 20, "Waiting for this month's ccusage data")]
    [InlineData(2026, 11, 5, "No agent usage recorded this month")]
    public void Build_I20_CurrentMonthDailyRowsWithoutMonthlyEntry_AreIgnored(
        int retrievedYear,
        int retrievedMonth,
        int retrievedDay,
        string expectedMessage)
    {
        var retrievedAt = new DateTimeOffset(retrievedYear, retrievedMonth, retrievedDay, 8, 0, 0, TimeSpan.Zero);
        var report = new AgentUsageReport(
            daily:
            [
                new DailyAgentUsage(new DateOnly(2026, 11, 9), [Usage("claude", ("claude-model", 4.00m))]),
                new DailyAgentUsage(new DateOnly(2026, 11, 10), [Usage("claude", ("claude-model", 1.50m))]),
            ],
            monthly: [new MonthlyAgentUsage(2026, 10, [Usage("zcode", ("z-code-model", 9.99m))])],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: retrievedAt,
            skippedRows: 0);

        var snapshot = Build(report, null, SpecBudgets(), Today);

        Assert.Empty(snapshot.Meters); // H1.1/I1: agents come from today's Monthly entry only (absent)

        // I17's table for state 3 with no current-month entry, on the same fixture.
        Assert.Equal(expectedMessage, Message(report, null, Today, TimeZoneInfo.Utc));
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

    // ── I12 / I17: EmptyMessage(lastGood, lastError, today, zone) — the full I17 table ──
    //
    // I17 (round 3) is the whole decision table and every row is pinned by a test below:
    //   state 1                                    -> "Checking ccusage..."
    //   state 2                                    -> null
    //   state 3 with >=1 meter                     -> null          (I17's new row, the O1 fixture)
    //   state 3, current-month entry, 0 agents     -> "No agent usage recorded this month"
    //   state 3, no current-month entry, rolled    -> "Waiting for this month's ccusage data"
    //   state 3, no current-month entry, this month-> "No agent usage recorded this month"

    [Fact]
    public void EmptyMessage_I3I10_State1_IsTheAsciiCheckingText()
    {
        // I3 state 1 + I10: exactly "Checking ccusage..." with three ASCII dots — NOT U+2026.
        var message = Message(null, null, Today, TimeZoneInfo.Utc);

        Assert.Equal("Checking ccusage...", message);
        Assert.DoesNotContain('\u2026', message!); // I10 names WO2's 5167681 ellipsis bug
    }

    [Fact]
    public void EmptyMessage_I3_State2_IsNullBecauseOnlyTheBannerShows()
    {
        // I3: "In state 2 it is null and only the error banner shows." I7's concrete error shape.
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "ccusage was not found on PATH.");

        Assert.Null(Message(null, failure, Today, TimeZoneInfo.Utc));
    }

    [Fact]
    public void EmptyMessage_I12_CurrentMonthEntryWithZeroAgents_IsTheRecordedText()
    {
        // I12 pins this case explicitly: a current-month entry with zero agents shows "No agent
        // usage recorded this month" — the rollover text needs the month difference too (below).
        var report = ReportWithAgents(2026, 11); // entry exists, no agents

        Assert.Equal("No agent usage recorded this month", Message(report, null, Today, TimeZoneInfo.Utc));
    }

    [Fact]
    public void EmptyMessage_I4_RolledToANewMonth_IsTheWaitingText()
    {
        // I12's trigger, both halves true: no Monthly entry for today's 2026-11 AND the month of
        // lastGood.RetrievedAt (2026-10-20, UTC) differs from today's → the I4 rollover text.
        var report = MonthlyReport(2026, 10, [Usage("zcode", ("z-code-model", 9.99m))],
            new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal("Waiting for this month's ccusage data", Message(report, null, Today, TimeZoneInfo.Utc));
    }

    [Fact]
    public void EmptyMessage_I12_NoCurrentMonthEntryButRetrievedThisMonth_IsTheRecordedText()
    {
        // I12's trigger is a conjunction: the report has no 2026-11 entry but was retrieved on
        // 2026-11-05 (this month, in zone) — NOT rollover, so I3's text stands. This separates the
        // rollover from O8's plain "no entry" case.
        var report = MonthlyReport(2026, 10, [Usage("zcode", ("z-code-model", 9.99m))],
            new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal("No agent usage recorded this month", Message(report, null, Today, TimeZoneInfo.Utc));
    }

    [Fact]
    public void EmptyMessage_H11_RolledReportWithLaterError_KeepsTheState3Text()
    {
        // H1.1 state 3 tail ("lastError is ignored when lastGood exists") applies to the message
        // too: a rolled last month's report with a fresh failure still shows the I4 waiting text.
        var report = MonthlyReport(2026, 10, [Usage("zcode", ("z-code-model", 9.99m))],
            new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.Failed, "ccusage failed this time");

        Assert.Equal("Waiting for this month's ccusage data", Message(report, failure, Today, TimeZoneInfo.Utc));
    }

    [Theory]
    // I12: the rollover condition is computed from the method's OWN zone — the same instant
    // (2026-11-30T20:00Z) sits in 2026-12 at UTC+5 but in 2026-11 at UTC, and today is 2026-12-01.
    // The report has only a 2026-11 entry, so in the +5 zone the retrieved month EQUALS today's
    // month (not rollover → I3's text) while in UTC it differs (rollover → I4's text). A builder
    // that hardcoded UTC or read TimeZoneInfo.Local would land on one row only.
    [InlineData(5, "No agent usage recorded this month")]
    [InlineData(0, "Waiting for this month's ccusage data")]
    public void EmptyMessage_I12_RolloverHonoursTheSuppliedZone(
        int offsetHours,
        string expected)
    {
        var today = new DateOnly(2026, 12, 1);
        var report = MonthlyReport(2026, 11, [Usage("claude", ("claude-model", 1m))],
            new DateTimeOffset(2026, 11, 30, 20, 0, 0, TimeSpan.Zero));
        var offset = TimeSpan.FromHours(offsetHours);
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            $"Fixture UTC{offsetHours:+0#;-0#;0}", offset, "Fixture zone", "Fixture zone");

        Assert.Equal(expected, Message(report, null, today, zone));
    }

    // I17 closes the spec gap this test was skipped for (round 3): the merged code returned the
    // recorded text whenever a current-month entry EXISTS, ignoring its agents, so the tab carried an
    // empty-state text above working gauges. efa9051 implements I17's rule — null whenever state 3
    // produces at least one meter.
    [Fact]
    public void EmptyMessage_I3_CurrentMonthEntryWithAgents_HasNoEmptyStateText()
    {
        // I17 row "state 3 with >=1 meter -> null", whose oracle the decision states directly: "the
        // O1 November fixture -> null". Same report as the O1 meter tests: the 2026-11 Monthly entry
        // holds four agents, so Build exposes four gauges (pinned by Build_O1_..._ZcodeIsAbsent) and
        // there is no empty state to message — I3's texts are for the no-meter states (state 1,
        // state 3 with zero meters) and state 2 is null too. The handed-off text must therefore be
        // null; a non-null one would render above the working gauges, which is exactly the mismatch
        // Reviewer2 reported.
        Assert.Null(Message(SpecReport(), null, Today, TimeZoneInfo.Utc));

        // The row's condition is ">=1 meter", so tie it to this same report's meters: not empty.
        Assert.Equal(4, Build(SpecReport(), null, SpecBudgets(), Today).Meters.Count);
    }

    [Fact]
    public void EmptyMessage_I17_MeterPresentBeatsTheRolloverCondition_IsNull()
    {
        // I17's headline rule is "null WHENEVER state 3 produces at least one meter", which ranks the
        // meter row above I4's rollover row — so the order of the two checks is pinned, not only the
        // texts. Realistic, and reachable only through the builder's own today/zone seam (I12): a
        // UTC-5 user whose report was retrieved at 2026-12-01T04:30Z, whose LOCAL RetrievedAt is
        // 2026-11-30T23:30 (November), while ccusage's monthly section — bucketed in UTC — already
        // holds the 2026-12 entry with claude in it. The rebuild happens minutes later at
        // 2026-12-01T05:10Z = 00:10 local (I8: today comes from that instant), so today's month is
        // 2026-12, the RetrievedAt month differs, and state 3 still has one meter -> null. A builder
        // that read the rollover condition first would answer "Waiting for this month's ccusage data"
        // above a working gauge.
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Fixture UTC-5", TimeSpan.FromHours(-5), "Fixture zone", "Fixture zone");
        var retrievedAt = new DateTimeOffset(2026, 12, 1, 4, 30, 0, TimeSpan.Zero); // 2026-11-30T23:30 local
        var now = new DateTimeOffset(2026, 12, 1, 5, 10, 0, TimeSpan.Zero); // 2026-12-01T00:10 local
        var today = new DateOnly(2026, 12, 1);
        var report = MonthlyReport(2026, 12, [Usage("claude", ("claude-model", 1m))], retrievedAt);

        var retrievedLocal = TimeZoneInfo.ConvertTime(retrievedAt, zone);
        Assert.Equal(11, retrievedLocal.Month); // I4's month-difference half IS true (2026-11)
        Assert.Equal(12, today.Month); // while today's month is 2026-12

        var snapshot = BuildRaw(report, null, NoBudgets, today, now: now, zone: zone);

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Meters); // I17's precondition: this state produces a meter
        Assert.Null(Message(report, null, today, zone));
    }

    [Theory]
    // I17's full table in one place, with the invariant the decision adds: in state 3 the message is
    // null EXACTLY when the same inputs make Build produce a meter ("EmptyMessage returns null
    // whenever state 3 produces at least one meter"). The row texts are I17's; the meter counts are
    // H1.1's (one gauge per current-month agent) — the fixture month is the O1 November one. The two
    // rows with no report at all (state 1 -> "Checking ccusage...", state 2 -> null) are the
    // EmptyMessage_I3I10_State1_.../EmptyMessage_I3_State2_... facts above.
    [InlineData("state 3, the O1 November fixture", 4, null)]
    [InlineData("state 3, current month with one agent", 1, null)]
    [InlineData("state 3, current month with zero agents", 0, "No agent usage recorded this month")]
    [InlineData("state 3, no current month, rolled", 0, "Waiting for this month's ccusage data")]
    [InlineData("state 3, no current month, retrieved this month", 0, "No agent usage recorded this month")]
    public void EmptyMessage_I17_Table_IsNullExactlyWhenStateThreeProducesAMeter(
        string scenario,
        int expectedMeters,
        string? expectedMessage)
    {
        var report = scenario switch
        {
            "state 3, the O1 November fixture" => SpecReport(),
            "state 3, current month with one agent" => ReportWithAgents(2026, 11, Usage("claude", ("claude-model", 1m))),
            "state 3, current month with zero agents" => ReportWithAgents(2026, 11),
            "state 3, no current month, rolled" => MonthlyReport(2026, 10, [Usage("zcode", ("z-code-model", 9.99m))],
                new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero)),
            "state 3, no current month, retrieved this month" => MonthlyReport(2026, 10, [Usage("zcode", ("z-code-model", 9.99m))],
                new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Not an I17 table row."),
        };

        var snapshot = BuildRaw(report, null, SpecBudgets(), Today);
        var message = Message(report, null, Today, TimeZoneInfo.Utc);

        Assert.NotNull(snapshot); // every row here is state 3 (lastGood exists)
        Assert.Equal(expectedMeters, snapshot!.Meters.Count);
        Assert.Equal(expectedMessage, message);

        // The invariant, on the code's own outputs rather than the table's data.
        Assert.Equal(snapshot.Meters.Count > 0, message is null);
    }
}
