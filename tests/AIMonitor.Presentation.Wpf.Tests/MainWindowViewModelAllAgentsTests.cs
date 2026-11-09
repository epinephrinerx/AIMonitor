using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// Tests for the "All agents" tab integration in <see cref="MainWindowViewModel"/>, written
/// from SPEC R-014 §P3.3/§P3.4 and the Coding Owner decisions C1–C5, C14, C15 and C18.
/// The ccusage read always runs over a hand-written fake <see cref="IAgentUsageSource"/>
/// inside the real <see cref="CachedAgentUsageSource"/>; no real ccusage, no sleeps.
/// </summary>
public sealed class MainWindowViewModelAllAgentsTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 11, 9, 13, 0, 0, TimeSpan.Zero);

    private static AgentUsageReport Report(AgentUsagePricing pricing)
    {
        var localToday = DateOnly.FromDateTime(DateTime.Now);
        var claude = new AgentUsage(
            "claude",
            new TokenCounts(1_000, 0, 0, 0, 1_000),
            [new ModelUsage("claude-opus-5", new TokenCounts(1_000, 0, 0, 0, 1_000), 0.25m)]);
        return new AgentUsageReport(
            [new DailyAgentUsage(localToday, [claude])],
            [new MonthlyAgentUsage(localToday.Year, localToday.Month, [claude])],
            [],
            [],
            pricing,
            FixedUtc,
            0);
    }

    private static ProviderSnapshot ClaudeSnapshot() => new(
        providerId: "claude",
        configured: true,
        meters: [new Meter("session", "session", "Session Quota", "", 50.0)]);

    private static FakeAgentUsageSource CompletedReadsSource() =>
        new(n => Task.FromResult(Report(n == 1 ? AgentUsagePricing.OnlineRequested : AgentUsagePricing.Offline)));

    // ── C1: Tabs shape ───────────────────────────────────────────────────────

    [Fact]
    public void Tabs_ExposesThreeProviderTabsThenAllAgentsTab_ProviderTabsUnchanged()
    {
        // Decision C1: ProviderTabs stays provider-only (widget, tray); the new
        // Tabs : IReadOnlyList<object> is the three provider tabs then AllAgentsTab.
        // Spec P3.2: TabTitle "All agents", TabId "all-agents".
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        using var session = new SettingsSession(new BlockingSettingsStore(), new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        Assert.Equal(3, vm.ProviderTabs.Count);
        Assert.Equal(4, vm.Tabs.Count);
        Assert.Same(vm.ClaudeTab, vm.Tabs[0]);
        Assert.Same(vm.OpenAiTab, vm.Tabs[1]);
        Assert.Same(vm.AgyTab, vm.Tabs[2]);
        Assert.Same(vm.AllAgentsTab, vm.Tabs[3]);
        Assert.Equal("All agents", vm.AllAgentsTab.TabTitle);
        Assert.Equal("all-agents", vm.AllAgentsTab.TabId);
        Assert.False(vm.IsAllAgentsSelected);
        Assert.Same(vm.ClaudeTab, vm.SelectedTab);
    }

    // ── C1 + P3.3: selecting tabs ────────────────────────────────────────────

    [Fact]
    public async Task SelectedTabItem_AllAgents_SetsFlagKeepsSelectedTabAndSavesAllAgents()
    {
        // Decision C1 + spec P3.3: selecting All agents sets IsAllAgentsSelected = true,
        // keeps SelectedTab unchanged, and saves ActiveProvider = "all-agents".
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        vm.SelectedTabItem = vm.AllAgentsTab;
        await session.FlushAsync();

        Assert.True(vm.IsAllAgentsSelected);
        Assert.Same(vm.ClaudeTab, vm.SelectedTab); // provider content untouched (C1)
        Assert.Equal("all-agents", session.Current.ActiveProvider);
        Assert.Equal("all-agents", store.SavedSettings[^1].ActiveProvider);
    }

    [Fact]
    public async Task SelectedTabItem_ProviderTab_ChangesSelectedTabClearsFlagAndSaves()
    {
        // Decision C1: SelectedTab changes only when a provider tab is picked;
        // spec P3.3: a provider tab saves ActiveProvider = provider id.
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);
        vm.SelectedTabItem = vm.AllAgentsTab;
        await session.FlushAsync();

        vm.SelectedTabItem = vm.OpenAiTab;
        await session.FlushAsync();

        Assert.False(vm.IsAllAgentsSelected);
        Assert.Same(vm.OpenAiTab, vm.SelectedTab);
        Assert.Equal("openai", session.Current.ActiveProvider);
    }

    [Fact]
    public async Task SelectedTabItem_SameProviderAfterAllAgents_StillSavesActiveProvider()
    {
        // Decision C14 (MAJOR 2): selecting a provider tab after All agents always saves
        // ActiveProvider = provider.ProviderId, even when it is the same SelectedTab instance.
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session); // SelectedTab = ClaudeTab already

        vm.SelectedTabItem = vm.AllAgentsTab;
        await session.FlushAsync();
        vm.SelectedTabItem = vm.ClaudeTab; // same SelectedTab instance → must still save
        await session.FlushAsync();

        Assert.Equal(["all-agents", "claude"], store.SavedSettings.Select(s => s.ActiveProvider));
        Assert.Same(vm.ClaudeTab, vm.SelectedTab);
        Assert.False(vm.IsAllAgentsSelected);
    }

    [Fact]
    public async Task SelectedTab_SetProgrammatically_SyncsSelectedTabItemAndClearsFlag()
    {
        // Decision C14 (MINOR 6): setting SelectedTab programmatically also sets
        // SelectedTabItem to it and IsAllAgentsSelected = false.
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);
        vm.SelectedTabItem = vm.AllAgentsTab;
        await session.FlushAsync();

        vm.SelectedTab = vm.AgyTab;
        await session.FlushAsync();

        Assert.False(vm.IsAllAgentsSelected);
        Assert.Same(vm.AgyTab, vm.SelectedTabItem);
        Assert.Equal("agy", session.Current.ActiveProvider);
    }

    // ── C2: settings normalisation + startup ─────────────────────────────────

    [Fact]
    public void AppSettingsNormalize_AcceptsAllAgents()
    {
        // Decision C2: AppSettings normalisation accepts "all-agents".
        Assert.Equal("all-agents", new AppSettings { ActiveProvider = "all-agents" }.Normalize().ActiveProvider);
    }

    [Fact]
    public void AppSettingsNormalize_UnknownProvider_StillFallsBackToClaude()
    {
        // Decision C2 widens normalisation for exactly one value; unknown ids keep falling back.
        Assert.Equal("claude", new AppSettings { ActiveProvider = "no-such-tab" }.Normalize().ActiveProvider);
    }

    [Fact]
    public void Startup_WithAllAgentsActiveProvider_SelectsAllAgentsTabWithClaudeContent()
    {
        // Decision C2: startup with "all-agents": IsAllAgentsSelected = true,
        // SelectedTab = ClaudeTab. Spec P3.3: startup with it selects the All agents tab
        // (the ListBox selection is the AllAgentsTab item).
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings { ActiveProvider = "all-agents" });
        using var session = new SettingsSession(store, new AppSettings { ActiveProvider = "all-agents" });
        Assert.Equal("all-agents", session.Current.ActiveProvider);

        var vm = new MainWindowViewModel(coordinator, session);

        Assert.True(vm.IsAllAgentsSelected);
        Assert.Same(vm.ClaudeTab, vm.SelectedTab);
        Assert.Same(vm.AllAgentsTab, vm.SelectedTabItem);
    }

    // ── C4: force only manual && All agents ──────────────────────────────────

    [Fact]
    public async Task RefreshAsync_ForceOnlyWhenManualAndAllAgentsSelected()
    {
        // Decision C4: force = manual && IsAllAgentsSelected. Timer/F5-less paths and
        // provider-tab manual refreshes must not burn a ccusage read: with the clock frozen
        // and MinInterval 15 min, only a forced read reaches the inner source.
        var source = CompletedReadsSource();
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc), TimeSpan.FromMinutes(15));
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);

        await vm.RefreshAsync(); // full refresh, not manual → reads through the empty cache
        Assert.NotNull(vm.AgentUsageTask);
        await vm.AgentUsageTask!;
        Assert.Equal(1, source.ReadCount);

        vm.SelectedTabItem = vm.AllAgentsTab;
        await session.FlushAsync();

        await vm.RefreshAsync(manual: false); // timer / view-change path → not forced → cached
        await vm.AgentUsageTask!;
        Assert.Equal(1, source.ReadCount);

        await vm.RefreshAsync(manual: true); // manual while All agents is selected → forced
        await vm.AgentUsageTask!;
        Assert.Equal(2, source.ReadCount);
        Assert.Equal("Prices: offline (built-in)", vm.AllAgentsTab.PricingText); // C5/C6: Updated wired through

        vm.SelectedTabItem = vm.ClaudeTab;
        await session.FlushAsync();

        await vm.RefreshAsync(manual: true); // manual but provider tab selected → not forced → cached
        await vm.AgentUsageTask!;
        Assert.Equal(2, source.ReadCount);
    }

    [Fact]
    public async Task RefreshCommand_PassesManualTrue_ForceOnlyAppliesWhenAllAgentsSelected()
    {
        // Decision C4: RefreshCommand passes manual: true. With All agents selected and a
        // fresh cache, only the forced path starts a second inner read. Wait on the fake
        // source itself: a refresh that completes synchronously may hand back the shared
        // Task.CompletedTask, so AgentUsageTask identity is deliberately not observed.
        var secondReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeAgentUsageSource(n =>
        {
            var report = Report(n == 1 ? AgentUsagePricing.OnlineRequested : AgentUsagePricing.Offline);
            if (n == 2) secondReadStarted.TrySetResult();
            return Task.FromResult(report);
        });
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc), TimeSpan.FromMinutes(15));
        var registrations = new[] { new ProviderClientRegistration("claude", new TestQuotaClient((r, t) => Task.FromResult(ClaudeSnapshot()))) };
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(registrations));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        Assert.Equal(1, source.ReadCount);
        Assert.Equal("Prices: online", vm.AllAgentsTab.PricingText);

        vm.SelectedTabItem = vm.AllAgentsTab;
        await session.FlushAsync();

        vm.RefreshCommand.Execute(null);

        // Bounded wait on a pool thread (pump-safe, see the slow-source test); a RefreshCommand
        // that does not force while All agents is selected never starts read 2 → red after 10 s, never hangs.
        var forceReadStarted = await Task.Run(
            () => SpinWait.SpinUntil(() => secondReadStarted.Task.IsCompleted, TimeSpan.FromSeconds(10)));
        Assert.True(forceReadStarted, "RefreshCommand did not start a forced ccusage read after the provider tabs.");
        await vm.AgentUsageTask!;

        Assert.Equal(2, source.ReadCount); // manual && All agents → force bypassed the fresh cache
        Assert.Equal("Prices: offline (built-in)", vm.AllAgentsTab.PricingText);
    }

    [Fact]
    public async Task RefreshAsync_SingleProviderRedetect_NeverStartsCcusageRead()
    {
        // Decision C4: a single-provider re-detect does not start a ccusage read —
        // not even with manual: true while All agents is selected (still a re-detect).
        var source = CompletedReadsSource();
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc), TimeSpan.FromMinutes(15));
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);

        await vm.RefreshAsync("claude");

        Assert.Null(vm.AgentUsageTask);
        Assert.Equal(0, source.ReadCount);

        vm.SelectedTabItem = vm.AllAgentsTab;
        await session.FlushAsync();

        await vm.RefreshAsync("claude", manual: true);

        Assert.Null(vm.AgentUsageTask);
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public async Task RefreshAsync_WithoutAgentUsageSource_StartsNoRead()
    {
        // Spec P3.3: the CachedAgentUsageSource ctor parameter is optional; with none
        // passed (P1 behaviour) refreshes must not touch ccusage at all.
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        using var session = new SettingsSession(new BlockingSettingsStore(), new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        await vm.RefreshAsync(manual: true);

        Assert.Null(vm.AgentUsageTask);
    }

    // ── C3 + P3.3: the read never blocks the provider refresh ────────────────

    [Fact]
    public async Task RefreshAsync_WhenAgentSourceIsSlow_ProviderRefreshCompletesFirstAndReadStartsAfterTabs()
    {
        // Spec P3.3 / decision C3: MainWindowViewModel starts the ccusage read (not awaited)
        // AFTER the provider tabs are updated, without delaying them; IsRefreshing and
        // "Refresh failed" never depend on ccusage. No sleeps: TCS-gated fake source.
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource<AgentUsageReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        int? metersAtReadStart = null;
        MainWindowViewModel? vmHandle = null;
        var source = new FakeAgentUsageSource(n =>
        {
            if (n == 1)
            {
                metersAtReadStart = vmHandle!.ClaudeTab.Meters.Count;
                readStarted.TrySetResult();
                return releaseRead.Task;
            }

            return Task.FromResult(Report(AgentUsagePricing.OnlineRequested));
        });
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc), TimeSpan.FromMinutes(15));
        var registrations = new[] { new ProviderClientRegistration("claude", new TestQuotaClient((r, t) => Task.FromResult(ClaudeSnapshot()))) };
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(registrations));
        using var session = new SettingsSession(new BlockingSettingsStore(), new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);
        vmHandle = vm;
        var localToday = DateOnly.FromDateTime(DateTime.Now);
        // Decision C11 oracle shape: since = first day of the month two months before the local today.
        var expectedSince = new DateOnly(localToday.Year, localToday.Month, 1).AddMonths(-2);

        var refresh = vm.RefreshAsync();
        await readStarted.Task;

        // The read starts only once the provider tab already shows the snapshot data.
        Assert.Equal(1, metersAtReadStart);

        // Bounded wait, run on a pool thread so the test thread keeps pumping continuations
        // posted to its own synchronization context (no sleeps; a wrong implementation goes
        // red after 10 s instead of hanging).
        var refreshFinished = await Task.Run(
            () => SpinWait.SpinUntil(() => refresh.IsCompleted, TimeSpan.FromSeconds(10)));
        Assert.True(refreshFinished, "RefreshAsync awaited the ccusage read instead of starting it.");
        await refresh;

        Assert.False(vm.IsRefreshing); // C3: IsRefreshing never depends on ccusage
        Assert.DoesNotContain("Refresh failed", vm.LastRefreshStatus);
        Assert.NotNull(vm.AgentUsageTask);
        Assert.False(vm.AgentUsageTask!.IsCompleted); // read is still in flight → not awaited

        releaseRead.SetResult(Report(AgentUsagePricing.OnlineRequested));
        await vm.AgentUsageTask!;

        // Decision C11 oracle: since = first day of the month two months before the local today.
        Assert.Equal(expectedSince, Assert.Single(source.Sincs));
        // Decision C5: Updated → AllAgentsTab.Update(lastGood, lastError, today, ChartRangeDays).
        Assert.NotNull(vm.AllAgentsTab.Today);
        Assert.True(vm.AllAgentsTab.HasDays); // C16 through the wiring
        Assert.Equal("Prices: online", vm.AllAgentsTab.PricingText);
    }

    [Fact]
    public async Task RefreshAsync_WhenProviderRefreshFails_CcusageReadStillStarts()
    {
        // Decision C15: start the ccusage read after the provider try/catch/finally block,
        // so a failed provider refresh still starts it (MINOR 4).
        var source = CompletedReadsSource();
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc), TimeSpan.FromMinutes(15));
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        using var session = new SettingsSession(new BlockingSettingsStore(), new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);
        await coordinator.DisposeAsync(); // the next QueueAsync throws → the provider try/catch fails

        await vm.RefreshAsync();

        Assert.StartsWith("Refresh failed", vm.LastRefreshStatus);
        Assert.False(vm.IsRefreshing);
        Assert.NotNull(vm.AgentUsageTask); // C15: the read started despite the provider failure
        await vm.AgentUsageTask!;
        Assert.Equal(1, source.ReadCount);
        // C3: ccusage failures never surface as "Refresh failed" — the tab is updated via Updated (C5).
        Assert.StartsWith("Refresh failed", vm.LastRefreshStatus);
        Assert.Equal("Prices: online", vm.AllAgentsTab.PricingText);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>Hand-written fake <see cref="IAgentUsageSource"/> (TESTING.md §4/§5).</summary>
    private sealed class FakeAgentUsageSource : IAgentUsageSource
    {
        private readonly Func<int, Task<AgentUsageReport>> _handler;
        private readonly object _sync = new();
        private readonly List<DateOnly?> _sincs = [];
        private int _readCount;

        public FakeAgentUsageSource(Func<int, Task<AgentUsageReport>> handler) => _handler = handler;

        public int ReadCount => Volatile.Read(ref _readCount);

        public IReadOnlyList<DateOnly?> Sincs
        {
            get { lock (_sync) return _sincs.ToList(); }
        }

        public Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct)
        {
            var number = Interlocked.Increment(ref _readCount);
            lock (_sync) _sincs.Add(since);
            return _handler(number);
        }
    }

    private sealed class TestQuotaClient : IProviderQuotaClient
    {
        public TestQuotaClient(Func<ProviderSnapshotRequest, CancellationToken, Task<ProviderSnapshot>> handler)
        {
            Handler = handler;
        }

        public Func<ProviderSnapshotRequest, CancellationToken, Task<ProviderSnapshot>> Handler { get; }

        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
            => Handler(request, cancellationToken);
    }
}
