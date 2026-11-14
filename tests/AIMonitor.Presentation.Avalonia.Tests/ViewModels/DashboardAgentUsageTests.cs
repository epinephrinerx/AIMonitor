using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

/// <summary>
/// The 2.0.3/2.0.4 tabs on the Mac dashboard: OpenCode, the Overview and All agents, and the
/// ccusage rows in the service tabs. The builders behind them are covered in <c>Agents/</c>; these
/// tests are about the dashboard putting them in the right place.
/// </summary>
public sealed class DashboardAgentUsageTests
{
    private sealed class StubClient(string id) : IProviderQuotaClient
    {
        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(TestData.Snapshot(id));
    }

    private sealed class FakeAgentUsage(AgentUsageReport report) : IAgentUsageSource
    {
        public int Reads { get; private set; }

        public Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(report);
        }
    }

    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).DateTime);

    private static AgentUsage Agent(string id, long tokens, decimal cost) =>
        new(id, new TokenCounts(tokens, 0, 0, 0, tokens), [new ModelUsage($"{id}-model", new TokenCounts(tokens, 0, 0, 0, tokens), cost)]);

    private static AgentUsageReport Report()
    {
        var today = Today;
        AgentUsage[] agents = [Agent("claude", 1_000, 1.50m), Agent("codex", 2_000, 0.75m), Agent("opencode", 3_000, 4.00m)];
        return new AgentUsageReport(
            [new DailyAgentUsage(today, agents)],
            [new MonthlyAgentUsage(today.Year, today.Month, agents)],
            [],
            [],
            AgentUsagePricing.Offline,
            DateTimeOffset.UtcNow,
            0);
    }

    private static async Task<(DashboardViewModel Dashboard, SettingsSession Settings, FakeAgentUsage Source)> BuildAsync(
        AppSettings? settings = null, bool withAgentUsage = true)
    {
        var session = await SettingsSession.CreateAsync(new InMemorySettingsStore(settings));
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
            [.. new[] { "claude", "openai", "agy" }.Select(id => new ProviderClientRegistration(id, new StubClient(id)))]));
        var source = new FakeAgentUsage(Report());
        var cached = withAgentUsage
            ? new CachedAgentUsageSource(source, new FakeClock(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(15))
            : null;
        return (new DashboardViewModel(coordinator, session, cached), session, source);
    }

    [Fact]
    public async Task TheTabsAreTheThreeServicesThenOpenCodeTheOverviewAndAllAgents()
    {
        var (dashboard, _, _) = await BuildAsync();

        var ids = dashboard.Tabs.Select(tab => tab switch
        {
            ProviderViewModel provider => provider.ProviderId,
            AllAgentsTabViewModel all => all.TabId,
            _ => "?",
        });

        Assert.Equal(["claude", "openai", "agy", "opencode", "overview", "all-agents"], ids);
    }

    [Fact]
    public async Task OpeningOpenCode_LeavesTheMenuBarOnTheLastServiceTab()
    {
        var (dashboard, settings, _) = await BuildAsync(new AppSettings { ActiveProvider = "openai" });

        dashboard.SelectedTab = dashboard.OpenCodeTab;

        Assert.Equal("openai", dashboard.SelectedProvider.ProviderId);
        await WaitForAsync(() => settings.Current.ActiveProvider == "opencode");
    }

    [Fact]
    public async Task OpeningAllAgents_IsRememberedAndReopenedNextTime()
    {
        var (dashboard, settings, _) = await BuildAsync();

        dashboard.SelectedTab = dashboard.AllAgentsTab;
        await WaitForAsync(() => settings.Current.ActiveProvider == DashboardViewModel.AllAgentsTabId);

        var (reopened, _, _) = await BuildAsync(settings.Current);
        Assert.Same(reopened.AllAgentsTab, reopened.SelectedTab);
        Assert.Equal("claude", reopened.SelectedProvider.ProviderId);
    }

    [Fact]
    public async Task OpeningAServiceTab_MakesItTheOneTheMenuBarFollows()
    {
        var (dashboard, _, _) = await BuildAsync();

        dashboard.SelectedTab = dashboard.Providers[2];

        Assert.Equal("agy", dashboard.SelectedProvider.ProviderId);
    }

    [Fact]
    public async Task ChoosingAServiceFromTheMenuBar_AlsoOpensItsTab()
    {
        var (dashboard, _, _) = await BuildAsync();
        dashboard.SelectedTab = dashboard.OverviewTab;

        dashboard.SelectedProvider = dashboard.Providers[1];

        Assert.Same(dashboard.Providers[1], dashboard.SelectedTab);
    }

    [Fact]
    public async Task AfterARefresh_EachAgentTabCarriesWhatCcusageRecordedForIt()
    {
        var (dashboard, _, _) = await BuildAsync();

        await dashboard.RefreshAsync();

        var claude = dashboard.Providers[0];
        Assert.Equal(["Today (ccusage)", "This month (ccusage)"], claude.AgentUsageRows.Select(row => row.Label));
        Assert.Equal("1,000 · $1.50", claude.AgentUsageRows[0].Value);
        Assert.Equal("2,000 · $0.75", dashboard.Providers[1].AgentUsageRows[0].Value);
        Assert.Equal("3,000 · $4.00", dashboard.OpenCodeTab.AgentUsageRows[0].Value);
        // ccusage recorded nothing for Antigravity, so the AGY tab shows no ccusage rows.
        Assert.False(dashboard.Providers[2].HasAgentUsage);
    }

    [Fact]
    public async Task AfterARefresh_AllAgentsListsTodayAndThisMonth()
    {
        var (dashboard, _, _) = await BuildAsync();

        await dashboard.RefreshAsync();

        Assert.NotNull(dashboard.AllAgentsTab.Today);
        Assert.NotNull(dashboard.AllAgentsTab.ThisMonth);
        Assert.Equal(3, dashboard.AllAgentsTab.Today!.Agents.Count);
    }

    [Fact]
    public async Task TheOverviewSaysItIsWaitingUntilCcusageAnswers()
    {
        var (dashboard, _, _) = await BuildAsync();

        Assert.Equal("Checking ccusage...", dashboard.OverviewTab.EmptyMessage);
        Assert.False(dashboard.OverviewTab.ShowSetupHint);
    }

    [Fact]
    public async Task AManualRefreshWithAllAgentsOpen_ReadsCcusageAgainInsteadOfUsingItsCache()
    {
        var (dashboard, _, source) = await BuildAsync();
        await dashboard.RefreshAsync();
        var before = source.Reads;

        dashboard.SelectedTab = dashboard.AllAgentsTab;
        await dashboard.RefreshAsync(manual: true);

        Assert.Equal(before + 1, source.Reads);
    }

    [Fact]
    public async Task AnAutomaticRefresh_AnswersFromTheCcusageCache()
    {
        var (dashboard, _, source) = await BuildAsync();
        await dashboard.RefreshAsync();
        var before = source.Reads;

        await dashboard.RefreshAsync();

        Assert.Equal(before, source.Reads);
    }

    [Fact]
    public async Task ABudgetSavedInSettings_PutsTheOpenCodeGaugeUpWithoutReadingCcusageAgain()
    {
        var (dashboard, settings, source) = await BuildAsync();
        await dashboard.RefreshAsync();
        var reads = source.Reads;

        await settings.UpdateAsync(s => s with
        {
            AgentBudgets = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["opencode"] = 50m },
        });

        await WaitForAsync(() => dashboard.OpenCodeTab.Meters.Count > 0);
        Assert.Equal(8, dashboard.OpenCodeTab.Meters[0].Percent);
        Assert.Equal(reads, source.Reads);
    }

    [Fact]
    public async Task WithoutCcusage_TheNewTabsStillExistAndRefreshStillWorks()
    {
        var (dashboard, _, _) = await BuildAsync(withAgentUsage: false);

        await dashboard.RefreshAsync();

        Assert.Equal(6, dashboard.Tabs.Count);
        Assert.False(dashboard.Providers[0].HasAgentUsage);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition was not met in time");
            await Task.Delay(10);
        }
    }
}
