using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-018 A3/D1/D6/D9 — <c>MainWindowViewModel</c> AGY composition, written from Docs/SPEC_R018.md
/// only (§5.6 Tests + decisions). The view model keeps the latest raw agy snapshot and pushes
/// <c>AgyUsageComposer.Compose(…)</c> to <c>AgyTab.UpdateFromSnapshot</c> both (a) where the
/// coordinator result is applied and (b) in <c>OnAgentUsageUpdated</c> when a raw agy snapshot
/// exists, always from one now/today pair (I8), never starting a ccusage read or an agy refresh for
/// it. D1: the chart range/metric setters recompose AGY synchronously inside the rebuild (asserted
/// with NO await between setter and assertion, the F1 pattern from the R-017 wiring tests), settings
/// changes recompose through the same path, and Overview stays excluded (R-016 I15). D6: with AGY
/// disabled the raw snapshot is dropped and never composed; an AGY-only re-detect recomposes from
/// the cached report without starting ccusage. D9: after data was composed the AGY ValueNote is
/// "Quota from AGY CLI · tokens and cost from ccusage"; otherwise the client's note is kept.
/// Hand-written fakes as in the R-014/R-015/R-017 view-model tests: no real ccusage, no agy, no
/// network; F21 — ONE local day captured per test (fixture and assertions read the same value; the
/// view model's own clock read is the documented midnight residual). The view model is constructed
/// under xUnit's own synchronization context, so both D9 outcomes are settled: inline when the
/// handler runs on the captured context, or posted to it — the extra awaits pump the queue before
/// anything is asserted. Oracles (spec test oracles): today + yesterday antigravity rows — Apr 30
/// pattern 10,000/500/8,500/$0.50 and May 1 pattern 20,000/1,000/17,000/$1.00 + 10,000/500/8,500/
/// null → tiles 40K / 2,000 / 34K / "$1.50 + unpriced". The composed behaviour does not exist at the
/// base commit — these tests are the expected-red R-018 set for the view model.
/// </summary>
public sealed class MainWindowViewModelAgyCompositionTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 11, 9, 7, 0, 0, TimeSpan.Zero);

    private const string ModelA = "Model A";
    private const string ModelX = "Model X";
    private const string ClientNote = "Synthetic AGY quota note";
    private const string ComposedNote = "Quota from AGY CLI · tokens and cost from ccusage";

    private static ModelUsage Model(string name, long total, long output, long cacheRead, decimal? costUsd) =>
        new(name, new TokenCounts(total - output - cacheRead, output, 0, cacheRead, total), costUsd);

    private static AgentUsage Agent(string id, params ModelUsage[] models) =>
        new(id, new TokenCounts(
            models.Sum(m => m.Tokens.Input),
            models.Sum(m => m.Tokens.Output),
            models.Sum(m => m.Tokens.CacheCreation),
            models.Sum(m => m.Tokens.CacheRead),
            models.Sum(m => m.Tokens.Total)),
            models);

    /// <summary>The oracle report on the test's local day pair: today = the May 1 pattern (A 20K +
    /// X 10K unpriced), yesterday = the Apr 30 pattern (A 10K). Codex and claude noise rows on the
    /// same days must never enter the AGY tiles, chart or the unpriced flag.</summary>
    private static AgentUsageReport Report(DateOnly today) => new(
        daily: [
            new DailyAgentUsage(today,
            [
                Agent("antigravity", Model(ModelA, 20_000, 1_000, 17_000, 1.00m), Model(ModelX, 10_000, 500, 8_500, null)),
                Agent("codex", Model("gpt-5.1-codex", 999_999, 9_999, 0, 99.99m)),
            ]),
            new DailyAgentUsage(today.AddDays(-1),
            [
                Agent("antigravity", Model(ModelA, 10_000, 500, 8_500, 0.50m)),
                Agent("claude", Model("claude-opus-5-5", 7_777, 777, 0, null)), // unpriced NOISE: yesterday's day must stay (0.50, false)
            ]),
        ],
        monthly: [],
        sessions: [],
        unpricedModels: [],
        pricing: AgentUsagePricing.OnlineRequested,
        retrievedAt: FixedUtc,
        skippedRows: 0);

    private static AgentUsageReport ReportWithoutAntigravity(DateOnly today) => new(
        daily: [new DailyAgentUsage(today,
            [
                Agent("claude", Model("claude-opus-5-5", 1_000, 100, 0, 1.00m)),
                Agent("codex", Model("gpt-5.1-codex", 2_000, 200, 0, 2.00m)),
            ])],
        monthly: [],
        sessions: [],
        unpricedModels: [],
        pricing: AgentUsagePricing.OnlineRequested,
        retrievedAt: FixedUtc,
        skippedRows: 0);

    private static ProviderSnapshot AgySnapshot(
        double sessionPercent = 40.0,
        string account = "paul@google.example") => new(
            "agy",
            configured: true,
            meters: [
                new Meter("session", "session", "Session", "5-hour window", sessionPercent),
                new Meter("weekly", "weekly", "Weekly", "7-day window", 61.0, section: "Gemini"),
            ],
            stats: [new Stat("Requests today", "128")],
            account: account,
            setupHint: "Install or sign in to AGY CLI, then refresh.",
            valueNote: ClientNote,
            fetchedAt: FixedUtc,
            detection: new DetectionInfo("agy", DetectionState.Connected, sourceId: "agy-cli", sourceLabel: "AGY CLI"));

    private static DashboardOption<int> RangeOption(MainWindowViewModel vm, int days) =>
        vm.RangeOptions.First(o => o.Value == days);

    private static DashboardOption<string> MetricOption(MainWindowViewModel vm, string metric) =>
        vm.MetricOptions.First(o => o.Value == metric);

    private static void AssertComposedTiles(ProviderTabViewModel tab)
    {
        Assert.Equal(["Requests today", "Total tokens", "Output tokens", "Cache reads", "Equivalent API value"],
            tab.StatTiles.Select(t => t.Label));
        Assert.Equal(["128", "40K", "2,000", "34K", "$1.50 + unpriced"],
            tab.StatTiles.Select(t => t.Value));
        Assert.Equal("ccusage · equivalent API price", tab.StatTiles[^1].Detail);
    }

    // ── A3: agy snapshot first, ccusage report second ───────────────────────────

    [Fact]
    public async Task A3_AgyFirstThenCcusage_AgyTabShowsHistoryAndFourStats_NoExtraReads()
    {
        var agy = new FakeAgyClient(AgySnapshot());
        var source = new FakeAgentUsageSource(_ => Task.FromResult(Report(DateOnly.FromDateTime(DateTime.Now))));
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
        [
            new ProviderClientRegistration("claude", new FixedClient("claude")),
            new ProviderClientRegistration("openai", new FixedClient("openai")),
            new ProviderClientRegistration("agy", agy),
        ]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);

        await vm.RefreshAsync();          // (a): the agy snapshot arrives first — no report stored yet
        await vm.AgentUsageTask!;         // (b): OnAgentUsageUpdated composes with the raw snapshot
        await Task.Yield();
        await Task.Yield();               // D9: settle a post to the context captured at construction

        var tab = vm.AgyTab;
        Assert.True(tab.HasSnapshot);
        Assert.True(tab.HasHistory); // spec A3: HasHistory true after the Updated
        Assert.Equal(14, tab.ChartBuckets.Count); // default ChartRangeDays 14
        Assert.Equal(10_000.0, tab.ChartBuckets[^2].Total); // yesterday (Apr 30 pattern)
        Assert.Equal(30_000.0, tab.ChartBuckets[^1].Total); // today (May 1 pattern)
        Assert.All(tab.ChartBuckets.Take(12), b => Assert.Empty(b.PerModel));
        Assert.Equal([ModelA, ModelX], tab.ChartSeries); // the 999,999-token codex noise never shows
        Assert.Equal(UsageMetric.TotalTokens, tab.ChartMetric);
        Assert.False(tab.ChartCosts[^2].HasUnpricedModels); // claude's null cost did not flag yesterday
        Assert.True(tab.ChartCosts[^1].HasUnpricedModels);  // antigravity Model X did

        // Stat tiles present: existing agy stat first, then the four hand-oracle stats.
        AssertComposedTiles(tab);

        // Raw snapshot fields preserved through the compose (A2/D5 at the tab).
        Assert.Equal(2, tab.Meters.Count);
        Assert.Equal(40.0, tab.Meters[0].Value);
        Assert.Equal(61.0, tab.Meters[1].Value);
        Assert.Equal("paul@google.example", tab.AccountLabel);
        Assert.Equal(FixedUtc, tab.LastUpdated); // FetchedAt kept (A2)

        // D9: ccusage data was composed → the composed note replaces the client's.
        Assert.Equal(ComposedNote, tab.ValueNote);

        // A3: composing never starts a ccusage read or an agy refresh.
        Assert.Equal(1, agy.CallCount);
        Assert.Equal(1, source.ReadCount);
    }

    // ── A3/D6: ccusage first (agy disabled), then an AGY-only re-detect ─────────

    [Fact]
    public async Task A3_CcusageFirstThenAgy_ReDetectRecomposesFromCachedReport_WithoutStartingCcusage()
    {
        var initial = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference> { ["agy"] = new ProviderPreference(Enabled: false) },
        };
        var agy = new FakeAgyClient(AgySnapshot());
        var source = new FakeAgentUsageSource(_ => Task.FromResult(Report(DateOnly.FromDateTime(DateTime.Now))));
        var clock = new FakeClock(FixedUtc);
        var cache = new CachedAgentUsageSource(source, clock);
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
        [
            new ProviderClientRegistration("claude", new FixedClient("claude")),
            new ProviderClientRegistration("openai", new FixedClient("openai")),
            new ProviderClientRegistration("agy", agy),
        ]));
        var store = new BlockingSettingsStore(initial);
        store.Release();
        using var session = new SettingsSession(store, initial);
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        await Task.Yield();
        await Task.Yield();

        // D6: AGY disabled → no raw snapshot; Updated never composes for the tab ("today's look").
        Assert.Equal(0, agy.CallCount); // the disabled client was skipped
        Assert.False(vm.LatestSnapshots.ContainsKey("agy"));
        Assert.False(vm.AgyTab.HasSnapshot);
        Assert.False(vm.AgyTab.HasHistory);
        Assert.Empty(vm.AgyTab.StatTiles);
        Assert.Equal(1, source.ReadCount);

        // Enable again (settings-driven, like the Connections page toggle).
        await session.UpdateAsync(s => s with
        {
            Providers = new Dictionary<string, ProviderPreference> { ["agy"] = new ProviderPreference(true) },
        });
        await Task.Yield();

        // D6 re-detect pin: the quota snapshot CHANGES while ccusage stays cached.
        agy.Next = AgySnapshot(sessionPercent: 55.0, account: "new-account@google.example");
        await vm.RefreshAsync("agy");
        await Task.Yield();

        // (a) path: the re-detected raw snapshot is composed with the STORED report — HasHistory and
        // the tiles arrive now, the new quota data is kept, and the composed note shows (D9).
        Assert.True(vm.AgyTab.HasSnapshot);
        Assert.True(vm.AgyTab.HasHistory);
        Assert.Equal(14, vm.AgyTab.ChartBuckets.Count);
        Assert.Equal(30_000.0, vm.AgyTab.ChartBuckets[^1].Total);
        Assert.Equal(10_000.0, vm.AgyTab.ChartBuckets[^2].Total);
        AssertComposedTiles(vm.AgyTab);
        Assert.Equal(55.0, vm.AgyTab.Meters[0].Value); // changed quota snapshot (D6 test spec)
        Assert.Equal("new-account@google.example", vm.AgyTab.AccountLabel);
        Assert.Equal(ComposedNote, vm.AgyTab.ValueNote);

        // "AGY-only re-detect recomposes with the cached report WITHOUT starting ccusage": one agy
        // client call in total, still exactly one ccusage read.
        Assert.Equal(1, agy.CallCount);
        Assert.Equal(1, source.ReadCount);
    }

    // ── D6: disabling AGY drops the raw snapshot; later Updateds never compose ──

    [Fact]
    public async Task D6_DisablingAgyDropsRawSnapshot_LaterCcusageUpdatesNeverCompose()
    {
        var agy = new FakeAgyClient(AgySnapshot());
        var source = new FakeAgentUsageSource(_ => Task.FromResult(Report(DateOnly.FromDateTime(DateTime.Now))));
        var clock = new FakeClock(FixedUtc);
        var cache = new CachedAgentUsageSource(source, clock);
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
        [
            new ProviderClientRegistration("claude", new FixedClient("claude")),
            new ProviderClientRegistration("openai", new FixedClient("openai")),
            new ProviderClientRegistration("agy", agy),
        ]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        await Task.Yield();
        await Task.Yield();
        Assert.True(vm.AgyTab.HasHistory); // composed while enabled
        var buckets = vm.AgyTab.ChartBuckets;
        var tiles = vm.AgyTab.StatTiles;

        await session.UpdateAsync(s => s with
        {
            Providers = new Dictionary<string, ProviderPreference> { ["agy"] = new ProviderPreference(Enabled: false) },
        });
        await Task.Yield();

        await vm.RefreshAsync(); // full refresh without agy: the stored raw snapshot must be dropped
        await vm.AgentUsageTask!; // a cached Updated still fires — composition must not
        await Task.Yield();
        await Task.Yield();

        Assert.False(vm.LatestSnapshots.ContainsKey("agy")); // "drop the raw agy snapshot" (D6)
        Assert.Same(buckets, vm.AgyTab.ChartBuckets); // no UpdateFromSnapshot push happened since
        Assert.Same(tiles, vm.AgyTab.StatTiles);
        Assert.Equal(1, agy.CallCount); // the disabled client was skipped in the full refresh

        // Even a fresh ccusage read (cache expired) must not compose for the disabled tab.
        clock.UtcNow += TimeSpan.FromMinutes(16);
        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        await Task.Yield();
        await Task.Yield();

        Assert.Equal(2, source.ReadCount); // a real second read happened — and still nothing was pushed
        Assert.Same(buckets, vm.AgyTab.ChartBuckets);
        Assert.Same(tiles, vm.AgyTab.StatTiles);
    }

    // ── A3/D9: no antigravity data → today's look, client note kept ─────────────

    [Fact]
    public async Task D9_ReportWithoutAntigravity_AgyKeepsClientNote_AndShowsNoStatsOrChart()
    {
        var agy = new FakeAgyClient(AgySnapshot());
        var source = new FakeAgentUsageSource(_ => Task.FromResult(ReportWithoutAntigravity(DateOnly.FromDateTime(DateTime.Now))));
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
        [
            new ProviderClientRegistration("claude", new FixedClient("claude")),
            new ProviderClientRegistration("openai", new FixedClient("openai")),
            new ProviderClientRegistration("agy", agy),
        ]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        await Task.Yield();
        await Task.Yield();

        // "agy unchanged (no stats, no history — today's look)" + D9 "otherwise the client's note
        // is kept".
        Assert.True(vm.AgyTab.HasSnapshot);
        Assert.False(vm.AgyTab.HasHistory);
        Assert.Equal(["Requests today"], vm.AgyTab.StatTiles.Select(t => t.Label));
        Assert.Equal(ClientNote, vm.AgyTab.ValueNote);
        Assert.False(vm.AgyTab.ShowSupplementary); // no antigravity ccusage line either (C8/C10)
    }

    // ── D1: range/metric change recomposes AGY synchronously; Overview excluded ─

    [Fact]
    public async Task D1_RangeAndMetricSetters_RecomposeAgySynchronously_NoReadsAndOverviewUntouched()
    {
        var agy = new FakeAgyClient(AgySnapshot());
        var source = new FakeAgentUsageSource(_ => Task.FromResult(Report(DateOnly.FromDateTime(DateTime.Now))));
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
        [
            new ProviderClientRegistration("claude", new FixedClient("claude")),
            new ProviderClientRegistration("openai", new FixedClient("openai")),
            new ProviderClientRegistration("agy", agy),
        ]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        await Task.Yield();
        await Task.Yield();
        var agyBuckets = vm.AgyTab.ChartBuckets;
        var overviewBuckets = vm.OverviewTab.ChartBuckets;
        Assert.Equal(14, agyBuckets.Count);

        // Every assertion below runs with NO await between the setter and the assert: the recompose
        // happens IN the setter (D1), exactly the F1 pattern of the R-017 wiring tests.
        vm.SelectedRange = RangeOption(vm, 30);
        Assert.Equal(30, vm.AgyTab.ChartBuckets.Count); // recomposed synchronously from raw agy + cached report
        Assert.NotSame(agyBuckets, vm.AgyTab.ChartBuckets);
        Assert.Equal(30, vm.OpenCodeTab.ChartBuckets.Count); // the existing OpenCode rebuild stands
        Assert.Same(overviewBuckets, vm.OverviewTab.ChartBuckets); // D1: Overview stays excluded (I15)
        Assert.Equal(1, source.ReadCount); // no ccusage read for the recompose
        Assert.Equal(1, agy.CallCount); // ...and no extra agy refresh either (the follow-up existing
                                        // refresh path only starts asynchronously, after these lines)

        vm.SelectedMetric = MetricOption(vm, UsageMetric.OutputTokens);
        Assert.Equal(UsageMetric.OutputTokens, vm.AgyTab.ChartMetric);
        Assert.Equal(new Dictionary<string, double> { [ModelA] = 1_000, [ModelX] = 500 },
            vm.AgyTab.ChartBuckets[^1].PerModel); // today's output values
        Assert.Equal(new Dictionary<string, double> { [ModelA] = 500 },
            vm.AgyTab.ChartBuckets[^2].PerModel); // yesterday's
        Assert.Equal(30, vm.AgyTab.ChartBuckets.Count);

        // D4: the four tiles are fixed sums, independent of the chart metric.
        AssertComposedTiles(vm.AgyTab);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, agy.CallCount);
    }

    [Fact]
    public async Task D1_SettingsDrivenRangeChange_RecomposesAgyTab_NoReads()
    {
        // D1: "Test direct selection and settings-driven changes" — the external settings path
        // (OnSettingsChanged sync) recomposes AGY through the same rebuild, without touching
        // ccusage or the agy client (no view-change refresh runs on the syncing path).
        var agy = new FakeAgyClient(AgySnapshot());
        var source = new FakeAgentUsageSource(_ => Task.FromResult(Report(DateOnly.FromDateTime(DateTime.Now))));
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
        [
            new ProviderClientRegistration("claude", new FixedClient("claude")),
            new ProviderClientRegistration("openai", new FixedClient("openai")),
            new ProviderClientRegistration("agy", agy),
        ]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        await Task.Yield();
        await Task.Yield();
        Assert.Equal(14, vm.AgyTab.ChartBuckets.Count);

        await session.UpdateAsync(s => s with { ChartRangeDays = 30 });
        await Task.Yield(); // the Changed raise is posted to the captured context — pump it
        await Task.Yield(); // (D9: a composed push from the same sync settles within these pumps)

        Assert.Equal(30, vm.SelectedRange.Value);
        Assert.Equal(30, vm.AgyTab.ChartBuckets.Count); // recomposed through the settings path
        Assert.Equal(30, vm.OpenCodeTab.ChartBuckets.Count);
        Assert.Equal(30_000.0, vm.AgyTab.ChartBuckets[^1].Total); // values from the cached report
        AssertComposedTiles(vm.AgyTab);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, agy.CallCount);
    }

    // ── Fakes (hand-written, same as the R-014/R-015/R-017 view-model tests) ────

    /// <summary>A provider client that always returns the same minimal configured snapshot.</summary>
    private sealed class FixedClient(string providerId) : IProviderQuotaClient
    {
        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderSnapshot(
                providerId,
                configured: true,
                meters: [new Meter("session", "session", "Session", "5-hour window", 10.0)]));
    }

    /// <summary>The AGY client: records calls, snapshot swap-able (D6 re-detect changes the quota).</summary>
    private sealed class FakeAgyClient : IProviderQuotaClient
    {
        private int _callCount;

        public FakeAgyClient(ProviderSnapshot next) => Next = next;

        public ProviderSnapshot Next { get; set; }

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(Next);
        }
    }

    private sealed class FakeAgentUsageSource(Func<DateOnly?, Task<AgentUsageReport>> handler) : IAgentUsageSource
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct)
        {
            Interlocked.Increment(ref _readCount);
            return handler(since);
        }
    }
}
