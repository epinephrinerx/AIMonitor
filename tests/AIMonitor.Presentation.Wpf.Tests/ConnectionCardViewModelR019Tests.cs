using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-019 (Docs/SPEC_R019.md): E2/E12a's connections-card rule — when the Claude card is in the
/// admin-only state (Admin key working, Claude Code OAuth missing/expired/rejected), "The Connections
/// card status reads <c>Admin key connected · Claude Code not signed in</c>" — and reads that ONLY in
/// that state.
///
/// <para><b>Aligned API (E11/E12a, read from the merged <c>src/</c>):</b> the card learns the signal
/// through
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

        // Detection truthfulness survives in the card (E2): not shown as signed in.
        Assert.DoesNotContain("Connected", card.StateWord, StringComparison.Ordinal);
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
    public void Update_ClaudeAdminOnly_WithExpiredDetection_ReadsAdminStatus_AndStateStaysExpired()
    {
        // E2/E12a: the AdminOnly signal covers expired OAuth too; the detection-derived state word
        // stays truthful ("Expired") while the source line carries the admin status.
        var card = new ConnectionCardViewModel(ProviderMeta.Claude);

        card.Update(
            new DetectionInfo("claude", DetectionState.Expired, "claude_code", "Claude Code", "org-user"),
            adminOnly: true);

        Assert.Equal("Expired", card.StateWord);
        Assert.Contains(AdminOnlyStatus, card.SourceLine, StringComparison.Ordinal);
    }
}
