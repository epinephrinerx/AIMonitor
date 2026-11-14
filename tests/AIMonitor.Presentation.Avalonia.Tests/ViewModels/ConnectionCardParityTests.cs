using AIMonitor.Application.OpenCode;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

/// <summary>The connections cards say what the Windows cards say (PAR-002): state, source, saved key.</summary>
public sealed class ConnectionCardParityTests
{
    private static async Task<(ConnectionsViewModel Page, TestSecretStore Secrets)> BuildAsync()
    {
        var session = await SettingsSession.CreateAsync(new InMemorySettingsStore());
        var store = new TestSecretStore();
        return (new ConnectionsViewModel(session, new ProviderConnectionStore(store, session)), store);
    }

    private static ProviderSnapshot WithDetection(string providerId, DetectionState state, bool adminOnly = false) =>
        new(providerId, configured: state == DetectionState.Connected, meters: [], adminOnly: adminOnly,
            detection: new DetectionInfo(providerId, state, "keychain", "Claude Code login (Keychain)", "someone@example.com",
                candidates: [new DetectionCandidate("keychain", "Claude Code login (Keychain)"), new DetectionCandidate("env", "ANTHROPIC_ADMIN_KEY")]));

    [Theory]
    [InlineData(DetectionState.Connected, "✓  Connected")]
    [InlineData(DetectionState.Limited, "!  Limited")]
    [InlineData(DetectionState.Expired, "!  Expired")]
    [InlineData(DetectionState.NotConnected, "–  Not connected")]
    public async Task TheStateChip_IsAWordAndAGlyph(DetectionState state, string expected)
    {
        var (page, _) = await BuildAsync();
        var claude = page.Cards[0];

        page.Update(new Dictionary<string, ProviderSnapshot> { ["claude"] = WithDetection("claude", state) });

        Assert.Equal(expected, claude.StateChip);
    }

    [Fact]
    public async Task AnExpiredSignIn_GetsItsOwnColour()
    {
        var (page, _) = await BuildAsync();

        page.Update(new Dictionary<string, ProviderSnapshot> { ["claude"] = WithDetection("claude", DetectionState.Expired) });

        Assert.True(page.Cards[0].IsSerious);
    }

    [Fact]
    public async Task TheSourceLine_AndWhatElseWasFound_AreShown()
    {
        var (page, _) = await BuildAsync();
        var claude = page.Cards[0];

        page.Update(new Dictionary<string, ProviderSnapshot> { ["claude"] = WithDetection("claude", DetectionState.Connected) });

        Assert.Equal("Detected from Claude Code login (Keychain)", claude.SourceLine);
        Assert.Equal("✓  Claude Code login (Keychain) — someone@example.com  ·  Connected", claude.FoundLine);
        Assert.Equal("Also present: ANTHROPIC_ADMIN_KEY", claude.AlsoPresent);
    }

    [Fact]
    public async Task ClaudeWithOnlyTheAdminKey_ReadsLimited_ButExpiredStaysExpired()
    {
        var (page, _) = await BuildAsync();
        var claude = page.Cards[0];

        page.Update(new Dictionary<string, ProviderSnapshot> { ["claude"] = WithDetection("claude", DetectionState.NotConnected, adminOnly: true) });
        Assert.Equal("Limited", claude.StateWord);
        Assert.Equal("Admin key connected · Claude Code not signed in", claude.SourceLine);

        page.Update(new Dictionary<string, ProviderSnapshot> { ["claude"] = WithDetection("claude", DetectionState.Expired, adminOnly: true) });
        Assert.Equal("Expired", claude.StateWord);
    }

    [Fact]
    public async Task ReDetectOnACard_AsksForThatServiceOnly()
    {
        var (page, _) = await BuildAsync();
        string? asked = "none";
        page.RedetectRequested += id => asked = id;

        page.Cards.Single(card => card.ProviderId == "openai").RedetectCommand.Execute(null);

        Assert.Equal("openai", asked);
    }

    [Fact]
    public async Task ASavedKey_IsShownMasked_NeverInFull()
    {
        var (page, secrets) = await BuildAsync();
        await secrets.SetAsync("providers/openai/key", "sk-admin-abcdefghijklmnop1234");

        await page.LoadSavedKeysAsync();
        var openai = page.Cards.Single(card => card.ProviderId == "openai");

        Assert.Equal("sk-admin…1234", openai.SavedKeyMask);
        Assert.Equal("Saved: sk-admin…1234", openai.KeyFieldPlaceholder);
        Assert.Equal(string.Empty, openai.Key);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("short", "•••••")]
    [InlineData("sk-ant-admin01-0123456789wxyz", "sk-ant-a…wxyz")]
    public void Mask_FollowsTheWindowsRule(string? secret, string expected)
    {
        Assert.Equal(expected, ConnectionCardViewModel.Mask(secret));
    }

    [Fact]
    public async Task Connect_OpensTheEditor_AndSavingClosesItAndRemembersTheKey()
    {
        var (page, _) = await BuildAsync();
        var openai = page.Cards.Single(card => card.ProviderId == "openai");

        Assert.Equal("Connect…", openai.ConnectButtonText);
        openai.ToggleEditCommand.Execute(null);
        Assert.True(openai.ShowKeyEditor);
        openai.Key = "sk-admin-abcdefghijklmnop1234";
        await page.SaveAsync(openai);

        Assert.False(openai.IsEditing);
        Assert.True(openai.HasSavedKey);
    }

    [Fact]
    public async Task ClosingTheEditor_ForgetsAHalfTypedKey_AndHidesItAgain()
    {
        var (page, _) = await BuildAsync();
        var openai = page.Cards.Single(card => card.ProviderId == "openai");
        openai.IsEditing = true;
        openai.Key = "sk-admin-half";
        openai.IsKeyRevealed = true;

        openai.IsEditing = false;

        Assert.Equal(string.Empty, openai.Key);
        Assert.False(openai.IsKeyRevealed);
    }

    [Fact]
    public async Task AServiceWithoutAKey_OffersSignInHelp()
    {
        var (page, _) = await BuildAsync();

        Assert.Equal("Sign-in help", page.Cards.Single(card => card.ProviderId == "agy").ConnectButtonText);
    }

    [Fact]
    public async Task OpenCode_ReadsConnectedWhenItHasSignIns()
    {
        var (page, _) = await BuildAsync();

        page.UpdateOpenCode(new OpenCodeAuthStatus(true, "opencode auth list", [new OpenCodeSignIn("anthropic", "OAuth")], false, ""));

        Assert.Equal("✓  Connected", page.OpenCodeCard.StateChip);
        Assert.True(page.OpenCodeCard.IsGood);
    }
}
