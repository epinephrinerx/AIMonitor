namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-020 fix round 2 (Docs/SPEC_R020_R021.md): J4 (a canceled read is not delivered to the card, the loop carries on
/// as after a delivery and never leaves the in-flight flag set) and J5 (the <c>OpenCodeCheckLoopEndedHook</c> seam:
/// the deterministic H11 interleaving and a signal in place of the old 2 s "nothing else happens" watches). Partial
/// class of <see cref="MainWindowViewModelOpenCodeCheckTests"/> so the fakes and the harness are shared.
/// </summary>
public sealed partial class MainWindowViewModelOpenCodeCheckTests
{
    /// <summary>How long J5's one remaining negative watch looks for a read #3 that starts beside the held read #2.
    /// A correct implementation can never fail it; it can only miss the bug if the bug needed longer than this.</summary>
    private static readonly TimeSpan StaleCleanupWatch = TimeSpan.FromSeconds(1);

    // ── J4 ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACanceledRead_IsNotDelivered_TheCardStaysChecking_AndTheFlagIsReleased_J4()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);

        reader.Cancel(0);
        await h.WaitForLoopEndsAsync(1); // the loop ended: the flag is clear

        Assert.Equal(0, h.Context!.PostCount); // nothing was delivered
        Assert.Equal("Checking...", h.Vm.Connections.OpenCodeCard.StateWord); // not "Unavailable"
        Assert.Equal("–", h.Vm.Connections.OpenCodeCard.StateGlyph);
        Assert.Equal(1, reader.CallCount);
    }

    [Fact]
    public async Task ACanceledRead_LeavesAPreviousResultOnTheCard_J4()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);
        reader.Complete(0, Connected("Anthropic"));
        await h.Context!.WaitForPostsAsync(1);
        await h.WaitForLoopEndsAsync(1);
        Assert.Equal("Connected", h.Vm.Connections.OpenCodeCard.StateWord);

        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(2);
        reader.Cancel(1);
        await h.WaitForLoopEndsAsync(2);

        Assert.Equal(1, h.Context.PostCount); // only the first read was delivered
        Assert.Equal("Connected", h.Vm.Connections.OpenCodeCard.StateWord);
        Assert.Equal("Signed in: Anthropic (OAuth)", h.Vm.Connections.OpenCodeCard.Account);
    }

    [Fact]
    public async Task ARequestAfterACanceledRead_StartsANewRead_J4()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);
        reader.Cancel(0);
        await h.WaitForLoopEndsAsync(1);

        h.Vm.RequestOpenCodeCheck();
        await PumpUntilCallAsync(h, reader, 2); // a stuck flag would make this a pending request that never runs
        reader.Complete(1, Connected("Later"));
        await h.Context!.WaitForPostsAsync(1);
        await h.WaitForLoopEndsAsync(2);

        Assert.Equal("Connected", h.Vm.Connections.OpenCodeCard.StateWord);
        Assert.Equal("Signed in: Later (OAuth)", h.Vm.Connections.OpenCodeCard.Account);
        Assert.Equal(2, reader.CallCount);
        Assert.Equal(1, reader.MaxConcurrent);
    }

    [Fact]
    public async Task ARequestDuringACanceledRead_GetsItsRerun_AndTheCanceledReadIsNotDelivered_J4()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);
        h.Vm.RequestOpenCodeCheck(); // pending while read #1 is in flight
        h.Vm.RequestOpenCodeCheck(); // coalesced

        reader.Cancel(0);
        await PumpUntilCallAsync(h, reader, 2); // the rerun, as after a delivery
        Assert.Equal(0, h.Context!.PostCount); // the canceled read delivered nothing
        Assert.Equal("Checking...", h.Vm.Connections.OpenCodeCard.StateWord);

        reader.Complete(1, Connected("Rerun"));
        await h.Context.WaitForPostsAsync(1);
        await h.WaitForLoopEndsAsync(1);

        Assert.Equal("Signed in: Rerun (OAuth)", h.Vm.Connections.OpenCodeCard.Account);
        Assert.Equal(2, reader.CallCount); // exactly one rerun
        Assert.Equal(1, reader.MaxConcurrent);
        Assert.Equal(1, h.Context.PostCount);
    }

    [Fact]
    public async Task AReaderThatThrowsOperationCanceled_IsTreatedLikeACanceledTask_J4()
    {
        var reader = new FakeOpenCodeReader { ThrowOnRead = new OperationCanceledException() };
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await h.WaitForLoopEndsAsync(1);

        Assert.Equal(0, h.Context!.PostCount);
        Assert.Equal("Checking...", h.Vm.Connections.OpenCodeCard.StateWord);
        Assert.Equal(1, reader.CallCount);

        reader.ThrowOnRead = null; // the flag was released: a later request reads again
        h.Vm.RequestOpenCodeCheck();
        await PumpUntilCallAsync(h, reader, 2);
        reader.Complete(1, Connected("Later"));
        await h.Context.WaitForPostsAsync(1);
        await h.WaitForLoopEndsAsync(2);
        Assert.Equal("Signed in: Later (OAuth)", h.Vm.Connections.OpenCodeCard.Account);
    }

    [Fact]
    public async Task AFaultThatIsNotCancellation_StillShowsTheErrorText_J4()
    {
        // Guards the J4 change against over-reach: only cancellation is dropped, any other fault is still delivered.
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);
        reader.Fail(0, new InvalidOperationException("boom"));
        await h.Context!.WaitForPostsAsync(1);
        await h.WaitForLoopEndsAsync(1);

        Assert.Equal(1, h.Context.PostCount);
        Assert.NotEqual("Checking...", h.Vm.Connections.OpenCodeCard.StateWord);
    }

    // ── J5 ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheLoopEndedHook_RunsOncePerLoop_AfterTheFlagWasCleared_J5()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader);
        h.Vm.RequestOpenCodeCheck();
        await reader.WaitForCallsAsync(1);
        h.Vm.RequestOpenCodeCheck(); // pending: the same loop runs a rerun
        reader.Complete(0, Connected("Read1"));
        await PumpUntilCallAsync(h, reader, 2);
        Assert.Equal(0, h.LoopEnds.Count); // the loop is still running: two reads, ONE loop, no hook yet

        reader.Complete(1, Connected("Read2"));
        await h.WaitForLoopEndsAsync(1);
        Assert.Equal(1, h.LoopEnds.Count);

        // The flag was already clear when the hook ran: a request now starts a new loop with its own hook call.
        h.Vm.RequestOpenCodeCheck();
        await PumpUntilCallAsync(h, reader, 3);
        reader.Complete(2, Connected("Read3"));
        await h.WaitForLoopEndsAsync(2);
        Assert.Equal(2, h.LoopEnds.Count);
        Assert.Equal(3, reader.CallCount);
    }

    [Fact]
    public async Task ARequestInsideTheLoopEndedHook_StartsANewRead_NotAPendingOne_J5()
    {
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader, queuedContext: false);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.OnLoopEnded = async number =>
        {
            if (number == 1)
            {
                h.Vm.RequestOpenCodeCheck(); // B, while A's loop is parked in the hook
                await releaseA.Task;
            }
        };

        try
        {
            h.Vm.RequestOpenCodeCheck(); // A
            await reader.WaitForCallsAsync(1);
            reader.Complete(0, Connected("ReadA"));
            await reader.WaitForCallsAsync(2); // B is a new read, started inside A's hook: the flag was already clear
            Assert.Equal(1, h.LoopEnds.Count);
        }
        finally
        {
            releaseA.TrySetResult();
        }

        reader.Complete(1, Connected("ReadB"));
        await h.WaitForLoopEndsAsync(2);
        Assert.Equal(2, reader.CallCount);
        Assert.Equal(1, reader.MaxConcurrent);
    }

    [Fact]
    public async Task AStaleCleanupOfReadLoopA_CannotClearTheFlagOfReadLoopB_RequestCCoalesces_H11_J5()
    {
        // Reviewer2 #3 with the J5 seam. A's loop has cleared its flag and is parked in the hook; request B arrives
        // there and starts read #2 (B's flag is set and B is held); A is released and returns; requests then arrive
        // while B is held. They must coalesce into B's pending rerun. If A's tail cleared B's flag, a request after
        // that would start read #3 beside the held #2.
        var reader = new FakeOpenCodeReader();
        await using var h = VmHarness.Create(reader, queuedContext: false);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.OnLoopEnded = async number =>
        {
            if (number == 1)
            {
                h.Vm.RequestOpenCodeCheck(); // B
                await releaseA.Task;
            }
        };

        try
        {
            h.Vm.RequestOpenCodeCheck(); // A
            await reader.WaitForCallsAsync(1);
            reader.Complete(0, Connected("ReadA"));
            await reader.WaitForCallsAsync(2); // B started and is held
        }
        finally
        {
            releaseA.TrySetResult(); // A's loop returns now (its tail runs asynchronously: see the hammering below)
        }

        // Request C, and keep requesting for the whole watch: A's tail may run any time after the release, and the
        // bug shows only for a request that arrives after it. With a correct implementation every request coalesces
        // into B's single pending rerun, so read #3 can never start while read #2 is held. If A's tail cleared B's
        // flag, the first request after it starts read #3 beside the held #2.
        var watchEnd = DateTime.UtcNow + StaleCleanupWatch;
        do
        {
            h.Vm.RequestOpenCodeCheck(); // C and its repeats
            await Task.Delay(10);
            Assert.True(
                reader.CallCount == 2,
                "Read #3 started while read #2 was still in flight: A's stale cleanup cleared B's in-flight flag (H11).");
        }
        while (DateTime.UtcNow < watchEnd);
        Assert.False(await reader.TryWaitForCallAsync(3, TimeSpan.FromMilliseconds(200)), "Read #3 started beside the held read #2 (H11).");

        reader.Complete(1, Connected("ReadB"));
        await reader.WaitForCallsAsync(3); // C's coalesced rerun, in B's loop
        reader.Complete(2, Connected("ReadC"));
        await h.WaitForLoopEndsAsync(2); // B's loop ended after the rerun

        Assert.Equal(3, reader.CallCount);
        Assert.Equal(1, reader.MaxConcurrent);

        h.Vm.RequestOpenCodeCheck(); // D: a fresh loop, the flag is not stuck
        await reader.WaitForCallsAsync(4);
        reader.Complete(3, Connected("ReadD"));
        await h.WaitForLoopEndsAsync(3);
        Assert.Equal(4, reader.CallCount);
        Assert.Equal(1, reader.MaxConcurrent);
    }
}
