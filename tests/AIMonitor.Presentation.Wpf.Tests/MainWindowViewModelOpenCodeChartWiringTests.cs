using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;
using System.Collections.Concurrent;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-017 G1.3 wiring — <c>MainWindowViewModel</c>, written from Docs/SPEC_R017.md only (§5.6).
/// The view model passes <c>ChartRangeDays</c> and the selected metric value into the (widened)
/// <c>OpenCodeSnapshotBuilder.Build</c>, so after the first ccusage <c>Updated</c> the OpenCode tab
/// carries the per-model chart plus its costs; F1 (Inspector A decision) pins the refresh paths:
/// "the OpenCode rebuild for range/metric is called from the SelectedRange and SelectedMetric
/// setters only (when the value really changes), not from ApplyViewChangeAsync. Changing the
/// refresh interval does not rebuild the OpenCode tab. ... E29's equal-budgets short-cut stays for
/// budget changes only." F9 pins the observable outcome: after Updated, then SelectedRange = 30 and
/// SelectedMetric = "Output tokens", the tab's ChartBuckets.Count == 30 with output-token values
/// and NO extra ccusage read — plus G1.3 keeps E10/E23 unchanged (a rebuild without a stored good
/// report pushes nothing). Later review decisions: F11 — a rebuild must not move the tab's
/// LastUpdated (FetchedAt is the report time) — and F13 — an external settings change rebuilds
/// the OpenCode tab through the _syncingFromSettings path, only when the value really changes.
/// The setter rebuild is asserted SYNCHRONOUSLY with no intervening await:
/// the fire-and-forget ApplyViewChangeAsync chain can only resume on the test's synchronization
/// context, which runs at the next await. Hand-written fakes as in the R-014/R-015 test files
/// (no real ccusage, no network); FakeClock freezes the cache so any later background refresh
/// serves from it and the "no extra read" assertion is timing-proof. The G1 implementation has
/// since merged into this branch (041433a), so these tests compile and run against it. Inspector B
/// decision F21: every test here captures ONE local day (CreateWithReport) — fixture and
/// assertions read the same value, and MainWindowViewModel's own clock read is the residual,
/// reported midnight-boundary risk (no production seam, F22).
/// F23/F24 (WO1 8afa278): the view model captures SynchronizationContext.Current in its
/// constructor and POSTS the settings sync when SettingsSession.Changed fires off that context.
/// xUnit's own AsyncTestSyncContext pumps its queue at every await and hides that, so every
/// view model here is constructed under a controllable QueuedSyncContext (installed before
/// construction, restored in finally) that only runs callbacks when the test drains it; the
/// tests that raise Changed through the session drain before asserting, and the F23 tests pin
/// posted-versus-inline and the one-post-per-raise contract directly.
/// </summary>
public sealed class MainWindowViewModelOpenCodeChartWiringTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 11, 10, 7, 0, 0, TimeSpan.Zero);

    private const string Kimi = "kimi-k2.7-code";
    private const string Deepseek = "deepseek-v4.1-flash";
    private const string ClaudeOpus = "claude-opus-5-5";

    private static MainWindowViewModel CreateVm(
        CachedAgentUsageSource? agentUsage,
        AppSettings initial,
        out BlockingSettingsStore store,
        out SettingsSession session,
        out QueuedSyncContext context)
    {
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        store = new BlockingSettingsStore(initial);
        store.Release(); // tab selection and view-change saves write through
        session = new SettingsSession(store, initial);

        // F24: install the controllable context BEFORE constructing so the view model captures it
        // as its UI context (xUnit's AsyncTestSyncContext would pump its queue at every await and
        // hide F23's posting), and restore it straight after so the test's own awaits stay on the
        // xUnit context. Off-context raises then sit in the queue until the test drains it.
        var previous = SynchronizationContext.Current;
        context = new QueuedSyncContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return new MainWindowViewModel(coordinator, session, agentUsage: agentUsage);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static MainWindowViewModel CreateVm(CachedAgentUsageSource? agentUsage, AppSettings initial, out BlockingSettingsStore store)
    {
        return CreateVm(agentUsage, initial, out store, out _, out _);
    }

    /// <summary>One opencode day row: kimi total 2000 / output 100 / $1.25, deepseek total 300 /
    /// output 30 / unpriced — plus a claude row on the SAME day that must stay invisible to the
    /// OpenCode chart (G1.2 source rows) and its null cost must not flag the day (H4's 11-09 rule).</summary>
    private static AgentUsageReport Report(DateOnly today) => new(
        daily: [new DailyAgentUsage(today,
            [
                new AgentUsage("opencode", new TokenCounts(2170, 130, 0, 0, 2300),
                [
                    new ModelUsage(Kimi, new TokenCounts(1900, 100, 0, 0, 2000), 1.25m),
                    new ModelUsage(Deepseek, new TokenCounts(270, 30, 0, 0, 300), null),
                ]),
                new AgentUsage("claude", new TokenCounts(5000, 0, 0, 0, 5000),
                    [new ModelUsage(ClaudeOpus, new TokenCounts(5000, 0, 0, 0, 5000), null)]),
            ])],
        monthly: [new MonthlyAgentUsage(today.Year, today.Month,
            [new AgentUsage("opencode", new TokenCounts(9999, 0, 0, 0, 9999),
                [new ModelUsage(Kimi, new TokenCounts(9999, 0, 0, 0, 9999), 25.00m)])])],
        sessions: [],
        unpricedModels: [],
        pricing: AgentUsagePricing.OnlineRequested,
        retrievedAt: FixedUtc,
        skippedRows: 0);

    /// <summary>F21 (Inspector B minor 4): ONE local day captured per test — the fixture date and
    /// every assertion read this value, never a second <c>DateTime.Now</c>. MainWindowViewModel
    /// takes no clock/TimeProvider (F21 forbids a production change, F22 forbids seams), so the
    /// residual risk is only the VM's own <c>DateTimeOffset.UtcNow</c> rolling past midnight
    /// between the capture below and its internal read — reported, not pinned.
    /// F24: the returned context is the one the view model captured at construction; tests that
    /// raise <c>Changed</c> through the session must drain it before asserting.</summary>
    private static (MainWindowViewModel Vm, FakeAgentUsageSource Source, SettingsSession Session, DateOnly Today, QueuedSyncContext Context) CreateWithReport()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var source = new FakeAgentUsageSource(_ => Task.FromResult(Report(today)));
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));
        var vm = CreateVm(cache, new AppSettings(), out _, out var session, out var context);
        return (vm, source, session, today, context);
    }

    private static DashboardOption<int> RangeOption(MainWindowViewModel vm, int days) =>
        vm.RangeOptions.First(o => o.Value == days);

    private static DashboardOption<string> MetricOption(MainWindowViewModel vm, string metric) =>
        vm.MetricOptions.First(o => o.Value == metric);

    // ── G1.3: the existing Updated trigger now carries the history through ─────

    [Fact]
    public async Task Updated_G13_OpenCodeTabShowsPerModelChartWithCosts()
    {
        var (vm, source, _, localToday, _) = CreateWithReport();

        await vm.RefreshAsync();
        await vm.AgentUsageTask!; // E2's Updated path: Build gets the live ChartRangeDays/metric

        var tab = vm.OpenCodeTab;
        Assert.True(tab.HasHistory);
        Assert.Equal(14, tab.ChartBuckets.Count); // AppSettings default ChartRangeDays 14, G1.3
        Assert.Equal(
            Enumerable.Range(0, 14).Select(i => localToday.AddDays(i - 13)),
            tab.ChartBuckets.Select(b => b.Day)); // oldest first, window ends at today
        Assert.Equal(2000.0, tab.ChartBuckets[^1].PerModel[Kimi]); // "Total tokens" (default metric)
        Assert.Equal(300.0, tab.ChartBuckets[^1].PerModel[Deepseek]);
        Assert.DoesNotContain(ClaudeOpus, tab.ChartBuckets[^1].PerModel.Keys); // claude row ignored
        Assert.Equal([Kimi, Deepseek], tab.ChartSeries);
        Assert.Equal(UsageMetric.TotalTokens, tab.ChartMetric);
        Assert.Equal("Usage per day · last 14 days", tab.ChartTitle);
        Assert.Equal([Kimi, Deepseek], tab.ModelRows.Select(r => r.Label)); // the "By model" card (G1.3)
        Assert.Empty(tab.ProjectRows); // "By project" stays hidden: ByProject is empty (G1.3)
        Assert.False(tab.HasProjectRows);

        Assert.Equal(14, tab.ChartCosts.Count); // G1.3/F7: costs mapped onto the tab
        var last = tab.ChartCosts[^1];
        Assert.Equal(localToday, last.Day);
        Assert.Equal(1.25, last.PricedUsd, 10);
        Assert.True(last.HasUnpricedModels); // deepseek CostUsd null
        Assert.All(tab.ChartCosts.Take(13), c => Assert.Equal((0.0, false), (c.PricedUsd, c.HasUnpricedModels)));
        Assert.Equal(1, source.ReadCount); // one read served gauge, rows and chart — no extra ccusage run
    }

    // ── F9: the range/metric setters rebuild the OpenCode tab, without reading ─

    [Fact]
    public async Task Updated_F9_RangeThenMetricChange_RebuildTab_NoExtraCcusageRead()
    {
        var (vm, source, _, localToday, _) = CreateWithReport();
        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        Assert.Equal(14, vm.OpenCodeTab.ChartBuckets.Count);

        // F1: the rebuild runs IN the setter — assert synchronously, with no await in between, so a
        // rebuild deferred to ApplyViewChangeAsync's refresh would not pass this line.
        vm.SelectedRange = RangeOption(vm, 30);
        Assert.Equal(30, vm.OpenCodeTab.ChartBuckets.Count); // F9
        Assert.Equal(30, vm.OpenCodeTab.ChartCosts.Count);   // costs keep the same width, aligned

        vm.SelectedMetric = MetricOption(vm, UsageMetric.OutputTokens);

        // F9: "its values are output tokens" — today's bucket and the by-model totals switch
        // (kimi 100, deepseek 30 — the H2/H1 pattern), not the totals 2000/300.
        var today = vm.OpenCodeTab.ChartBuckets[^1];
        Assert.Equal(localToday, today.Day);
        Assert.Equal(100.0, today.PerModel[Kimi]);
        Assert.Equal(30.0, today.PerModel[Deepseek]);
        Assert.Equal(30, vm.OpenCodeTab.ChartBuckets.Count);
        Assert.Equal(UsageMetric.OutputTokens, vm.OpenCodeTab.ChartMetric);
        Assert.Equal([Kimi, Deepseek], vm.OpenCodeTab.ChartSeries);
        Assert.Equal(100.0, vm.OpenCodeTab.ModelRows[0].Value); // By model rebuilt with the new metric
        Assert.Equal(30.0, vm.OpenCodeTab.ModelRows[1].Value);
        Assert.Equal("Usage per day · last 30 days", vm.OpenCodeTab.ChartTitle);

        // F9: "with no extra ccusage read" — the clock is frozen so the background refresh chains
        // these setters fire serve from the cache; ReadCount can only ever still be 1.
        Assert.Equal(1, source.ReadCount);
    }

    // ── F1: only the two setters rebuild; the same value and the interval do not ──

    [Fact]
    public async Task F1_IntervalChange_DoesNotRebuildTheOpenCodeTab()
    {
        // F1: "Changing the refresh interval does not rebuild the OpenCode tab." Observable: the
        // tab's chart/gauge instances are untouched even after the settings change has fully
        // settled through the session (a rebuild wired from OnSettingsChanged would replace them).
        var (vm, source, session, _, context) = CreateWithReport();
        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        var buckets = vm.OpenCodeTab.ChartBuckets;
        var costs = vm.OpenCodeTab.ChartCosts;
        var meters = vm.OpenCodeTab.Meters;

        vm.SelectedInterval = vm.IntervalOptions.First(o => o.Value == 0); // "Manual only"
        await session.FlushAsync(); // the save has fully settled
        context.Drain(); // F23/F24: the Changed round-trip is POSTED to the captured context — run it

        Assert.Equal("Manual only", vm.SelectedInterval.Label); // the interval itself did change
        Assert.Same(buckets, vm.OpenCodeTab.ChartBuckets);
        Assert.Same(costs, vm.OpenCodeTab.ChartCosts);
        Assert.Same(meters, vm.OpenCodeTab.Meters);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task F1_SameValueAssigned_DoesNotRebuild()
    {
        // F1: "when the value really changes" — re-selecting the current range (e.g. a combobox
        // re-binding the same option) is not a view change at all.
        var (vm, _, _, _, _) = CreateWithReport();
        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        var buckets = vm.OpenCodeTab.ChartBuckets;

        vm.SelectedRange = vm.SelectedRange; // same DashboardOption instance

        Assert.Same(buckets, vm.OpenCodeTab.ChartBuckets);
    }

    [Fact]
    public async Task F1_E29_EqualBudgetsStillShortCircuit_ButARangeChangeRebuildsAnyway()
    {
        // F1: "E29's equal-budgets short-cut stays for budget changes only."
        var (vm, source, _, _, _) = CreateWithReport();
        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        var buckets = vm.OpenCodeTab.ChartBuckets;

        vm.ApplyAgentBudgets(new Dictionary<string, decimal>()); // equal to the stored empty map
        Assert.Same(buckets, vm.OpenCodeTab.ChartBuckets);       // E29: no rebuild

        vm.SelectedRange = RangeOption(vm, 30);                   // budgets still equal — rebuild anyway
        Assert.NotSame(buckets, vm.OpenCodeTab.ChartBuckets);     // F1: not gated by BudgetsEqual
        Assert.Equal(30, vm.OpenCodeTab.ChartBuckets.Count);
        Assert.Equal(1, source.ReadCount);                        // neither path runs ccusage
    }

    // ── F1/G1.3: the rebuild is "as in E2" — the stored pair decides what is pushed ──

    [Fact]
    public void F1_SettersBeforeFirstUpdated_PushNothing()
    {
        // E2 semantics for the new triggers: no ccusage result stored yet (no source at all) →
        // Build returns null → nothing is pushed; the tab keeps "Checking..." (E23 unchanged).
        var vm = CreateVm(agentUsage: null, new AppSettings(), out _);

        vm.SelectedRange = RangeOption(vm, 30);
        vm.SelectedMetric = MetricOption(vm, UsageMetric.OutputTokens);

        Assert.False(vm.OpenCodeTab.HasSnapshot);
        Assert.Equal("Checking...", vm.OpenCodeTab.Status);
        Assert.Empty(vm.OpenCodeTab.ChartBuckets);
        Assert.Empty(vm.OpenCodeTab.ChartCosts);
    }

    [Fact]
    public async Task F1_RangeChangeWithOnlyAnErrorStored_RebuildsErrorSnapshot_StillNoHistory()
    {
        // G1.3: "Error-only and 'no result' states (E10/E23) are unchanged (no history)" — this
        // must hold for the NEW trigger too: a range change rebuilds from the stored pair and the
        // error snapshot arrives without chart or costs.
        var failure = new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "ccusage is not installed");
        var source = new FakeAgentUsageSource(_ => Task.FromException<AgentUsageReport>(failure));
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));
        var vm = CreateVm(cache, new AppSettings(), out _);

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        Assert.True(vm.OpenCodeTab.HasError); // E10
        Assert.False(vm.OpenCodeTab.HasHistory);

        vm.SelectedRange = RangeOption(vm, 30);

        Assert.True(vm.OpenCodeTab.HasError);
        Assert.Empty(vm.OpenCodeTab.ChartBuckets);
        Assert.False(vm.OpenCodeTab.HasHistory);
        Assert.Empty(vm.OpenCodeTab.ChartCosts);
        // ReadCount is deliberately not pinned here: with no good report the cache never fills, so
        // the range setter's PRE-EXISTING ApplyViewChangeAsync → RefreshAsync path re-reads the
        // (failing) source in the background. That is the old R-015 behaviour — R-017 only promises
        // that the rebuild itself pushes an unchanged error snapshot without a history, which the
        // final-state asserts above pin (they also hold after any later background Updated).
    }

    // ── F11: rebuilds must not move the tab's LastUpdated ──────────────────────

    [Fact]
    public async Task F11_RangeAndBudgetRebuilds_LeaveLastUpdatedAtTheReportTime()
    {
        // F11 (R1 m2): "a range/metric/budget rebuild does not move LastUpdated" — the snapshot's
        // FetchedAt is lastGood.RetrievedAt (FixedUtc), not the wall clock at rebuild time.
        var (vm, source, _, _, _) = CreateWithReport();
        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        Assert.Equal(FixedUtc, vm.OpenCodeTab.LastUpdated);

        vm.SelectedRange = RangeOption(vm, 30);
        vm.SelectedMetric = MetricOption(vm, UsageMetric.OutputTokens);
        vm.ApplyAgentBudgets(new Dictionary<string, decimal> { ["opencode"] = 50m });

        Assert.Equal(FixedUtc, vm.OpenCodeTab.LastUpdated);
        Assert.Equal(30, vm.OpenCodeTab.ChartBuckets.Count); // the rebuilds themselves still happened
        Assert.Equal(1, source.ReadCount);
    }

    // ── F13: an external settings change rebuilds too — only when the value changes ──

    [Fact]
    public async Task F13_ExternalRangeChange_RebuildsOpenCodeTab_NoCcusageRead()
    {
        // F13 (R1 m4): "the OpenCode rebuild also runs when range/metric change through
        // OnSettingsChanged (the _syncingFromSettings path), still only when the value really
        // changes." The change enters through the session, not the combobox setter. F23/F24: that
        // raise fires on a thread-pool thread, so the sync is posted to the captured context —
        // drain the queue before asserting (before F24 this test raced xUnit's own pump).
        var (vm, source, session, _, context) = CreateWithReport();
        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        Assert.Equal(14, vm.OpenCodeTab.ChartBuckets.Count);

        await session.UpdateAsync(s => s with { ChartRangeDays = 30, ChartMetric = UsageMetric.OutputTokens });
        context.Drain();

        Assert.Equal(30, vm.OpenCodeTab.ChartBuckets.Count);
        Assert.Equal(100.0, vm.OpenCodeTab.ChartBuckets[^1].PerModel[Kimi]); // values switched with the metric
        Assert.Equal(UsageMetric.OutputTokens, vm.OpenCodeTab.ChartMetric);
        Assert.Equal(1, source.ReadCount); // the rebuild reads only the stored pair (E2 pattern)
    }

    [Fact]
    public async Task F13_UnrelatedSettingsChange_DoesNotRebuild()
    {
        // F13's "only when the value really changes" holds on the sync path too: OnSettingsChanged
        // re-assigns every selector, and a same-valued re-assignment must not rebuild the tab.
        // F24: drain so the sync actually runs — otherwise the assert would pass on a starved queue.
        var (vm, _, session, _, context) = CreateWithReport();
        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        var buckets = vm.OpenCodeTab.ChartBuckets;

        await session.UpdateAsync(s => s with { Theme = "dark" });
        context.Drain();

        Assert.Same(buckets, vm.OpenCodeTab.ChartBuckets);
    }

    // ── F23/F24: the settings sync is posted to the context captured at construction ──

    [Fact]
    public async Task F23_OffContextRaise_IsPosted_AppliedOnDrain_ExactlyOnePost()
    {
        // F23 (R2 major) + F24: Changed fires on a thread-pool thread (the blocking store's save
        // continuation is RunContinuationsAsynchronously), so OnSettingsChanged must POST to the
        // context the view model captured — nothing applies before Drain, SelectedRange and the
        // OpenCode chart apply on Drain, and one raise produces exactly one Post (no re-post loop:
        // the posted body never re-tests the context).
        var (vm, source, session, _, context) = CreateWithReport();
        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        context.Drain(); // settle any round-trips of the first refresh: clean baseline below
        Assert.Equal(14, vm.SelectedRange.Value);
        Assert.Equal(14, vm.OpenCodeTab.ChartBuckets.Count);

        var postsBefore = context.PostCount;
        await session.UpdateAsync(s => s with { ChartRangeDays = 30, ChartMetric = UsageMetric.OutputTokens });

        // posted, not run: exactly one callback sits in the queue with no visible effect
        Assert.Equal(postsBefore + 1, context.PostCount);
        Assert.Equal(1, context.PendingCount);
        Assert.Equal(14, vm.SelectedRange.Value);
        Assert.Equal(14, vm.OpenCodeTab.ChartBuckets.Count);

        context.Drain();

        Assert.Equal(0, context.PendingCount);
        Assert.Equal(postsBefore + 1, context.PostCount); // the body did not re-post itself
        Assert.Equal(30, vm.SelectedRange.Value);
        Assert.Equal(30, vm.OpenCodeTab.ChartBuckets.Count);
        Assert.Equal(100.0, vm.OpenCodeTab.ChartBuckets[^1].PerModel[Kimi]); // values switched with the metric
        Assert.Equal(UsageMetric.OutputTokens, vm.OpenCodeTab.ChartMetric);
        Assert.Equal(1, source.ReadCount); // rebuild from the stored pair only (E2 pattern)
    }

    [Fact]
    public async Task F23_OnContextRaise_RunsInline_NoPost()
    {
        // F23: "runs inline when already on [the captured] it". The blocking store can only raise
        // off-context (its save continuation always hops to the thread pool), so this test uses a
        // store whose SaveAsync completes synchronously: UpdateAsync then raises Changed ON the
        // test thread while the very context the view model captured is installed — the sync must
        // run there and then, without a single Post.
        var today = DateOnly.FromDateTime(DateTime.Now); // F21: ONE local day for the whole test
        var source = new FakeAgentUsageSource(_ => Task.FromResult(Report(today)));
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc));
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var session = new SettingsSession(new InlineSettingsStore(), new AppSettings());
        var context = new QueuedSyncContext();
        var previous = SynchronizationContext.Current;

        MainWindowViewModel vm;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await vm.RefreshAsync();
        await vm.AgentUsageTask!;
        Assert.Equal(14, vm.OpenCodeTab.ChartBuckets.Count);

        var postsBefore = context.PostCount;
        Task update;
        SynchronizationContext.SetSynchronizationContext(context); // the captured context is current
        try
        {
            update = session.UpdateAsync(s => s with { ChartRangeDays = 30, ChartMetric = UsageMetric.OutputTokens });
            Assert.True(update.IsCompleted); // the synchronous store keeps the whole raise on this thread
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await update;

        Assert.Equal(postsBefore, context.PostCount); // ran inline — nothing was posted
        Assert.Equal(30, vm.SelectedRange.Value);
        Assert.Equal(30, vm.OpenCodeTab.ChartBuckets.Count);
        Assert.Equal(100.0, vm.OpenCodeTab.ChartBuckets[^1].PerModel[Kimi]);
        Assert.Equal(UsageMetric.OutputTokens, vm.OpenCodeTab.ChartMetric);
        Assert.Equal(1, source.ReadCount);
    }

    // ── Fakes (hand-written, same as the R-014/R-015 test files) ───────────────

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

    /// <summary>F24 helper: a <see cref="SynchronizationContext"/> that queues every Post and runs
    /// nothing until the test calls <see cref="Drain"/>. xUnit's own AsyncTestSyncContext pumps its
    /// queue at every await, which hid F23's "posted, not inline" behaviour (the failing F13 test
    /// raced that pump). Installed around the view model's construction and restored in the
    /// caller's finally — the VM captures this context as its UI context while the test thread
    /// keeps using the xUnit context for its own awaits, so nothing here ever blocks.</summary>
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
            throw new InvalidOperationException("F23: OnSettingsChanged must Post to the captured context, never Send.");

        /// <summary>
        /// Runs the queued callbacks FIFO on the calling thread WITHOUT installing this context —
        /// the posted body must not depend on Current (WO1 kept the context check out of
        /// OnSettingsChangedCore for exactly this reason).
        /// </summary>
        public void Drain()
        {
            for (var run = 0; _queue.TryDequeue(out var item); run++)
            {
                if (run >= DrainGuard)
                {
                    throw new InvalidOperationException(
                        "The settings sync re-posted to its own context: the queue never drains (F23 re-post loop).");
                }

                item.Callback(item.State);
            }
        }
    }

    /// <summary>Store whose SaveAsync completes synchronously, so SettingsSession raises Changed on
    /// the CALLER's thread — the only way to get an on-context raise for F23's inline branch (the
    /// blocking store's already-completed continuation task always hops to the thread pool).</summary>
    private sealed class InlineSettingsStore : ISettingsStore
    {
        private AppSettings _current = new();

        public bool Exists => true;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _current, settings);
            return Task.CompletedTask;
        }
    }
}
