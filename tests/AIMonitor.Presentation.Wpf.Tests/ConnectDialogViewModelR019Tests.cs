using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-019 (from Docs/SPEC_R019.md only): C3 — for claude, a typed key not starting with
/// <c>sk-ant-admin</c> shows a warning in <see cref="ConnectDialogViewModel.KeyGuidance"/> (same style
/// as the OpenAI <c>sk-admin-</c> warning), the key is never echoed; and with C1's NeedsKey=true the
/// dialog's Save path actually persists a claude key through the store (C2).
/// All tests fail on base 29f4048: KeyGuidance is hardcoded to the openai branch (claude returns empty)
/// and SaveAsync early-returns when <c>!Meta.NeedsKey</c>.
/// </summary>
public sealed class ConnectDialogViewModelR019Tests
{
    private const string ValidAdminKey = "sk-ant-admin-SENTINEL-DIALOG-77c1";
    private const string NonAdminKey = "sk-ant-api03-SENTINEL-WORKSPACE-31be";

    private static (ProviderConnectionStore Store, TestSecretStore SecretStore) CreateStore(
        IDictionary<string, string>? initialSecrets = null)
    {
        var secretStore = new TestSecretStore(initialSecrets);
        var settings = new AppSettings();
        var settingsStore = new BlockingSettingsStore(settings);
        settingsStore.Release();
        var session = new SettingsSession(settingsStore, settings);
        var store = new ProviderConnectionStore(secretStore, session);
        return (store, secretStore);
    }

    private static ConnectDialogViewModel CreateClaudeDialog(
        ProviderConnectionStore store,
        ProviderMeta? meta = null,
        DetectionInfo? detection = null,
        ProviderConnection? existing = null) =>
        new(
            meta ?? ProviderMeta.Claude,
            detection ?? new DetectionInfo("claude", DetectionState.NotConnected),
            existing ?? new ProviderConnection(null, ""),
            store);

    [Fact]
    public void KeyGuidance_Claude_NonAdminPrefixKey_WarnsOrganizationAdminKeyNeeded()
    {
        var (store, _) = CreateStore();
        var vm = CreateClaudeDialog(store);

        // C3: typed key not starting with sk-ant-admin → warning that only organization Admin keys can
        // read usage and cost. Concept fragments pinned; exact wording follows the OpenAI warning style.
        vm.Key = NonAdminKey;
        var guidance = vm.KeyGuidance;

        Assert.NotEqual("", guidance);
        Assert.Contains("sk-ant-admin", guidance, StringComparison.Ordinal);
        Assert.Contains("organization", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("usage", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cost", guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeyGuidance_Claude_NonAdminPrefixKey_NeverEchoesTypedKey()
    {
        var (store, _) = CreateStore();
        var vm = CreateClaudeDialog(store);

        vm.Key = NonAdminKey;

        // C3: "Never echo the key." Also guards E8 at the dialog level.
        Assert.DoesNotContain(NonAdminKey, vm.KeyGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", vm.KeyGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyGuidance_Claude_AdminPrefixKey_GuidanceDoesNotWarn()
    {
        var (store, _) = CreateStore();
        var vm = CreateClaudeDialog(store);

        vm.Key = ValidAdminKey;
        var guidance = vm.KeyGuidance;

        // Whatever the recognized-format message says (OpenAI style: "Admin API key format recognized
        // (sk-admin-…)."), it must not be a rejection warning and must not echo the key.
        Assert.DoesNotContain("cannot", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ValidAdminKey, guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyGuidance_Claude_EmptyKey_IsEmpty()
    {
        var (store, _) = CreateStore();
        var vm = CreateClaudeDialog(store);

        // Same rule as openai: guidance only appears for a typed key.
        vm.Key = "   ";
        Assert.Equal("", vm.KeyGuidance);
    }

    [Fact]
    public void KeyGuidance_OpenAi_StillWarnsForNonAdminKey_R019DoesNotChangeIt()
    {
        var (store, _) = CreateStore();
        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            new DetectionInfo("openai", DetectionState.Connected),
            new ProviderConnection(null, ""),
            store);

        vm.Key = "sk-proj-SENTINEL-9999";

        // Regression pin (C3 changes claude only): the OpenAI warning text is unchanged from base.
        Assert.Equal(
            "API spend requires an organization Admin key (sk-admin-…). Project or user keys (e.g. sk-proj-…) cannot read organization usage or costs.",
            vm.KeyGuidance);
        Assert.DoesNotContain("SENTINEL", vm.KeyGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_Claude_NeedsKeyTrue_ReadsConnectClaude()
    {
        var (store, _) = CreateStore();
        var vm = CreateClaudeDialog(store);

        // C1 (NeedsKey=true) flows into the existing Title rule: $"Connect {Meta.DisplayName}".
        Assert.Equal("Connect Claude", vm.Title);
        Assert.True(vm.NeedsKey);
    }

    [Fact]
    public void CanBrowse_Claude_IsFalse_AdminKeyIsTypedNotJsonFile()
    {
        var (store, _) = CreateStore();
        var vm = CreateClaudeDialog(store);

        // C1 "no extra field" + a typed key placeholder: no JSON browse button for claude.
        Assert.False(vm.CanBrowse);
    }

    [Fact]
    public async Task SaveAsync_Claude_TypedAdminKey_PersistsToProvidersClaudeKeyAndCloses()
    {
        var (store, secretStore) = CreateStore();
        var vm = CreateClaudeDialog(store);

        bool? closed = null;
        vm.RequestClose += ok => closed = ok;

        vm.Key = ValidAdminKey;
        await vm.SaveAsync();

        // C1/C2/C3 together: the dialog saves a claude admin key like the OpenAI admin key path.
        Assert.Equal(ValidAdminKey, secretStore.Secrets["providers/claude/key"]);
        Assert.True(closed);
    }

    [Fact]
    public async Task SaveAsync_Claude_ClearRequested_RemovesStoredKeyAndCloses()
    {
        var (store, secretStore) = CreateStore(
            new Dictionary<string, string> { ["providers/claude/key"] = ValidAdminKey });

        var vm = CreateClaudeDialog(store, existing: new ProviderConnection(ValidAdminKey, ""));

        bool? closed = null;
        vm.RequestClose += ok => closed = ok;

        vm.ClearCommand.Execute(null);
        await vm.SaveAsync();

        Assert.False(secretStore.Secrets.ContainsKey("providers/claude/key"));
        Assert.True(closed);
    }

    [Fact]
    public void Constructor_Claude_WithExistingKey_PlaceholderShowsMaskedKeyNotSentinel()
    {
        var (store, _) = CreateStore();

        // Base masking rule, now reachable for claude because NeedsKey=true (C1): an existing key is
        // displayed masked, never verbatim.
        var vm = CreateClaudeDialog(store, existing: new ProviderConnection(ValidAdminKey, ""));

        Assert.Equal("sk-ant-a…77c1", vm.KeyPlaceholder);
        Assert.DoesNotContain(ValidAdminKey, vm.KeyPlaceholder, StringComparison.Ordinal);
    }
}
