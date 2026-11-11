using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// C9 — the tab → agent map { claude: claude, openai: codex, agy: antigravity } applied by
/// MainWindowViewModel when the ccusage report arrives (P4 call sites, decision C5), driven through a
/// fake CachedAgentUsageSource. C23: awaiting <c>vm.AgentUsageTask</c> means the Updated handler has
/// already run, so the tabs are updated when it completes. Written from the spec only (§5.6).
/// TokensText/CostText oracles follow C11 and the P3.1 rules (ChartFormat.Compact / ChartFormat.Money /
/// CostText of a single priced model): 1,000 → "1,000", 2,000 → "2,000", 3,500 → "3,500",
/// 9,999 → "9,999", 12,900 → "12.9K", 10,000 → "10K"; $1.23, $0.50, $2.00, $10.50, $11.00, $12.00.
/// </summary>
public sealed class MainWindowViewModelAgentUsageMappingTests
{
    private static AgentUsage Agent(string id, long total, decimal cost) =>
        new(id, new TokenCounts(total, 0, 0, 0, total),
            [new ModelUsage(id + "-model", new TokenCounts(total, 0, 0, 0, total), cost)]);

    private static AgentUsageReport Report(DateOnly today, IReadOnlyList<AgentUsage> dailyAgents, IReadOnlyList<AgentUsage> monthAgents) =>
        new(
            daily: dailyAgents.Count == 0 ? [] : [new DailyAgentUsage(today, dailyAgents)],
            monthly: monthAgents.Count == 0 ? [] : [new MonthlyAgentUsage(today.Year, today.Month, monthAgents)],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: new DateTimeOffset(2026, 11, 9, 7, 3, 22, TimeSpan.Zero),
            skippedRows: 0);

    private static TestQuotaClient ConfiguredClient(string providerId, IReadOnlyList<Stat>? stats = null) =>
        new((req, ct) => Task.FromResult(new ProviderSnapshot(providerId, configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 40.0)],
            stats: stats ?? [])));

    [Fact]
    public async Task RefreshAsync_MapsClaudeCodexAndAntigravityLinesToTheirTabs()
    {
        // C9: { claude → claude, openai → codex, agy → antigravity }; "opencode" maps to no tab.
        // The report carries today's daily row and this month's monthly row (C11 SummaryFor shape).
        var today = DateOnly.FromDateTime(DateTime.Now);
        var report = Report(today,
            [Agent("claude", 1_000, 1.23m), Agent("codex", 2_000, 0.50m), Agent("antigravity", 3_500, 2.00m), Agent("opencode", 700, 0.10m)],
            [Agent("claude", 9_999, 10.50m), Agent("codex", 12_900, 11.00m), Agent("antigravity", 10_000, 12.00m)]);

        var clock = new FakeClock(new DateTimeOffset(2026, 11, 9, 7, 0, 0, TimeSpan.Zero));
        var agentUsage = new CachedAgentUsageSource(new FakeAgentUsageSource(report), clock);

        var registrations = new[]
        {
            new ProviderClientRegistration("claude", ConfiguredClient("claude")),
            new ProviderClientRegistration("openai", ConfiguredClient("openai")),
            new ProviderClientRegistration("agy", ConfiguredClient("agy", [new Stat("Requests today", "128")])),
        };

        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(registrations));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: agentUsage);

        await vm.RefreshAsync();

        // C23: the task completes only after the Updated handler ran, so the tabs are updated now.
        Assert.NotNull(vm.AgentUsageTask);
        await vm.AgentUsageTask!;

        AssertCcusageRows(vm.ClaudeTab, "1,000 · $1.23", "9,999 · $10.50"); // C9: claude ← claude
        AssertCcusageRows(vm.OpenAiTab, "2,000 · $0.50", "12.9K · $11.00"); // C9: openai ← codex
        AssertCcusageRows(vm.AgyTab, "3,500 · $2.00", "10K · $12.00");      // C9: agy ← antigravity

        // C8: the AGY section is ccusage-only; the stat tiles keep the snapshot stats (rows never
        // enter the tiles).
        Assert.Equal(2, vm.AgyTab.SupplementaryRows.Count);
        Assert.False(vm.AgyTab.HasResetInfo); // C19: ccusage-only has no reset part

        // R-018 C9/D11 oracle: the tab now shows the COMPOSED snapshot — the agy stat followed by
        // the four R-018 tiles. Hand-computed from this test's own data (antigravity daily row
        // 3,500 total / 0 output / 0 cache reads; one priced model, $2.00): ChartFormat.Compact
        // gives "3,500" (below 10K keeps the grouped plain form), "0", "0"; CostText over all-priced
        // models is Money(2.00) = "$2.00" with no " + unpriced".
        var agyTiles = vm.AgyTab.StatTiles;
        Assert.Equal(
            ["Requests today", "Total tokens", "Output tokens", "Cache reads", "Equivalent API value"],
            agyTiles.Select(t => t.Label));
        Assert.Equal(["128", "3,500", "0", "0", "$2.00"], agyTiles.Select(t => t.Value));
        Assert.Equal("ccusage · equivalent API price", agyTiles[^1].Detail);

        // C9: "opencode" has no tab — its line appears nowhere (no 700-token / $0.10 value).
        foreach (var tab in vm.ProviderTabs)
        {
            Assert.DoesNotContain(tab.SupplementaryRows, r => r.Value.Contains("$0.10", StringComparison.Ordinal));
            Assert.DoesNotContain(tab.StatTiles, t => t.Label.Contains("(ccusage)", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task RefreshAsync_WhenNextReportHasNoLinesForMappedAgents_RestoresExistingSections()
    {
        // C9 + C10 + C8: every Updated re-maps the tabs; a report with no line for an agent results in
        // SetAgentUsage(null), which restores the existing visibility rule.
        var today = DateOnly.FromDateTime(DateTime.Now);
        var first = Report(today,
            [Agent("claude", 1_000, 1.23m), Agent("codex", 2_000, 0.50m), Agent("antigravity", 3_500, 2.00m)],
            []); // no monthly rows → SummaryFor gives ThisMonth null → the month row is omitted (P4)

        var clock = new FakeClock(new DateTimeOffset(2026, 11, 9, 7, 0, 0, TimeSpan.Zero));
        var fake = new FakeAgentUsageSource(first);

        var registrations = new[]
        {
            new ProviderClientRegistration("claude", ConfiguredClient("claude")),
            new ProviderClientRegistration("openai", ConfiguredClient("openai")),
            new ProviderClientRegistration("agy", ConfiguredClient("agy")),
        };

        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(registrations));
        var store = new BlockingSettingsStore(new AppSettings());
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: new CachedAgentUsageSource(fake, clock));

        await vm.RefreshAsync();
        Assert.NotNull(vm.AgentUsageTask);
        await vm.AgentUsageTask!;

        foreach (var tab in vm.ProviderTabs)
        {
            Assert.True(tab.ShowSupplementary); // C8/C20: a line exists on every mapped tab
        }

        // P4: no monthly rows → SummaryFor gives ThisMonth null → only "Today (ccusage)" is shown.
        AssertTodayOnlyRow(vm.ClaudeTab, "1,000 · $1.23");
        AssertTodayOnlyRow(vm.OpenAiTab, "2,000 · $0.50");
        AssertTodayOnlyRow(vm.AgyTab, "3,500 · $2.00");

        // Second report: only the unmapped "opencode" agent → SummaryFor is null for all three tabs.
        fake.Report = Report(today, [Agent("opencode", 700, 0.10m)], []);
        clock.UtcNow += TimeSpan.FromMinutes(16); // past MinInterval → the cached source re-reads (P1.2)

        await vm.RefreshAsync();
        Assert.NotNull(vm.AgentUsageTask);
        await vm.AgentUsageTask!;

        Assert.Equal(2, fake.ReadCount);

        // C10 via C5: the rows are gone and the existing rules stand — Claude/OpenAI stay shown
        // (configured snapshots), AGY goes back to hidden as today (C8).
        foreach (var tab in vm.ProviderTabs)
        {
            Assert.DoesNotContain(tab.SupplementaryRows, r => r.Label.Contains("(ccusage)", StringComparison.Ordinal));
        }

        Assert.True(vm.ClaudeTab.ShowSupplementary);
        Assert.True(vm.OpenAiTab.ShowSupplementary);
        Assert.False(vm.AgyTab.ShowSupplementary);
        Assert.Null(vm.AgyTab.Supplementary);
    }

    private static void AssertTodayOnlyRow(ProviderTabViewModel tab, string todayValue)
    {
        // P4: a missing line is omitted — only "Today (ccusage)" exists, group "ccusage", appended last.
        var ccusageRows = tab.SupplementaryRows.Where(r => r.Group == "ccusage").ToList();
        var row = Assert.Single(ccusageRows);
        Assert.Equal("Today (ccusage)", row.Label);
        Assert.Equal(todayValue, row.Value);
        Assert.Equal("Today (ccusage)", tab.SupplementaryRows[^1].Label);
    }

    private static void AssertCcusageRows(ProviderTabViewModel tab, string todayValue, string monthValue)
    {
        // P4: group "ccusage", appended after the existing rows, Today before This month,
        // value "<TokensText> · <CostText>".
        var rows = tab.SupplementaryRows;
        Assert.True(tab.ShowSupplementary);

        var today = rows.Single(r => r.Label == "Today (ccusage)");
        var month = rows.Single(r => r.Label == "This month (ccusage)");
        Assert.Equal(todayValue, today.Value);
        Assert.Equal(monthValue, month.Value);
        Assert.Equal("ccusage", today.Group);
        Assert.Equal("ccusage", month.Group);
        Assert.Same(today, rows[^2]);
        Assert.Same(month, rows[^1]);
    }

    private sealed class FakeAgentUsageSource : IAgentUsageSource
    {
        public FakeAgentUsageSource(AgentUsageReport report) => Report = report;

        public AgentUsageReport Report { get; set; }

        public int ReadCount { get; private set; }

        public Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct)
        {
            ReadCount++;
            return Task.FromResult(Report);
        }
    }

    private sealed class TestQuotaClient : IProviderQuotaClient
    {
        public TestQuotaClient(Func<ProviderSnapshotRequest, CancellationToken, Task<ProviderSnapshot>> handler)
        {
            Handler = handler;
        }

        public Func<ProviderSnapshotRequest, CancellationToken, Task<ProviderSnapshot>> Handler { get; set; }

        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
            => Handler(request, cancellationToken);
    }
}
