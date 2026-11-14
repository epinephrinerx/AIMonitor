using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.Agents;

/// <summary>
/// Tests for <see cref="AllAgentsTabViewModel"/> written from SPEC R-014 §P3.2 and the
/// Coding Owner decisions C6, C11, C16 and C18. The C11 oracle values are quoted verbatim.
/// </summary>
public sealed class AllAgentsTabViewModelTests
{
    // C11 oracle fixture: RetrievedAt 2026-11-09T07:03:22Z, zone UTC+7 → local 14:03:22.
    private static readonly DateOnly Today = new(2026, 11, 9);
    private static readonly DateTimeOffset RetrievedAt = new(2026, 11, 9, 7, 3, 22, TimeSpan.Zero);
    private static readonly TimeZoneInfo ZonePlus7 = TimeZoneInfo.CreateCustomTimeZone(
        "test-utc-plus-7", TimeSpan.FromHours(7), "UTC+07 (test)", "UTC+07 (test)");
    private static readonly TimeZoneInfo ZoneMinus8 = TimeZoneInfo.CreateCustomTimeZone(
        "test-utc-minus-8", TimeSpan.FromHours(-8), "UTC-08 (test)", "UTC-08 (test)");

    private static AgentUsage Agent(string id, long total, decimal? cost) =>
        new(id, new TokenCounts(total, 0, 0, 0, total),
            cost is null ? [] : [new ModelUsage("model-0", new TokenCounts(total, 0, 0, 0, total), cost)]);

    private static AgentUsageReport Report(
        IReadOnlyList<DailyAgentUsage>? daily = null,
        IReadOnlyList<MonthlyAgentUsage>? monthly = null,
        IReadOnlyList<SessionAgentUsage>? sessions = null,
        AgentUsagePricing pricing = AgentUsagePricing.OnlineRequested,
        int skippedRows = 0) =>
        new(daily ?? [], monthly ?? [], sessions ?? [], [], pricing, RetrievedAt, skippedRows);

    /// <summary>
    /// Daily rows spanning 2026-10-20 … 2026-11-09 with a gap at 2026-11-04,
    /// monthly rows September–November 2026 (decision C11 "Days" fixture).
    /// </summary>
    private static AgentUsageReport RangedReport(
        AgentUsagePricing pricing = AgentUsagePricing.OnlineRequested,
        int skippedRows = 0,
        IReadOnlyList<SessionAgentUsage>? sessions = null)
    {
        DateOnly[] dates =
        [
            new(2026, 10, 20), new(2026, 11, 2), new(2026, 11, 3),
            new(2026, 11, 5), new(2026, 11, 6), new(2026, 11, 7),
            new(2026, 11, 8), new(2026, 11, 9),
        ];
        var daily = dates.Select(d => new DailyAgentUsage(d, [Agent("claude", 100, 0.10m)])).ToList();
        var monthly = new List<MonthlyAgentUsage>
        {
            new(2026, 11, [Agent("claude", 700, 0.70m)]),
            new(2026, 9, [Agent("claude", 500, 0.50m)]),
            new(2026, 10, [Agent("claude", 600, 0.60m)]),
        };
        return Report(daily, monthly, sessions, pricing, skippedRows);
    }

    private static SessionAgentUsage Session(int index, DateTimeOffset lastActivity)
    {
        var id = $"{index:D2}-session-{index}";
        return new SessionAgentUsage(id, lastActivity, Agent("claude", 100, 0.10m));
    }

    // ── Tab identity (spec P3.2) ─────────────────────────────────────────────

    [Fact]
    public void TabTitleAndTabId_MatchSpec()
    {
        // Spec P3.2: TabTitle "All agents", TabId "all-agents".
        var vm = new AllAgentsTabViewModel();

        Assert.Equal("All agents", vm.TabTitle);
        Assert.Equal("all-agents", vm.TabId);
    }

    // ── No data yet (spec P3.2) ──────────────────────────────────────────────

    [Fact]
    public void Update_NoReportNoError_ShowsLoadingAndHidesEverything()
    {
        // Spec P3.2: no report and no error yet → StatusText "Loading ccusage…".
        // Decision C16: nothing is visible without data.
        var vm = new AllAgentsTabViewModel();

        vm.Update(null, null, Today, 7, ZonePlus7);

        Assert.Equal("Loading ccusage…", vm.StatusText);
        Assert.Null(vm.Today);
        Assert.Null(vm.ThisMonth);
        Assert.Empty(vm.Days);
        Assert.Empty(vm.Months);
        Assert.Empty(vm.Sessions);
        Assert.False(vm.HasDays);
        Assert.False(vm.HasMonths);
        Assert.False(vm.HasSessions);
        Assert.False(vm.HasError);
        Assert.True(string.IsNullOrEmpty(vm.ErrorText));
        Assert.False(vm.ShowInstallHint);
    }

    // ── Days — decision C11 oracle ───────────────────────────────────────────

    [Fact]
    public void Update_DaysWithRangeSeven_FromC11_KeepsLastSevenLocalDaysNewestFirstOmittingGaps()
    {
        // Decision C11 oracle: Days with today 2026-11-09 and range 7 → rows dated
        // 2026-11-03…2026-11-09 that exist, newest first (missing days omitted).
        // 2026-11-04 is missing from the report and 2026-11-02 / 2026-10-20 are outside the window.
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(), null, Today, 7, ZonePlus7);

        Assert.Equal(
            ["2026-11-09", "2026-11-08", "2026-11-07", "2026-11-06", "2026-11-05", "2026-11-03"],
            vm.Days.Select(d => d.Title));
    }

    [Fact]
    public void Update_DaysWithRangeSeven_IncludesWindowBoundaryDayExactly()
    {
        // Spec P3.2: the last rangeDays local days *including today* → 2026-11-03 (day 7) is included.
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(), null, Today, 7, ZonePlus7);

        Assert.Contains(vm.Days, d => d.Title == "2026-11-03");
        Assert.DoesNotContain(vm.Days, d => d.Title == "2026-11-02");
    }

    // ── Months (spec P3.2) ───────────────────────────────────────────────────

    [Fact]
    public void Update_MonthsListsEveryMonthlyRowNewestFirst()
    {
        // Spec P3.2: Months = all monthly rows newest first; titles "MMMM yyyy" invariant (P3.1).
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(), null, Today, 7, ZonePlus7);

        Assert.Equal(["November 2026", "October 2026", "September 2026"], vm.Months.Select(m => m.Title));
    }

    // ── Sessions — decision C11 oracle + C18 zone ────────────────────────────

    [Fact]
    public void Update_SessionsWithTwentyFive_TakesFirstTwentyInReportOrder()
    {
        // Decision C11 oracle: Sessions: 25 → first 20 (report order).
        // Spec P3.2: 20 most recent by LastActivity; the report normalizes newest-first (R10).
        var baseUtc = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        var sessions = Enumerable.Range(0, 25)
            .Select(i => Session(i, baseUtc.AddHours(i)))
            .ToList();
        var vm = new AllAgentsTabViewModel();

        vm.Update(Report(sessions: sessions), null, Today, 7, ZonePlus7);

        Assert.Equal(20, vm.Sessions.Count);
        // Report order is LastActivity descending → ids 24 down to 5; ShortId = first 8 chars (P3.2).
        var expectedShortIds = Enumerable.Range(5, 20).Reverse()
            .Select(i => $"{i:D2}-session-{i}"[..8])
            .ToList();
        Assert.Equal(expectedShortIds, vm.Sessions.Select(s => s.ShortId));
        Assert.Equal("Claude Code", vm.Sessions[0].DisplayName); // P3.1 mapping through P3.2
        Assert.Equal("100", vm.Sessions[0].TokensText);
        Assert.Equal("$0.10", vm.Sessions[0].CostText);
        // Newest session: 2026-11-09T00:00Z + 24 h = 2026-11-10T00:00Z; UTC+7 → 07:00 same local day.
        Assert.Equal("2026-11-10 07:00", vm.Sessions[0].LastActivityText);
    }

    [Fact]
    public void Update_SessionLastActivityText_ConvertsToGivenZoneAcrossDayBoundary()
    {
        // Decision C18: the session/time texts take the zone parameter (no DateTime.UtcNow /
        // ToLocalTime inside these types). Spec P3.2: LastActivityText local "yyyy-MM-dd HH:mm".
        var session = Session(0, new DateTimeOffset(2026, 11, 9, 2, 30, 0, TimeSpan.Zero));
        var vm = new AllAgentsTabViewModel();

        vm.Update(Report(sessions: [session]), null, Today, 7, ZoneMinus8);

        // 2026-11-09T02:30Z at UTC-8 → 2026-11-08 18:30 (previous local day).
        Assert.Equal("2026-11-08 18:30", vm.Sessions[0].LastActivityText);
    }

    // ── Summary cards (spec P3.2) ────────────────────────────────────────────

    [Fact]
    public void Update_TodayAndThisMonth_ComeFromTheMatchingRows()
    {
        // Spec P3.2: Today = today's daily row; ThisMonth = the current month row.
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(), null, Today, 7, ZonePlus7);

        Assert.Equal("2026-11-09", vm.Today?.Title);
        Assert.Equal("November 2026", vm.ThisMonth?.Title);
        Assert.Equal("100", vm.Today?.TokensText);
        Assert.Equal("$0.10", vm.Today?.CostText);
    }

    [Fact]
    public void Update_TodayAndMonthRowsAbsent_CardsAreNull()
    {
        // Spec P3.2: null if absent.
        var vm = new AllAgentsTabViewModel();
        var report = Report(
            [new DailyAgentUsage(new DateOnly(2026, 10, 20), [Agent("claude", 100, 0.10m)])],
            [new MonthlyAgentUsage(2026, 8, [Agent("claude", 100, 0.10m)])]);

        vm.Update(report, null, Today, 7, ZonePlus7);

        Assert.Null(vm.Today);
        Assert.Null(vm.ThisMonth);
    }

    // ── StatusText (spec P3.2 + C6) ──────────────────────────────────────────

    [Fact]
    public void Update_StatusText_ShowsUpdatedTimeInZonePlusPricingText()
    {
        // Spec P3.2: StatusText = "Updated HH:mm · " + PricingText from RetrievedAt (local).
        // Decision C11/C18 oracle: RetrievedAt 2026-11-09T07:03:22Z, zone UTC+7 → 14:03.
        // Decision C6: PricingText for OnlineRequested = "Prices: online".
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(), null, Today, 7, ZonePlus7);

        Assert.Equal("Updated 14:03 · Prices: online", vm.StatusText);
    }

    [Fact]
    public void Update_StatusText_AppendsSkippedRowsSuffix_WhenRowsWereSkipped()
    {
        // Spec P3.2 (Update cases list): skipped rows → " · N rows skipped".
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(skippedRows: 5), null, Today, 7, ZonePlus7);

        Assert.Equal("Updated 14:03 · Prices: online · 5 rows skipped", vm.StatusText);
    }

    [Fact]
    public void Update_StatusText_UsesOfflinePricingText_ForOfflineReport()
    {
        // Decision C6: "Prices: offline (built-in)".
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(pricing: AgentUsagePricing.Offline), null, Today, 7, ZonePlus7);

        Assert.Equal("Updated 14:03 · Prices: offline (built-in)", vm.StatusText);
    }

    // ── Error states (spec P3.2 + C11 oracle) ────────────────────────────────

    [Fact]
    public void Update_ErrorWithLastGood_AppendsShowingDataFromTime()
    {
        // Decision C11 oracle: Error with last good: RetrievedAt 2026-11-09T07:03:22Z, zone
        // UTC+7 → suffix "Showing data from 14:03." (spec P3.2: add it when lastGood is present).
        var error = new AgentUsageUnavailableException(
            AgentUsageErrorKind.Timeout, "ccusage did not finish in time.");
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(), error, Today, 7, ZonePlus7);

        Assert.True(vm.HasError);
        Assert.Contains("ccusage did not finish in time.", vm.ErrorText);
        Assert.EndsWith("Showing data from 14:03.", vm.ErrorText);
        // The last-good content stays visible (spec P3.2 "Showing data from …").
        Assert.Equal("Updated 14:03 · Prices: online", vm.StatusText);
        Assert.True(vm.HasDays);
        Assert.False(vm.ShowInstallHint);
    }

    [Fact]
    public void Update_NotInstalledWithoutLastGood_ShowsInstallHintOnly()
    {
        // Spec P3.2: ShowInstallHint = lastError.Kind == NotInstalled && lastGood == null;
        // nothing else is shown then (decision C16).
        var error = new AgentUsageUnavailableException(
            AgentUsageErrorKind.NotInstalled,
            "ccusage was not found. Install it with `npm i -g ccusage`, then restart AIMonitor.");
        var vm = new AllAgentsTabViewModel();

        vm.Update(null, error, Today, 7, ZonePlus7);

        Assert.True(vm.ShowInstallHint);
        Assert.True(vm.HasError);
        // P3.1: NotInstalled → the exception message verbatim (it already says how to install).
        Assert.Equal(
            "ccusage was not found. Install it with `npm i -g ccusage`, then restart AIMonitor.",
            vm.ErrorText);
        Assert.Null(vm.Today);
        Assert.Null(vm.ThisMonth);
        Assert.False(vm.HasDays);
        Assert.False(vm.HasMonths);
        Assert.False(vm.HasSessions);
    }

    [Fact]
    public void Update_NotInstalledWithLastGood_DoesNotShowInstallHint()
    {
        // Spec P3.2: install hint only when there is no lastGood to show instead.
        var error = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "not installed");
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(), error, Today, 7, ZonePlus7);

        Assert.False(vm.ShowInstallHint);
        Assert.True(vm.HasError);
    }

    [Fact]
    public void Update_OtherErrorKindWithoutLastGood_DoesNotShowInstallHint()
    {
        // Spec P3.2: ShowInstallHint is specific to NotInstalled.
        var error = new AgentUsageUnavailableException(AgentUsageErrorKind.Failed, "ccusage could not read usage.");
        var vm = new AllAgentsTabViewModel();

        vm.Update(null, error, Today, 7, ZonePlus7);

        Assert.False(vm.ShowInstallHint);
        Assert.True(vm.HasError);
        Assert.Equal("ccusage could not read usage.", vm.ErrorText);
    }

    // ── Visibility booleans — decision C16 ───────────────────────────────────

    [Fact]
    public void Update_WithData_ExposesHasDaysHasMonthsHasSessions()
    {
        // Decision C16: AllAgentsTabViewModel exposes HasDays, HasMonths, HasSessions
        // (section headers bind to them).
        var session = Session(0, RetrievedAt);
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(sessions: [session]), null, Today, 7, ZonePlus7);

        Assert.True(vm.HasDays);
        Assert.True(vm.HasMonths);
        Assert.True(vm.HasSessions);
    }

    [Fact]
    public void Update_ReportWithEmptySections_BooleansAreFalse_EvenThoughStatusIsUpdated()
    {
        // Decision C16: the booleans reflect section content, not report presence.
        var vm = new AllAgentsTabViewModel();

        vm.Update(Report(), null, Today, 7, ZonePlus7);

        Assert.False(vm.HasDays);
        Assert.False(vm.HasMonths);
        Assert.False(vm.HasSessions);
        Assert.StartsWith("Updated 14:03 · ", vm.StatusText);
    }

    // ── Pricing properties — decision C6 ─────────────────────────────────────

    [Fact]
    public void PricingProperties_OnlineRequested_ExposeTextAndToolTip()
    {
        // Decision C6: PricingText "Prices: online"; PricingToolTip (OnlineRequested only) =
        // "ccusage requested online prices; it uses its built-in prices if the fetch fails." (OP3).
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(), null, Today, 7, ZonePlus7);

        Assert.Equal("Prices: online", vm.PricingText);
        Assert.Equal(
            "ccusage requested online prices; it uses its built-in prices if the fetch fails.",
            vm.PricingToolTip);
    }

    [Fact]
    public void PricingProperties_Offline_ExposeTextAndNoToolTip()
    {
        // Decision C6: the tooltip exists for OnlineRequested only.
        var vm = new AllAgentsTabViewModel();

        vm.Update(RangedReport(pricing: AgentUsagePricing.Offline), null, Today, 7, ZonePlus7);

        Assert.Equal("Prices: offline (built-in)", vm.PricingText);
        Assert.True(string.IsNullOrEmpty(vm.PricingToolTip));
    }
}
