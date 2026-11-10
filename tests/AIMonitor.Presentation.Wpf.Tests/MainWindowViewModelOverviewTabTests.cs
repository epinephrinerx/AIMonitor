using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-016 H1.2 — the Overview tab in <see cref="MainWindowViewModel"/>, written from
/// Docs/SPEC_R016.md only (§5.6): O9 (Tabs order Claude, OpenAI, AGY, OpenCode, Overview, All
/// agents; ProviderTabs — tray/widget — unchanged), E4's restore mechanism extended by H1.2
/// (selecting Overview saves ActiveProvider = "overview"; startup restores the selected tab),
/// E28 (ActiveProviderTabId keeps returning a ProviderTabs provider — unchanged), the rebuild rule
/// (the Overview snapshot is rebuilt "at every point where the OpenCode tab is rebuilt from the
/// cached pair": ccusage update and ApplyAgentBudgets — O10 — with the E29 equal-budgets short-cut;
/// no view-rebuild hook because Overview has no chart) and O10: "ApplyAgentBudgets with a new
/// budget for codex moves the codex gauge to a percent without a ccusage read".
/// The tab-level EmptyMessage texts (O8 "No agent usage recorded this month", I4 "Waiting for this
/// month's ccusage data") are checked here through the real wiring. I8: no new clock seam exists in
/// the view model — each run captures one local day (the midnight-crossing residual risk is the one
/// I8 accepts, same as R-017 F21); every monthly fixture row is built from the captured month, and
/// the stale fixture is retrieved on day 15 of the previous month so no ±14 h local conversion can
/// make its month equal the captured one. Hand-written fakes follow the R-014/R-015 test files; no
/// real ccusage, no network. These tests are expected not to compile until the R-016 H1 branch
/// merges (written from the spec only, §5.6).
/// </summary>
public sealed class MainWindowViewModelOverviewTabTests
{
    // One captured local day per run (I8). Today's-month fixtures and the live retrieval instant are
    // derived from it; DateOnly.FromDateTime(DateTime.Now) matches the view model's own
    // UtcNow→Local conversion except across the machine's own midnight, the accepted residual risk.
    private static readonly DateTimeOffset LiveNow = DateTimeOffset.UtcNow;
    private static readonly DateOnly CapturedToday = DateOnly.FromDateTime(LiveNow.LocalDateTime);

    private static readonly DateTimeOffset FirstOfThisMonthUtc =
        new(CapturedToday.Year, CapturedToday.Month, 1, 0, 0, 0, TimeSpan.Zero);

    // Day 15, 00:00 UTC of the PREVIOUS month: any local conversion (UTC-11 … UTC+14) lands on day
    // 14–15 of that month, so it never equals the captured month — the I4 rollover text is
    // deterministic in every zone.
    private static readonly DateTimeOffset PreviousMonthFetchedAt = FirstOfThisMonthUtc.AddMonths(-1).AddDays(14);

    private static AgentUsage Agent(string name, decimal cost, bool unpricedModel = false)
    {
        var models = new List<ModelUsage>
        {
            new(name + "-model", new TokenCounts(1_000, 0, 0, 0, 1_000), cost),
        };

        if (unpricedModel)
        {
            // priced $cost plus an unpriced model — the codex shape of the spec fixture (E26 spent text).
            models.Add(new ModelUsage(name + "-mystery", new TokenCounts(100, 0, 0, 0, 100), null));
        }

        return new AgentUsage(name, new TokenCounts(1_000, 0, 0, 0, unpricedModel ? 1_100 : 1_000), models);
    }

    /// <summary>The two current-month gauge rows of the oracle fixture (claude $12.50; codex $3.20
    /// + unpriced) — enough to show O1's order and the two subtitle shapes without a 4-agent table.</summary>
    private static AgentUsageReport CurrentMonthReport(DateOnly month) =>
        new(
            daily: [],
            monthly: [new MonthlyAgentUsage(month.Year, month.Month, [Agent("claude", 12.50m), Agent("codex", 3.20m, unpricedModel: true)])],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: LiveNow,
            skippedRows: 0);

    /// <summary>O8: the current month has an entry but no agents.</summary>
    private static AgentUsageReport CurrentMonthNoAgentsReport(DateOnly month) =>
        new(
            daily: [],
            monthly: [new MonthlyAgentUsage(month.Year, month.Month, [])],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: LiveNow,
            skippedRows: 0);

    /// <summary>I4: the report is from last month and holds no current-month entry (O11's zcode shape).</summary>
    private static AgentUsageReport StaleReport()
    {
        var lastDayOfPreviousMonth = FirstOfThisMonthUtc.AddDays(-1);
        return new AgentUsageReport(
            daily: [],
            monthly: [new MonthlyAgentUsage(lastDayOfPreviousMonth.Year, lastDayOfPreviousMonth.Month, [Agent("zcode", 9.99m)])],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: PreviousMonthFetchedAt,
            skippedRows: 0);
    }

    private static MainWindowViewModel CreateVm(
        CachedAgentUsageSource? agentUsage,
        AppSettings initial,
        out BlockingSettingsStore store,
        out SettingsSession session)
    {
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        store = new BlockingSettingsStore(initial);
        store.Release(); // tab selection may save ActiveProvider
        session = new SettingsSession(store, initial);
        return new MainWindowViewModel(coordinator, session, agentUsage: agentUsage);
    }

    private static MainWindowViewModel CreateVm(CachedAgentUsageSource? agentUsage, AppSettings initial, out BlockingSettingsStore store)
    {
        return CreateVm(agentUsage, initial, out store, out _);
    }

    // ── O9: tab shape ───────────────────────────────────────────────────────────

    [Fact]
    public void Tabs_O9_OverviewSitsBetweenOpenCodeAndAllAgents_ProviderTabsUnchanged()
    {
        // H1.2: OverviewTab = new ProviderTabViewModel(OverviewSnapshotBuilder.ProviderId, "Overview")
        // and O9: Tabs = Claude, OpenAI, AGY, OpenCode, Overview, All agents. ProviderTabs (tray)
        // unchanged — Overview is not in the tray, the widget or the provider refresh.
        var vm = CreateVm(agentUsage: null, new AppSettings(), out _);

        Assert.Equal(6, vm.Tabs.Count);
        Assert.Same(vm.ClaudeTab, vm.Tabs[0]);
        Assert.Same(vm.OpenAiTab, vm.Tabs[1]);
        Assert.Same(vm.AgyTab, vm.Tabs[2]);
        Assert.Same(vm.OpenCodeTab, vm.Tabs[3]);
        Assert.Same(vm.OverviewTab, vm.Tabs[4]); // O9: between OpenCode and All agents
        Assert.Same(vm.AllAgentsTab, vm.Tabs[5]);

        Assert.Equal(3, vm.ProviderTabs.Count);
        Assert.Same(vm.ClaudeTab, vm.ProviderTabs[0]);
        Assert.Same(vm.OpenAiTab, vm.ProviderTabs[1]);
        Assert.Same(vm.AgyTab, vm.ProviderTabs[2]);

        Assert.Equal("overview", OverviewSnapshotBuilder.ProviderId); // H1.1 const
        Assert.Equal("overview", vm.OverviewTab.ProviderId); // H1.2 ctor arguments
        Assert.Equal("Overview", vm.OverviewTab.DisplayName);
    }

    // ── Selecting saves; startup restores (H1.2 / AppSettings "overview") ───────

    [Fact]
    public async Task SelectedTabItem_Overview_SavesActiveProviderOverview()
    {
        // H1.2: AppSettings.ActiveProvider accepts "overview" so a restart restores the selected
        // tab — the save half (the E4 mechanism extended to the new tab).
        var vm = CreateVm(agentUsage: null, new AppSettings(), out var store, out var session);

        vm.SelectedTabItem = vm.OverviewTab;
        await session.FlushAsync();

        Assert.False(vm.IsAllAgentsSelected);
        Assert.Same(vm.OverviewTab, vm.SelectedTab);
        Assert.Equal("overview", session.Current.ActiveProvider);
        Assert.Equal("overview", store.SavedSettings[^1].ActiveProvider);
    }

    [Fact]
    public void Startup_ActiveProviderOverview_RestoresTheTab_AndE28WidgetStaysClaude()
    {
        // H1.2 restore half + O9/E28: startup "overview" selects the Overview tab (Normalize keeps
        // "overview"); the widget keeps returning a ProviderTabs provider — unchanged E28 behaviour.
        var vm = CreateVm(agentUsage: null, new AppSettings { ActiveProvider = "overview" }, out _, out var session);

        Assert.Equal("overview", session.Current.ActiveProvider);
        Assert.Same(vm.OverviewTab, vm.SelectedTab);
        Assert.Same(vm.OverviewTab, vm.SelectedTabItem);
        Assert.False(vm.IsAllAgentsSelected);
        Assert.Equal("claude", vm.ActiveProviderTabId); // E28: Overview is not in ProviderTabs
    }

    [Fact]
    public void ActiveProviderTabId_E28_SelectingOverview_HoldsTheLastProviderTab()
    {
        // E28 unchanged with the new tab in Tabs: selecting Overview must not move the widget's
        // provider id, exactly like OpenCode and All agents (the R-015 E28 test's shape).
        var vm = CreateVm(agentUsage: null, new AppSettings(), out _, out _);

        vm.SelectedTabItem = vm.OpenAiTab;
        vm.SelectedTabItem = vm.OverviewTab;

        Assert.Equal("openai", vm.ActiveProviderTabId); // held across Overview…

        vm.SelectedTabItem = vm.AgyTab;
        Assert.Equal("agy", vm.ActiveProviderTabId); // …and a provider tab moves it again
    }

    // ── The rebuild wiring: ccusage update + ApplyAgentBudgets, no extra read ───

    [Fact]
    public async Task Updated_H12_OverviewGaugesComeFromTheCachedPair()
    {
        // H1.2: the Overview tab is rebuilt wherever the OpenCode tab is rebuilt from the cached
        // (lastGood, lastError) pair. Oracle-fixture shapes: claude $12.50 against budget 50 → 25 %
        // (12.50 ÷ 50 × 100, hand-derived), codex $3.20 + unpriced → "--" + E26 no-budget text.
        var report = CurrentMonthReport(CapturedToday);
        var source = new FakeAgentUsageSource(_ => Task.FromResult(report));
        var cache = new CachedAgentUsageSource(source, new FakeClock(LiveNow));
        var initial = new AppSettings { AgentBudgets = new Dictionary<string, decimal> { ["claude"] = 50m } };
        var vm = CreateVm(cache, initial, out _);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;

        var tab = vm.OverviewTab;
        Assert.True(tab.HasSnapshot);
        Assert.Equal(["claude", "codex"], tab.Meters.Select(m => m.Title)); // O1 order among the fixture pair
        Assert.Equal("25%", tab.Meters[0].ValueText);
        Assert.Equal("$12.50 of $50.00 budget", tab.Meters[0].Subtitle);
        Assert.Equal("--", tab.Meters[1].ValueText);
        Assert.Equal("$3.20 + unpriced · No budget set — add one in Settings", tab.Meters[1].Subtitle);
        Assert.Equal(2, tab.MeterColumns); // I5: min(2, 3)
        Assert.Equal("Overview 25%", tab.TabTitle);
        Assert.Null(tab.EmptyMessage); // meters present — the empty texts are for the no-meter states
        Assert.Equal(LiveNow, tab.LastUpdated); // I4 (F11): LastUpdated shows lastGood.RetrievedAt
        Assert.False(tab.HasHistory); // H1.2: no chart
        Assert.False(tab.ShowSupplementary); // H1.2: no ccusage supplementary section
        Assert.Equal(1, source.ReadCount); // one cached read drives every tab
    }

    [Fact]
    public void ApplyAgentBudgets_E2_BeforeTheFirstUpdate_OverviewOnlyStores()
    {
        // E2's semantics extend to Overview (H1.2 "rebuilt at every point where the OpenCode tab is
        // rebuilt from the cached pair"): before the first Updated nothing is pushed — the tab keeps
        // its state-1 look, which I3 pins as "Checking ccusage...".
        var vm = CreateVm(agentUsage: null, new AppSettings(), out _);

        vm.ApplyAgentBudgets(new Dictionary<string, decimal> { ["claude"] = 50m });

        Assert.False(vm.OverviewTab.HasSnapshot);
        Assert.Equal("Checking...", vm.OverviewTab.Status);
        Assert.Equal("Checking ccusage...", vm.OverviewTab.EmptyMessage); // I3 state 1
        Assert.Empty(vm.OverviewTab.Meters);
    }

    [Fact]
    public async Task ApplyAgentBudgets_O10_CodexBudgetMovesTheGauge_WithoutACcusageRead()
    {
        // O10: "ApplyAgentBudgets with a new budget for codex moves the codex gauge to a percent
        // without a ccusage read". Hand check: 3.20 ÷ 8 × 100 = 40 % (and claude moves with the same
        // dict: 12.50 ÷ 50 × 100 = 25 %); E26 subtitle texts, same as the builder oracles O2/O3.
        var report = CurrentMonthReport(CapturedToday);
        var source = new FakeAgentUsageSource(_ => Task.FromResult(report));
        var cache = new CachedAgentUsageSource(source, new FakeClock(LiveNow));
        var vm = CreateVm(cache, new AppSettings(), out _);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        Assert.Equal("--", vm.OverviewTab.Meters[1].ValueText); // no budgets yet
        Assert.Equal("$3.20 + unpriced · No budget set — add one in Settings", vm.OverviewTab.Meters[1].Subtitle);
        Assert.Equal(1, source.ReadCount);

        vm.ApplyAgentBudgets(new Dictionary<string, decimal> { ["claude"] = 50m, ["codex"] = 8m });

        Assert.Equal("40%", vm.OverviewTab.Meters[1].ValueText);
        Assert.Equal("$3.20 of $8.00 budget", vm.OverviewTab.Meters[1].Subtitle);
        Assert.Equal("25%", vm.OverviewTab.Meters[0].ValueText);
        Assert.Equal("$12.50 of $50.00 budget", vm.OverviewTab.Meters[0].Subtitle);
        Assert.Equal(1, source.ReadCount); // O10: the rebuild used the cached pair — no new ccusage read
    }

    [Fact]
    public async Task ApplyAgentBudgets_E29_EqualBudgets_ShortcutExtendsToOverview()
    {
        // E29's short-cut semantics hold at the shared rebuild point: equal budgets (case-insensitive
        // keys, same values) rebuild neither tab; a changed value rebuilds Overview. Meter-instance
        // identity is the observable "no rebuild happened" (E16: assert final state, not call counts).
        var report = CurrentMonthReport(CapturedToday);
        var source = new FakeAgentUsageSource(_ => Task.FromResult(report));
        var cache = new CachedAgentUsageSource(source, new FakeClock(LiveNow));
        var initial = new AppSettings { AgentBudgets = new Dictionary<string, decimal> { ["claude"] = 50m } };
        var vm = CreateVm(cache, initial, out _);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        var before = vm.OverviewTab.Meters;
        var openCodeBefore = vm.OpenCodeTab.Meters;
        Assert.Equal("$12.50 of $50.00 budget", before[0].Subtitle); // the settings' budget is live

        // Equal content — new instance, different key case (the session stored OrdinalIgnoreCase
        // "claude"). No rebuild on either tab.
        vm.ApplyAgentBudgets(new Dictionary<string, decimal> { ["Claude"] = 50m });
        Assert.Same(before, vm.OverviewTab.Meters);
        Assert.Same(openCodeBefore, vm.OpenCodeTab.Meters);

        // A changed budget rebuilds: 12.50 ÷ 25 × 100 = 50 % (hand-derived).
        vm.ApplyAgentBudgets(new Dictionary<string, decimal> { ["claude"] = 25m });
        var after = vm.OverviewTab.Meters;
        Assert.NotSame(before, after);
        Assert.Equal("$12.50 of $25.00 budget", after[0].Subtitle);
        Assert.Equal("50%", after[0].ValueText);
        Assert.Equal(1, source.ReadCount); // still exactly one ccusage read across every rebuild
    }

    // ── State 2 and the two empty texts through the wiring ──────────────────────

    [Fact]
    public async Task Updated_State2_ErrorWithoutLastGood_PushesTheBannerWithoutCards()
    {
        // O7 state 2 through the wiring + I3: only the error banner — EmptyMessage null, no setup
        // card (E24 extended), no gauge cards, no supplementary rows. I7's concrete message.
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "ccusage was not found on PATH.");
        var cache = new CachedAgentUsageSource(
            new FakeAgentUsageSource(_ => Task.FromException<AgentUsageReport>(failure)), new FakeClock(LiveNow));
        var vm = CreateVm(cache, new AppSettings(), out _);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;

        Assert.Equal(
            AgentUsagePresenter.ErrorText(AgentUsageErrorKind.NotInstalled, "ccusage was not found on PATH."),
            vm.OverviewTab.ErrorMessage);
        Assert.True(vm.OverviewTab.HasError);
        Assert.Empty(vm.OverviewTab.Meters);
        Assert.Equal(0, vm.OverviewTab.MeterColumns);
        Assert.False(vm.OverviewTab.ShowSetupCard); // E24 exemption covers "overview"
        Assert.Null(vm.OverviewTab.EmptyMessage); // I3 state 2
        Assert.False(vm.OverviewTab.ShowSupplementary);
    }

    [Fact]
    public async Task Updated_O8_CurrentMonthWithNoAgents_ShowsTheRecordedText()
    {
        // O8 through the wiring: state 3, zero meters → the tab shows "No agent usage recorded this
        // month" and is visibly updated (it "must not look stuck": HasSnapshot true, LastUpdated the
        // fresh retrieval instant).
        var report = CurrentMonthNoAgentsReport(CapturedToday);
        var cache = new CachedAgentUsageSource(
            new FakeAgentUsageSource(_ => Task.FromResult(report)), new FakeClock(LiveNow));
        var vm = CreateVm(cache, new AppSettings(), out _);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;

        Assert.True(vm.OverviewTab.HasSnapshot);
        Assert.Empty(vm.OverviewTab.Meters);
        Assert.Equal("No agent usage recorded this month", vm.OverviewTab.EmptyMessage); // I3/O8
        Assert.Equal(LiveNow, vm.OverviewTab.LastUpdated);
    }

    [Fact]
    public async Task Updated_I4_StaleLastMonthReportWithoutCurrentMonth_ShowsTheWaitingText()
    {
        // I4 rollover through the wiring: lastGood.RetrievedAt's local month ≠ today's month and the
        // current month has no entry → "Waiting for this month's ccusage data" (not I3's text), and
        // LastUpdated still shows the old retrieval instant (staleness visible, F11 style).
        var cache = new CachedAgentUsageSource(
            new FakeAgentUsageSource(_ => Task.FromResult(StaleReport())), new FakeClock(LiveNow));
        var vm = CreateVm(cache, new AppSettings(), out _);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;

        Assert.Empty(vm.OverviewTab.Meters);
        Assert.Equal("Waiting for this month's ccusage data", vm.OverviewTab.EmptyMessage); // I4
        Assert.Equal(PreviousMonthFetchedAt, vm.OverviewTab.LastUpdated);
    }

    // ── Fakes (hand-written, same as the R-014/R-015 test files) ─────────────────

    private sealed class FakeAgentUsageSource(Func<int, Task<AgentUsageReport>> handler) : IAgentUsageSource
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct)
        {
            var number = Interlocked.Increment(ref _readCount);
            return handler(number);
        }
    }
}
