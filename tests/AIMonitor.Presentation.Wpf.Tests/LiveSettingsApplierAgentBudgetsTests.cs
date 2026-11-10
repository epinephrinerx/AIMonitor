using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Tray;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-015 Q2.2 live budget apply — <c>LiveSettingsApplier.Apply(settings)</c> calls the new
/// <c>MainWindowViewModel.ApplyAgentBudgets(settings.AgentBudgets)</c>, written from
/// Docs/SPEC_R015.md only (§5.6). The gauge's final state is the observable (E16): a settings apply
/// after the first Updated re-renders the OpenCode meter from the stored lastGood WITHOUT running
/// ccusage (E2, "no ccusage run"). Hand-written fakes; no real ccusage, no network. Q2 tests:
/// expected not to compile until the Q2 branch merges (E16).
/// </summary>
public sealed class LiveSettingsApplierAgentBudgetsTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 11, 9, 7, 0, 0, TimeSpan.Zero);

    private static AgentUsage OpencodeAgent()
    {
        return new AgentUsage("opencode", new TokenCounts(9_999, 0, 0, 0, 9_999),
            [new ModelUsage("glm-4.6", new TokenCounts(9_999, 0, 0, 0, 9_999), 25.00m)]);
    }

    private static AgentUsageReport WithMonth(DateOnly today) =>
        new(
            daily: [],
            monthly: [new MonthlyAgentUsage(today.Year, today.Month, [OpencodeAgent()])],
            sessions: [],
            unpricedModels: [],
            pricing: AgentUsagePricing.OnlineRequested,
            retrievedAt: FixedUtc,
            skippedRows: 0);

    [Fact]
    public async Task Apply_Q22_ForwardsAgentBudgets_OpenCodeGaugeRebuildsWithoutRunningCcusage()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var report = WithMonth(today);
        var source = new FakeAgentUsageSource(_ => Task.FromResult(report));
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));

        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;

        // After Updated with no budgets applied yet: the O3 text.
        Assert.Equal("No budget set — add one in Settings", Assert.Single(vm.OpenCodeTab.Meters).Subtitle);

        // Live budget apply (Q2.2): the same Apply that carries theme/interval now forwards budgets.
        // money(50) = "$50.00"; percent = (25 / 50) × 100 = 50 (hand-derived).
        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: vm,
            createTray: () => new FakeTrayHost(),
            setRefreshInterval: _ => { },
            isAnyWindowVisible: () => true,
            showDashboard: () => { });

        applier.Apply(new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 50m },
        });

        var meter = Assert.Single(vm.OpenCodeTab.Meters);
        Assert.Equal("of your $50.00 budget", meter.Subtitle);
        Assert.Equal("50%", meter.ValueText);

        // E2: rebuilding the snapshot never runs ccusage (final state plus the read count that the
        // handoff explicitly calls out: "without a ccusage run").
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task Apply_Q22_BeforeFirstUpdated_StoresBudgetsForTheNextSnapshot()
    {
        // E2: before the first Updated, the apply only stores; the gauge shows the budget on the
        // next Updated — still exactly one read for both steps.
        var today = DateOnly.FromDateTime(DateTime.Now);
        var report = WithMonth(today);
        var source = new FakeAgentUsageSource(_ => Task.FromResult(report));
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));

        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);

        var applier = new LiveSettingsApplier(
            widgetViewModel: null,
            mainViewModel: vm,
            createTray: () => new FakeTrayHost(),
            setRefreshInterval: _ => { },
            isAnyWindowVisible: () => true,
            showDashboard: () => { });

        applier.Apply(new AppSettings
        {
            AgentBudgets = new Dictionary<string, decimal> { ["opencode"] = 25m },
        });

        Assert.Empty(vm.OpenCodeTab.Meters); // stores only — nothing pushed yet

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;

        var meter = Assert.Single(vm.OpenCodeTab.Meters);
        Assert.Equal("of your $25.00 budget", meter.Subtitle); // money(25)
        Assert.Equal("100%", meter.ValueText);                 // (25 / 25) × 100, not capped
        Assert.Equal(1, source.ReadCount);
    }

    // ── Fakes (same hand-written pattern as LiveSettingsApplierTests) ──────────

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

    private sealed class FakeTrayHost : ITrayHost
    {
        public void UpdateReadings(IEnumerable<TrayReading> readings)
        {
        }

        public void Dispose()
        {
        }
    }
}
