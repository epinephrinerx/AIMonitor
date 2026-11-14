namespace AIMonitor.Application.Tests.Providers;

using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.TestSupport;
using Xunit;

/// <summary>
/// R-020 F7 (Docs/SPEC_R020_R021.md): <c>GetAsync("opencode")</c> returns a secret-less connection without
/// reading secrets (like agy); <c>SaveAsync("opencode", ...)</c> with a key or extra throws like agy and writes
/// nothing. Mirrors the agy tests in ProviderConnectionStoreTests (same harness, same assertions).
/// </summary>
public sealed class ProviderConnectionStoreOpenCodeTests
{
    private static (ProviderConnectionStore Store, TestSecretStore SecretStore, BlockingSettingsStore Settings) CreateStore(
        AppSettings? initialSettings = null)
    {
        var settings = initialSettings ?? new AppSettings();
        var secretStore = new TestSecretStore(new Dictionary<string, string>
        {
            ["providers/opencode/key"] = "SENTINEL-SHOULD-NEVER-BE-READ",
        });
        var settingsStore = new BlockingSettingsStore(settings);
        settingsStore.Release();
        var session = new SettingsSession(settingsStore, settings);
        return (new ProviderConnectionStore(secretStore, session), secretStore, settingsStore);
    }

    [Fact]
    public async Task GetAsync_ForOpenCode_ReturnsSecretlessConnectionWithoutTouchingSecretStore_F7()
    {
        var (store, secretStore, _) = CreateStore(new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["opencode"] = new(Enabled: true, Extra: "ignored-extra"),
            },
        });

        var connection = await store.GetAsync("opencode");

        Assert.Null(connection.Key);
        Assert.Equal("", connection.Extra);
        Assert.Equal(0, secretStore.GetCalls);
    }

    [Theory]
    [InlineData("OpenCode")]
    [InlineData("OPENCODE")]
    [InlineData(" opencode ")]
    public async Task GetAsync_ForOpenCode_IsCaseInsensitiveLikeOtherProviders_F7(string providerId)
    {
        var (store, secretStore, _) = CreateStore();

        var connection = await store.GetAsync(providerId);

        Assert.Null(connection.Key);
        Assert.Equal("", connection.Extra);
        Assert.Equal(0, secretStore.GetCalls);
    }

    [Fact]
    public async Task SaveAsync_ForOpenCode_WhenKeyProvided_Throws_AndWritesNothing_F7()
    {
        var (store, secretStore, settingsStore) = CreateStore();
        string? changed = null;
        store.Changed += p => changed = p;

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.SaveAsync("opencode", "some-key", clearRequested: false, extra: null));

        Assert.Equal(0, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
        Assert.Empty(settingsStore.SavedSettings);
        Assert.Null(changed);
    }

    [Fact]
    public async Task SaveAsync_ForOpenCode_WhenExtraProvided_Throws_AndWritesNothing_F7()
    {
        var (store, secretStore, settingsStore) = CreateStore();
        string? changed = null;
        store.Changed += p => changed = p;

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.SaveAsync("opencode", typedKey: null, clearRequested: false, extra: "some-extra"));

        Assert.Equal(0, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
        Assert.Empty(settingsStore.SavedSettings);
        Assert.Null(changed);
    }

    [Fact]
    public async Task SaveAsync_ForOpenCode_WhenClearRequestedWithAKey_StillThrows_AndRemovesNothing_F7()
    {
        var (store, secretStore, _) = CreateStore();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.SaveAsync("opencode", "some-key", clearRequested: true, extra: null));

        Assert.Equal(0, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
        Assert.True(secretStore.Secrets.ContainsKey("providers/opencode/key")); // the planted value was never touched
    }
}
