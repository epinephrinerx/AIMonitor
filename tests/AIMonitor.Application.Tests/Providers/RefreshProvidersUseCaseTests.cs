using AIMonitor.Application.Providers;
using AIMonitor.Domain;

namespace AIMonitor.Application.Tests.Providers;

public sealed class RefreshProvidersUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_UnexpectedFailureIsSanitizedAndNextProviderRuns()
    {
        var afterCalls = 0;
        var useCase = Create(
            ("claude", (_, _) => throw new InvalidOperationException("secret detail")),
            ("gemini", (_, _) =>
            {
                afterCalls++;
                return Task.FromResult(new ProviderSnapshot("gemini", configured: true));
            }
        ));

        var result = await useCase.ExecuteAsync(7, new ProviderSnapshotRequest(30, "Total tokens", true), CancellationToken.None);

        Assert.Equal(7, result.RequestId);
        Assert.Equal(30, result.Request.HistoryDays);
        Assert.Equal("Unexpected provider error. Try again.", result.Snapshots["claude"].Error);
        Assert.DoesNotContain("secret", result.Snapshots["claude"].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, afterCalls);
        Assert.True(result.Snapshots["gemini"].Ok);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationStopsBeforeNextProvider()
    {
        using var cts = new CancellationTokenSource();
        var afterCalls = 0;
        var useCase = Create(
            ("claude", (_, token) =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new ProviderSnapshot("claude"));
            }
        ),
            ("openai", (_, _) =>
            {
                afterCalls++;
                return Task.FromResult(new ProviderSnapshot("openai"));
            }
        ));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => useCase.ExecuteAsync(1, ProviderSnapshotRequest.Default, cts.Token));
        Assert.Equal(0, afterCalls);
    }

    [Fact]
    public void Constructor_DuplicateProviderId_Throws()
    {
        var client = new StubClient((_, _) => Task.FromResult(new ProviderSnapshot("claude")));
        Assert.Throws<ArgumentException>(() => new RefreshProvidersUseCase(
            [new("claude", client), new("claude", client)]));
    }

    [Fact]
    public async Task QueueAsync_WhileBusyRunsOnlyNewestQueuedRequest()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenDays = new List<int>();
        var client = new StubClient(async (request, token) =>
        {
            seenDays.Add(request.HistoryDays);
            if (request.HistoryDays == 14)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
            return new ProviderSnapshot("claude", configured: true);
        });
        var useCase = new RefreshProvidersUseCase([new("claude", client)]);
        await using var coordinator = new LatestRefreshCoordinator(useCase);

        var first = coordinator.QueueAsync(1, new ProviderSnapshotRequest(14));
        await firstStarted.Task;
        var superseded = coordinator.QueueAsync(2, new ProviderSnapshotRequest(30));
        var newest = coordinator.QueueAsync(3, new ProviderSnapshotRequest(7));
        releaseFirst.TrySetResult();

        Assert.Equal(1, (await first).RequestId);
        Assert.Equal(3, (await superseded).RequestId);
        Assert.Equal(3, (await newest).RequestId);
        Assert.Equal([14, 7], seenDays);
    }

    [Fact]
    public async Task DisposeAsync_CancelsActiveAndQueuedRefreshes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubClient(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ProviderSnapshot("claude");
        });
        var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([new("claude", client)]));
        var active = coordinator.QueueAsync(1, ProviderSnapshotRequest.Default);
        await started.Task;
        var queued = coordinator.QueueAsync(2, new ProviderSnapshotRequest(30));

        await coordinator.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
    }

    [Fact]
    public async Task QueueAsync_AfterSynchronousCompletion_StartsAnotherRunner()
    {
        var calls = 0;
        var client = new StubClient((_, _) =>
        {
            calls++;
            return Task.FromResult(new ProviderSnapshot("claude", configured: true));
        });
        await using var coordinator = new LatestRefreshCoordinator(
            new RefreshProvidersUseCase([new("claude", client)]));

        var first = await coordinator.QueueAsync(1, ProviderSnapshotRequest.Default);
        var second = await coordinator.QueueAsync(2, ProviderSnapshotRequest.Default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, first.RequestId);
        Assert.Equal(2, second.RequestId);
        Assert.Equal(2, calls);
    }

    private static RefreshProvidersUseCase Create(
        params (string Id, Func<ProviderSnapshotRequest, CancellationToken, Task<ProviderSnapshot>> Handler)[] providers) =>
        new(providers.Select(provider => new ProviderClientRegistration(provider.Id, new StubClient(provider.Handler))));

    private sealed class StubClient(
        Func<ProviderSnapshotRequest, CancellationToken, Task<ProviderSnapshot>> handler) : IProviderQuotaClient
    {
        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
