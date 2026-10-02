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
}
