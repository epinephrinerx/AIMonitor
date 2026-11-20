using AIMonitor.Infrastructure.MacOS;

namespace AIMonitor.Infrastructure.Tests.MacOS;

/// <summary>
/// These drive real Unix domain sockets rather than a fake, because the behaviour under test is
/// exactly what the operating system does when two copies of the app start: the loser must be the
/// one that exits. Unix sockets exist on every platform .NET supports, so these run anywhere.
///
/// The directory name is kept short on purpose: a Unix socket path has about 104 bytes to live in,
/// and a GUID-length temp directory plus the socket name overruns it.
/// </summary>
public sealed class LocalSocketSingleInstanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "aim" + Guid.NewGuid().ToString("N")[..6]);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private LocalSocketSingleInstance Coordinator(TimeSpan? connectTimeout = null) =>
        LocalSocketSingleInstance.ForSocketPath(
            Path.Combine(_directory, "si.sock"), connectTimeout ?? TimeSpan.FromSeconds(2));

    [Fact]
    public async Task TryAcquire_FirstInstance_TakesOwnership()
    {
        await using var first = Coordinator();

        Assert.True(await first.TryAcquireAsync());
        Assert.True(first.HasOwnership);
        Assert.True(File.Exists(first.SocketPath));
    }

    [Fact]
    public async Task TryAcquire_SecondInstance_IsRefusedWhileTheFirstKeepsOwnership()
    {
        await using var first = Coordinator();
        Assert.True(await first.TryAcquireAsync());

        await using var second = Coordinator();

        Assert.False(await second.TryAcquireAsync());
        Assert.False(second.HasOwnership);
        Assert.True(first.HasOwnership);
    }

    [Fact]
    public async Task TryAcquire_IsIdempotentForTheOwner()
    {
        await using var first = Coordinator();

        Assert.True(await first.TryAcquireAsync());
        Assert.True(await first.TryAcquireAsync());
    }

    [Fact]
    public async Task NotifyExistingInstance_DeliversTheArgumentsToTheRunningCopy()
    {
        await using var first = Coordinator();
        Assert.True(await first.TryAcquireAsync());

        var delivered = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Activated += arguments => delivered.TrySetResult(arguments);

        await using var second = Coordinator();
        Assert.False(await second.TryAcquireAsync());
        Assert.True(await second.NotifyExistingInstanceAsync(["--tray", "--open=usage log"]));

        var received = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["--tray", "--open=usage log"], received);
    }

    [Fact]
    public async Task NotifyExistingInstance_WithNoArguments_StillActivatesTheRunningCopy()
    {
        await using var first = Coordinator();
        Assert.True(await first.TryAcquireAsync());

        var delivered = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Activated += arguments => delivered.TrySetResult(arguments);

        await using var second = Coordinator();
        Assert.True(await second.NotifyExistingInstanceAsync([]));

        Assert.Empty(await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task NotifyExistingInstance_WhenNothingIsRunning_ReportsFailureRatherThanHanging()
    {
        await using var lonely = Coordinator(TimeSpan.FromMilliseconds(500));

        Assert.False(await lonely.NotifyExistingInstanceAsync(["--tray"]));
    }

    [Fact]
    public async Task TryAcquire_AfterTheOwnerExits_IsGrantedAgain()
    {
        var first = Coordinator();
        Assert.True(await first.TryAcquireAsync());
        await first.DisposeAsync();

        await using var second = Coordinator();

        Assert.True(await second.TryAcquireAsync());
    }

    [Fact]
    public async Task TryAcquire_WhenACrashedCopyLeftItsSocketFileBehind_ReclaimsIt()
    {
        var socketPath = Path.Combine(_directory, "si.sock");
        Directory.CreateDirectory(_directory);
        // A file at the socket's path with nothing listening is what a killed process leaves; bind
        // fails with "address already in use" until it is removed.
        await File.WriteAllTextAsync(socketPath, "leftover");

        await using var coordinator = Coordinator(TimeSpan.FromMilliseconds(500));

        Assert.True(await coordinator.TryAcquireAsync());
    }

    [Fact]
    public async Task Dispose_RemovesTheSocketFileSoNothingIsLeftOnDisk()
    {
        var coordinator = Coordinator();
        Assert.True(await coordinator.TryAcquireAsync());
        var path = coordinator.SocketPath;

        await coordinator.DisposeAsync();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Dispose_IsSafeToCallTwice()
    {
        var coordinator = Coordinator();
        Assert.True(await coordinator.TryAcquireAsync());

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquire_AfterDispose_Throws()
    {
        var coordinator = Coordinator();
        await coordinator.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await coordinator.TryAcquireAsync());
    }

    [Fact]
    public void Constructor_WhenThePathIsTooLongForASocket_SaysSoAndByHowMuch()
    {
        var tooLong = "/tmp/" + new string('x', 120) + ".sock";

        var exception = Assert.Throws<ArgumentException>(() => LocalSocketSingleInstance.ForSocketPath(tooLong));

        Assert.Contains("more than a Unix domain socket allows", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSocketPath_IsStableForOneUserAndAppButDiffersAcrossApps()
    {
        var first = LocalSocketSingleInstance.ResolveSocketPath("/tmp", "AIUsageMonitor");
        var again = LocalSocketSingleInstance.ResolveSocketPath("/tmp", "AIUsageMonitor");
        var other = LocalSocketSingleInstance.ResolveSocketPath("/tmp", "AIUsageMonitor.Test");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.StartsWith(Path.Combine("/tmp", "si-"), first, StringComparison.Ordinal);
        Assert.EndsWith(".sock", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyExistingInstance_WithAPayloadOverTheLimit_IsRefusedBeforeItIsSent()
    {
        await using var coordinator = Coordinator(TimeSpan.FromMilliseconds(500));
        var huge = new[] { new string('a', 70 * 1024) };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await coordinator.NotifyExistingInstanceAsync(huge));
    }
}
