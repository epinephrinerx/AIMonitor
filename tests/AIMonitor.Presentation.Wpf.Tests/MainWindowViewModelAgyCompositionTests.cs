using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;
using System.Collections.Concurrent;

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
/// D11 (round 2, the code is correct): the setter test does not count agy calls — the PRE-EXISTING
/// follow-up refresh may run them synchronously inside the setter — and the settings-driven test
/// pumps the sync with the R-017 F13/F24 QueuedSyncContext + Drain pattern instead of Task.Yield.
/// D14 (round 3): (1) D9 — <see cref="D9_OffThreadRefresh_TabChangesWaitUntilTheCapturedContextDrains"/>
/// starts the refresh from a <see cref="Task.Run"/> thread under a controlled context and asserts the
/// OpenCode/Overview/AGY tab changes appear only after that context drains; it FAILS while the handler
/// runs off-thread (left failing for Writing Owner 1, not weakened). (2) D6 — the stale-snapshot test
/// re-enables AGY without a new agy result and recomposes from the cached report: the dropped raw
/// snapshot must not revive the tab. (3) D13 — a controlled AGY composition failure (a daily cost
/// <c>AgentHistoryBuilder</c> rejects) must leave OpenCode and Overview already updated. (4) D14(6) —
/// every test in this class now constructs the view model on its own QueuedSyncContext and settles
/// with an explicit <c>Drain</c> instead of repeated <c>Task.Yield</c> pumping. The view model has no
/// clock seam (it reads <c>DateTimeOffset.UtcNow</c>/<c>TimeZoneInfo.Local</c> inside, and the
/// refresher's own clock is not injectable from outside), so the fixture local day still comes from
/// <c>DateTime.Now</c> — the documented F21 midnight residual; replacing it needs a production seam.
/// D16 (round 4, replaces the D15 mechanism): the view model hands the context it captured at
/// construction to the refresher per call when a refresh is entered with no context, so the Updated
/// handler is posted there and <c>AgentUsageTask</c> completes only after the tab work ran. The D9
/// test now asserts the task is NOT complete before <c>Drain()</c>; new tests cover entry on another
/// context (today's behaviour), overlapping bare + UI refreshes (also the <c>_pendingForce</c> re-run)
/// and a handler exception delivered as posted work (contained, no throw from <c>Drain()</c>); every
/// wait is bounded. Because xUnit's own context is gone after the first await, a test continuation
/// that refreshes again is "entered with no context" too, so the older tests no longer await the task
/// and THEN drain: <c>SettleAgentUsageAsync</c> pumps the construction context until the task completes.
/// Hand-written fakes as in the R-014/R-015/R-017 view-model tests: no real ccusage, no agy, no
/// network; F21 — ONE local day captured per test (fixture and assertions read the same value; the
/// view model's own clock read is the documented midnight residual). Every test builds the view model
/// on its own QueuedSyncContext, so the settings sync and the posted handler work settle on an
/// explicit <c>Drain</c> (D16: <c>AgentUsageTask</c> completes in the handler callback's finally,
/// i.e. only once that context has run it). Oracles (spec test
/// oracles): today + yesterday antigravity rows — Apr 30
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

    /// <summary>D13 fixture — a report row the AGY composer rejects. Yesterday's antigravity model
    /// carries a negative <see cref="ModelUsage.CostUsd"/>: <c>ModelUsage</c> itself allows it and
    /// every other tab call in <c>OnAgentUsageUpdated</c> formats negative money without validating
    /// (<c>ChartFormat.Money</c> prints "$-0.25"; All agents/Claude/OpenAI/OpenCode/Overview rows and
    /// the AGY tiles never test the sign). The negative cost DELIBERATELY BYPASSES the ccusage parser:
    /// SPEC_R014 rejects a negative parsed cost (the row would be skipped), so a real ccusage report
    /// never carries it — but the view model must not rely on parser validation, so this is the
    /// controlled injection seam for "the AGY composition throws" (D16). <c>AgentHistoryBuilder.Build</c> validates each day's
    /// priced total through <c>UsageHistoryBucket.RequireFiniteNonNegative</c>, so
    /// <c>AgyUsageComposer.Compose</c> throws inside <c>UpdateAgyTab</c> — the controlled AGY
    /// composition failure D13 orders against OpenCode/Overview. Today's antigravity row is clean so
    /// <c>SummaryFor</c> (which runs before the three Update* calls) cannot throw; the opencode row
    /// gives the OpenCode tab a real 8,000-token history day to prove it was fully updated.</summary>
    private static AgentUsageReport ReportWithRejectedAgyRow(DateOnly today) => new(
        daily: [
            new DailyAgentUsage(today,
            [
                Agent("antigravity", Model(ModelA, 20_000, 1_000, 17_000, 1.00m)),
                Agent("opencode", Model("deepseek", 8_000, 400, 6_800, 0.40m)),
            ]),
            new DailyAgentUsage(today.AddDays(-1),
            [
                Agent("antigravity", Model(ModelA, 10_000, 500, 8_500, -0.25m)),
            ]),
        ],
        monthly: [],
        sessions: [],
        unpricedModels: [],
        pricing: AgentUsagePricing.OnlineRequested,
        retrievedAt: FixedUtc,
        skippedRows: 0);

    /// <summary>D16 fixture — a clean report with one OpenCode row of the given token total on the
    /// local day (and today's clean antigravity row), so the OpenCode chart's last bucket shows the
    /// total and a second report with another total makes a later handler run observable.</summary>
    private static AgentUsageReport ReportWithOpenCode(DateOnly today, long openCodeTokens) => new(
        daily: [
            new DailyAgentUsage(today,
            [
                Agent("antigravity", Model(ModelA, 20_000, 1_000, 17_000, 1.00m)),
                Agent("opencode", Model("deepseek", openCodeTokens, 400, 0, 0.40m)),
            ]),
        ],
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

    /// <summary>D14(6): the R-017 F13/F24 construction — install the test's own queued context so the
    /// view model captures it as its UI context, then restore the test thread's context. The settings
    /// sync (and, once the D9 fix posts the handler work) is settled by an explicit <c>Drain</c>
    /// instead of racing xUnit's pump with repeated <c>Task.Yield</c>. Extracted from the
    /// D1_SettingsDriven test, which used it inline in round 2.</summary>
    private static MainWindowViewModel ConstructViewModel(
        QueuedSyncContext context,
        LatestRefreshCoordinator coordinator,
        SettingsSession session,
        CachedAgentUsageSource cache)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return new MainWindowViewModel(coordinator, session, agentUsage: cache);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Upper bound for every wait on the D16 tests: generous for a loaded machine, finite so a
    /// regression fails with a timeout instead of hanging the run.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>D16: the view model, its own QueuedSyncContext (captured at construction) and the
    /// fakes behind it, built the way every test here builds them (default settings unless given).
    /// <paramref name="read"/> receives the test's one local day and is the ccusage read.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly SettingsSession _session;

        private Harness(
            MainWindowViewModel vm,
            QueuedSyncContext context,
            FakeAgyClient agy,
            FakeAgentUsageSource source,
            SettingsSession session)
        {
            Vm = vm;
            Context = context;
            Agy = agy;
            Source = source;
            _session = session;
        }

        public MainWindowViewModel Vm { get; }

        public QueuedSyncContext Context { get; }

        public FakeAgyClient Agy { get; }

        public FakeAgentUsageSource Source { get; }

        public static Harness Create(Func<DateOnly, Task<AgentUsageReport>> read, AppSettings? settings = null)
        {
            settings ??= new AppSettings();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var agy = new FakeAgyClient(AgySnapshot());
            var source = new FakeAgentUsageSource(_ => read(today));
            var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));
            var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
            [
                new ProviderClientRegistration("claude", new FixedClient("claude")),
                new ProviderClientRegistration("openai", new FixedClient("openai")),
                new ProviderClientRegistration("agy", agy),
            ]));
            var store = new BlockingSettingsStore(settings);
            store.Release();
            var session = new SettingsSession(store, settings);
            var context = new QueuedSyncContext();
            var vm = ConstructViewModel(context, coordinator, session, cache);
            return new Harness(vm, context, agy, source, session);
        }

        public void Dispose() => _session.Dispose();
    }

    /// <summary>Polls a condition with a finite bound (no fixed sleep): the awaited effect is a post to
    /// a queue or a task completion that may arrive from another thread a moment later.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(5);
        }
    }

    /// <summary>Starts <paramref name="action"/> on a pool thread with <paramref name="entry"/> installed
    /// as its <see cref="SynchronizationContext"/> (the refresh is "entered on" that context) and pumps
    /// the context like a UI thread — Current == the context while a callback runs, ONE callback per
    /// pass — until the action's task completes: the view model's own <c>ConfigureAwait(true)</c>
    /// continuation is posted there. Pumping one callback at a time leaves a handler that the refresher
    /// posts to the same context queued instead of running it in the same pass.</summary>
    private static async Task RunOnContextAsync(QueuedSyncContext entry, Func<Task> action)
    {
        var task = Task.Run(() =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(entry);
            try
            {
                return action();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
        var deadline = DateTime.UtcNow + Bound;
        while (!task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out pumping the entry context.");
            await Task.Run(entry.DrainOneInstalled);
            await Task.WhenAny(task, Task.Delay(5));
        }

        await task;
    }

    /// <summary>D16: waits for <c>AgentUsageTask</c> while pumping the view model's construction
    /// context, bounded. A refresh that starts with no <see cref="SynchronizationContext.Current"/> (any
    /// test continuation after an await — xUnit's context is gone after the first hop) now has its
    /// handler POSTED to that context, so awaiting the task without draining it would hang. Replaces the
    /// round-3 "await the task, then Drain()" pair; an exception from a drained callback propagates.</summary>
    private static async Task SettleAgentUsageAsync(MainWindowViewModel vm, QueuedSyncContext context)
    {
        var task = vm.AgentUsageTask!;
        var deadline = DateTime.UtcNow + Bound;
        while (!task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for AgentUsageTask while pumping the construction context.");
            context.Drain();
            await Task.WhenAny(task, Task.Delay(5));
        }

        await task;
        context.Drain();
    }

    /// <summary>The D9 "before the drain" picture: <c>OnAgentUsageUpdated</c> has not touched any tab —
    /// OpenCode/Overview only ever change there, and AGY keeps the path (a) raw push without
    /// history, composed tiles or the composed note.</summary>
    private static void AssertNoHandlerTabChanges(MainWindowViewModel vm)
    {
        Assert.False(vm.OpenCodeTab.HasSnapshot);
        Assert.False(vm.OverviewTab.HasSnapshot);
        Assert.False(vm.AgyTab.HasHistory);
        Assert.Equal(["Requests today"], vm.AgyTab.StatTiles.Select(t => t.Label));
        Assert.Equal(ClientNote, vm.AgyTab.ValueNote);
    }

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
        var context = new QueuedSyncContext();
        var vm = ConstructViewModel(context, coordinator, session, cache);

        await vm.RefreshAsync();          // (a): the agy snapshot arrives first — no report stored yet
        await SettleAgentUsageAsync(vm, context); // D16: pumps the construction context until the task completes

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
        var context = new QueuedSyncContext();
        var vm = ConstructViewModel(context, coordinator, session, cache);

        await vm.RefreshAsync();
        await SettleAgentUsageAsync(vm, context); // D16: pumps the construction context until the task completes

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
        context.Drain();                  // D14(6): runs the posted settings sync explicitly

        // D6 re-detect pin: the quota snapshot CHANGES while ccusage stays cached.
        agy.Next = AgySnapshot(sessionPercent: 55.0, account: "new-account@google.example");
        await vm.RefreshAsync("agy");     // path (a) composes inline in the refresh; no refresher run

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
        var context = new QueuedSyncContext();
        var vm = ConstructViewModel(context, coordinator, session, cache);

        await vm.RefreshAsync();
        await SettleAgentUsageAsync(vm, context); // D16: pumps the construction context until the task completes
        Assert.True(vm.AgyTab.HasHistory); // composed while enabled
        var buckets = vm.AgyTab.ChartBuckets;
        var tiles = vm.AgyTab.StatTiles;

        await session.UpdateAsync(s => s with
        {
            Providers = new Dictionary<string, ProviderPreference> { ["agy"] = new ProviderPreference(Enabled: false) },
        });
        context.Drain();

        await vm.RefreshAsync(); // full refresh without agy: the stored raw snapshot must be dropped
        await SettleAgentUsageAsync(vm, context); // D16: pumps the construction context until the task completes

        Assert.False(vm.LatestSnapshots.ContainsKey("agy")); // "drop the raw agy snapshot" (D6)
        Assert.Same(buckets, vm.AgyTab.ChartBuckets); // no UpdateFromSnapshot push happened since
        Assert.Same(tiles, vm.AgyTab.StatTiles);
        Assert.Equal(1, agy.CallCount); // the disabled client was skipped in the full refresh

        // Even a fresh ccusage read (cache expired) must not compose for the disabled tab.
        clock.UtcNow += TimeSpan.FromMinutes(16);
        await vm.RefreshAsync();
        await SettleAgentUsageAsync(vm, context); // D16: pumps the construction context until the task completes

        Assert.Equal(2, source.ReadCount); // a real second read happened — and still nothing was pushed
        Assert.Same(buckets, vm.AgyTab.ChartBuckets);
        Assert.Same(tiles, vm.AgyTab.StatTiles);
    }

    // ── D6: re-enable without a new AGY result — the dropped snapshot must not revive ──

    [Fact]
    public async Task D6_ReEnablingAgyWithoutNewResult_CachedReportRecomposeDoesNotReviveStaleQuota()
    {
        // Inspector B finding 2: the test above passes when `_latestRawAgySnapshot = null` is removed,
        // because the enabled-guard inside UpdateAgyTab masks the retained snapshot. Here the drop
        // itself is pinned: disable AGY (a refresh drops the raw snapshot), re-enable it WITHOUT any
        // new agy result, then recompose from the cached report (the D1 settings-driven rebuild) —
        // with the snapshot really dropped there is nothing to compose, so the tab must not move.
        // Removing the drop leaves the stale snapshot in place: the recompose pushes it and this
        // test fails.
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
        var context = new QueuedSyncContext();
        var vm = ConstructViewModel(context, coordinator, session, cache);

        await vm.RefreshAsync();
        await SettleAgentUsageAsync(vm, context); // D16: pumps the construction context until the task completes

        // Composed while enabled; remember the exact pushed lists.
        Assert.True(vm.AgyTab.HasHistory);
        AssertComposedTiles(vm.AgyTab);
        var buckets = vm.AgyTab.ChartBuckets;
        var tiles = vm.AgyTab.StatTiles;
        Assert.Equal(14, buckets.Count);

        // Disable AGY and refresh: the raw snapshot is dropped, nothing new is pushed (guard).
        await session.UpdateAsync(s => s with
        {
            Providers = new Dictionary<string, ProviderPreference> { ["agy"] = new ProviderPreference(Enabled: false) },
        });
        context.Drain();
        await vm.RefreshAsync();
        await SettleAgentUsageAsync(vm, context); // D16: pumps the construction context until the task completes
        Assert.Equal(1, agy.CallCount); // the disabled client is skipped — no new agy result
        Assert.Same(buckets, vm.AgyTab.ChartBuckets);

        // Re-enable through settings only — no re-detect, no refresh, so no NEW agy result exists.
        await session.UpdateAsync(s => s with
        {
            Providers = new Dictionary<string, ProviderPreference> { ["agy"] = new ProviderPreference(true) },
        });
        context.Drain();
        Assert.Same(buckets, vm.AgyTab.ChartBuckets); // re-enabling alone must not compose

        // Trigger a cached-report update: the settings-driven range sync runs the same rebuild the
        // D1 test pins (RebuildUsageTabsForViewChange → UpdateAgyTab from the CACHED report).
        await session.UpdateAsync(s => s with { ChartRangeDays = 30 });
        context.Drain();

        Assert.Equal(30, vm.SelectedRange.Value); // the settings sync really ran…
        Assert.Equal(30, vm.OpenCodeTab.ChartBuckets.Count); // …and rebuilt the OpenCode tab…
        Assert.Same(buckets, vm.AgyTab.ChartBuckets); // …but must NOT recompose the AGY tab:
        Assert.Same(tiles, vm.AgyTab.StatTiles);      // the stale quota snapshot was dropped (D6).
        Assert.Equal(14, vm.AgyTab.ChartBuckets.Count); // frozen at the pre-disable 14-day push.
        Assert.Equal(1, agy.CallCount); // re-enable + recompose never called the agy client
        Assert.Equal(1, source.ReadCount); // nor did it start a ccusage read
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
        var context = new QueuedSyncContext();
        var vm = ConstructViewModel(context, coordinator, session, cache);

        await vm.RefreshAsync();
        await SettleAgentUsageAsync(vm, context); // D16: pumps the construction context until the task completes

        // "agy unchanged (no stats, no history — today's look)" + D9 "otherwise the client's note
        // is kept".
        Assert.True(vm.AgyTab.HasSnapshot);
        Assert.False(vm.AgyTab.HasHistory);
        Assert.Equal(["Requests today"], vm.AgyTab.StatTiles.Select(t => t.Label));
        Assert.Equal(ClientNote, vm.AgyTab.ValueNote);
        Assert.False(vm.AgyTab.ShowSupplementary); // no antigravity ccusage line either (C8/C10)
    }

    // ── D9/D16: an update arriving OFF the view model's thread is delivered on its captured context ──

    [Fact]
    public async Task D9_OffThreadRefresh_TabChangesWaitUntilTheCapturedContextDrains()
    {
        // D16 (replaces D15). AgentUsageRefresher captures the SYNCHRONIZATION CONTEXT OF WHOEVER
        // CALLS RefreshAsync, so a refresh started from a Task.Run thread (Current == null) used to
        // run the Updated handler — OnAgentUsageUpdated — inline on the thread pool. D16: the view
        // model passes the context it captured at construction to the refresher for that call, so the
        // refresher posts the handler there and completes AgentUsageTask only after the tab work ran.
        // Hence BEFORE Drain(): the task is not complete and no handler tab change exists; AFTER
        // Drain(): the task completes (bounded wait, so a regression fails instead of hanging) and
        // every tab is updated. Fails against the pre-D16 code (inline handler) and must not be weakened.
        using var h = Harness.Create(today => Task.FromResult(Report(today)));

        // Start the refresh from a thread-pool thread: Current is null there.
        await Task.Run(() => h.Vm.RefreshAsync());
        var task = h.Vm.AgentUsageTask!;
        await WaitUntilAsync(() => task.IsCompleted || h.Context.PendingCount > 0,
            "the handler to be posted to the captured context (or, in the regression, to run inline)");

        // The refresh itself (not OnAgentUsageUpdated) already applied path (a): the RAW agy
        // snapshot pushed while no report was stored. That is allowed to be visible before the drain.
        Assert.True(h.Vm.AgyTab.HasSnapshot);

        // D16: the task means "tab work done" — it must NOT complete before the captured context runs it.
        Assert.False(task.IsCompleted);
        Assert.Equal(1, h.Context.PendingCount); // exactly the posted handler
        AssertNoHandlerTabChanges(h.Vm);

        h.Context.Drain(); // the posted OnAgentUsageUpdated work runs exactly here
        await task.WaitAsync(Bound);

        // …and now every D9-pinned tab change exists, composed once with the one cached report.
        Assert.True(h.Vm.OpenCodeTab.HasSnapshot);
        Assert.True(h.Vm.OpenCodeTab.HasHistory);
        Assert.True(h.Vm.OverviewTab.HasSnapshot);
        Assert.True(h.Vm.AgyTab.HasHistory);
        AssertComposedTiles(h.Vm.AgyTab);
        Assert.Equal(ComposedNote, h.Vm.AgyTab.ValueNote);
        Assert.Equal(1, h.Agy.CallCount); // the off-thread refresh never re-called agy for the compose
        Assert.Equal(1, h.Source.ReadCount); // and never started an extra ccusage read
    }

    // ── D16: a refresh entered on another context keeps today's behaviour ───────────────

    [Fact]
    public async Task D16_RefreshEnteredOnAnotherContext_HandlerIsPostedThereNotRunInline()
    {
        // D16: "A refresh entered on any context keeps today's behaviour" — only a refresh entered
        // with NO context gets the view model's construction context. So a refresh entered on a second
        // controlled context (a different "dispatcher" than the one the view model was built on, and
        // installed while its callbacks run) has its handler delivered through THAT context by the
        // refresher: posted (not run inline off the posting thread), the task incomplete and the tabs
        // unchanged until that context drains, and nothing posted to the construction context. This
        // also guards the pre-existing tests, which enter on xUnit's own context while the view model
        // was built on a queued one. (It kills a gate that runs inline for any non-null context —
        // Inspector B round 2 finding 2 — and one that forces the construction context onto it.)
        using var h = Harness.Create(today => Task.FromResult(Report(today)));
        var other = new QueuedSyncContext();
        var baseline = h.Context.PostCount;

        await RunOnContextAsync(other, () => h.Vm.RefreshAsync());
        var task = h.Vm.AgentUsageTask!;
        await WaitUntilAsync(
            () => task.IsCompleted || other.PendingCount > 0 || h.Context.PendingCount > 0,
            "the handler to be posted to some context (or, in the regression, to run inline)");

        Assert.False(task.IsCompleted);
        Assert.Equal(1, other.PendingCount);          // delivered through the context it was entered on…
        Assert.Equal(baseline, h.Context.PostCount);  // …and nothing went to the construction context
        AssertNoHandlerTabChanges(h.Vm);

        other.Drain();
        await task.WaitAsync(Bound);

        Assert.True(h.Vm.OpenCodeTab.HasSnapshot);
        Assert.True(h.Vm.OverviewTab.HasSnapshot);
        Assert.True(h.Vm.AgyTab.HasHistory);
        AssertComposedTiles(h.Vm.AgyTab);
        Assert.Equal(1, h.Source.ReadCount);
    }

    // ── D16: overlapping refreshes must not turn a pool-thread update inline ────────────

    [Fact]
    public async Task D16_OverlappingBareAndConstructionContextRefreshes_PoolThreadUpdateStillWaitsForTheDrain()
    {
        // A bare (Current == null) refresh whose ccusage read is blocked on a gate; while it is in
        // flight a second refresh is entered on the construction context (its refresher call returns
        // immediately because a read is already running). Then the gate is released from a pool
        // thread, so the first refresher's continuation — and, before D16, the handler — runs inline
        // there. D16: no per-call context may leak from the second refresh into the first (a shared
        // "entry context" field would be overwritten by it); the first call's handler must still be
        // posted to the construction context and wait for its drain.
        var gate = new TaskCompletionSource<AgentUsageReport>(); // continuations run inline on the releasing thread
        using var h = Harness.Create(_ => gate.Task);
        var today = DateOnly.FromDateTime(DateTime.Now);

        await Task.Run(() => h.Vm.RefreshAsync()); // bare; its agent-usage read is now blocked
        var first = h.Vm.AgentUsageTask!;
        Assert.False(first.IsCompleted);
        Assert.Equal(1, h.Source.ReadCount);

        await RunOnContextAsync(h.Context, () => h.Vm.RefreshAsync()); // entered on the UI context
        var second = h.Vm.AgentUsageTask!;
        await second.WaitAsync(Bound); // returned at once: a read is already running
        Assert.Equal(1, h.Source.ReadCount);
        Assert.Equal(0, h.Context.PendingCount);

        await Task.Run(() => gate.SetResult(ReportWithOpenCode(today, 8_000))); // release from a pool thread
        await WaitUntilAsync(() => first.IsCompleted || h.Context.PendingCount > 0,
            "the first refresh's handler to be posted (or, in the regression, to run inline)");

        Assert.False(first.IsCompleted);
        Assert.Equal(1, h.Context.PendingCount);
        AssertNoHandlerTabChanges(h.Vm);

        h.Context.Drain();
        await first.WaitAsync(Bound);

        Assert.True(h.Vm.OpenCodeTab.HasSnapshot);
        Assert.Equal(8_000.0, h.Vm.OpenCodeTab.ChartBuckets[^1].Total);
        Assert.True(h.Vm.OverviewTab.HasSnapshot);
        Assert.True(h.Vm.AgyTab.HasHistory);
        Assert.Equal(1, h.Source.ReadCount);
    }

    [Fact]
    public async Task D16_PendingForceRerun_AlsoDeliversOnTheConstructionContext()
    {
        // D16: "_pendingForce re-runs must deliver on the view model's context too". A bare refresh
        // blocks on its first read; a manual refresh with All agents selected (entered on the
        // construction context) marks a forced re-run. The gate is released from a pool thread: the
        // first handler is posted (and its drain unblocks the re-run), the forced second read then
        // produces a DIFFERENT OpenCode total whose handler must also be posted — not run inline on
        // the pool thread — so the tab still shows the first total until the second drain.
        var gate = new TaskCompletionSource<AgentUsageReport>();
        var reads = 0;
        var today = DateOnly.FromDateTime(DateTime.Now);
        using var h = Harness.Create(
            _ => Interlocked.Increment(ref reads) == 1
                ? gate.Task
                : Task.FromResult(ReportWithOpenCode(today, 9_000)),
            new AppSettings { ActiveProvider = "all-agents" });
        Assert.True(h.Vm.IsAllAgentsSelected);

        await Task.Run(() => h.Vm.RefreshAsync()); // bare, not manual → not forced; read 1 blocks
        var first = h.Vm.AgentUsageTask!;
        Assert.Equal(1, h.Source.ReadCount);

        await RunOnContextAsync(h.Context, () => h.Vm.RefreshAsync(manual: true));
        await h.Vm.AgentUsageTask!.WaitAsync(Bound); // returns at once; only sets _pendingForce

        await Task.Run(() => gate.SetResult(ReportWithOpenCode(today, 8_000)));
        await WaitUntilAsync(() => first.IsCompleted || h.Context.PendingCount > 0,
            "the first handler to be posted (or, in the regression, to run inline)");
        Assert.False(first.IsCompleted);
        Assert.Equal(1, h.Context.PendingCount);
        AssertNoHandlerTabChanges(h.Vm);

        h.Context.Drain(); // first handler: unblocks the refresher, which re-runs forced
        Assert.Equal(8_000.0, h.Vm.OpenCodeTab.ChartBuckets[^1].Total);
        await WaitUntilAsync(() => first.IsCompleted || h.Context.PendingCount > 0,
            "the forced re-run's handler to be posted (or, in the regression, to run inline)");

        Assert.False(first.IsCompleted); // the task covers the re-run's tab work too
        Assert.Equal(1, h.Context.PendingCount);
        Assert.Equal(8_000.0, h.Vm.OpenCodeTab.ChartBuckets[^1].Total); // the 9,000 total is not applied yet
        Assert.Equal(2, h.Source.ReadCount); // the forced re-read really ran

        h.Context.Drain();
        await first.WaitAsync(Bound);
        Assert.Equal(9_000.0, h.Vm.OpenCodeTab.ChartBuckets[^1].Total);
    }

    // ── D16: an exception thrown by the posted handler stays inside the refresher ───────

    [Fact]
    public async Task D16_PostedHandlerException_IsContained_TaskCompletesWithoutFault_DrainDoesNotThrow()
    {
        // The D13 composition failure (see ReportWithRejectedAgyRow) delivered through a bare
        // refresh, so the handler runs as POSTED work. The refresher's own catch around the handler
        // must contain the AGY throw: Drain() must not throw (an exception escaping a posted callback
        // would be unhandled on a real UI thread), AgentUsageTask completes without fault, and
        // OpenCode/Overview — updated before the AGY tab (D13) — are done. The "before the drain"
        // asserts make sure the failure really happens inside the posted work (otherwise an inline
        // handler would also pass the rest vacuously).
        using var h = Harness.Create(today => Task.FromResult(ReportWithRejectedAgyRow(today)));

        await Task.Run(() => h.Vm.RefreshAsync());
        var task = h.Vm.AgentUsageTask!;
        await WaitUntilAsync(() => task.IsCompleted || h.Context.PendingCount > 0,
            "the handler to be posted (or, in the regression, to run inline)");
        Assert.False(task.IsCompleted);
        Assert.False(h.Vm.OpenCodeTab.HasSnapshot);
        Assert.False(h.Vm.OverviewTab.HasSnapshot);

        var drainException = Record.Exception(h.Context.Drain);
        Assert.Null(drainException); // contained by the refresher's handler catch

        await task.WaitAsync(Bound);
        Assert.Equal(TaskStatus.RanToCompletion, task.Status); // not faulted, not cancelled

        Assert.True(h.Vm.OpenCodeTab.HasSnapshot);
        Assert.True(h.Vm.OpenCodeTab.HasHistory);
        Assert.Equal(8_000.0, h.Vm.OpenCodeTab.ChartBuckets[^1].Total);
        Assert.True(h.Vm.OverviewTab.HasSnapshot);

        // Only the AGY recompose failed: path (a)'s raw push remains, the client's note is kept.
        Assert.True(h.Vm.AgyTab.HasSnapshot);
        Assert.False(h.Vm.AgyTab.HasHistory);
        Assert.Equal(ClientNote, h.Vm.AgyTab.ValueNote);
        Assert.Equal(1, h.Source.ReadCount);
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
        var context = new QueuedSyncContext();
        var vm = ConstructViewModel(context, coordinator, session, cache);

        await vm.RefreshAsync();
        await SettleAgentUsageAsync(vm, context); // D16: pumps the construction context until the task completes
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
        // D11: the agy call count is deliberately NOT asserted here — the setter's PRE-EXISTING
        // follow-up chain (ApplyViewChangeAsync → RefreshAsync) runs its fakes to completion
        // synchronously and calls the agy client again; what D1 pins is the synchronous recompose
        // above and that no ccusage read happens (source.ReadCount stays 1).

        vm.SelectedMetric = MetricOption(vm, UsageMetric.OutputTokens);
        Assert.Equal(UsageMetric.OutputTokens, vm.AgyTab.ChartMetric);
        Assert.Equal(new Dictionary<string, double> { [ModelA] = 1_000, [ModelX] = 500 },
            vm.AgyTab.ChartBuckets[^1].PerModel); // today's output values
        Assert.Equal(new Dictionary<string, double> { [ModelA] = 500 },
            vm.AgyTab.ChartBuckets[^2].PerModel); // yesterday's
        Assert.Equal(30, vm.AgyTab.ChartBuckets.Count);

        // D4: the four tiles are fixed sums, independent of the chart metric.
        AssertComposedTiles(vm.AgyTab);
        Assert.Equal(1, source.ReadCount); // D11: agy.CallCount is not pinned after a setter — see above.
    }

    [Fact]
    public async Task D1_SettingsDrivenRangeChange_RecomposesAgyTab_NoReads()
    {
        // D1: "Test direct selection and settings-driven changes" — the external settings path
        // (OnSettingsChanged sync) recomposes AGY through the same rebuild, without touching
        // ccusage or the agy client (no view-change refresh runs on the syncing path).
        // D11: the sync is pumped the way the existing R-017 wiring tests do (F13/F24): the view
        // model is constructed under a controllable QueuedSyncContext it captures as its UI
        // context, the Changed raise posts there, and the test drains it before asserting —
        // instead of racing xUnit's own pump with Task.Yield.
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

        var context = new QueuedSyncContext();
        var vm = ConstructViewModel(context, coordinator, session, cache);

        await vm.RefreshAsync();
        await SettleAgentUsageAsync(vm, context); // D16: pumps the construction context until the task completes
        Assert.Equal(14, vm.AgyTab.ChartBuckets.Count);

        await session.UpdateAsync(s => s with { ChartRangeDays = 30 });
        context.Drain(); // F13/F24: applies OnSettingsChangedCore — the sync path rebuilds here

        Assert.Equal(30, vm.SelectedRange.Value);
        Assert.Equal(30, vm.AgyTab.ChartBuckets.Count); // recomposed through the settings path
        Assert.Equal(30, vm.OpenCodeTab.ChartBuckets.Count);
        Assert.Equal(30_000.0, vm.AgyTab.ChartBuckets[^1].Total); // values from the cached report
        AssertComposedTiles(vm.AgyTab);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, agy.CallCount); // the _syncingFromSettings path never fires the view-change refresh
    }

    // ── D13: an AGY composition failure must not stop OpenCode/Overview ─────────────────

    [Fact]
    public async Task D13_AgyCompositionFailure_OpenCodeAndOverviewAlreadyUpdated()
    {
        // Inspector B finding 3: these tests pass when UpdateAgyTab is moved before
        // UpdateOpenCodeTab/UpdateOverviewTab in MainWindowViewModel.OnAgentUsageUpdated (the
        // refresher swallows every handler exception, so a throw there is silent). The report here
        // carries a daily cost AgyUsageComposer rejects (see ReportWithRejectedAgyRow): the handler
        // MUST still have updated the OpenCode and Overview tabs — they run before UpdateAgyTab —
        // while the AGY tab keeps its pre-handler state (the refresher swallows the throw).
        var agy = new FakeAgyClient(AgySnapshot());
        var source = new FakeAgentUsageSource(_ => Task.FromResult(ReportWithRejectedAgyRow(DateOnly.FromDateTime(DateTime.Now))));
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
        var context = new QueuedSyncContext();
        var vm = ConstructViewModel(context, coordinator, session, cache);

        await vm.RefreshAsync();

        // Settle the handler work. D16: the AGY throw happens inside the refresher's own
        // try/catch whether the handler runs inline or as posted work, so NO exception may escape
        // Drain() (the dedicated posted-handler test pins the posted path itself).
        Assert.Null(await Record.ExceptionAsync(() => SettleAgentUsageAsync(vm, context)));

        // D13 order: OpenCode and Overview were already updated by this handler run…
        Assert.True(vm.OpenCodeTab.HasSnapshot);
        Assert.True(vm.OpenCodeTab.HasHistory);
        Assert.Equal(14, vm.OpenCodeTab.ChartBuckets.Count);
        Assert.Equal(8_000.0, vm.OpenCodeTab.ChartBuckets[^1].Total); // today's deepseek row
        Assert.True(vm.OverviewTab.HasSnapshot); // UpdateOverviewTab ran to its push

        // …and only the AGY recompose failed: path (a)'s raw push (report was null then) is still
        // the tab's state — no history, no added tiles, the client's note kept.
        Assert.True(vm.AgyTab.HasSnapshot);
        Assert.False(vm.AgyTab.HasHistory);
        Assert.Equal(["Requests today"], vm.AgyTab.StatTiles.Select(t => t.Label));
        Assert.Equal(ClientNote, vm.AgyTab.ValueNote);

        Assert.Equal(1, agy.CallCount); // the failure never retries the agy client…
        Assert.Equal(1, source.ReadCount); // …and never starts an extra ccusage read
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

    /// <summary>R-017 F24 pattern (copied from MainWindowViewModelOpenCodeChartWiringTests): a
    /// <see cref="SynchronizationContext"/> that queues every Post and runs nothing until the test
    /// calls <see cref="Drain"/>. Installed around the view model's construction (D14(6): via
    /// <see cref="ConstructViewModel"/> in every test here) so the settings sync — and, once the D9
    /// fix posts the handler work, that too — is posted to a queue the test controls; xUnit's own
    /// AsyncTestSyncContext would pump it at any await and hide the ordering.</summary>
    private sealed class QueuedSyncContext : SynchronizationContext
    {
        // A posted body that re-posted itself (the loop F23's design forbids) would otherwise hang
        // the test run; fail fast instead — the real body drains after the first callback.
        private const int DrainGuard = 64;

        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private int _postCount;

        /// <summary>Total Post calls received, from any thread.</summary>
        public int PostCount => Volatile.Read(ref _postCount);

        /// <summary>Callbacks queued and not yet drained.</summary>
        public int PendingCount => _queue.Count;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            _queue.Enqueue((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException(
                "R-017 F23: OnSettingsChanged must Post to the captured context, never Send.");

        /// <summary>D16: runs at most ONE queued callback with this context installed as
        /// <see cref="SynchronizationContext.Current"/> (as a real UI thread would), restored afterwards.</summary>
        public void DrainOneInstalled()
        {
            if (!_queue.TryDequeue(out var item))
            {
                return;
            }

            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                item.Callback(item.State);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        /// <summary>
        /// Runs the queued callbacks FIFO on the calling thread WITHOUT installing this context —
        /// the posted body must not depend on Current (the sync body never re-tests it).
        /// </summary>
        public void Drain()
        {
            for (var run = 0; _queue.TryDequeue(out var item); run++)
            {
                if (run >= DrainGuard)
                {
                    throw new InvalidOperationException(
                        "The settings sync re-posted to its own context: the queue never drains (R-017 F23 re-post loop).");
                }

                item.Callback(item.State);
            }
        }
    }
}
