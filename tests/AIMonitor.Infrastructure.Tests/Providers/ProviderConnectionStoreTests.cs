namespace AIMonitor.Infrastructure.Tests.Providers;

using System.IO;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Infrastructure.Storage;
using AIMonitor.TestSupport;

public sealed class ProviderConnectionStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-conn-" + Guid.NewGuid().ToString("N"));

    public ProviderConnectionStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private (JsonSettingsStore Store, string Path) CreateSettingsStore()
    {
        var path = Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json");
        return (new JsonSettingsStore(path), path);
    }

    [Fact]
    public async Task GetAsync_ForClaude_AlwaysReturnsNullKeyAndCurrentExtra()
    {
        var (settingsStore, _) = CreateSettingsStore();
        var initial = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["claude"] = new(Enabled: true, Extra: "synthetic-extra")
            }
        };
        using var session = new SettingsSession(settingsStore, initial);
        var secretStore = new TestSecretStore(new Dictionary<string, string>
        {
            ["providers/claude/key"] = "should-not-be-read"
        });
        var connectionStore = new ProviderConnectionStore(secretStore, session);

        var connection = await connectionStore.GetAsync("claude");

        Assert.Null(connection.Key);
        Assert.Equal("synthetic-extra", connection.Extra);
        Assert.Equal(0, secretStore.GetCalls);
    }

    [Fact]
    public async Task GetAsync_ForOpenAiAndGemini_ReturnsStoredKeyAndExtra()
    {
        var (settingsStore, _) = CreateSettingsStore();
        var initial = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["openai"] = new(Enabled: true, Extra: "120.0"),
                ["gemini"] = new(Enabled: false, Extra: "my-project-id")
            }
        };
        using var session = new SettingsSession(settingsStore, initial);
        var secretStore = new TestSecretStore(new Dictionary<string, string>
        {
            ["providers/openai/key"] = "sk-admin-secret",
            ["providers/gemini/key"] = "credentials.json"
        });
        var connectionStore = new ProviderConnectionStore(secretStore, session);

        var openAi = await connectionStore.GetAsync("openai");
        var gemini = await connectionStore.GetAsync("gemini");

        Assert.Equal("sk-admin-secret", openAi.Key);
        Assert.Equal("120.0", openAi.Extra);
        Assert.Equal("credentials.json", gemini.Key);
        Assert.Equal("my-project-id", gemini.Extra);
    }

    [Fact]
    public async Task GetAsync_UnknownProvider_ThrowsArgumentException()
    {
        var (settingsStore, _) = CreateSettingsStore();
        using var session = new SettingsSession(settingsStore, new AppSettings());
        var connectionStore = new ProviderConnectionStore(new TestSecretStore(), session);

        await Assert.ThrowsAsync<ArgumentException>(() => connectionStore.GetAsync("anthropic"));
        await Assert.ThrowsAsync<ArgumentException>(() => connectionStore.GetAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => connectionStore.GetAsync("   "));
    }

    [Fact]
    public async Task SaveAsync_ReplaceKey_WhenTypedKeyNonEmpty_UpdatesSecret()
    {
        var (settingsStore, settingsPath) = CreateSettingsStore();
        var initial = new AppSettings();
        using var session = new SettingsSession(settingsStore, initial);
        var secretStore = new TestSecretStore();
        var connectionStore = new ProviderConnectionStore(secretStore, session);

        await connectionStore.SaveAsync("openai", typedKey: " sk-admin-new ", clearRequested: false, extra: " 50.0 ");

        Assert.Equal("sk-admin-new", secretStore.Secrets["providers/openai/key"]);
        Assert.Equal(1, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);

        var reloaded = await new JsonSettingsStore(settingsPath).LoadAsync();
        Assert.Equal("50.0", reloaded.Providers["openai"].Extra);
    }

    [Fact]
    public async Task SaveAsync_RemoveKey_WhenTypedKeyEmptyAndClearRequested_RemovesSecret()
    {
        var (settingsStore, _) = CreateSettingsStore();
        using var session = new SettingsSession(settingsStore, new AppSettings());
        var secretStore = new TestSecretStore(new Dictionary<string, string>
        {
            ["providers/openai/key"] = "existing-key"
        });
        var connectionStore = new ProviderConnectionStore(secretStore, session);

        await connectionStore.SaveAsync("openai", typedKey: "", clearRequested: true, extra: null);

        Assert.False(secretStore.Secrets.ContainsKey("providers/openai/key"));
        Assert.Equal(1, secretStore.RemoveCalls);
        Assert.Equal(0, secretStore.SetCalls);
    }

    [Fact]
    public async Task SaveAsync_UntouchedKey_WhenTypedKeyEmptyAndClearNotRequested_LeavesSecretUntouched()
    {
        var (settingsStore, _) = CreateSettingsStore();
        using var session = new SettingsSession(settingsStore, new AppSettings());
        var secretStore = new TestSecretStore(new Dictionary<string, string>
        {
            ["providers/openai/key"] = "existing-key"
        });
        var connectionStore = new ProviderConnectionStore(secretStore, session);

        await connectionStore.SaveAsync("openai", typedKey: "   ", clearRequested: false, extra: null);

        Assert.Equal("existing-key", secretStore.Secrets["providers/openai/key"]);
        Assert.Equal(0, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
    }

    [Fact]
    public async Task SaveAsync_ClearAndRetype_WhenTypedKeyNonEmptyAndClearRequested_ReplacesKey()
    {
        var (settingsStore, _) = CreateSettingsStore();
        using var session = new SettingsSession(settingsStore, new AppSettings());
        var secretStore = new TestSecretStore(new Dictionary<string, string>
        {
            ["providers/openai/key"] = "old-key"
        });
        var connectionStore = new ProviderConnectionStore(secretStore, session);

        await connectionStore.SaveAsync("openai", typedKey: "sk-brand-new", clearRequested: true, extra: null);

        Assert.Equal("sk-brand-new", secretStore.Secrets["providers/openai/key"]);
        Assert.Equal(1, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
    }

    [Fact]
    public async Task SaveAsync_WhenTypedKeyHasSurroundingQuotes_TrimsQuotesBeforeSaving()
    {
        var (settingsStore, _) = CreateSettingsStore();
        using var session = new SettingsSession(settingsStore, new AppSettings());
        var secretStore = new TestSecretStore();
        var connectionStore = new ProviderConnectionStore(secretStore, session);

        await connectionStore.SaveAsync("openai", typedKey: "  \"sk-quoted-key\"  ", clearRequested: false, extra: null);

        Assert.Equal("sk-quoted-key", secretStore.Secrets["providers/openai/key"]);
    }

    [Fact]
    public async Task SaveAsync_WhenSecretStoreFails_DoesNotTouchSettings_AndRethrows()
    {
        var (settingsStore, settingsPath) = CreateSettingsStore();
        var initial = new AppSettings
        {
            Theme = "light",
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["openai"] = new(Enabled: true, Extra: "original-budget")
            }
        };
        await settingsStore.SaveAsync(initial);

        using var session = new SettingsSession(settingsStore, initial);
        var secretStore = new TestSecretStore
        {
            FailOnSet = new IOException("Disk failure when writing secret")
        };
        var connectionStore = new ProviderConnectionStore(secretStore, session);

        var ex = await Assert.ThrowsAsync<IOException>(
            () => connectionStore.SaveAsync("openai", typedKey: "new-key", clearRequested: false, extra: "changed-budget"));

        Assert.Equal("Disk failure when writing secret", ex.Message);
        Assert.Equal("original-budget", session.Current.Providers["openai"].Extra);

        var reloaded = await new JsonSettingsStore(settingsPath).LoadAsync();
        Assert.Equal("original-budget", reloaded.Providers["openai"].Extra);
    }

    [Fact]
    public async Task SaveAsync_PreservesOtherSettingsAndOtherProviders_EvenIfConcurrentModificationOccurred()
    {
        var (settingsStore, settingsPath) = CreateSettingsStore();
        var initial = new AppSettings
        {
            Theme = "system",
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["gemini"] = new(Enabled: true, Extra: "gemini-project")
            }
        };
        await settingsStore.SaveAsync(initial);

        using var session = new SettingsSession(settingsStore, initial);
        var secretStore = new TestSecretStore();
        var connectionStore = new ProviderConnectionStore(secretStore, session);

        await session.UpdateAsync(s => s with { Theme = "dark" });
        await connectionStore.SaveAsync("openai", typedKey: "sk-openai", clearRequested: false, extra: "200.0");

        Assert.Equal("dark", session.Current.Theme);
        Assert.Equal("gemini-project", session.Current.Providers["gemini"].Extra);
        Assert.Equal("200.0", session.Current.Providers["openai"].Extra);

        var reloaded = await new JsonSettingsStore(settingsPath).LoadAsync();
        Assert.Equal("dark", reloaded.Theme);
        Assert.Equal("gemini-project", reloaded.Providers["gemini"].Extra);
        Assert.Equal("200.0", reloaded.Providers["openai"].Extra);
    }

    [Fact]
    public async Task SaveAsync_PreservesProviderEnabledState()
    {
        var (settingsStore, _) = CreateSettingsStore();
        var initial = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["openai"] = new(Enabled: false, Extra: "10.0")
            }
        };
        using var session = new SettingsSession(settingsStore, initial);
        var connectionStore = new ProviderConnectionStore(new TestSecretStore(), session);

        await connectionStore.SaveAsync("openai", typedKey: null, clearRequested: false, extra: "50.0");

        Assert.False(session.Current.Providers["openai"].Enabled);
        Assert.Equal("50.0", session.Current.Providers["openai"].Extra);
    }

    [Fact]
    public async Task SaveAsync_WhenExtraIsEmptyOrWhitespace_ClearsExtra()
    {
        var (settingsStore, _) = CreateSettingsStore();
        var initial = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["openai"] = new(Enabled: true, Extra: "50.0")
            }
        };
        using var session = new SettingsSession(settingsStore, initial);
        var connectionStore = new ProviderConnectionStore(new TestSecretStore(), session);

        await connectionStore.SaveAsync("openai", typedKey: null, clearRequested: false, extra: "   ");

        Assert.Equal("", session.Current.Providers["openai"].Extra);
    }

    [Fact]
    public async Task SaveAsync_FiresChangedEvent_OnlyWhenSuccessful()
    {
        var (settingsStore, _) = CreateSettingsStore();
        using var session = new SettingsSession(settingsStore, new AppSettings());
        var secretStore = new TestSecretStore();
        var connectionStore = new ProviderConnectionStore(secretStore, session);

        var changedEvents = new List<string>();
        connectionStore.Changed += id => changedEvents.Add(id);

        await connectionStore.SaveAsync("openai", typedKey: "sk-key", clearRequested: false, extra: "10.0");
        Assert.Single(changedEvents);
        Assert.Equal("openai", changedEvents[0]);

        secretStore.FailOnSet = new InvalidOperationException("Secret write failed");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connectionStore.SaveAsync("openai", typedKey: "sk-key-2", clearRequested: false, extra: "20.0"));

        Assert.Single(changedEvents);
    }

    [Fact]
    public async Task SaveAsync_ForClaude_WhenTypedKeyProvided_ThrowsArgumentException()
    {
        var (settingsStore, _) = CreateSettingsStore();
        using var session = new SettingsSession(settingsStore, new AppSettings());
        var connectionStore = new ProviderConnectionStore(new TestSecretStore(), session);

        await Assert.ThrowsAsync<ArgumentException>(
            () => connectionStore.SaveAsync("claude", typedKey: "sk-claude", clearRequested: false, extra: null));
    }

    [Fact]
    public async Task SaveAsync_ForClaude_WhenNonEmptyExtraProvided_ThrowsArgumentException()
    {
        var (settingsStore, _) = CreateSettingsStore();
        using var session = new SettingsSession(settingsStore, new AppSettings());
        var connectionStore = new ProviderConnectionStore(new TestSecretStore(), session);

        await Assert.ThrowsAsync<ArgumentException>(
            () => connectionStore.SaveAsync("claude", typedKey: null, clearRequested: false, extra: "some-extra"));
    }

    [Fact]
    public async Task SaveAsync_ForClaude_WhenExtraNullOrEmpty_Succeeds()
    {
        var (settingsStore, _) = CreateSettingsStore();
        using var session = new SettingsSession(settingsStore, new AppSettings());
        var connectionStore = new ProviderConnectionStore(new TestSecretStore(), session);

        var changedEvents = new List<string>();
        connectionStore.Changed += id => changedEvents.Add(id);

        await connectionStore.SaveAsync("claude", typedKey: null, clearRequested: false, extra: "   ");

        Assert.Equal("", session.Current.Providers["claude"].Extra);
        Assert.Single(changedEvents);
        Assert.Equal("claude", changedEvents[0]);
    }

    [Fact]
    public async Task SaveAsync_UnknownProvider_ThrowsArgumentException()
    {
        var (settingsStore, _) = CreateSettingsStore();
        using var session = new SettingsSession(settingsStore, new AppSettings());
        var connectionStore = new ProviderConnectionStore(new TestSecretStore(), session);

        await Assert.ThrowsAsync<ArgumentException>(
            () => connectionStore.SaveAsync("unknown-provider", typedKey: "key", clearRequested: false, extra: null));
    }
}
