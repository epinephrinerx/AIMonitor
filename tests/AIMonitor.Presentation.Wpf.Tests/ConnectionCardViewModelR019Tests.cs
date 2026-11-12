using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-019 (Docs/SPEC_R019.md): E2/E12a's connections-card rule — when the Claude card is in the
/// admin-only state (Admin key working, Claude Code OAuth missing/expired/rejected), "The Connections
/// card status reads <c>Admin key connected · Claude Code not signed in</c>" — and reads that ONLY in
/// that state. E13f/E14 add the card's state rendering: a Claude card with <c>adminOnly</c> shows
/// StateWord "Limited" and glyph "!" for a Connected, Limited or NotConnected detection (never the
/// detection-derived "Connected"/"✓" green; only an Expired detection keeps "Expired"/"!") while
/// the DetectionInfo itself stays the credential-file truth (E13f: "DetectionInfo itself unchanged,
/// still credential-file truth").
///
/// <para><b>Aligned API (E11/E12a/E13f, read from the merged <c>src/</c>):</b> the card learns the
/// signal through
/// <c>ConnectionCardViewModel.Update(DetectionInfo? detection, bool adminOnly = false)</c>, fed from
/// <c>ProviderSnapshot.AdminOnly</c> by <c>ConnectionsViewModel.Update</c>. E12a: the card uses the
/// AdminOnly flag alone — <c>AdminApiConnected</c> keeps meaning "Admin call succeeded" and must not
/// repaint the card.</para>
/// </summary>
public sealed class ConnectionCardViewModelR019Tests
{
    // E2 card status line, verbatim (middle dot · with single spaces).
    private const string AdminOnlyStatus = "Admin key connected · Claude Code not signed in";

    [Fact]
    public void Update_ClaudeAdminOnly_ReadsAdminKeyConnectedClaudeCodeNotSignedIn()
    {
        // E2: the signal is explicit (admin-only), the detection stays truthful (NotConnected here).
        var card = new ConnectionCardViewModel(ProviderMeta.Claude);

        card.Update(
            new DetectionInfo("claude", DetectionState.NotConnected),
            adminOnly: true);

        // The status line, verbatim from E2.
        var status = string.Join(" | ", card.StateWord, card.Account, card.SourceLine);
        Assert.Contains(AdminOnlyStatus, status, StringComparison.Ordinal);

        // E13f: an admin-only Claude card reads "Limited"/"!" — never a signed-in word.
        Assert.Equal("Limited", card.StateWord);
        Assert.Equal("!", card.StateGlyph);

        // Detection truthfulness survives in the card (E2/E13f): not shown as signed in, and the
        // DetectionInfo itself is the untouched credential-file truth.
        Assert.DoesNotContain("Connected", card.StateWord, StringComparison.Ordinal);
        Assert.Equal(DetectionState.NotConnected, card.Detection!.State);
    }

    [Theory]
    [InlineData(DetectionState.Connected)]
    [InlineData(DetectionState.Limited)]
    [InlineData(DetectionState.NotConnected)]
    public void Update_ClaudeAdminOnly_WithNonExpiredDetection_ShowsLimitedNeverGreenConnected(DetectionState state)
    {
        // E14 (clarifies E13f): for Claude with adminOnly the card shows StateWord "Limited" and glyph
        // "!" whenever detection is Connected, Limited or NotConnected. The Connected row is the
        // owner's actual case (OAuth 401/403 + Admin ok: the credential file is valid, so detection is
        // still Connected) — 401/403 + Admin ok never shows "Connected ✓". DetectionInfo stays the
        // untouched credential-file truth (E13f).
        var card = new ConnectionCardViewModel(ProviderMeta.Claude);

        card.Update(
            new DetectionInfo("claude", state, "claude_code", "Claude Code", "org-user"),
            adminOnly: true);

        Assert.Equal("Limited", card.StateWord);
        Assert.Equal("!", card.StateGlyph);
        Assert.NotEqual("Connected", card.StateWord);
        Assert.NotEqual("✓", card.StateGlyph);
        Assert.Contains(AdminOnlyStatus, card.SourceLine, StringComparison.Ordinal);
        Assert.Equal(state, card.Detection!.State); // still credential-file truth (E13f)
    }

    [Fact]
    public void Update_ClaudeAdminOnly_WithExpiredDetection_KeepsExpiredWordAndGlyph()
    {
        // E14: "only an Expired detection keeps 'Expired' / '!'" — the admin-only repaint to "Limited"
        // does not apply to an expired sign-in; the status line still reads the admin-only text (E2).
        var card = new ConnectionCardViewModel(ProviderMeta.Claude);

        card.Update(
            new DetectionInfo("claude", DetectionState.Expired, "claude_code", "Claude Code", "org-user"),
            adminOnly: true);

        Assert.Equal("Expired", card.StateWord);
        Assert.Equal("!", card.StateGlyph);
        Assert.Contains(AdminOnlyStatus, card.SourceLine, StringComparison.Ordinal);
        Assert.Equal(DetectionState.Expired, card.Detection!.State); // truth preserved (E13f)
    }

    [Fact]
    public void Update_ClaudeNormalOAuth_DoesNotReadAdminKeyConnected()
    {
        // Negative leg of the same rule: signed-in Claude Code must not claim admin-only status,
        // even if a key happens to be saved (R-019 must not repaint existing states — E10).
        var card = new ConnectionCardViewModel(ProviderMeta.Claude);

        card.Update(
            new DetectionInfo("claude", DetectionState.Connected, "claude_code", "Claude Code"),
            adminOnly: false);

        var status = string.Join(" | ", card.StateWord, card.Account, card.SourceLine);
        Assert.DoesNotContain("Admin key connected", status, StringComparison.Ordinal);
        Assert.Equal("Connected", card.StateWord);
        Assert.Equal("✓", card.StateGlyph); // E13f touches admin-only cards only
    }

    [Fact]
    public void Update_ClaudeNotConnectedWithoutAdminOnly_KeepsNormalSourceLine()
    {
        // E12a "only then": without the AdminOnly signal a plain not-connected Claude card keeps the
        // pre-R-019 presentation — the admin status line may appear only when adminOnly is true.
        var card = new ConnectionCardViewModel(ProviderMeta.Claude);

        card.Update(new DetectionInfo("claude", DetectionState.NotConnected), adminOnly: false);

        Assert.Equal("Not connected", card.StateWord);
        Assert.DoesNotContain("Admin key connected", card.SourceLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_OtherProviderWithAdminOnlyFlag_IsUnaffected()
    {
        // E13f's rule is scoped to the CLAUDE card — an admin-only repaint on any other provider's
        // card (unreachable today, cheap to pin) must never appear.
        var card = new ConnectionCardViewModel(ProviderMeta.OpenAi);

        card.Update(
            new DetectionInfo("openai", DetectionState.Connected, "manual", "Saved key", "org-1"),
            adminOnly: true);

        Assert.Equal("Connected", card.StateWord);
        Assert.Equal("✓", card.StateGlyph);
        Assert.DoesNotContain(AdminOnlyStatus, card.SourceLine, StringComparison.Ordinal);
    }
}
