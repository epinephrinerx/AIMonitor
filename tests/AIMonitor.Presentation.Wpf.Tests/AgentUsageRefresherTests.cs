using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// Tests for <see cref="AgentUsageRefresher"/> written from SPEC R-014 decision C3, the C11
/// "since" and "Sessions" oracles, C12 (a forced read waits, never cancels) and C13
/// (Updated runs on the caller's SynchronizationContext; a throwing handler never wedges it).
/// Built over a hand-written fake <see cref="IAgentUsageSource"/> wrapped in the real
/// <see cref="CachedAgentUsageSource"/>. No sleeps: all waits are TaskCompletionSource-gated.
/// </summary>
public sealed class AgentUsageRefresherTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 11, 9, 13, 0, 0, TimeSpan.Zero);

    private static AgentUsageReport MakeReport(AgentUsagePricing pricing) =>
        new(
            [new DailyAgentUsage(new DateOnly(2026, 11, 9), [
                new AgentUsage("claude", new TokenCounts(100, 0, 0, 0, 100),
                    [new ModelUsage("model-0", new TokenCounts(100, 0, 0, 0, 100), 0.10m)])])],
            [],
            [],
            [],
            pricing,
            FixedUtc,
            0);

    private static readonly AgentUsageReport ReportOnline = MakeReport(AgentUsagePricing.OnlineRequested);
    private static readonly AgentUsageReport ReportOffline = MakeReport(AgentUsagePricing.Offline);

    private static (CachedAgentUsageSource cache, FakeAgentUsageSource source) CacheWith(
        FakeAgentUsageSource source)
    {
        var cache = new CachedAgentUsageSource(source, new FakeClock(FixedUtc), TimeSpan.FromMinutes(15));
        return (cache, source);
    }

    // ── since — decision C11 oracles (spec P3.3 / C3) ────────────────────────

    [Fact]
    public async Task RefreshAsync_WhenUtcNowIs2026_11_09_ReadsSince2026_09_01()
    {
        // Decision C11 oracle: since: today 2026-11-09 → 2026-09-01.
        var source = new FakeAgentUsageSource(_ => Task.FromResult(ReportOnline));
        var (cache, _) = CacheWith(source);
        var refresher = new AgentUsageRefresher(cache, () => FixedUtc, TimeZoneInfo.Utc);

        await refresher.RefreshAsync(force: false);

        Assert.Equal(new DateOnly(2026, 9, 1), Assert.Single(source.Sincs));
    }

    [Fact]
    public async Task RefreshAsync_WhenUtcNowIs2026_12_01_ReadsSince2026_10_01()
    {
        // Decision C11 oracle: 2026-12-01 → 2026-10-01.
        var utcNow = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var source = new FakeAgentUsageSource(_ => Task.FromResult(ReportOnline));
        var (cache, _) = CacheWith(source);
        var refresher = new AgentUsageRefresher(cache, () => utcNow, TimeZoneInfo.Utc);

        await refresher.RefreshAsync(force: false);

        Assert.Equal(new DateOnly(2026, 10, 1), Assert.Single(source.Sincs));
    }

    [Fact]
    public async Task RefreshAsync_WhenUtcNowIs2026_01_15_ReadsSince2025_11_01()
    {
        // Decision C11 oracle: 2026-01-15 → 2025-11-01 (year roll-back).
        var utcNow = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var source = new FakeAgentUsageSource(_ => Task.FromResult(ReportOnline));
        var (cache, _) = CacheWith(source);
        var refresher = new AgentUsageRefresher(cache, () => utcNow, TimeZoneInfo.Utc);

        await refresher.RefreshAsync(force: false);

        Assert.Equal(new DateOnly(2025, 11, 1), Assert.Single(source.Sincs));
    }

    [Fact]
    public async Task RefreshAsync_UsesLocalDayOfZone_NotTheUtcDay_ForSince()
    {
        // Decision C3: since = first day of the month two months before the local today.
        // 2026-12-01T00:00Z is still 2026-11-30 at UTC-8 → since 2026-09-01, not 2026-10-01.
        var zoneMinus8 = TimeZoneInfo.CreateCustomTimeZone(
            "test-utc-minus-8", TimeSpan.FromHours(-8), "UTC-08 (test)", "UTC-08 (test)");
        var utcNow = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var source = new FakeAgentUsageSource(_ => Task.FromResult(ReportOnline));
        var (cache, _) = CacheWith(source);
        var refresher = new AgentUsageRefresher(cache, () => utcNow, zoneMinus8);

        await refresher.RefreshAsync(force: false);

        Assert.Equal(new DateOnly(2026, 9, 1), Assert.Single(source.Sincs));
    }

    // ── One read in flight — decision C3 ─────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_WhenCalledWhileReadInFlight_NonForcedCallDoesNothing()
    {
        // Decision C3: at most one read in flight; a call while one runs does nothing
        // (no second Updated, not even a cached one).
        var firstGate = new TaskCompletionSource<AgentUsageReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeAgentUsageSource(n =>
        {
            if (n == 1)
            {
                readStarted.TrySetResult();
                return firstGate.Task;
            }

            return Task.FromResult(ReportOffline);
        });
        var (cache, _) = CacheWith(source);
        var refresher = new AgentUsageRefresher(cache, () => FixedUtc, TimeZoneInfo.Utc);
        var updates = new List<(AgentUsageReport? Report, AgentUsageUnavailableException? Error)>();
        refresher.Updated += (report, error) => { lock (updates) updates.Add((report, error)); };

        var first = refresher.RefreshAsync(force: false);
        await readStarted.Task;

        var second = refresher.RefreshAsync(force: false);

        firstGate.SetResult(ReportOnline);
        await first;
        await second; // completes either way — "does nothing" must not hang the caller

        Assert.Equal(1, source.ReadCount);
        Assert.Single(updates);
        Assert.Same(ReportOnline, updates[0].Report);
        Assert.Null(updates[0].Error);
    }

    [Fact]
    public async Task RefreshAsync_WhenForcedWhileReadInFlight_RunsOnceMoreAfter_AndNeverCancelsTheFirst()
    {
        // Decision C3: force then runs once more after the in-flight read.
        // Decision C12: a forced read waits for an in-flight read (P1 single gate);
        // it does not cancel it — both reads complete and both reports are delivered,
        // and the source is never entered twice concurrently.
        var firstGate = new TaskCompletionSource<AgentUsageReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeAgentUsageSource(n =>
        {
            if (n == 1)
            {
                readStarted.TrySetResult();
                return firstGate.Task;
            }

            return Task.FromResult(ReportOffline);
        });
        var (cache, _) = CacheWith(source);
        var refresher = new AgentUsageRefresher(cache, () => FixedUtc, TimeZoneInfo.Utc);
        var reports = new List<AgentUsageReport?>();
        refresher.Updated += (report, _) => { lock (reports) reports.Add(report); };

        var first = refresher.RefreshAsync(force: false);
        await readStarted.Task;

        var forced = refresher.RefreshAsync(force: true);

        firstGate.SetResult(ReportOnline);
        await first;
        await forced;

        Assert.Equal(2, source.ReadCount);
        Assert.Equal(1, source.MaxConcurrentReads); // sequential, not cancelled or overlapped
        Assert.Equal(2, reports.Count);
        Assert.Same(ReportOnline, reports[0]);
        Assert.Same(ReportOffline, reports[1]);
    }

    // ── Nothing escapes — decision C3 ────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_WhenReadFails_CompletesAndRaisesUpdatedWithError()
    {
        // Decision C3: it catches every exception — nothing escapes. P3.3: exceptions other
        // than cancellation are already captured in LastError → delivered through Updated.
        var failure = new AgentUsageUnavailableException(
            AgentUsageErrorKind.NotInstalled,
            "ccusage was not found. Install it with `npm i -g ccusage`, then restart AIMonitor.");
        var source = new FakeAgentUsageSource(_ => Task.FromException<AgentUsageReport>(failure));
        var (cache, _) = CacheWith(source);
        var refresher = new AgentUsageRefresher(cache, () => FixedUtc, TimeZoneInfo.Utc);
        var updates = new List<(AgentUsageReport? Report, AgentUsageUnavailableException? Error)>();
        refresher.Updated += (report, error) => updates.Add((report, error));

        await refresher.RefreshAsync(force: false);

        var update = Assert.Single(updates);
        Assert.Null(update.Report);
        Assert.NotNull(update.Error);
        Assert.Equal(AgentUsageErrorKind.NotInstalled, update.Error!.Kind);
    }

    [Fact]
    public async Task RefreshAsync_WhenSourceFailsAfterSuccess_RaisesLastGoodAndErrorTogether()
    {
        // Decision C3 (Updated carries LastGood + LastError from the cache) + E4: a failed
        // read keeps the previous good report available → the tab can show "data from HH:mm".
        var failSecond = false;
        var source = new FakeAgentUsageSource(n =>
            n == 1 || !failSecond
                ? Task.FromResult(ReportOnline)
                : Task.FromException<AgentUsageReport>(
                    new AgentUsageUnavailableException(AgentUsageErrorKind.Timeout, "ccusage did not finish in time.")));
        var (cache, _) = CacheWith(source);
        var refresher = new AgentUsageRefresher(cache, () => FixedUtc, TimeZoneInfo.Utc);
        var updates = new List<(AgentUsageReport? Report, AgentUsageUnavailableException? Error)>();
        refresher.Updated += (report, error) => updates.Add((report, error));

        await refresher.RefreshAsync(force: false);
        failSecond = true;
        await refresher.RefreshAsync(force: true);

        Assert.Equal(2, updates.Count);
        Assert.Same(ReportOnline, updates[0].Report);
        Assert.Null(updates[0].Error);
        Assert.Same(ReportOnline, updates[1].Report); // cache keeps the previous good report
        Assert.NotNull(updates[1].Error);
        Assert.Equal(AgentUsageErrorKind.Timeout, updates[1].Error!.Kind);
    }

    [Fact]
    public async Task RefreshAsync_WhenReadThrowsCancellation_CompletesWithoutException_AndRecovers()
    {
        // Decision C3: it catches every exception (cancellation included) — nothing escapes.
        var source = new FakeAgentUsageSource(n =>
            n == 1
                ? Task.FromException<AgentUsageReport>(new OperationCanceledException())
                : Task.FromResult(ReportOnline));
        var (cache, _) = CacheWith(source);
        var refresher = new AgentUsageRefresher(cache, () => FixedUtc, TimeZoneInfo.Utc);

        // Must not throw out of the await (it would crash the caller that only started the task).
        await refresher.RefreshAsync(force: false);

        // Not wedged: the next refresh still starts a read (nothing holds the in-flight flag).
        await refresher.RefreshAsync(force: false);
        Assert.Equal(2, source.ReadCount);
    }

    // ── Caller context — decision C13 ────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_RaisesUpdated_OnTheCallersSynchronizationContext()
    {
        // Decision C13: capture SynchronizationContext.Current at RefreshAsync entry; the
        // Updated event must run there (Reviewer1 MAJOR 1, Reviewer2 M-1).
        // The read genuinely suspends and completes on another thread, so an implementation
        // that ignored the captured context would raise bare on a pool thread with
        // SynchronizationContext.Current == null and go red here.
        var release = new TaskCompletionSource<AgentUsageReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeAgentUsageSource(n => n == 1 ? release.Task : Task.FromResult(ReportOnline));
        var (cache, _) = CacheWith(source);
        var refresher = new AgentUsageRefresher(cache, () => FixedUtc, TimeZoneInfo.Utc);

        await Task.Run(async () =>
        {
            var context = new InlineExecutionSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                SynchronizationContext? observed = null;
                refresher.Updated += (_, _) => observed = SynchronizationContext.Current;

                var refresh = refresher.RefreshAsync(force: false);

                // Complete the read from a different thread, with no context installed there.
                _ = Task.Run(() => release.SetResult(ReportOnline));

                await refresh;

                Assert.NotNull(observed);
                Assert.Same(context, observed);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        });
    }

    [Fact]
    public async Task RefreshAsync_WhenUpdatedHandlerThrows_RecoversWithoutLeakingAndWithoutWedging()
    {
        // Decision C13: handler exceptions are caught too, and the in-flight flag is reset
        // in finally, so the refresher always recovers (MAJOR 3).
        var source = new FakeAgentUsageSource(n =>
            Task.FromResult(n == 1 ? ReportOnline : ReportOffline));
        var (cache, _) = CacheWith(source);
        var refresher = new AgentUsageRefresher(cache, () => FixedUtc, TimeZoneInfo.Utc);
        var raised = 0;
        refresher.Updated += (_, _) =>
        {
            raised++;
            throw new InvalidOperationException("handler boom");
        };

        // First read: the throwing handler must not surface out of RefreshAsync.
        await refresher.RefreshAsync(force: false);
        Assert.Equal(1, raised);

        // Not wedged: a forced second read still runs and still raises Updated.
        await refresher.RefreshAsync(force: true);
        Assert.Equal(2, raised);
        Assert.Equal(2, source.ReadCount);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>Hand-written fake <see cref="IAgentUsageSource"/> (TESTING.md §4/§5).</summary>
    private sealed class FakeAgentUsageSource : IAgentUsageSource
    {
        private readonly Func<int, Task<AgentUsageReport>> _handler;
        private readonly object _sync = new();
        private readonly List<DateOnly?> _sincs = [];
        private int _readCount;
        private int _active;
        private int _maxActive;

        public FakeAgentUsageSource(Func<int, Task<AgentUsageReport>> handler) => _handler = handler;

        public int ReadCount => Volatile.Read(ref _readCount);

        public int MaxConcurrentReads
        {
            get { lock (_sync) return _maxActive; }
        }

        public IReadOnlyList<DateOnly?> Sincs
        {
            get { lock (_sync) return _sincs.ToList(); }
        }

        async Task<AgentUsageReport> IAgentUsageSource.ReadAsync(DateOnly? since, CancellationToken ct)
        {
            var number = Interlocked.Increment(ref _readCount);
            lock (_sync)
            {
                _sincs.Add(since);
                _active++;
                _maxActive = Math.Max(_maxActive, _active);
            }

            try
            {
                return await _handler(number);
            }
            finally
            {
                lock (_sync) _active--;
            }
        }
    }

    /// <summary>
    /// SynchronizationContext that executes posted/sent callbacks immediately but makes
    /// itself the ambient context for the duration, like a real dispatcher thread. Callbacks
    /// can therefore prove they ran through this context even when the completion was
    /// delivered by another thread.
    /// </summary>
    private sealed class InlineExecutionSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => Invoke(d, state);

        public override void Send(SendOrPostCallback d, object? state) => Invoke(d, state);

        private void Invoke(SendOrPostCallback d, object? state)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                d(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
