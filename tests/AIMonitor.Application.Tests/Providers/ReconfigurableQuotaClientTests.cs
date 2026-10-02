namespace AIMonitor.Application.Tests.Providers;

using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.TestSupport;

public sealed class ReconfigurableQuotaClientTests
{
    private static (ProviderConnectionStore Store, TestSecretStore SecretStore, SettingsSession Session) CreateStore(
        string providerId, string? initialKey = null, string initialExtra = "")
    {
        var secretMap = new Dictionary<string, string>();
        if (initialKey is not null)
        {
            secretMap[$"providers/{providerId}/key"] = initialKey;
        }

        var secretStore = new TestSecretStore(secretMap);
        var initialSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>
            {
                [providerId] = new(Enabled: true, Extra: initialExtra)
            }
        };

        var settingsStore = new BlockingSettingsStore(initialSettings);
        settingsStore.ReleaseSource.TrySetResult();
        var session = new SettingsSession(settingsStore, initialSettings);
        var store = new ProviderConnectionStore(secretStore, session);

        return (store, secretStore, session);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenKeyChangesBetweenCalls_InvokesFactoryWithNewConnection()
    {
        var (store, _, _) = CreateStore("openai", initialKey: "key-1", initialExtra: "100");
        var factoryInvocations = new List<ProviderConnection>();

        using var client = new ReconfigurableQuotaClient(
            store,
            "openai",
            conn =>
            {
                factoryInvocations.Add(conn);
                return new DisposableTestClient();
            });

        var snap1 = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);
        Assert.NotNull(snap1);
        Assert.Single(factoryInvocations);
        Assert.Equal("key-1", factoryInvocations[0].Key);
        Assert.Equal("100", factoryInvocations[0].Extra);

        // Update key
        await store.SaveAsync("openai", typedKey: "key-2", clearRequested: false, extra: null);

        var snap2 = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);
        Assert.NotNull(snap2);
        Assert.Equal(2, factoryInvocations.Count);
        Assert.Equal("key-2", factoryInvocations[1].Key);
        Assert.Equal("100", factoryInvocations[1].Extra);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenConnectionUnchanged_CachesAndDoesNotReinvokeFactory()
    {
        var (store, _, _) = CreateStore("openai", initialKey: "key-constant", initialExtra: "50");
        var factoryCallCount = 0;

        using var client = new ReconfigurableQuotaClient(
            store,
            "openai",
            conn =>
            {
                factoryCallCount++;
                return new DisposableTestClient();
            });

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);
        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);
        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Equal(1, factoryCallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenExtraChanges_InvokesFactoryWithNewConnection()
    {
        var (store, _, _) = CreateStore("openai", initialKey: "key-1", initialExtra: "50");
        var factoryInvocations = new List<ProviderConnection>();

        using var client = new ReconfigurableQuotaClient(
            store,
            "openai",
            conn =>
            {
                factoryInvocations.Add(conn);
                return new DisposableTestClient();
            });

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        // Update extra only
        await store.SaveAsync("openai", typedKey: null, clearRequested: false, extra: "75");

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.Equal(2, factoryInvocations.Count);
        Assert.Equal("50", factoryInvocations[0].Extra);
        Assert.Equal("75", factoryInvocations[1].Extra);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenReconfigured_DisposesPreviousInnerClient()
    {
        var (store, _, _) = CreateStore("openai", initialKey: "key-v1");
        var createdClients = new List<DisposableTestClient>();

        using var client = new ReconfigurableQuotaClient(
            store,
            "openai",
            conn =>
            {
                var inner = new DisposableTestClient();
                createdClients.Add(inner);
                return inner;
            });

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);
        Assert.Single(createdClients);
        Assert.False(createdClients[0].IsDisposed);

        // Change key -> triggers new client creation and disposal of old client
        await store.SaveAsync("openai", typedKey: "key-v2", clearRequested: false, extra: null);

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);
        Assert.Equal(2, createdClients.Count);
        Assert.True(createdClients[0].IsDisposed);
        Assert.False(createdClients[1].IsDisposed);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var (store, _, _) = CreateStore("openai", initialKey: "key");
        using var client = new ReconfigurableQuotaClient(
            store,
            "openai",
            _ => new DisposableTestClient());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSnapshotAsync(ProviderSnapshotRequest.Default, cts.Token));
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenCancelledWhileRequestBlocked_ThrowsOperationCanceledException_AndReleasesLease()
    {
        var (store, _, _) = CreateStore("openai", initialKey: "key-1");
        BlockingDisposableTestClient? inner = null;

        var client = new ReconfigurableQuotaClient(
            store,
            "openai",
            _ =>
            {
                inner = new BlockingDisposableTestClient();
                return inner;
            });

        using var cts = new CancellationTokenSource();
        var req = client.GetSnapshotAsync(ProviderSnapshotRequest.Default, cts.Token);

        Assert.NotNull(inner);
        await inner.StartedTcs.Task;

        // Cancel while the inner client request is blocked
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => req);

        // Assert the lease was released: inner must not be disposed prior to retire,
        // and when retired (via Dispose), inner must be disposed because lease count is 0.
        Assert.Equal(0, inner.DisposeCount);
        client.Dispose();
        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public async Task Dispose_DisposesCurrentInnerClient()
    {
        var (store, _, _) = CreateStore("openai", initialKey: "key");
        DisposableTestClient? inner = null;

        var client = new ReconfigurableQuotaClient(
            store,
            "openai",
            _ =>
            {
                inner = new DisposableTestClient();
                return inner;
            });

        await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);
        Assert.NotNull(inner);
        Assert.False(inner.IsDisposed);

        client.Dispose();
        Assert.True(inner.IsDisposed);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None));
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenConnectionChangesMidRequest_DefersDisposalUntilBlockedRequestCompletes()
    {
        var (store, _, _) = CreateStore("openai", initialKey: "key-1");
        var clients = new List<BlockingDisposableTestClient>();

        using var client = new ReconfigurableQuotaClient(
            store,
            "openai",
            _ =>
            {
                var c = new BlockingDisposableTestClient();
                clients.Add(c);
                return c;
            });

        // Start request 1 on client 1 (will block)
        var req1 = client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);
        await clients[0].StartedTcs.Task;

        // Change connection in store
        await store.SaveAsync("openai", typedKey: "key-2", clearRequested: false, extra: null);

        // Start request 2 on client 2
        var req2 = client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);
        await clients[1].StartedTcs.Task;

        // Client 1 must NOT be disposed while its request is still running
        Assert.Equal(0, clients[0].DisposeCount);

        // Complete request 2
        clients[1].BlockTcs.TrySetResult(true);
        await req2;

        // Client 1 STILL must NOT be disposed while request 1 is running
        Assert.Equal(0, clients[0].DisposeCount);

        // Release client 1 request
        clients[0].BlockTcs.TrySetResult(true);
        await req1;

        // Now client 1 must be disposed exactly once
        Assert.Equal(1, clients[0].DisposeCount);
    }

    [Fact]
    public async Task Dispose_WhenCalledMidRequest_DefersInnerDisposalUntilBlockedRequestCompletes()
    {
        var (store, _, _) = CreateStore("openai", initialKey: "key-1");
        BlockingDisposableTestClient? inner = null;

        var client = new ReconfigurableQuotaClient(
            store,
            "openai",
            _ =>
            {
                inner = new BlockingDisposableTestClient();
                return inner;
            });

        var req = client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);
        Assert.NotNull(inner);
        await inner.StartedTcs.Task;

        // Call Dispose() mid-request
        client.Dispose();

        // Inner must NOT be disposed yet while request is still running
        Assert.Equal(0, inner.DisposeCount);

        // Release the blocked request
        inner.BlockTcs.TrySetResult(true);
        await req;

        // Now inner must be disposed exactly once
        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_PropagatesCancellationTokenToStoreAndInnerClient()
    {
        var secretStore = new TokenCapturingSecretStore();
        var initialSettings = new AppSettings();
        var settingsStore = new BlockingSettingsStore(initialSettings);
        settingsStore.ReleaseSource.TrySetResult();
        var session = new SettingsSession(settingsStore, initialSettings);
        var store = new ProviderConnectionStore(secretStore, session);

        TokenCapturingTestClient? inner = null;
        using var client = new ReconfigurableQuotaClient(
            store,
            "openai",
            _ =>
            {
                inner = new TokenCapturingTestClient();
                return inner;
            });

        using var cts = new CancellationTokenSource();
        var distinctToken = cts.Token;
        Assert.False(distinctToken.IsCancellationRequested);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, distinctToken);
        Assert.NotNull(snapshot);

        Assert.NotNull(inner);
        Assert.Equal(distinctToken, secretStore.CapturedGetToken);
        Assert.Equal(distinctToken, inner.CapturedToken);
    }

    private sealed class DisposableTestClient : IProviderQuotaClient, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderSnapshot("openai", configured: true));
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class BlockingDisposableTestClient : IProviderQuotaClient, IDisposable
    {
        public TaskCompletionSource<bool> StartedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BlockTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount;

        public async Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
        {
            StartedTcs.TrySetResult(true);
            await BlockTcs.Task.WaitAsync(cancellationToken);
            return new ProviderSnapshot("openai", configured: true);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
        }
    }

    private sealed class TokenCapturingTestClient : IProviderQuotaClient
    {
        public CancellationToken CapturedToken { get; private set; }

        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken)
        {
            CapturedToken = cancellationToken;
            return Task.FromResult(new ProviderSnapshot("openai", configured: true));
        }
    }

    private sealed class TokenCapturingSecretStore : ISecretStore
    {
        public CancellationToken CapturedGetToken { get; private set; }

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            CapturedGetToken = cancellationToken;
            return Task.FromResult<string?>("secret-key");
        }

        public Task SetAsync(string name, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
