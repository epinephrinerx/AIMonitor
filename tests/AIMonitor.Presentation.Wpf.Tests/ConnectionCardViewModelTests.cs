using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class ConnectionCardViewModelTests
{
    [Fact]
    public void Constructor_WithNullMeta_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConnectionCardViewModel(null!));
    }

    [Fact]
    public void Constructor_InitializesWithCheckingState_AndCorrectButtonText()
    {
        var claudeCard = new ConnectionCardViewModel(ProviderMeta.Claude);
        Assert.Equal("Checking...", claudeCard.StateWord);
        Assert.Equal("–", claudeCard.StateGlyph);
        Assert.Equal("Not signed in", claudeCard.Account);
        Assert.Equal("Claude Code · full quota", claudeCard.SourceLine);
        Assert.Equal("Sign-in help", claudeCard.ConnectButtonText);

        var openAiCard = new ConnectionCardViewModel(ProviderMeta.OpenAi);
        Assert.Equal("Checking...", openAiCard.StateWord);
        Assert.Equal("–", openAiCard.StateGlyph);
        Assert.Equal("Codex quota · optional API platform spend", openAiCard.SourceLine);
        Assert.Equal("Connect...", openAiCard.ConnectButtonText);
    }

    [Fact]
    public void Update_WhenDetectionNull_BehavesLikeNotConnectedWithCheckingStateWord()
    {
        var card = new ConnectionCardViewModel(ProviderMeta.OpenAi);

        // First transition to connected
        var connected = new DetectionInfo("openai", DetectionState.Connected, "manual", "Saved key", "org-123");
        card.Update(connected);
        Assert.Equal("Connected", card.StateWord);
        Assert.Equal("✓", card.StateGlyph);
        Assert.Equal("org-123", card.Account);

        // Update back to null
        card.Update(null);
        Assert.Null(card.Detection);
        Assert.Equal("Checking...", card.StateWord);
        Assert.Equal("–", card.StateGlyph);
        Assert.Equal("Not signed in", card.Account);
        Assert.Equal(ProviderMeta.OpenAi.Tagline, card.SourceLine);
        Assert.Equal("Connect...", card.ConnectButtonText);
    }

    [Theory]
    [InlineData(DetectionState.Connected, "Connected", "✓", "Change...")]
    [InlineData(DetectionState.Limited, "Limited", "!", "Connect...")]
    [InlineData(DetectionState.Expired, "Expired", "!", "Connect...")]
    [InlineData(DetectionState.NotConnected, "Not connected", "–", "Connect...")]
    public void Update_ForOpenAi_RendersCorrectStateWordGlyphAndButton(
        DetectionState state, string expectedWord, string expectedGlyph, string expectedButton)
    {
        var card = new ConnectionCardViewModel(ProviderMeta.OpenAi);
        var detection = new DetectionInfo("openai", state, "source", "Source Label", "my-account");

        card.Update(detection);

        Assert.Equal(expectedWord, card.StateWord);
        Assert.Equal(expectedGlyph, card.StateGlyph);
        Assert.Equal("my-account", card.Account);
        Assert.Equal(expectedButton, card.ConnectButtonText);
    }

    [Fact]
    public void Update_ForClaude_AlwaysRendersSignInHelpButton_RegardlessOfState()
    {
        var card = new ConnectionCardViewModel(ProviderMeta.Claude);

        card.Update(new DetectionInfo("claude", DetectionState.Connected));
        Assert.Equal("Sign-in help", card.ConnectButtonText);

        card.Update(new DetectionInfo("claude", DetectionState.NotConnected));
        Assert.Equal("Sign-in help", card.ConnectButtonText);

        card.Update(new DetectionInfo("claude", DetectionState.Expired));
        Assert.Equal("Sign-in help", card.ConnectButtonText);
    }

    [Fact]
    public void Update_Account_WhenAccountMissing_FallsBackToNotSignedInOrTagline()
    {
        var card = new ConnectionCardViewModel(ProviderMeta.OpenAi);

        // NotConnected without account -> "Not signed in"
        card.Update(new DetectionInfo("openai", DetectionState.NotConnected, account: ""));
        Assert.Equal("Not signed in", card.Account);

        // Connected without account -> Tagline
        card.Update(new DetectionInfo("openai", DetectionState.Connected, account: ""));
        Assert.Equal(ProviderMeta.OpenAi.Tagline, card.Account);

        // Limited without account -> Tagline
        card.Update(new DetectionInfo("openai", DetectionState.Limited, account: ""));
        Assert.Equal(ProviderMeta.OpenAi.Tagline, card.Account);

        // Explicit account -> explicit account
        card.Update(new DetectionInfo("openai", DetectionState.Connected, account: "user@example.com"));
        Assert.Equal("user@example.com", card.Account);
    }

    [Fact]
    public void Update_SourceLine_HandlesCombinationsOfSourceLabelAndHint()
    {
        var card = new ConnectionCardViewModel(ProviderMeta.Gemini);

        // Both SourceLabel and Hint
        card.Update(new DetectionInfo("gemini", DetectionState.Connected, sourceLabel: "Gemini CLI", hint: "Monitoring Viewer"));
        Assert.Equal("Detected from Gemini CLI  -  Monitoring Viewer", card.SourceLine);

        // Only SourceLabel
        card.Update(new DetectionInfo("gemini", DetectionState.Connected, sourceLabel: "Gemini CLI", hint: ""));
        Assert.Equal("Detected from Gemini CLI", card.SourceLine);

        // Only Hint
        card.Update(new DetectionInfo("gemini", DetectionState.Connected, sourceLabel: "", hint: "Monitoring Viewer"));
        Assert.Equal("Monitoring Viewer", card.SourceLine);

        // Neither
        card.Update(new DetectionInfo("gemini", DetectionState.Connected, sourceLabel: "", hint: ""));
        Assert.Equal(ProviderMeta.Gemini.Tagline, card.SourceLine);
    }

    [Fact]
    public void Commands_FireEventsWithProviderId()
    {
        var card = new ConnectionCardViewModel(ProviderMeta.OpenAi);
        string? requestedConnectId = null;
        string? requestedRedetectId = null;

        card.ConnectRequested += id => requestedConnectId = id;
        card.RedetectRequested += id => requestedRedetectId = id;

        card.ConnectCommand.Execute(null);
        Assert.Equal("openai", requestedConnectId);

        card.RedetectCommand.Execute(null);
        Assert.Equal("openai", requestedRedetectId);
    }
}
