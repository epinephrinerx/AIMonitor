namespace AIMonitor.Application.Tests.Providers;

using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.TestSupport;
using Xunit;

public sealed class ProviderConnectionStoreTests
{
    private static (ProviderConnectionStore Store, TestSecretStore SecretStore, SettingsSession Session) CreateStore(
        AppSettings? initialSettings = null)
    {
        var settings = initialSettings ?? new AppSettings();
        var secretStore = new TestSecretStore(new Dictionary<string, string>());
        var settingsStore = new BlockingSettingsStore(settings);
        settingsStore.Release();
        var session = new SettingsSession(settingsStore, settings);
        var store = new ProviderConnectionStore(secretStore, session);
        return (store, secretStore, session);
    }

    [Fact]
    public async Task GetAsync_ForAgy_ReturnsSecretlessConnectionWithoutTouchingSecretStore()
    {
        var (store, secretStore, session) = CreateStore(new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["agy"] = new(Enabled: true, Extra: "ignored-extra")
            }
        });

        var conn = await store.GetAsync("agy");

        Assert.Null(conn.Key);
        Assert.Equal("", conn.Extra);
        Assert.Equal(0, secretStore.GetCalls);
    }

    [Fact]
    public async Task SaveAsync_ForAgy_WhenKeyProvided_ThrowsArgumentException()
    {
        var (store, secretStore, _) = CreateStore();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync("agy", "some-key", clearRequested: false, extra: null));

        Assert.Contains("does not use an API key", ex.Message);
        Assert.Equal(0, secretStore.SetCalls);
    }

    [Fact]
    public async Task SaveAsync_ForAgy_WhenExtraProvided_ThrowsArgumentException()
    {
        var (store, secretStore, _) = CreateStore();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync("agy", typedKey: null, clearRequested: false, extra: "some-extra"));

        Assert.Contains("does not support extra settings", ex.Message);
        Assert.Equal(0, secretStore.SetCalls);
    }

    [Fact]
    public async Task SaveAsync_ForAgy_WhenKeyAndExtraNullOrEmpty_FiresChangedWithoutTouchingSecretsOrSession()
    {
        var (store, secretStore, session) = CreateStore();

        string? changedProvider = null;
        store.Changed += p => changedProvider = p;

        await store.SaveAsync("agy", typedKey: null, clearRequested: false, extra: null);

        Assert.Equal("agy", changedProvider);
        Assert.Equal(0, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
    }
}
