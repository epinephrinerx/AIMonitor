using System.Threading;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;
using AIMonitor.TestSupport;

namespace AIMonitor.Application.Tests.AgentUsage;

/// <summary>
/// Tests for <see cref="CachedAgentUsageSource"/> per SPEC R-014 §P1.2 and decisions A1, A7 and E4.
/// Every timing expectation uses the A7 oracle: a successful read at 10:00:00 with
/// <c>MinInterval</c> 15 min serves a call at 10:14:59 from cache, while a call at 10:15:00
/// re-reads ("age equal to MinInterval is not 'younger'").
/// Seam per decision M2: <c>CachedAgentUsageSource(IAgentUsageSource inner, IClock clock,
/// TimeSpan? minInterval = null)</c> with null meaning 15 min, so the shared helper omits the argument.
/// </summary>
public class CachedAgentUsageSourceTests
{
    private static readonly DateTimeOffset TenOClock = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly SinceDay = new(2026, 10, 1);

    /// Reports are distinguished by <see cref="AgentUsageReport.RetrievedAt"/>, which the fakes take
    /// from the fake clock at call time, so a cache hit (older RetrievedAt) is observable.
    private static AgentUsageReport ReportAt(DateTimeOffset retrievedAt) =>
        new(
            Array.Empty<DailyAgentUsage>(),
            Array.Empty<MonthlyAgentUsage>(),
            Array.Empty<SessionAgentUsage>(),
            Array.Empty<string>(),
            AgentUsagePricing.OnlineRequested,
            retrievedAt,
            0);

    private sealed class FakeSource : IAgentUsageSource
    {
        private readonly FakeClock _clock;
        private readonly object _sync = new();
        private readonly List<DateOnly?> _sinceValues = new();

        public FakeSource(FakeClock clock) => _clock = clock;

        /// When the 1-based call number equals this value, the fake fails instead of returning a report.
        public int FailOnCall { get; set; } = -1;
        public AgentUsageErrorKind FailKind { get; set; }

        public int Calls
        {
            get { lock (_sync) return _sinceValues.Count; }
        }

        public IReadOnlyList<DateOnly?> SinceValues
        {
            get { lock (_sync) return _sinceValues.ToArray(); }
        }

        public Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct)
        {
            int call;
            lock (_sync)
            {
                _sinceValues.Add(since);
                call = _sinceValues.Count;
            }

            if (call == FailOnCall)
                // Decision M1: "AgentUsageUnavailableException(AgentUsageErrorKind kind, string message)"
                // — kind first, as SPEC P1.2 says.
                return Task.FromException<AgentUsageReport>(new AgentUsageUnavailableException(FailKind, "synthetic failure"));

            return Task.FromResult(ReportAt(_clock.UtcNow));
        }
    }

    private sealed class GatedSource : IAgentUsageSource
    {
        private readonly FakeClock _clock;
        private readonly TaskCompletionSource<AgentUsageReport> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<int, bool> _succeedImmediately;
        private int _calls;

        public GatedSource(FakeClock clock, Func<int, bool>? succeedImmediately = null)
        {
            _clock = clock;
            _succeedImmediately = succeedImmediately ?? (_ => false);
        }

        public int Calls => Volatile.Read(ref _calls);

        public Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (_succeedImmediately(call))
                return Task.FromResult(ReportAt(_clock.UtcNow));

            // Cooperates with caller cancellation like a real read would (SPEC P1.2 last bullet).
            return _gate.Task.WaitAsync(ct);
        }

        public void Release(AgentUsageReport report) => _gate.TrySetResult(report);
    }

    /// <summary>
    /// A cache as SPEC P1.2's A7 oracle describes it. Decision M2 makes the interval an optional
    /// <c>TimeSpan?</c> whose null value means 15 min, so the argument is omitted here and every
    /// interval expectation below uses the A7 points (10:14:59 cached, 10:15:00 re-read).
    /// </summary>
    private static (CachedAgentUsageSource Cache, FakeSource Inner, FakeClock Clock) A7OracleCache()
    {
        var clock = new FakeClock(TenOClock);
        var inner = new FakeSource(clock);
        return (new CachedAgentUsageSource(inner, clock), inner, clock); // M2: minInterval omitted → 15 min
    }

    [Fact]
    public void LastGood_And_LastError_AreNull_BeforeAnyRead()
    {
        var (cache, _, _) = A7OracleCache();

        Assert.Null(cache.LastGood);
        Assert.Null(cache.LastError);
    }

    [Fact]
    public async Task FirstRead_ReadsThroughInner_AndBecomesLastGood()
    {
        var (cache, inner, _) = A7OracleCache();

        var report = await cache.ReadAsync(null, default);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(TenOClock, report.RetrievedAt);
        Assert.NotNull(cache.LastGood);
        Assert.Equal(TenOClock, cache.LastGood!.RetrievedAt);
        Assert.Null(cache.LastError); // Decision E4: "a successful read … clears LastError" (nothing to clear yet)
    }

    [Fact]
    public async Task SecondRead_WithSameSince_YoungerThanMinInterval_ReturnsCached()
    {
        // Decision A7: successful read at 10:00:00, MinInterval 15 min → a call at 10:14:59 returns cached.
        var (cache, inner, clock) = A7OracleCache();
        await cache.ReadAsync(null, default);

        clock.UtcNow = TenOClock.AddSeconds(14 * 60 + 59); // 10:14:59
        var cached = await cache.ReadAsync(null, default);

        Assert.Equal(1, inner.Calls); // the inner source was not called again
        Assert.Equal(TenOClock, cached.RetrievedAt); // the 10:00:00 report, not a fresh 10:14:59 one
    }

    [Fact]
    public async Task SecondRead_WithSameSince_AtExactMinInterval_ReReads()
    {
        // Decision A7: a call at 10:15:00 re-reads (age equal to MinInterval is not "younger").
        var (cache, inner, clock) = A7OracleCache();
        await cache.ReadAsync(null, default);

        clock.UtcNow = TenOClock.AddMinutes(15); // 10:15:00 exactly
        var fresh = await cache.ReadAsync(null, default);

        Assert.Equal(2, inner.Calls);
        Assert.Equal(TenOClock.AddMinutes(15), fresh.RetrievedAt);
    }

    [Fact]
    public async Task MinIntervalOmitted_M2Default_IsFifteenMinutes()
    {
        // SPEC P1.2 "(default 15 min, constructor parameter)" as fixed by decision M2:
        // "TimeSpan? minInterval = null; null → 15 min". Tested with the A7 oracle points: cached at
        // 10:14:59, re-read at 10:15:00.
        var clock = new FakeClock(TenOClock);
        var inner = new FakeSource(clock);
        var cache = new CachedAgentUsageSource(inner, clock); // M2: third argument omitted

        await cache.ReadAsync(null, default);
        clock.UtcNow = TenOClock.AddSeconds(14 * 60 + 59); // 10:14:59
        await cache.ReadAsync(null, default);
        Assert.Equal(1, inner.Calls);

        clock.UtcNow = TenOClock.AddMinutes(15); // 10:15:00
        await cache.ReadAsync(null, default);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task ExplicitMinInterval_IsHonoured_NotHardcodedToFifteenMinutes()
    {
        // Decision M2 keeps the interval as a constructor parameter, so a 1-minute value must shorten
        // the cache window; the A7 boundary rule ("age equal to MinInterval is not younger") is
        // re-applied at that shorter interval: cached at 10:00:59, re-read at 10:01:00.
        var clock = new FakeClock(TenOClock);
        var inner = new FakeSource(clock);
        var cache = new CachedAgentUsageSource(inner, clock, TimeSpan.FromMinutes(1));

        await cache.ReadAsync(null, default);
        clock.UtcNow = TenOClock.AddSeconds(59); // 10:00:59 → age 59 s < 1 min → cached
        var cached = await cache.ReadAsync(null, default);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(TenOClock, cached.RetrievedAt);

        clock.UtcNow = TenOClock.AddMinutes(1); // 10:01:00 → age 1 min = interval → re-read
        var fresh = await cache.ReadAsync(null, default);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(TenOClock.AddMinutes(1), fresh.RetrievedAt);
    }

    [Fact]
    public async Task Call_WithDifferentSince_ReadsThroughAndReplacesSingleSlot()
    {
        // Decision A1: "The cache holds one report and the since it was read with. A call with a
        // different since reads through and replaces the slot."
        var (cache, inner, clock) = A7OracleCache();
        await cache.ReadAsync(null, default);                      // slot: since = null @10:00:00

        clock.UtcNow = TenOClock.AddSeconds(1);
        await cache.ReadAsync(SinceDay, default);                  // different since → read #2, slot: since = SinceDay

        clock.UtcNow = TenOClock.AddSeconds(2);
        await cache.ReadAsync(SinceDay, default);                  // same since, younger → cached, no new call
        Assert.Equal(2, inner.Calls);

        clock.UtcNow = TenOClock.AddSeconds(3);
        await cache.ReadAsync(null, default);                      // since differs again → read #3 (single slot)
        Assert.Equal(3, inner.Calls);
        Assert.Equal(new DateOnly?[] { null, SinceDay, null }, inner.SinceValues);
    }

    [Fact]
    public async Task Force_WithinInterval_BypassesInterval()
    {
        // SPEC P1.2: "ReadAsync(..., force) overload bypasses the interval."
        var (cache, inner, clock) = A7OracleCache();
        await cache.ReadAsync(null, default);

        clock.UtcNow = TenOClock.AddSeconds(30); // 10:00:30 — well inside 15 min
        var forced = await cache.ReadAsync(null, force: true, CancellationToken.None);

        Assert.Equal(2, inner.Calls);
        Assert.Equal(TenOClock.AddSeconds(30), forced.RetrievedAt);
    }

    [Fact]
    public async Task Force_False_WithinInterval_ServesCacheLikeARegularRead()
    {
        var (cache, inner, clock) = A7OracleCache();
        await cache.ReadAsync(null, default);

        clock.UtcNow = TenOClock.AddSeconds(30);
        var cached = await cache.ReadAsync(null, force: false, CancellationToken.None);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(TenOClock, cached.RetrievedAt);
    }

    [Fact]
    public async Task Failure_AfterAGoodRead_Rethrows_AndKeepsLastGood_AndSetsLastError()
    {
        // SPEC P1.2: "on failure: rethrow the exception and keep the previous good report available
        // via LastGood (…) and LastError (…)".
        var (cache, inner, clock) = A7OracleCache();
        await cache.ReadAsync(null, default); // good report @10:00:00

        inner.FailOnCall = 2;
        inner.FailKind = AgentUsageErrorKind.Failed;
        clock.UtcNow = TenOClock.AddMinutes(15).AddSeconds(1); // past the interval → read-through
        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => cache.ReadAsync(null, default));

        Assert.Equal(AgentUsageErrorKind.Failed, error.Kind);
        Assert.NotNull(cache.LastGood);
        Assert.Equal(TenOClock, cache.LastGood!.RetrievedAt); // still the previous good report
        Assert.NotNull(cache.LastError);
        Assert.Equal(AgentUsageErrorKind.Failed, cache.LastError!.Kind);
    }

    [Fact]
    public async Task Failure_BeforeAnySuccess_Rethrows_WithLastGoodNull()
    {
        // SPEC P1.2: LastGood is the "previous good report"; before any success there is none.
        var (cache, inner, _) = A7OracleCache();
        inner.FailOnCall = 1;
        inner.FailKind = AgentUsageErrorKind.Timeout;

        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => cache.ReadAsync(null, default));

        Assert.Equal(AgentUsageErrorKind.Timeout, error.Kind);
        Assert.Null(cache.LastGood);
        Assert.NotNull(cache.LastError);
        Assert.Equal(AgentUsageErrorKind.Timeout, cache.LastError!.Kind);
    }

    [Fact]
    public async Task Success_AfterFailure_UpdatesLastGood_AndIsServedFromCache()
    {
        // SPEC P1.2: the cache returns "the last successful read"; a failure must not evict it,
        // and the next success replaces it.
        var (cache, inner, clock) = A7OracleCache();
        await cache.ReadAsync(null, default); // report #1 @10:00:00

        inner.FailOnCall = 2;
        inner.FailKind = AgentUsageErrorKind.NotInstalled;
        clock.UtcNow = TenOClock.AddMinutes(15).AddSeconds(1);
        await Assert.ThrowsAsync<AgentUsageUnavailableException>(() => cache.ReadAsync(null, default));
        Assert.NotNull(cache.LastError); // the failure is recorded before any later success clears it

        inner.FailOnCall = -1;
        clock.UtcNow = TenOClock.AddMinutes(30);
        await cache.ReadAsync(null, default); // report #2 @10:30:00
        Assert.Equal(TenOClock.AddMinutes(30), cache.LastGood!.RetrievedAt);
        Assert.Null(cache.LastError); // Decision E4: "a successful read (forced or not) clears LastError"

        clock.UtcNow = TenOClock.AddMinutes(30).AddSeconds(30); // inside #2's interval
        var cached = await cache.ReadAsync(null, default);
        Assert.Equal(3, inner.Calls);
        Assert.Equal(TenOClock.AddMinutes(30), cached.RetrievedAt);
    }

    [Fact]
    public async Task ForcedSuccess_AfterFailure_ClearsLastError_AndReplacesLastGood()
    {
        // Decision E4 names the forced path explicitly: "a successful read (forced or not) clears
        // LastError". The success here can only come from a forced read, because 10:00:30/10:00:40 are
        // inside the 15-minute M2 default interval that the failed read at 10:00:30 left untouched.
        var (cache, inner, clock) = A7OracleCache();
        await cache.ReadAsync(null, default); // report #1 @10:00:00

        inner.FailOnCall = 2;
        inner.FailKind = AgentUsageErrorKind.Failed;
        clock.UtcNow = TenOClock.AddSeconds(30);
        await Assert.ThrowsAsync<AgentUsageUnavailableException>(
            () => cache.ReadAsync(null, force: true, CancellationToken.None));
        Assert.Equal(AgentUsageErrorKind.Failed, cache.LastError!.Kind);

        inner.FailOnCall = -1;
        clock.UtcNow = TenOClock.AddSeconds(40);
        var forced = await cache.ReadAsync(null, force: true, CancellationToken.None); // read #3 succeeds

        Assert.Equal(3, inner.Calls);
        Assert.Null(cache.LastError); // E4: the successful forced read cleared it
        Assert.Equal(TenOClock.AddSeconds(40), cache.LastGood!.RetrievedAt);
        Assert.Equal(TenOClock.AddSeconds(40), forced.RetrievedAt);
    }

    [Fact]
    public async Task FailedForcedRead_DoesNotEvictTheCache_NonForcedCallWithinIntervalReturnsLastGood()
    {
        // Decision E4: "A failed forced read does not evict the cache: a later non-forced call within
        // MinInterval of the last success returns LastGood." The last success is the read at
        // 10:00:00, so 10:00:59 is still inside the 15-minute interval even though the forced read at
        // 10:00:30 failed.
        var (cache, inner, clock) = A7OracleCache();
        await cache.ReadAsync(null, default); // success @10:00:00 fills the slot (A1: one slot)

        inner.FailOnCall = 2;
        inner.FailKind = AgentUsageErrorKind.Timeout;
        clock.UtcNow = TenOClock.AddSeconds(30);
        var error = await Assert.ThrowsAsync<AgentUsageUnavailableException>(
            () => cache.ReadAsync(null, force: true, CancellationToken.None));

        Assert.Equal(AgentUsageErrorKind.Timeout, error.Kind); // E4 keeps the SPEC P1.2 rethrow
        Assert.Equal(TenOClock, cache.LastGood!.RetrievedAt); // the failed forced read evicted nothing

        clock.UtcNow = TenOClock.AddSeconds(59); // 10:00:59 → within MinInterval of the 10:00:00 success
        var served = await cache.ReadAsync(null, default);

        Assert.Equal(2, inner.Calls); // no third read: served from the slot the failure left in place
        Assert.Equal(TenOClock, served.RetrievedAt); // E4: the cached LastGood, not a fresh report
        // E4 does not say whether a cache hit counts as "a successful read" for clearing LastError,
        // so LastError is deliberately not asserted here (reported to the Coding Owner).
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneInFlightRead()
    {
        // SPEC P1.2: "concurrent callers share one in-flight read (no parallel ccusage processes)."
        // Decision M2's parameter is passed explicitly here (15 min), which must behave like the default.
        var clock = new FakeClock(TenOClock);
        var inner = new GatedSource(clock);
        var cache = new CachedAgentUsageSource(inner, clock, TimeSpan.FromMinutes(15));

        var first = cache.ReadAsync(null, default);
        var second = cache.ReadAsync(null, default);
        var third = cache.ReadAsync(null, default);

        Assert.Equal(1, inner.Calls); // one read through the port for three callers

        var report = ReportAt(TenOClock);
        inner.Release(report);
        var results = await Task.WhenAll(first, second, third);

        Assert.All(results, r => Assert.Equal(TenOClock, r.RetrievedAt));
    }

    [Fact]
    public async Task CallerCancellation_IsRethrown_AndNotStoredAsLastError()
    {
        // SPEC P1.2: "OperationCanceledException from the caller's token is rethrown and is not
        // stored as LastError."
        var clock = new FakeClock(TenOClock);
        var inner = new GatedSource(clock);
        var cache = new CachedAgentUsageSource(inner, clock, TimeSpan.FromMinutes(15));

        using var cancellation = new CancellationTokenSource();
        var pending = cache.ReadAsync(null, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(cache.LastError);
        Assert.Null(cache.LastGood);
    }

    [Fact]
    public async Task CallerCancellation_AfterAGoodRead_KeepsLastGood_AndDoesNotSetLastError()
    {
        // Cancellation is not a provider failure: the LastGood from the 10:00:00 success must survive
        // and LastError must stay null (SPEC P1.2: LastError is stored "on failure", and caller
        // cancellation "is rethrown and is not stored as LastError").
        var clock = new FakeClock(TenOClock);
        var inner = new GatedSource(clock, call => call == 1); // call 1 succeeds, call 2 stays pending
        var cache = new CachedAgentUsageSource(inner, clock, TimeSpan.FromMinutes(15));

        await cache.ReadAsync(null, default); // good report @10:00:00
        Assert.Equal(TenOClock, cache.LastGood!.RetrievedAt);

        clock.UtcNow = TenOClock.AddMinutes(15).AddSeconds(1); // past the interval → a fresh read is attempted
        using var cancellation = new CancellationTokenSource();
        var pending = cache.ReadAsync(null, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(cache.LastError);
        Assert.Equal(TenOClock, cache.LastGood!.RetrievedAt);
    }
}
