using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class ConnectDialogViewModelTests
{
    private static (ProviderConnectionStore Store, TestSecretStore SecretStore, SettingsSession Session) CreateConnectionStore(
        string? initialKey = null, string initialExtra = "")
    {
        var secretMap = new Dictionary<string, string>();
        if (initialKey is not null)
        {
            secretMap["providers/openai/key"] = initialKey;
        }

        var secretStore = new TestSecretStore(secretMap);
        var initialSettings = new AppSettings
        {
            Providers = new Dictionary<string, ProviderPreference>
            {
                ["openai"] = new(Enabled: true, Extra: initialExtra)
            }
        };

        var settingsStore = new BlockingSettingsStore(initialSettings);
        settingsStore.Release();
        var session = new SettingsSession(settingsStore, initialSettings);
        var store = new ProviderConnectionStore(secretStore, session);

        return (store, secretStore, session);
    }

    [Fact]
    public void Constructor_ForClaude_SetsTitleSignIn_AndNeedsKeyFalse()
    {
        var (store, _, _) = CreateConnectionStore();
        var vm = new ConnectDialogViewModel(
            ProviderMeta.Claude,
            new DetectionInfo("claude", DetectionState.Connected),
            new ProviderConnection(null, ""),
            store);

        Assert.Equal("Claude sign-in", vm.Title);
        Assert.False(vm.NeedsKey);
        Assert.False(vm.CanBrowse);
    }

    [Fact]
    public void Constructor_ForOpenAi_SetsTitleConnect_AndNeedsKeyTrue()
    {
        var (store, _, _) = CreateConnectionStore();
        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            new DetectionInfo("openai", DetectionState.NotConnected),
            new ProviderConnection("sk-admin-1234567890", "50"),
            store);

        Assert.Equal("Connect OpenAI / Codex", vm.Title);
        Assert.True(vm.NeedsKey);
        Assert.False(vm.CanBrowse);
        Assert.Equal("sk-admin…7890", vm.KeyPlaceholder);
        Assert.Equal("50", vm.Extra);
    }

    [Fact]
    public void Constructor_ForGemini_SetsCanBrowseTrue()
    {
        var (store, _, _) = CreateConnectionStore();
        var vm = new ConnectDialogViewModel(
            ProviderMeta.Gemini,
            null,
            new ProviderConnection(null, ""),
            store);

        Assert.True(vm.CanBrowse);
        Assert.Equal(@"C:\path\to\service-account.json  (optional)", vm.KeyPlaceholder);
    }

    [Fact]
    public void FoundLine_WhenNotConnectedOrNull_ReturnsNoExistingSignInFound()
    {
        var (store, _, _) = CreateConnectionStore();

        var vmNull = new ConnectDialogViewModel(ProviderMeta.OpenAi, null, new ProviderConnection(null, ""), store);
        Assert.Equal("No existing sign-in found for this service.", vmNull.FoundLine);

        var vmNotConnected = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            new DetectionInfo("openai", DetectionState.NotConnected),
            new ProviderConnection(null, ""),
            store);
        Assert.Equal("No existing sign-in found for this service.", vmNotConnected.FoundLine);
    }

    [Fact]
    public void FoundLine_WhenConnectedWithAccount_FormatsGlyphSourceAccountAndState()
    {
        var (store, _, _) = CreateConnectionStore();
        var detection = new DetectionInfo("openai", DetectionState.Connected, "codex_cli", "Codex CLI", "user@example.com");
        var vm = new ConnectDialogViewModel(ProviderMeta.OpenAi, detection, new ProviderConnection(null, ""), store);

        Assert.Equal("✓  Codex CLI — user@example.com  ·  Connected", vm.FoundLine);
    }

    [Fact]
    public void AlsoPresent_WhenOtherCandidatesExist_FormatsCandidateLabels()
    {
        var (store, _, _) = CreateConnectionStore();
        var candidates = new[]
        {
            new DetectionCandidate("active_source", "Active Source"),
            new DetectionCandidate("env_var", "Environment variable OPENAI_API_KEY"),
            new DetectionCandidate("saved", "Saved in this app")
        };
        var detection = new DetectionInfo("openai", DetectionState.Connected, "active_source", "Active Source", candidates: candidates);
        var vm = new ConnectDialogViewModel(ProviderMeta.OpenAi, detection, new ProviderConnection(null, ""), store);

        Assert.Equal("Also present: Environment variable OPENAI_API_KEY, Saved in this app", vm.AlsoPresent);
    }

    [Fact]
    public void AlsoPresent_WhenNoOtherCandidatesExist_ReturnsEmpty()
    {
        var (store, _, _) = CreateConnectionStore();
        var candidates = new[]
        {
            new DetectionCandidate("active_source", "Active Source")
        };
        var detection = new DetectionInfo("openai", DetectionState.Connected, "active_source", "Active Source", candidates: candidates);
        var vm = new ConnectDialogViewModel(ProviderMeta.OpenAi, detection, new ProviderConnection(null, ""), store);

        Assert.Equal("", vm.AlsoPresent);
    }

    [Fact]
    public void ClearCommand_ThenCancelCommand_DoesNotCallSecretStore_AndPreservesExistingKey()
    {
        var (store, secretStore, _) = CreateConnectionStore(initialKey: "existing-secret-key");
        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection("existing-secret-key", ""),
            store);

        var closeResult = true;
        vm.RequestClose += result => closeResult = result;

        vm.ClearCommand.Execute(null);

        Assert.True(vm.ClearRequested);
        Assert.Equal("", vm.Key);
        Assert.Equal("(cleared when you save)", vm.KeyPlaceholder);

        // Cancel
        vm.CancelCommand.Execute(null);

        Assert.False(closeResult);
        Assert.Equal(0, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
        Assert.Equal("existing-secret-key", secretStore.Secrets["providers/openai/key"]);
    }

    [Fact]
    public async Task ClearCommand_ThenSaveCommand_RemovesKeyFromSecretStore()
    {
        var (store, secretStore, _) = CreateConnectionStore(initialKey: "existing-secret-key");
        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection("existing-secret-key", "50"),
            store);

        var closeResult = false;
        vm.RequestClose += result => closeResult = result;

        vm.ClearCommand.Execute(null);
        await vm.SaveAsync();

        Assert.True(closeResult);
        Assert.Equal(1, secretStore.RemoveCalls);
        Assert.False(secretStore.Secrets.ContainsKey("providers/openai/key"));
    }

    [Fact]
    public async Task ClearCommand_ThenTypingNewKeyAndSaveCommand_ReplacesKeyInSecretStore()
    {
        var (store, secretStore, _) = CreateConnectionStore(initialKey: "old-key");
        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection("old-key", "50"),
            store);

        var closeResult = false;
        vm.RequestClose += result => closeResult = result;

        vm.ClearCommand.Execute(null);
        vm.Key = "new-replacement-key";
        await vm.SaveAsync();

        Assert.True(closeResult);
        Assert.Equal(1, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
        Assert.Equal("new-replacement-key", secretStore.Secrets["providers/openai/key"]);
    }

    [Fact]
    public async Task SaveCommand_WhenKeyEmptyAndClearNotRequested_LeavesKeyUntouchedInSecretStore()
    {
        var (store, secretStore, _) = CreateConnectionStore(initialKey: "untouched-key", initialExtra: "10");
        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection("untouched-key", "10"),
            store);

        var closeResult = false;
        vm.RequestClose += result => closeResult = result;

        vm.Key = "";
        vm.Extra = "20";
        await vm.SaveAsync();

        var updatedConn = await store.GetAsync("openai");

        Assert.True(closeResult);
        Assert.Equal(0, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
        Assert.Equal("untouched-key", updatedConn.Key);
        Assert.Equal("20", updatedConn.Extra);
    }

    [Fact]
    public async Task SaveCommand_ForOpenAi_SavesProvidedExtra()
    {
        var (store, _, session) = CreateConnectionStore();
        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection(null, "10"),
            store);

        vm.Key = "sk-admin-new";
        vm.Extra = "75.50";
        await vm.SaveAsync();

        var conn = await store.GetAsync("openai");
        Assert.Equal("75.50", conn.Extra);
        Assert.Equal("75.50", session.Current.Providers["openai"].Extra);
    }

    [Fact]
    public async Task SaveCommand_WhenExtraLabelEmpty_SendsNullExtraToStore()
    {
        var (store, secretStore, _) = CreateConnectionStore(initialExtra: "initial-extra");
        var customMeta = new ProviderMeta(
            Id: "openai",
            DisplayName: "OpenAI Custom",
            BadgeLetter: "O",
            Tagline: "Custom",
            NeedsKey: true,
            KeyLabel: "Key",
            KeyPlaceholder: "placeholder",
            ExtraLabel: "", // empty extra label
            ExtraPlaceholder: "",
            SetupHint: "hint");

        var vm = new ConnectDialogViewModel(
            customMeta,
            null,
            new ProviderConnection(null, "initial-extra"),
            store);

        vm.Key = "sk-custom-key";
        vm.Extra = "should-be-ignored-since-extralabel-empty";
        await vm.SaveAsync();

        var conn = await store.GetAsync("openai");
        Assert.Equal("sk-custom-key", secretStore.Secrets["providers/openai/key"]);
        Assert.Equal("initial-extra", conn.Extra); // extra remains untouched
    }

    [Fact]
    public async Task SaveCommand_ForClaude_DoesNotCallStore_AndDoesNotCloseDialog()
    {
        var (store, secretStore, _) = CreateConnectionStore();
        var vm = new ConnectDialogViewModel(
            ProviderMeta.Claude,
            null,
            new ProviderConnection(null, ""),
            store);

        var closeInvoked = false;
        vm.RequestClose += _ => closeInvoked = true;

        await vm.SaveAsync();

        Assert.False(closeInvoked);
        Assert.Equal(0, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
    }

    [Fact]
    public async Task SaveCommand_WhenStoreThrowsIOException_DoesNotClose_FiresSaveFailed_AndMessageDoesNotContainKey()
    {
        var (store, secretStore, _) = CreateConnectionStore();
        secretStore.FailOnSet = new IOException("Disk failure when saving secret");

        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection(null, ""),
            store);

        var closeInvoked = false;
        vm.RequestClose += _ => closeInvoked = true;

        string? failureMessage = null;
        vm.SaveFailed += msg => failureMessage = msg;

        var sensitiveKey = "sk-admin-super-secret-key-12345";
        vm.Key = sensitiveKey;
        await vm.SaveAsync();

        Assert.False(closeInvoked);
        Assert.NotNull(failureMessage);
        Assert.Contains("I/O", failureMessage);
        Assert.DoesNotContain(sensitiveKey, failureMessage);
    }

    [Fact]
    public async Task SaveCommand_WhenStoreThrowsUnauthorizedAccessException_DoesNotClose_FiresSaveFailed()
    {
        var (store, secretStore, _) = CreateConnectionStore();
        secretStore.FailOnSet = new UnauthorizedAccessException("Access denied to secrets file");

        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection(null, ""),
            store);

        var closeInvoked = false;
        vm.RequestClose += _ => closeInvoked = true;

        string? failureMessage = null;
        vm.SaveFailed += msg => failureMessage = msg;

        vm.Key = "sk-admin-key";
        await vm.SaveAsync();

        Assert.False(closeInvoked);
        Assert.NotNull(failureMessage);
        Assert.Contains("Access denied", failureMessage);
    }

    [Fact]
    public async Task SaveCommand_WhenStoreThrowsCryptographicException_DoesNotClose_FiresSaveFailed()
    {
        var (store, secretStore, _) = CreateConnectionStore();
        secretStore.FailOnSet = new CryptographicException("DPAPI unsealing failure");

        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection(null, ""),
            store);

        var closeInvoked = false;
        vm.RequestClose += _ => closeInvoked = true;

        string? failureMessage = null;
        vm.SaveFailed += msg => failureMessage = msg;

        vm.Key = "sk-admin-key";
        await vm.SaveAsync();

        Assert.False(closeInvoked);
        Assert.NotNull(failureMessage);
        Assert.Contains("Cryptographic", failureMessage);
    }

    [Fact]
    public async Task SaveCommand_AfterClearArmedAndNewKeyTyped_ReplacesKeyAndSavesExtraAndClosesWithTrue()
    {
        var (store, secretStore, session) = CreateConnectionStore(initialKey: "old-key", initialExtra: "10");
        var closeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection("old-key", "10"),
            store);

        vm.RequestClose += result =>
        {
            if (result)
            {
                closeTcs.TrySetResult(true);
            }
            else
            {
                closeTcs.TrySetException(new InvalidOperationException("Dialog closed with false instead of true."));
            }
        };

        // Clear armed
        vm.ClearCommand.Execute(null);
        Assert.True(vm.ClearRequested);

        // Typed new key, with an extra value for OpenAI
        vm.Key = "sk-replacement-new-key";
        vm.Extra = "75.00";

        // Drive the REAL SaveCommand (not vm.SaveAsync())
        vm.SaveCommand.Execute(null);

        // Wait on TaskCompletionSource completed by RequestClose(true) (WaitAsync 5s)
        await closeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert secret store holds the new key (replace beats clear) and the extra was saved
        Assert.Equal("sk-replacement-new-key", secretStore.Secrets["providers/openai/key"]);
        Assert.Equal(1, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);

        var conn = await store.GetAsync("openai");
        Assert.Equal("sk-replacement-new-key", conn.Key);
        Assert.Equal("75.00", conn.Extra);
        Assert.Equal("75.00", session.Current.Providers["openai"].Extra);
    }

    [Fact]
    public async Task SaveCommand_AfterClearArmedAndBlankKey_RemovesKeyFromSecretStoreAndClosesWithTrue()
    {
        var (store, secretStore, _) = CreateConnectionStore(initialKey: "existing-key-to-remove", initialExtra: "50");
        var closeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection("existing-key-to-remove", "50"),
            store);

        vm.RequestClose += result =>
        {
            if (result)
            {
                closeTcs.TrySetResult(true);
            }
            else
            {
                closeTcs.TrySetException(new InvalidOperationException("Dialog closed with false instead of true."));
            }
        };

        // Clear armed
        vm.ClearCommand.Execute(null);
        Assert.True(vm.ClearRequested);
        Assert.Equal("", vm.Key);

        // Save with blank key via REAL SaveCommand
        vm.SaveCommand.Execute(null);

        // Wait on TaskCompletionSource completed by RequestClose(true) (WaitAsync 5s)
        await closeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert key removed from secret store and closed with true
        Assert.Equal(1, secretStore.RemoveCalls);
        Assert.Equal(0, secretStore.SetCalls);
        Assert.False(secretStore.Secrets.ContainsKey("providers/openai/key"));

        var conn = await store.GetAsync("openai");
        Assert.Null(conn.Key);
    }

    [Fact]
    public void BrowseCommand_FiresBrowseRequested()
    {
        var (store, _, _) = CreateConnectionStore();
        var vm = new ConnectDialogViewModel(ProviderMeta.Gemini, null, new ProviderConnection(null, ""), store);

        var browseRequested = false;
        vm.BrowseRequested += () => browseRequested = true;

        vm.BrowseCommand.Execute(null);
        Assert.True(browseRequested);
    }

    [Fact]
    public void SetKeyFromBrowse_UpdatesKey_AndSetsIsKeyRevealedTrue()
    {
        var (store, _, _) = CreateConnectionStore();
        var vm = new ConnectDialogViewModel(ProviderMeta.Gemini, null, new ProviderConnection(null, ""), store);

        Assert.False(vm.IsKeyRevealed);
        Assert.Equal("", vm.Key);

        vm.SetKeyFromBrowse(@"C:\Users\test\creds.json");

        Assert.Equal(@"C:\Users\test\creds.json", vm.Key);
        Assert.True(vm.IsKeyRevealed);
    }

    [Fact]
    public void CloseAndCancelCommands_InvokeRequestCloseFalse_WithoutTouchingStore()
    {
        var (store, secretStore, _) = CreateConnectionStore();
        var vm = new ConnectDialogViewModel(ProviderMeta.Claude, null, new ProviderConnection(null, ""), store);

        var results = new List<bool>();
        vm.RequestClose += r => results.Add(r);

        vm.CancelCommand.Execute(null);
        vm.CloseCommand.Execute(null);

        Assert.Equal(2, results.Count);
        Assert.False(results[0]);
        Assert.False(results[1]);
        Assert.Equal(0, secretStore.SetCalls);
        Assert.Equal(0, secretStore.RemoveCalls);
    }

    [Fact]
    public async Task SaveCommand_WhenStoreThrowsWin32ExceptionWithKey_DoesNotClose_FiresSaveFailed_AndMessageDoesNotContainKey()
    {
        var (store, secretStore, _) = CreateConnectionStore();
        var typedKey = "sk-admin-win32-secret-key-99999";
        secretStore.FailOnSet = new Win32Exception(5, $"Native DPAPI error for key: {typedKey}");

        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection(null, ""),
            store);

        var closeInvoked = false;
        vm.RequestClose += _ => closeInvoked = true;

        string? failureMessage = null;
        vm.SaveFailed += msg => failureMessage = msg;

        vm.Key = typedKey;
        await vm.SaveAsync();

        Assert.False(closeInvoked);
        Assert.NotNull(failureMessage);
        Assert.Contains(nameof(Win32Exception), failureMessage);
        Assert.DoesNotContain(typedKey, failureMessage);
    }

    [Fact]
    public async Task SaveCommand_WhenStoreThrowsInvalidDataExceptionWithKey_DoesNotClose_FiresSaveFailed_AndMessageDoesNotContainKey()
    {
        var (store, secretStore, _) = CreateConnectionStore();
        var typedKey = "sk-admin-invalid-data-secret-88888";
        secretStore.FailOnSet = new InvalidDataException($"Corrupted store payload containing: {typedKey}");

        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection(null, ""),
            store);

        var closeInvoked = false;
        vm.RequestClose += _ => closeInvoked = true;

        string? failureMessage = null;
        vm.SaveFailed += msg => failureMessage = msg;

        vm.Key = typedKey;
        await vm.SaveAsync();

        Assert.False(closeInvoked);
        Assert.NotNull(failureMessage);
        Assert.Contains(nameof(InvalidDataException), failureMessage);
        Assert.DoesNotContain(typedKey, failureMessage);
    }

    [Fact]
    public async Task SaveCommand_WhenStoreThrowsJsonExceptionWithKey_DoesNotClose_FiresSaveFailed_AndMessageDoesNotContainKey()
    {
        var (store, secretStore, _) = CreateConnectionStore();
        var typedKey = "sk-admin-json-secret-77777";
        secretStore.FailOnSet = new JsonException($"Syntax error parsing JSON containing: {typedKey}");

        var vm = new ConnectDialogViewModel(
            ProviderMeta.OpenAi,
            null,
            new ProviderConnection(null, ""),
            store);

        var closeInvoked = false;
        vm.RequestClose += _ => closeInvoked = true;

        string? failureMessage = null;
        vm.SaveFailed += msg => failureMessage = msg;

        vm.Key = typedKey;
        await vm.SaveAsync();

        Assert.False(closeInvoked);
        Assert.NotNull(failureMessage);
        Assert.Contains(nameof(JsonException), failureMessage);
        Assert.DoesNotContain(typedKey, failureMessage);
    }
}
