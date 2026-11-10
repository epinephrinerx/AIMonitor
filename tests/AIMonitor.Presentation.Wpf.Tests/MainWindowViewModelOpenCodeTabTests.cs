using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-015 Q2.1/Q2.2 — the OpenCode tab in <see cref="MainWindowViewModel"/>, written from
/// Docs/SPEC_R015.md only (§5.6): Q2.1 (OpenCodeTab = new ProviderTabViewModel("opencode", "OpenCode");
/// Tabs = [Claude, OpenAI, Agy, OpenCode, AllAgents]; ProviderTabs stays the three provider tabs — no
/// tray/widget/provider refresh), E4 (selecting saves ActiveProvider = "opencode"; startup restores it
/// by looking among the ProviderTabViewModels in Tabs; unknown → Claude), Q2.2 (on every Updated the
/// view model pushes the OpenCodeSnapshotBuilder gauge and the SummaryFor ccusage rows) and E2 (the
/// last (lastGood, lastError) and the last budgets are stored: ApplyAgentBudgets rebuilds from the
/// stored pair WITHOUT running ccusage; before the first Updated it only stores). Hand-written fakes
/// follow the R-014 test files; no real ccusage, no network. Q2 tests: expected not to compile until
/// the Q2 branch merges (E16).
/// </summary>
public sealed class MainWindowViewModelOpenCodeTabTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 11, 9, 7, 0, 0, TimeSpan.Zero);

    private static DateOnly LocalToday() => DateOnly.FromDateTime(DateTime.Now);

    private static AgentUsage Agent(string id, long total, decimal cost) =>
        new(id, new TokenCounts(total, 0, 0, 0, total),
            [new ModelUsage(id + "-model", new TokenCounts(total, 0, 0, 0, total), cost)]);

    private static AgentUsageReport Report(DateOnly today) =>
        new(
            daily: [new DailyAgentUsage(today, [Agent("opencode", 1_234, 2.50m), Agent("claude", 500, 1.00m)])],
            monthly: [new MonthlyAgentUsage(today.Year, today.Month,
                [Agent("opencode", 9_999, 25.00m), Agent("claude", 4_000, 8.00m)])],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: FixedUtc,
            skippedRows: 0);

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

    // ── Q2.1: tab shape ────────────────────────────────────────────────────────

    [Fact]
    public void Tabs_Q21_OpenCodeTabSitsBetweenAgyAndAllAgents_ProviderTabsUnchanged()
    {
        var vm = CreateVm(agentUsage: null, new AppSettings(), out _);

        Assert.Equal(5, vm.Tabs.Count);
        Assert.Same(vm.ClaudeTab, vm.Tabs[0]);
        Assert.Same(vm.OpenAiTab, vm.Tabs[1]);
        Assert.Same(vm.AgyTab, vm.Tabs[2]);
        Assert.Same(vm.OpenCodeTab, vm.Tabs[3]); // Q2.1: Tabs = [Claude, OpenAI, Agy, OpenCode, AllAgents]
        Assert.Same(vm.AllAgentsTab, vm.Tabs[4]);

        // Q2.1: ProviderTabs stays [ClaudeTab, OpenAiTab, AgyTab] — OpenCode is not in the tray,
        // the widget or the provider refresh.
        Assert.Equal(3, vm.ProviderTabs.Count);
        Assert.Same(vm.ClaudeTab, vm.ProviderTabs[0]);
        Assert.Same(vm.OpenAiTab, vm.ProviderTabs[1]);
        Assert.Same(vm.AgyTab, vm.ProviderTabs[2]);

        Assert.Equal("opencode", vm.OpenCodeTab.ProviderId); // Q2.1 ctor arguments
        Assert.Equal("OpenCode", vm.OpenCodeTab.DisplayName);
    }

    // ── Q2.1 / E4: selecting saves; startup restores ──────────────────────────

    [Fact]
    public async Task SelectedTabItem_OpenCode_Q21_SavesActiveProviderOpenCode()
    {
        var vm = CreateVm(agentUsage: null, new AppSettings(), out var store, out var session);

        vm.SelectedTabItem = vm.OpenCodeTab; // Q2.1: selecting the tab saves ActiveProvider
        await session.FlushAsync();

        Assert.False(vm.IsAllAgentsSelected);
        Assert.Same(vm.OpenCodeTab, vm.SelectedTab);
        Assert.Equal("opencode", session.Current.ActiveProvider);
        Assert.Equal("opencode", store.SavedSettings[^1].ActiveProvider);
    }

    [Fact]
    public void Startup_ActiveProviderOpenCode_E4_SelectsOpenCodeTabLikeAllAgents()
    {
        // E4: startup selection looks up ActiveProvider among the ProviderTabViewModels in Tabs, so
        // "opencode" selects OpenCodeTab (same mechanism/scope as "all-agents" in R-014 C13–C15).
        var initial = new AppSettings { ActiveProvider = "opencode" };
        var vm = CreateVm(agentUsage: null, initial, out _, out var session);

        Assert.Equal("opencode", session.Current.ActiveProvider); // E4 (Normalize keeps it — Q1 file)
        Assert.Same(vm.OpenCodeTab, vm.SelectedTab);
        Assert.Same(vm.OpenCodeTab, vm.SelectedTabItem);
        Assert.False(vm.IsAllAgentsSelected);
    }

    [Fact]
    public void Startup_UnknownActiveProvider_E4_StillSelectsClaude()
    {
        // E4: "Unknown value → Claude as now." (Normalize maps it to "claude"; selection follows.)
        var vm = CreateVm(agentUsage: null, new AppSettings { ActiveProvider = "no-such-tab" }, out _, out _);

        Assert.Same(vm.ClaudeTab, vm.SelectedTab);
    }

    // ── Q2.2 / E2 / E10 / state 1: the Updated wiring ─────────────────────────

    [Fact]
    public async Task Updated_Q22_StoredBudgetsDriveTheGauge_AndSummaryForFillsTheRows()
    {
        var report = Report(LocalToday());
        var source = new FakeAgentUsageSource(_ => Task.FromResult(report));
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));
        var vm = CreateVm(cache, new AppSettings(), out _);

        // E2: before the first Updated, ApplyAgentBudgets only stores — nothing is pushed.
        vm.ApplyAgentBudgets(new Dictionary<string, decimal> { ["opencode"] = 50m });
        Assert.False(vm.OpenCodeTab.HasSnapshot);
        Assert.Empty(vm.OpenCodeTab.Meters);
        Assert.Equal("Checking...", vm.OpenCodeTab.Status);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;

        // Q2.2: the gauge meter is built from the stored budgets and the stored lastGood.
        var meter = Assert.Single(vm.OpenCodeTab.Meters);
        Assert.Equal("Month to date", meter.Title);
        Assert.Equal("of your $50.00 budget", meter.Subtitle); // money(50) per E12
        Assert.Equal("50%", meter.ValueText);                  // (25 / 50) × 100 hand-derived
        Assert.Equal("$25.00", meter.Detail);                  // MonthCostUsd, not the daily sum

        // Q2.2: SetAgentUsage(SummaryFor(lastGood, "opencode", today)) — C9-style opencode lines.
        Assert.True(vm.OpenCodeTab.ShowSupplementary);
        var rows = vm.OpenCodeTab.SupplementaryRows;
        Assert.Equal(2, rows.Count); // ccusage-only for opencode (Q2.2 rows)
        Assert.Equal("Today (ccusage)", rows[0].Label);
        Assert.Equal("1,234 · $2.50", rows[0].Value);
        Assert.Equal("This month (ccusage)", rows[1].Label);
        Assert.Equal("9,999 · $25.00", rows[1].Value);
        Assert.Equal(1, source.ReadCount); // one read produced both the gauge and the rows
    }

    [Fact]
    public async Task Updated_Q22_WithoutAnyBudgets_GaugeSaysNoBudgetSet()
    {
        var report = Report(LocalToday());
        var cache = new CachedAgentUsageSource(new FakeAgentUsageSource(_ => Task.FromResult(report)), new FakeClock(FixedUtc));
        var vm = CreateVm(cache, new AppSettings(), out _);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;

        var meter = Assert.Single(vm.OpenCodeTab.Meters);
        Assert.Equal("No budget set — add one in Settings", meter.Subtitle); // O3
        Assert.Equal("--", meter.ValueText);                                 // O3 gauge text
        Assert.Equal("$25.00", meter.Detail);
    }

    [Fact]
    public async Task ApplyAgentBudgets_E2_RebuildsFromStoredReport_WithoutRunningCcusage()
    {
        var report = Report(LocalToday());
        var source = new FakeAgentUsageSource(_ => Task.FromResult(report));
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));
        var vm = CreateVm(cache, new AppSettings(), out _);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        Assert.Equal("No budget set — add one in Settings", Assert.Single(vm.OpenCodeTab.Meters).Subtitle);
        Assert.Equal(1, source.ReadCount);

        // E2: store the budgets and rebuild the snapshot from the stored (lastGood, lastError) —
        // no ccusage run, no provider refresh. Assert the final state (E16), not call counts.
        vm.ApplyAgentBudgets(new Dictionary<string, decimal> { ["opencode"] = 50m });

        var meter = Assert.Single(vm.OpenCodeTab.Meters);
        Assert.Equal("of your $50.00 budget", meter.Subtitle);
        Assert.Equal("50%", meter.ValueText);
        Assert.Equal(1, source.ReadCount); // still: ApplyAgentBudgets never runs ccusage

        // Clearing every budget returns the gauge to the O3 text (final-state check).
        vm.ApplyAgentBudgets(new Dictionary<string, decimal>());
        Assert.Equal("No budget set — add one in Settings", Assert.Single(vm.OpenCodeTab.Meters).Subtitle);
        Assert.Equal("--", Assert.Single(vm.OpenCodeTab.Meters).ValueText);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task Updated_E10_ErrorWithoutLastGood_PushesBannerWithoutGaugeCards()
    {
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "ccusage is not installed");
        var cache = new CachedAgentUsageSource(
            new FakeAgentUsageSource(_ => Task.FromException<AgentUsageReport>(failure)), new FakeClock(FixedUtc));
        var vm = CreateVm(cache, new AppSettings(), out _);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;

        // E10/Q2.2 state 2 at the tab level: error banner shown, no gauge cards, no ccusage rows
        // (SummaryFor of a null lastGood has none).
        Assert.Equal(
            AgentUsagePresenter.ErrorText(AgentUsageErrorKind.NotInstalled, "ccusage is not installed"),
            vm.OpenCodeTab.ErrorMessage);
        Assert.True(vm.OpenCodeTab.HasError);
        Assert.Empty(vm.OpenCodeTab.Meters);
        Assert.Empty(vm.OpenCodeTab.MeterSections);
        Assert.False(vm.OpenCodeTab.ShowSupplementary);
    }

    [Fact]
    public async Task Updated_Q22_WhenNothingIsKnownYet_NoSnapshotIsPushed()
    {
        // Q2.2 state 1 at the view-model level: a read that yields neither a report nor an error
        // (OperationCanceled-style swallow inside the refresher) reaches Updated(null, null) —
        // "no snapshot is pushed; the tab keeps its initial 'Checking...' state".
        var cache = new CachedAgentUsageSource(
            new FakeAgentUsageSource(_ => Task.FromResult<AgentUsageReport>(null!)), new FakeClock(FixedUtc));
        var vm = CreateVm(cache, new AppSettings(), out _);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;

        Assert.False(vm.OpenCodeTab.HasSnapshot);
        Assert.Equal("Checking...", vm.OpenCodeTab.Status);
        Assert.Empty(vm.OpenCodeTab.Meters);
        Assert.False(vm.OpenCodeTab.ShowSupplementary);
    }

    // ── Fakes (hand-written, same as the R-014 test files) ─────────────────────

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
