using System.Collections.Concurrent;
using AIMonitor.Application.OpenCode;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-020 F9 (Docs/SPEC_R020_R021.md): the OpenCode check is its own trigger, <c>RequestOpenCodeCheck()</c>,
/// never part of <c>RefreshAsync</c>. Callers: startup (App; source contract in
/// <see cref="ConnectionsSourceOpenCodeContractTests"/>), "Re-detect all" (provider refresh AND one check) and
/// Re-detect on the OpenCode card (one check, no provider refresh). Idle -> start a read; a request while a read
/// is in flight sets ONE pending flag (any number of requests -> one rerun after the current read); each
/// completed read is applied through <c>Connections.UpdateOpenCode</c> on the view model's captured context
/// (inline when none); the read runs off the UI thread; a fault other than cancellation shows the F5 Error text;
/// a null reader keeps the card "Checking..." and runs nothing.
///
/// SEAM ASSUMPTION: the optional <see cref="IOpenCodeAuthReader"/> is the FIFTH positional constructor argument
/// of <see cref="MainWindowViewModel"/> (after <c>trayHost</c> and <c>agentUsage</c>); change
/// <see cref="VmHarness.Create"/> if the code puts it elsewhere. Every wait here is a bounded await on a
/// semaphore/TCS signalled by the code under test; nothing sleeps to "let things settle".
/// </summary>
public sealed partial class MainWindowViewModelOpenCodeCheckTests
{
    private const string ErrorText = "Could not read OpenCode sign-ins. Run `opencode auth list` in a terminal to check.";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>The only wall-clock wait left: how long an "an extra read must NOT happen" check watches. A correct
    /// implementation can never fail it; it is long (not 300 ms) so a late extra read is still seen.</summary>
    private static readonly TimeSpan AbsenceBound = TimeSpan.FromSeconds(2);

    private static OpenCodeAuthStatus Connected(string name, string kind = "OAuth", string source = "opencode auth list") =>
        new(true, source, [new OpenCodeSignIn(name, kind)], false, "");

    // ── null reader ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task NullReader_RequestIsANoOp_CardStaysChecking_F9()
    {
        await using var h = VmHarness.Create(reader: null);

        h.Vm.RequestOpenCodeCheck();

        Assert.Equal("Checking...", h.Vm.Connections.OpenCodeCard.StateWord);
        Assert.Equal("–", h.Vm.Connections.OpenCodeCard.StateGlyph);
        Assert.Equal(0, h.Context!.PostCount);
    }

    [Fact]
    public async Task NullReader_RedetectAll_StillRefreshesProviders_AndOpenCodeRedetectDoesNothing_F9()
    {
        await using var h = VmHarness.Create(reader: null);

        h.Vm.Connections.OpenCodeCard.RedetectCommand.Execute(null);
        Assert.Equal("Checking...", h.Vm.Connections.OpenCodeCard.StateWord);
        Assert.Equal(0, h.Claude.CallCount);
        Assert.False(h.Vm.IsRefreshing);

        h.Vm.Connections.RedetectAllCommand.Execute(null);
        await h.Claude.FirstCall.WaitAsync(Bound);

        Assert.Equal("Checking...", h.Vm.Connections.OpenCodeCard.StateWord);
    }

    // ── one read, off the UI thread, delivered on the captured context ───────────

    [Fact]
    public async Task RequestOpenCodeCheck_StartsExactlyOneRead_OffTheCallingThread_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);

        var callerThreadId = -1;
        using var callerDone = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var caller = new Thread(() =>
        {
            callerThreadId = Environment.CurrentManagedThreadId;
            h.Vm.RequestOpenCodeCheck();
            callerDone.Set();
            release.Wait(Bound); // keep the "UI thread" alive so no other thread can share its id
        })
        { IsBackground = true };
        caller.Start();
        Assert.True(callerDone.Wait(Bound));

        await reader.WaitForCallsAsync(1);
        release.Set();

        Assert.Equal(1, reader.CallCount);
        Assert.NotEqual(callerThreadId, reader.ThreadIdOfCall(0));
        Assert.Equal("Checking...", h.Vm.Connections.OpenCodeCard.StateWord); // nothing applied before the read completes
    }

    [Fact]
    public async Task CompletedRead_IsAppliedToTheCardOnTheCapturedContext_NotBefore_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);

        reader.Complete(0, Connected("Anthropic"));
        await h.Context!.WaitForPostsAsync(1);

        // Delivered by Post to the construction context: not applied until that context runs it.
        Assert.Equal("Checking...", h.Vm.Connections.OpenCodeCard.StateWord);

        h.Context.Drain();

        Assert.Equal("Connected", h.Vm.Connections.OpenCodeCard.StateWord);
        Assert.Equal("Signed in: Anthropic (OAuth)", h.Vm.Connections.OpenCodeCard.Account);
        Assert.Equal("Detected from opencode auth list", h.Vm.Connections.OpenCodeCard.SourceLine);
    }

    [Fact]
    public async Task WithoutACapturedContext_TheResultIsAppliedInline_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader, queuedContext: false);
        // Wait for the WHOLE card update (every displayed field), not only StateWord: the update sets the fields one
        // after another, so StateWord alone would race the assertions on Account (Reviewer2 #4).
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new HashSet<string>();
        string[] fields =
        [
            nameof(ConnectionCardViewModel.StateWord),
            nameof(ConnectionCardViewModel.StateGlyph),
            nameof(ConnectionCardViewModel.Account),
            nameof(ConnectionCardViewModel.SourceLine),
        ];
        h.Vm.Connections.OpenCodeCard.PropertyChanged += (_, e) =>
        {
            lock (seen)
            {
                if (e.PropertyName is not null && fields.Contains(e.PropertyName))
                {
                    seen.Add(e.PropertyName);
                    if (seen.Count == fields.Length)
                    {
                        applied.TrySetResult();
                    }
                }
            }
        };
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);

        reader.Complete(0, Connected("OpenAI", "API key"));
        await applied.Task.WaitAsync(Bound);

        Assert.Equal("Connected", h.Vm.Connections.OpenCodeCard.StateWord);
        Assert.Equal("✓", h.Vm.Connections.OpenCodeCard.StateGlyph);
        Assert.Equal("Signed in: OpenAI (API key)", h.Vm.Connections.OpenCodeCard.Account);
        Assert.Equal("Detected from opencode auth list", h.Vm.Connections.OpenCodeCard.SourceLine);
    }

    [Fact]
    public async Task NotInstalledResult_ShowsTheInstallGuidanceCard_F9_F8()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);

        reader.Complete(0, new OpenCodeAuthStatus(false, "", [], false, ""));
        await h.Context!.WaitForPostsAsync(1);
        h.Context.Drain();

        Assert.Equal("Not connected", h.Vm.Connections.OpenCodeCard.StateWord);
        Assert.Equal("OpenCode not found on PATH", h.Vm.Connections.OpenCodeCard.Account);
    }

    // ── faults ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReaderTaskThatFaults_ShowsTheF5ErrorText_NeverTheExceptionText_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);

        reader.Fail(0, new InvalidOperationException("SENTINEL exception text C:\\secret"));
        await h.Context!.WaitForPostsAsync(1);
        h.Context.Drain();

        var card = h.Vm.Connections.OpenCodeCard;
        Assert.Equal("Unavailable", card.StateWord);
        Assert.Equal("!", card.StateGlyph);
        Assert.Equal("Could not check", card.Account);
        Assert.Equal(ErrorText, card.SourceLine);
        Assert.DoesNotContain("SENTINEL", card.Account + card.SourceLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReaderThatThrowsSynchronously_ShowsTheF5ErrorText_AndDoesNotThrowToTheCaller_F9()
    {
        var reader = new FakeOpenCodeReader { ThrowOnRead = new InvalidOperationException("SENTINEL sync failure") };
        await using var h = VmHarness.Create(reader);

        var thrown = Record.Exception(h.Vm.RequestOpenCodeCheck);
        Assert.Null(thrown);
        await h.Context!.WaitForPostsAsync(1);
        h.Context.Drain();

        Assert.Equal("Unavailable", h.Vm.Connections.OpenCodeCard.StateWord);
        Assert.Equal(ErrorText, h.Vm.Connections.OpenCodeCard.SourceLine);
    }

    [Fact]
    public async Task AfterAFault_TheNextRequestStartsANewRead_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);
        reader.Fail(0, new InvalidOperationException("x"));
        await h.Context!.WaitForPostsAsync(1);
        h.Context.Drain();

        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(2);
        reader.Complete(1, Connected("Anthropic"));
        await h.Context.WaitForPostsAsync(2);
        h.Context.Drain();

        Assert.Equal("Connected", h.Vm.Connections.OpenCodeCard.StateWord);
    }

    // ── coalescing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThreeRequestsDuringAnInFlightRead_CauseExactlyOneRerun_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);

        h.Vm.RequestOpenCodeCheck();
        h.Vm.RequestOpenCodeCheck();
        h.Vm.RequestOpenCodeCheck();
        Assert.Equal(1, reader.CallCount); // never a second read while one is in flight

        reader.Complete(0, Connected("Read1"));
        // The rerun may start only after the first result was delivered to the view model's context, so the
        // test pumps that context (like a UI thread) while it waits for the single rerun.
        await PumpUntilCallAsync(h, reader, 2);
        reader.Complete(1, Connected("Read2"));
        await h.Context!.WaitForPostsAsync(2);
        h.Context.Drain();

        // Each completed read was applied, in order: the card ends on the rerun's result.
        Assert.Equal("Signed in: Read2 (OAuth)", h.Vm.Connections.OpenCodeCard.Account);
        Assert.Equal(2, reader.CallCount);

        // No third read follows (see AssertQuiescentAsync).
        await AssertQuiescentAsync(h, reader, expectedCalls: 2);
    }

    [Fact]
    public async Task ARequestAfterTheReadsFinish_StartsAFreshRead_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);
        reader.Complete(0, Connected("Read1"));
        await h.Context!.WaitForPostsAsync(1);
        h.Context.Drain();

        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(2);
        reader.Complete(1, Connected("Read2"));
        await h.Context.WaitForPostsAsync(2);
        h.Context.Drain();

        Assert.Equal(2, reader.CallCount);
        Assert.Equal("Signed in: Read2 (OAuth)", h.Vm.Connections.OpenCodeCard.Account);
    }

    [Fact]
    public async Task ASingleRequest_NeverCausesARerun_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);

        reader.Complete(0, Connected("Read1"));
        await h.Context!.WaitForPostsAsync(1);
        h.Context.Drain();

        await AssertQuiescentAsync(h, reader, expectedCalls: 1);
    }

    // ── provider refresh never starts a check ───────────────────────────────────

    [Fact]
    public async Task RefreshAsync_InEveryForm_NeverStartsAnOpenCodeRead_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);

        await h.Vm.RefreshAsync();                       // timer / widget refresh form
        await h.Vm.RefreshAsync(manual: true);           // Refresh button form
        await h.Vm.RefreshAsync("claude");               // single-provider refresh
        await h.Vm.RefreshAsync();

        Assert.True(h.Claude.CallCount >= 4);
        Assert.Equal(0, reader.CallCount);
        Assert.Equal("Checking...", h.Vm.Connections.OpenCodeCard.StateWord);
    }

    [Fact]
    public async Task PendingRefreshRerun_NeverStartsAnOpenCodeRead_F9()
    {
        var reader = new FakeOpenCodeReader();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = VmHarness.Create(reader, claudeGate: gate.Task);

        var first = h.Vm.RefreshAsync();
        await h.Claude.FirstCall.WaitAsync(Bound);
        var second = h.Vm.RefreshAsync(); // arrives while refreshing -> _pendingRefresh -> a rerun with null id
        gate.SetResult();
        await first.WaitAsync(Bound);
        await second.WaitAsync(Bound);

        Assert.Equal(0, reader.CallCount);
    }

    // ── Re-detect ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RedetectAll_RefreshesProvidersAndStartsExactlyOneRead_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsRefreshing) && h.Vm.IsRefreshing)
            {
                refreshing.TrySetResult();
            }
        };

        h.Vm.Connections.RedetectAllCommand.Execute(null);

        await h.Claude.FirstCall.WaitAsync(Bound);   // the existing RefreshAsync(null)
        await reader.WaitForCallsAsync(1);           // and the OpenCode check
        await refreshing.Task.WaitAsync(Bound);
        Assert.Equal(1, reader.CallCount);

        reader.Complete(0, Connected("Anthropic"));
        await h.Context!.WaitForPostsAsync(1);
        h.Context.Drain();
        Assert.Equal("Connected", h.Vm.Connections.OpenCodeCard.StateWord);
    }

    [Fact]
    public async Task RedetectOnTheOpenCodeCard_StartsOneRead_AndNoProviderRefresh_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        var refreshStarted = false;
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.IsRefreshing) or nameof(MainWindowViewModel.LastRefreshStatus))
            {
                refreshStarted = true;
            }
        };

        h.Vm.Connections.OpenCodeCard.RedetectCommand.Execute(null);
        await reader.WaitForCallsAsync(1);
        reader.Complete(0, Connected("Anthropic"));
        await h.Context!.WaitForPostsAsync(1);
        h.Context.Drain();

        Assert.Equal(1, reader.CallCount);
        Assert.False(refreshStarted); // RefreshAsync sets IsRefreshing / LastRefreshStatus synchronously when it starts
        Assert.Equal(0, h.Claude.CallCount);
        Assert.Equal("Connected", h.Vm.Connections.OpenCodeCard.StateWord);
    }

    [Fact]
    public async Task RedetectOnAnotherCard_RefreshesThatProviderOnly_AndStartsNoOpenCodeRead_F9()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);

        h.Vm.Connections.ClaudeCard.RedetectCommand.Execute(null);
        await h.Claude.FirstCall.WaitAsync(Bound);

        Assert.Equal(0, reader.CallCount);
    }

    /// <summary>
    /// "No extra read ran" after <paramref name="expectedCalls"/> reads. The view model exposes no "loop ended"
    /// signal, so this is the one place with a bounded watch (<see cref="AbsenceBound"/>); around it the checks are
    /// signal-driven: the context is drained so the loop can finish, a follow-up request must start exactly one
    /// more read (the in-flight flag was released) which is completed and delivered, and only then is the watch run.
    /// </summary>
    private static async Task AssertQuiescentAsync(VmHarness h, FakeOpenCodeReader reader, int expectedCalls)
    {
        h.Context!.Drain();
        h.Vm.RequestOpenCodeCheck();
        await PumpUntilCallAsync(h, reader, expectedCalls + 1);
        reader.Complete(expectedCalls, Connected("Fence"));
        await h.Context.WaitForPostsAsync(expectedCalls + 1);
        h.Context.Drain();

        Assert.Equal("Signed in: Fence (OAuth)", h.Vm.Connections.OpenCodeCard.Account);
        Assert.False(
            await reader.TryWaitForCallAsync(expectedCalls + 2, AbsenceBound),
            $"An unexpected extra read ran after read #{expectedCalls}.");
        Assert.Equal(expectedCalls + 1, reader.CallCount);
    }

    private static async Task PumpUntilCallAsync(VmHarness h, FakeOpenCodeReader reader, int count)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (true)
        {
            h.Context?.Drain();
            if (await reader.TryWaitForCallAsync(count, TimeSpan.FromMilliseconds(20)))
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for read #{count} while pumping the context.");
        }
    }

    // ── harness ─────────────────────────────────────────────────────────────────

    private sealed class CountingClient : IProviderQuotaClient
    {
        private readonly Task? _gate;
        private int _calls;

        public CountingClient(Task? gate) => _gate = gate;

        public int CallCount => Volatile.Read(ref _calls);

        public TaskCompletionSource FirstCallSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstCall => FirstCallSource.Task;

        public async Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            FirstCallSource.TrySetResult();
            if (_gate is not null)
            {
                await _gate.ConfigureAwait(false);
            }

            return new ProviderSnapshot(
                "claude",
                configured: true,
                meters: [new Meter("session", "session", "Session", "5-hour window", 10.0)]);
        }
    }

    private sealed class VmHarness : IAsyncDisposable
    {
        private readonly SettingsSession _session;
        private readonly LatestRefreshCoordinator _coordinator;

        private VmHarness(MainWindowViewModel vm, SignalingContext? context, CountingClient claude, SettingsSession session, LatestRefreshCoordinator coordinator)
        {
            Vm = vm;
            Context = context;
            Claude = claude;
            _session = session;
            _coordinator = coordinator;
        }

        public MainWindowViewModel Vm { get; }

        public SignalingContext? Context { get; }

        public CountingClient Claude { get; }

        /// <summary>The ONLY place that passes the reader to the view model (assumed 5th positional parameter).</summary>
        public static VmHarness Create(IOpenCodeAuthReader? reader, bool queuedContext = true, Task? claudeGate = null)
        {
            var claude = new CountingClient(claudeGate);
            var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(
            [
                new ProviderClientRegistration("claude", claude),
            ]));
            var settings = new AppSettings();
            var store = new BlockingSettingsStore(settings);
            store.Release();
            var session = new SettingsSession(store, settings);

            var context = queuedContext ? new SignalingContext() : null;
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var vm = new MainWindowViewModel(coordinator, session, null, null, reader);
                context?.MarkBaseline();
                return new VmHarness(vm, context, claude, session, coordinator);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _session.Dispose();
            await _coordinator.DisposeAsync();
        }
    }

    /// <summary>Hand-written reader: every call is one TaskCompletionSource the test completes or faults.</summary>
    private sealed class FakeOpenCodeReader : IOpenCodeAuthReader
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<OpenCodeAuthStatus>> _calls = [];
        private readonly List<int> _threadIds = [];
        private readonly SemaphoreSlim _started = new(0);

        public Exception? ThrowOnRead { get; set; }

        public int CallCount
        {
            get
            {
                lock (_gate)
                {
                    return _calls.Count;
                }
            }
        }

        public int ThreadIdOfCall(int index)
        {
            lock (_gate)
            {
                return _threadIds[index];
            }
        }

        public Task<OpenCodeAuthStatus> ReadAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<OpenCodeAuthStatus> source;
            lock (_gate)
            {
                if (ThrowOnRead is not null)
                {
                    _calls.Add(new TaskCompletionSource<OpenCodeAuthStatus>());
                    _threadIds.Add(Environment.CurrentManagedThreadId);
                    _started.Release();
                    throw ThrowOnRead;
                }

                source = new TaskCompletionSource<OpenCodeAuthStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                _calls.Add(source);
                _threadIds.Add(Environment.CurrentManagedThreadId);
                _running++;
                _max = Math.Max(_max, _running);
            }

            _started.Release();
            return source.Task;
        }

        private int _running;
        private int _max;

        /// <summary>The most reads that were ever pending at once (the contract is one at a time).</summary>
        public int MaxConcurrent
        {
            get
            {
                lock (_gate)
                {
                    return _max;
                }
            }
        }

        public void Complete(int index, OpenCodeAuthStatus status)
        {
            lock (_gate)
            {
                _running--;
                _calls[index].SetResult(status);
            }
        }

        public void Fail(int index, Exception exception)
        {
            lock (_gate)
            {
                _running--;
                _calls[index].SetException(exception);
            }
        }

        public async Task WaitForCallsAsync(int count)
        {
            Assert.True(await TryWaitForCallAsync(count, Bound), $"Timed out waiting for read #{count}.");
        }

        public async Task<bool> TryWaitForCallAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (CallCount < count)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !await _started.WaitAsync(left))
                {
                    return CallCount >= count;
                }
            }

            return true;
        }
    }

    /// <summary>R-017 F24 pattern with a signal: queues every Post (counting it), runs nothing until
    /// <see cref="Drain"/>, which runs the callbacks with this context installed like a UI thread.</summary>
    private sealed class SignalingContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly SemaphoreSlim _posted = new(0);
        private int _postCount;

        private int _baseline;

        /// <summary>Posts received since <see cref="MarkBaseline"/> (construction-time posts excluded).</summary>
        public int PostCount => Volatile.Read(ref _postCount) - _baseline;

        public void MarkBaseline() => _baseline = Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Enqueue((d, state));
            Interlocked.Increment(ref _postCount);
            _posted.Release();
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
