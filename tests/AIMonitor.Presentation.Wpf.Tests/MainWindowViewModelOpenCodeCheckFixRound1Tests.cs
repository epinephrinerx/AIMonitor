using AIMonitor.Application.OpenCode;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-020 fix round 1 (Docs/SPEC_R020_R021.md): H11 (the in-flight flag belongs to the read loop that owns it; a
/// throwing delivery is observed and a pending rerun still runs) and H12 (Re-detect all starts the OpenCode read
/// first and never waits for the provider refresh). Partial class of
/// <see cref="MainWindowViewModelOpenCodeCheckTests"/> so the fakes (reader, context, view-model harness) are shared.
/// </summary>
public sealed partial class MainWindowViewModelOpenCodeCheckTests
{
    // ── H11 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestsWhileAReadIsPausedInItsDelivery_CoalesceIntoOneRerun_AndReadsNeverOverlap_H11()
    {
        // Reviewer2 #3 interleaving as far as it can be reached from outside: read A is paused before its cleanup
        // (blocked inside its delivery, which runs inline on the read thread because there is no captured context),
        // requests B and C arrive, A resumes, request D arrives while the rerun is in flight. Reads must stay
        // strictly one at a time and every request must be covered by exactly one rerun. The window between a
        // loop's own flag clear and its cleanup has no outside seam; the stress test below hammers it instead.
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader, queuedContext: false);
        var deliveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDelivery = new ManualResetEventSlim(false);
        var blocked = 0;
        h.Vm.Connections.OpenCodeCard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionCardViewModel.StateWord) && Interlocked.Exchange(ref blocked, 1) == 0)
            {
                deliveryEntered.TrySetResult();
                releaseDelivery.Wait(Bound);
            }
        };

        h.Vm.RequestOpenCodeCheck(); // A
        await reader.WaitForCallsAsync(1);
        reader.Complete(0, Connected("ReadA"));
        await deliveryEntered.Task.WaitAsync(Bound); // A is paused before its cleanup

        h.Vm.RequestOpenCodeCheck(); // B
        h.Vm.RequestOpenCodeCheck(); // C
        Assert.Equal(1, reader.CallCount); // never a second read beside A

        releaseDelivery.Set(); // A resumes
        await reader.WaitForCallsAsync(2); // exactly one rerun for B and C
        h.Vm.RequestOpenCodeCheck(); // D while the rerun is in flight
        Assert.Equal(2, reader.CallCount);

        reader.Complete(1, Connected("ReadB"));
        await reader.WaitForCallsAsync(3); // D's rerun
        reader.Complete(2, Connected("ReadD"));
        Assert.False(await reader.TryWaitForCallAsync(4, AbsenceBound), "an extra read ran");

        // The flag was released exactly once by its owner: a fresh request starts a fresh read now.
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(4);
        reader.Complete(3, Connected("ReadE"));
        Assert.Equal(1, reader.MaxConcurrent);
        Assert.Equal(4, reader.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryThatThrows_IsObserved_AndAPendingRerunStillRuns_H11(bool queuedContext)
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader, queuedContext);
        const string marker = "SENTINEL delivery failure H11";
        var unobserved = new List<string>();

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.ToString().Contains(marker, StringComparison.Ordinal))
            {
                lock (unobserved)
                {
                    unobserved.Add(e.Exception.Message);
                }
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var throwsLeft = 1;
            h.Vm.Connections.OpenCodeCard.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConnectionCardViewModel.StateWord) && Interlocked.Decrement(ref throwsLeft) == 0)
                {
                    throw new InvalidOperationException(marker);
                }
            };

            h.Vm.RequestOpenCodeCheck(); // A
            await reader.WaitForCallsAsync(1);
            h.Vm.RequestOpenCodeCheck(); // the pending rerun, requested while A is in flight
            reader.Complete(0, Connected("ReadA")); // delivering A throws
            if (queuedContext)
            {
                await h.Context!.WaitForPostsAsync(1);
                h.Context.Drain();
            }

            // The throw must not lose the pending rerun.
            await PumpUntilCallAsync(h, reader, 2);
            reader.Complete(1, Connected("ReadB"));
            if (queuedContext)
            {
                await h.Context!.WaitForPostsAsync(2);
                h.Context.Drain();
            }

            // And the flag is not stuck: a later request starts a new read.
            await WaitUntilRequestStartsAReadAsync(h, reader, 3);
            reader.Complete(2, Connected("ReadC"));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            lock (unobserved)
            {
                Assert.Empty(unobserved); // the delivery exception was observed (caught), not left on a dropped task
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task DeliveryThatThrows_WithoutAPendingRequest_ReleasesTheFlag_H11()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader, queuedContext: false);
        var throwsLeft = 1;
        h.Vm.Connections.OpenCodeCard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionCardViewModel.StateWord) && Interlocked.Decrement(ref throwsLeft) == 0)
            {
                throw new InvalidOperationException("SENTINEL delivery failure, no pending");
            }
        };
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);
        reader.Complete(0, Connected("ReadA"));

        // The loop ends after the failed delivery. A request that arrives before it released the flag is the pending
        // rerun, one that arrives after starts a fresh read: either way read #2 must happen (flag not stuck).
        await WaitUntilRequestStartsAReadAsync(h, reader, 2);
    }

    [Fact]
    public async Task ManyThreadsHammeringRequests_NeverRunTwoReadsAtOnce_H11()
    {
        // Stress for the window between a read loop clearing the flag and its cleanup. A correct implementation can
        // never fail this; the pre-fix code lets a stale cleanup clear a newer read's flag (Reviewer2 #3), after
        // which two reads overlap. Probabilistic for the buggy code, deterministic for a correct one.
        var reader = new ConcurrencyReader();
        await using var h = VmHarness.Create(reader, queuedContext: false);
        using var start = new Barrier(4);

        var hammer = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 20_000; i++)
            {
                h.Vm.RequestOpenCodeCheck();
            }
        })).ToArray();
        await Task.WhenAll(hammer).WaitAsync(Bound);

        reader.MarkFinal();
        h.Vm.RequestOpenCodeCheck(); // guarantees one more read that starts after MarkFinal
        await reader.FinalRead.WaitAsync(Bound);

        Assert.Equal(1, reader.MaxConcurrent);
        Assert.True(reader.Reads >= 2);
    }

    // ── H12 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RedetectAll_StartsTheOpenCodeRead_BeforeTheProviderRefreshCompletes_H12()
    {
        var reader = new FakeOpenCodeReader();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = VmHarness.Create(reader, claudeGate: gate.Task);
        var refreshDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawRefreshing = false;
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsRefreshing))
            {
                if (h.Vm.IsRefreshing)
                {
                    sawRefreshing = true;
                }
                else if (sawRefreshing)
                {
                    refreshDone.TrySetResult();
                }
            }
        };

        h.Vm.Connections.RedetectAllCommand.Execute(null);
        await h.Claude.FirstCall.WaitAsync(Bound); // the provider refresh is running and held at the gate
        Assert.True(
            await reader.TryWaitForCallAsync(1, TimeSpan.FromSeconds(10)),
            "The OpenCode read must start without waiting for the provider refresh (H12).");
        Assert.False(refreshDone.Task.IsCompleted); // ...while the refresh has not completed

        reader.Complete(0, Connected("Anthropic"));
        await h.Context!.WaitForPostsAsync(1);
        h.Context.Drain();
        Assert.Equal("Connected", h.Vm.Connections.OpenCodeCard.StateWord); // delivered before the refresh finished
        Assert.False(refreshDone.Task.IsCompleted);

        gate.SetResult();
        await refreshDone.Task.WaitAsync(Bound);
        Assert.Equal(1, reader.CallCount);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Requests until a read with that number starts (a request either starts it or is the pending rerun
    /// that does); a flag that stays set never lets it start and the wait times out.</summary>
    private static async Task WaitUntilRequestStartsAReadAsync(VmHarness h, FakeOpenCodeReader reader, int callNumber)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (reader.CallCount < callNumber)
        {
            Assert.True(DateTime.UtcNow < deadline, $"No read #{callNumber} started: the in-flight flag is stuck.");
            h.Vm.RequestOpenCodeCheck();
            h.Context?.Drain();
            await reader.TryWaitForCallAsync(callNumber, TimeSpan.FromMilliseconds(50));
        }
    }

    /// <summary>Completes every read asynchronously and records how many were ever pending at once.</summary>
    private sealed class ConcurrencyReader : IOpenCodeAuthReader
    {
        private readonly TaskCompletionSource _finalRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        private int _max;
        private int _reads;
        private volatile bool _final;

        public int MaxConcurrent => Volatile.Read(ref _max);

        public int Reads => Volatile.Read(ref _reads);

        public Task FinalRead => _finalRead.Task;

        public void MarkFinal() => _final = true;

        public async Task<OpenCodeAuthStatus> ReadAsync(CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _running);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            Interlocked.Increment(ref _reads);
            var final = _final;
            await Task.Yield();
            Interlocked.Decrement(ref _running);
            if (final)
            {
                _finalRead.TrySetResult();
            }

            return Connected("Stress");
        }
    }
}
