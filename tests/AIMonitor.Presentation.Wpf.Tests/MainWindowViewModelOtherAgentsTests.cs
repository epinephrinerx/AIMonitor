using System.Collections.Concurrent;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-021 G4 (Docs/SPEC_R020_R021.md): <c>OnAgentUsageUpdated</c> calls <c>Connections.UpdateOtherAgents</c>
/// FIRST, inside its own try/catch, on the same context as the existing tab updates, so (a) an exception in
/// UpdateOtherAgents never stops the existing tab updates and (b) an exception in an existing tab update
/// (here: the AGY composition rejecting a negative daily cost, the R-018 D13 fixture) cannot prevent
/// OtherAgents from updating. The refresher swallows every handler exception, so a missing try/catch is
/// silent except for the effect these tests assert.
/// The (a) direction needs UpdateOtherAgents to throw: the test attaches a PropertyChanged handler to
/// <c>Connections</c> that throws when OtherAgentsStatus/OtherAgents change, which propagates out of
/// UpdateOtherAgents without any production seam.
/// Fakes as in the R-014/R-018 view-model tests: no real ccusage, no sleeps, no polling; the handler work is posted
/// to the context the view model captured at construction and settled by draining that context each time it signals
/// a new post.
/// Fixed clock (fix round 1): the reports use a fixed past date, never the real date. The OtherAgents rows depend
/// only on the agent ids, and the "existing tabs still update" checks use outputs that do not depend on "today"
/// (<c>AllAgentsTab.Months</c> / <c>HasMonths</c>, set by the FIRST existing update). The view model reads the wall
/// clock only for the date-dependent tab content, which these tests deliberately do not assert.
/// </summary>
public sealed class MainWindowViewModelOtherAgentsTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 11, 9, 7, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static ModelUsage Model(string name, long total, decimal? cost) =>
        new(name, new TokenCounts(total, 0, 0, 0, total), cost);

    private static AgentUsage Agent(string id, long total = 1_000, decimal? cost = 1.00m) =>
        new(id, new TokenCounts(total, 0, 0, 0, total), [Model(id + "-model", total, cost)]);

    private static readonly DateOnly FixedDay = DateOnly.FromDateTime(FixedUtc.UtcDateTime);

    /// <summary>Claude, opencode, copilot, zcode on the fixed day; claude in the month. "Other" = copilot + zcode.</summary>
    private static AgentUsageReport ReportWithOthers() => new(
        daily: [new DailyAgentUsage(FixedDay, [Agent("claude"), Agent("opencode", 8_000, 0.40m), Agent("copilot"), Agent("zcode")])],
        monthly: [new MonthlyAgentUsage(FixedDay.Year, FixedDay.Month, [Agent("claude", 9_999, 10.50m)])],
        sessions: [],
        unpricedModels: [],
        pricing: AgentUsagePricing.OnlineRequested,
        retrievedAt: FixedUtc,
        skippedRows: 0);

    // ── delivery and content ────────────────────────────────────────────────────

    [Fact]
    public async Task UsageUpdate_FillsOtherAgents_OnTheCapturedContext_G4()
    {
        await using var h = Harness.Create(_ => Task.FromResult(ReportWithOthers()));

        await h.RefreshAsync();
        await h.Context.WaitForPostsAsync(1); // the handler is posted to the construction context
        Assert.Empty(h.Vm.Connections.OtherAgents); // not run yet: same context rule as the existing tab updates

        await h.SettleAsync();

        Assert.Equal("Agents found by ccusage that have no tab of their own:", h.Vm.Connections.OtherAgentsStatus);
        Assert.Equal(new[] { "copilot", "zcode" }, h.Vm.Connections.OtherAgents.Select(r => r.AgentId));
        // ...and the existing tabs were updated by the same handler run (a date-independent output).
        Assert.True(h.Vm.AllAgentsTab.HasMonths);
    }

    [Fact]
    public async Task CcusageFailureWithoutAReport_SaysCcusageIsNotAvailable_G3_G4()
    {
        await using var h = Harness.Create(_ => Task.FromException<AgentUsageReport>(
            new AgentUsageUnavailableException(AgentUsageErrorKind.NotInstalled, "SENTINEL ccusage was not found.")));

        await h.RefreshAsync();
        await h.SettleAsync();

        Assert.Equal("ccusage is not available, so other agents cannot be listed.", h.Vm.Connections.OtherAgentsStatus);
        Assert.Empty(h.Vm.Connections.OtherAgents);
        Assert.DoesNotContain("SENTINEL", h.Vm.Connections.OtherAgentsStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALaterFailure_KeepsTheRowsFromTheLastGoodReport_G3_G4()
    {
        var reads = 0;
        await using var h = Harness.Create(_ =>
            ++reads == 1
                ? Task.FromResult(ReportWithOthers())
                : Task.FromException<AgentUsageReport>(new AgentUsageUnavailableException(AgentUsageErrorKind.Failed, "SENTINEL refresh failed")));
        await h.RefreshAsync();
        await h.SettleAsync();
        Assert.Equal(new[] { "copilot", "zcode" }, h.Vm.Connections.OtherAgents.Select(r => r.AgentId));

        h.Clock.UtcNow = FixedUtc.AddHours(2); // past the cache interval: the next refresh reads (and fails) again
        await h.RefreshAsync();
        await h.SettleAsync();

        Assert.Equal(2, reads);
        Assert.Equal("Agents found by ccusage that have no tab of their own:", h.Vm.Connections.OtherAgentsStatus);
        Assert.Equal(new[] { "copilot", "zcode" }, h.Vm.Connections.OtherAgents.Select(r => r.AgentId));
        Assert.DoesNotContain("SENTINEL", h.Vm.Connections.OtherAgentsStatus, StringComparison.Ordinal);
    }

    // ── isolation, direction 1: an existing tab update throws -> OtherAgents still updates ──────

    [Fact]
    public async Task ExistingTabUpdateThatThrows_CannotPreventOtherAgentsFromUpdating_G4()
    {
        // The existing update that throws is the FIRST one (AllAgentsTab.Update): a handler on the tab throws when
        // its Months change, which propagates out of that update exactly like a failing composition would, and
        // stops every later existing update. UpdateOtherAgents must already have run (it is called first).
        await using var h = Harness.Create(_ => Task.FromResult(ReportWithOthers()));
        var thrown = 0;
        h.Vm.AllAgentsTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AllAgentsTabViewModel.Months) or nameof(AllAgentsTabViewModel.HasMonths))
            {
                thrown++;
                throw new InvalidOperationException("synthetic failure inside an existing tab update");
            }
        };

        await h.RefreshAsync();
        await h.SettleAsync();

        Assert.True(thrown >= 1, "The existing tab update was never reached, so the fixture did not exercise the isolation.");
        Assert.Equal("Agents found by ccusage that have no tab of their own:", h.Vm.Connections.OtherAgentsStatus);
        Assert.Equal(new[] { "copilot", "zcode" }, h.Vm.Connections.OtherAgents.Select(r => r.AgentId));
    }

    // ── isolation, direction 2: UpdateOtherAgents throws -> existing tabs still update ──────────

    [Fact]
    public async Task UpdateOtherAgentsThatThrows_NeverStopsTheExistingTabUpdates_G4()
    {
        await using var h = Harness.Create(_ => Task.FromResult(ReportWithOthers()));
        var attempts = 0;
        h.Vm.Connections.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ConnectionsViewModel.OtherAgentsStatus) or nameof(ConnectionsViewModel.OtherAgents))
            {
                attempts++;
                throw new InvalidOperationException("synthetic failure inside UpdateOtherAgents");
            }
        };

        await h.RefreshAsync();
        Assert.Null(await Record.ExceptionAsync(h.SettleAsync)); // contained in its own try/catch

        Assert.True(attempts >= 1, "UpdateOtherAgents was never reached, so the fixture did not exercise the isolation.");
        // The first existing update still ran after the failure (a date-independent output):
        Assert.True(h.Vm.AllAgentsTab.HasMonths);
        Assert.Single(h.Vm.AllAgentsTab.Months);
    }

    // ── order: the existing updates keep their order ────────────────────────────

    [Fact]
    public async Task SecondUpdate_AfterAThrowingHandler_StillWorks_NoStateLeaksBetweenRuns_G4()
    {
        var reads = 0;
        await using var h = Harness.Create(_ =>
        {
            reads++;
            return Task.FromResult(ReportWithOthers());
        });
        var failuresLeft = 1;
        h.Vm.Connections.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionsViewModel.OtherAgentsStatus) && failuresLeft-- > 0)
            {
                throw new InvalidOperationException("first run only");
            }
        };
        await h.RefreshAsync();
        await h.SettleAsync();

        h.Clock.UtcNow = FixedUtc.AddHours(2);
        await h.RefreshAsync();
        await h.SettleAsync();

        Assert.Equal(2, reads);
        Assert.Equal(new[] { "copilot", "zcode" }, h.Vm.Connections.OtherAgents.Select(r => r.AgentId));
    }

    // ── harness ─────────────────────────────────────────────────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SettingsSession _session;
        private readonly LatestRefreshCoordinator _coordinator;

        private Harness(MainWindowViewModel vm, SignalingContext context, FakeClock clock, SettingsSession session, LatestRefreshCoordinator coordinator)
        {
            Vm = vm;
            Context = context;
            Clock = clock;
            _session = session;
            _coordinator = coordinator;
        }

        public MainWindowViewModel Vm { get; }

        public SignalingContext Context { get; }

        public FakeClock Clock { get; }

        public static Harness Create(Func<int, Task<AgentUsageReport>> read)
        {
            var source = new FakeAgentUsageSource(read);
            var clock = new FakeClock(FixedUtc);
            var cache = new CachedAgentUsageSource(source, clock);
            var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
            [
                new ProviderClientRegistration("claude", new FixedClient("claude")),
                new ProviderClientRegistration("openai", new FixedClient("openai")),
                new ProviderClientRegistration("agy", new FixedClient("agy")),
            ]));
            var settings = new AppSettings();
            var store = new BlockingSettingsStore(settings);
            store.Release();
            var session = new SettingsSession(store, settings);

            var context = new SignalingContext();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var vm = new MainWindowViewModel(coordinator, session, agentUsage: cache);
                context.MarkBaseline();
                return new Harness(vm, context, clock, session, coordinator);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        /// <summary>Enters the refresh from a pool thread (no ambient context), so the view model delivers the
        /// handler to the context it captured at construction instead of xUnit's own (D16 rule).</summary>
        public Task RefreshAsync() => Task.Run(() => Vm.RefreshAsync()).WaitAsync(Bound);

        /// <summary>Waits for <c>AgentUsageTask</c> while pumping the construction context (the D16 pattern): the
        /// context signals every new post, so the pump drains exactly when there is something to run, no polling.</summary>
        public async Task SettleAsync()
        {
            var task = Vm.AgentUsageTask!;
            var deadline = DateTime.UtcNow + Bound;
            while (!task.IsCompleted)
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for AgentUsageTask while pumping the captured context.");
                var arrived = Context.NextPost; // taken before draining: a post after this point completes it
                Context.Drain();
                if (task.IsCompleted)
                {
                    break;
                }

                await Task.WhenAny(task, arrived).WaitAsync(Bound);
            }

            await task;
            Context.Drain();
        }

        public async ValueTask DisposeAsync()
        {
            _session.Dispose();
            await _coordinator.DisposeAsync();
        }
    }

    /// <summary>A provider client that returns a minimal configured snapshot (AGY needs one for the D13 fixture).</summary>
    private sealed class FixedClient(string providerId) : IProviderQuotaClient
    {
        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderSnapshot(
                providerId,
                configured: true,
                meters: [new Meter("session", "session", "Session", "5-hour window", 10.0)]));
    }

    private sealed class FakeAgentUsageSource(Func<int, Task<AgentUsageReport>> handler) : IAgentUsageSource
    {
        private int _readCount;

        public Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct) =>
            handler(Interlocked.Increment(ref _readCount));
    }

    private sealed class SignalingContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly SemaphoreSlim _posted = new(0);
        private int _postCount;
        private int _baseline;

        public int PostCount => Volatile.Read(ref _postCount) - _baseline;

        private TaskCompletionSource _nextPost = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the next Post arrives (a fresh one is armed by every Post).</summary>
        public Task NextPost => Volatile.Read(ref _nextPost).Task;

        public void MarkBaseline() => _baseline = Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Enqueue((d, state));
            Interlocked.Increment(ref _postCount);
            _posted.Release();
            Interlocked.Exchange(ref _nextPost, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("View-model context work must Post, never Send.");

        public async Task WaitForPostsAsync(int total)
        {
            var deadline = DateTime.UtcNow + Bound;
            while (PostCount < total)
            {
                var left = deadline - DateTime.UtcNow;
                Assert.True(left > TimeSpan.Zero && await _posted.WaitAsync(left), $"Timed out waiting for post #{total}.");
            }
        }

        /// <summary>Runs the queued callbacks FIFO with this context installed, like a UI thread.</summary>
        public void Drain()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_queue.TryDequeue(out var item))
                {
                    item.Callback(item.State);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
