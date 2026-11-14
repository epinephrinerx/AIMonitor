namespace AIMonitor.Application.Tests.Providers;

using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.TestSupport;
using Xunit;

/// <summary>
/// R-019 (from Docs/SPEC_R019.md only): C2 — Claude accepts, replaces and clears a key exactly like
/// OpenAI under secret name <c>providers/claude/key</c>; extra stays rejected for claude; agy
/// unchanged; <c>GetAsync("claude")</c> returns the stored key. E8 — <see cref="ProviderConnection"/>
/// <c>ToString()</c> redacts the key.
/// Expected failures on base 29f4048: the claude-key tests fail because the base store still rejects a
/// claude key and <c>GetAsync</c> never reads one, and the ToString tests fail because the base
/// positional record prints <c>Key</c> verbatim. The agy-unchanged tests pass on base and pin that R-019
/// must not regress them.
/// </summary>
public sealed class ProviderConnectionStoreR019Tests
{
    // E8 sentinel: an admin-key-shaped string that must never surface in any exception text or ToString.
    private const string SentinelAdminKey = "sk-ant-admin-SENTINEL-KEY-a8f3c1";
    private const string ClaudeSecretName = "providers/claude/key";

    private static (ProviderConnectionStore Store, TestSecretStore SecretStore, SettingsSession Session) CreateStore(
        IDictionary<string, string>? initialSecrets = null)
    {
        var secretStore = new TestSecretStore(initialSecrets);
        var settings = new AppSettings();
        var settingsStore = new BlockingSettingsStore(settings);
        settingsStore.Release();
        var session = new SettingsSession(settingsStore, settings);
        var store = new ProviderConnectionStore(secretStore, session);
        return (store, secretStore, session);
    }

    [Fact]
    public async Task SaveAsync_ForClaude_TypedKey_StoresUnderProvidersClaudeKey()
    {
        var (store, secretStore, _) = CreateStore();

        await store.SaveAsync("claude", SentinelAdminKey, clearRequested: false, extra: null);

        Assert.Equal(SentinelAdminKey, secretStore.Secrets[ClaudeSecretName]);
        Assert.Equal(1, secretStore.SetCalls);
    }

    [Fact]
    public async Task SaveAsync_ForClaude_TrimsAndStripsQuotesExactlyLikeOpenAiPath()
    {
        var (store, secretStore, _) = CreateStore();

        // Base SaveAsync's sanitize rule (trim, strip surrounding quotes, trim) applied to claude now:
        // C2 says claude accepts a key "exactly like OpenAI".
        await store.SaveAsync("claude", $"  \"{SentinelAdminKey}\"  ", clearRequested: false, extra: null);

        Assert.Equal(SentinelAdminKey, secretStore.Secrets[ClaudeSecretName]);
    }

    [Fact]
    public async Task SaveAsync_ForClaude_SecondTypedKey_ReplacesStoredKey()
    {
        var (store, secretStore, _) = CreateStore(new Dictionary<string, string> { [ClaudeSecretName] = SentinelAdminKey });

        const string replacement = "sk-ant-admin-REPLACEMENT-b7d2e4";
        await store.SaveAsync("claude", replacement, clearRequested: false, extra: null);

        Assert.Equal(replacement, secretStore.Secrets[ClaudeSecretName]);
    }

    [Fact]
    public async Task SaveAsync_ForClaude_EmptyTypedKeyWithClear_RemovesKey()
    {
        var (store, secretStore, _) = CreateStore(new Dictionary<string, string> { [ClaudeSecretName] = SentinelAdminKey });

        await store.SaveAsync("claude", typedKey: "", clearRequested: true, extra: null);

        Assert.False(secretStore.Secrets.ContainsKey(ClaudeSecretName));
        Assert.Equal(1, secretStore.RemoveCalls);
    }

    [Fact]
    public async Task SaveAsync_ForClaude_EmptyTypedKeyWithoutClear_LeavesStoredKeyUntouched()
    {
        var (store, secretStore, _) = CreateStore(new Dictionary<string, string> { [ClaudeSecretName] = SentinelAdminKey });

        await store.SaveAsync("claude", typedKey: "", clearRequested: false, extra: null);

        Assert.Equal(SentinelAdminKey, secretStore.Secrets[ClaudeSecretName]);
        Assert.Equal(0, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
    }

    [Fact]
    public async Task SaveAsync_ForClaude_ExtraProvided_ThrowsAndWritesNoSecret()
    {
        var (store, secretStore, _) = CreateStore();

        // C2: extra stays rejected for claude. Base rejects claude pre-write, so a rejection here must
        // not leave a partially saved key behind (secrets updated first; failure before completion).
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync("claude", SentinelAdminKey, clearRequested: false, extra: "some-extra"));

        Assert.Contains("extra", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, secretStore.SetCalls);
    }

    [Fact]
    public async Task SaveAsync_ForAgy_WithTypedKey_StillThrowsAgyUnchanged()
    {
        var (store, secretStore, _) = CreateStore();

        // agy unchanged (C2): the base rejection must survive R-019.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync("agy", "any-key", clearRequested: false, extra: null));

        Assert.Contains("does not use an API key", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, secretStore.SetCalls);
    }

    [Fact]
    public async Task SaveAsync_ForClaude_TypedKey_FiresChangedWithClaude()
    {
        var (store, _, _) = CreateStore();

        string? changedProvider = null;
        store.Changed += id => changedProvider = id;

        await store.SaveAsync("claude", SentinelAdminKey, clearRequested: false, extra: null);

        Assert.Equal("claude", changedProvider);
    }

    [Fact]
    public async Task GetAsync_ForClaude_ReturnsStoredKey()
    {
        var (store, _, _) = CreateStore(new Dictionary<string, string> { [ClaudeSecretName] = SentinelAdminKey });

        var conn = await store.GetAsync("claude");

        Assert.Equal(SentinelAdminKey, conn.Key);
    }

    [Fact]
    public async Task GetAsync_ForClaude_NoStoredKey_ReturnsNullKey()
    {
        var (store, secretStore, _) = CreateStore();

        var conn = await store.GetAsync("claude");

        Assert.Null(conn.Key);
        Assert.Equal(1, secretStore.GetCalls);
    }

    [Fact]
    public async Task GetAsync_ForAgy_StillReturnsSecretlessConnectionWithoutReadingSecrets()
    {
        var (store, secretStore, _) = CreateStore(new Dictionary<string, string> { ["providers/agy/key"] = "unused" });

        var conn = await store.GetAsync("agy");

        // agy unchanged (C2): no key read for agy at all.
        Assert.Null(conn.Key);
        Assert.Equal(0, secretStore.GetCalls);
    }

    [Fact]
    public void ToString_ForClaudeConnectionWithKey_DoesNotContainTheKeySentinel()
    {
        // E8: ProviderConnection.ToString() redacts the key.
        var conn = new ProviderConnection(SentinelAdminKey, "");

        Assert.DoesNotContain(SentinelAdminKey, conn.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", conn.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ForOpenAiConnectionWithKey_DoesNotContainTheKeySentinel()
    {
        var conn = new ProviderConnection("sk-admin-SENTINEL-KEY-9d44ab", "");

        Assert.DoesNotContain("SENTINEL", conn.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ForConnectionWithKey_KeepsNonSecretExtraVisibleButRedactedKey()
    {
        // Only the key must be redacted; Extra (a local budget value, not a secret) stays visible so
        // existing diagnostics keep working. (Spec E8 names the key as the thing redacted.)
        var conn = new ProviderConnection(SentinelAdminKey, "50");

        var text = conn.ToString();
        Assert.DoesNotContain(SentinelAdminKey, text, StringComparison.Ordinal);
        Assert.Contains("50", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ForConnectionWithNullKey_DoesNotThrow()
    {
        var conn = new ProviderConnection(null, "");

        Assert.False(string.IsNullOrEmpty(conn.ToString()));
    }
}
