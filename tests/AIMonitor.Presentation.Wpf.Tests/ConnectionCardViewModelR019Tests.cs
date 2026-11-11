using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-019 (from Docs/SPEC_R019.md only): E2's connections-card rule — when the Claude tab is in the
/// admin-only state (Admin key working, Claude Code OAuth missing/expired/rejected), "The Connections
/// card status reads <c>Admin key connected · Claude Code not signed in</c>".
///
/// <para><b>Expected API (alignment contract):</b> the card must learn the E2 signal from the
/// composition. The test below guesses a new overload
/// <c>ConnectionCardViewModel.Update(DetectionInfo? detection, bool adminOnly)</c>. If the Writing
/// Owner threaded the signal differently (e.g. a DetectionInfo member or a separate setter), align
/// this one call — the assertions (status text, truthful detection-derived fields, no key material)
/// come straight from E2 and stay.</para>
///
/// <para>Fails on base 29f4048 because the overload (and the R-019 signal) does not exist yet.
/// Activate with <c>-p:DefineConstants=R019_IMPLEMENTED</c>.</para>
/// </summary>
public sealed class ConnectionCardViewModelR019Tests
{
    [Fact]
    public void Update_ClaudeAdminOnly_ReadsAdminKeyConnectedClaudeCodeNotSignedIn()
    {
#if R019_IMPLEMENTED
        // E2: the signal is explicit (admin-only), the detection stays truthful (NotConnected here).
        var card = new ConnectionCardViewModel(ProviderMeta.Claude);

        card.Update(
            new DetectionInfo("claude", DetectionState.NotConnected),
            adminOnly: true);

        // The status line, verbatim from E2 (middle dot · with single spaces).
        var status = string.Join(" | ", card.StateWord, card.Account, card.SourceLine);
        Assert.Contains("Admin key connected · Claude Code not signed in", status, StringComparison.Ordinal);

        // Detection truthfulness survives in the card (E2): not shown as signed in.
        Assert.DoesNotContain("Connected", card.StateWord, StringComparison.Ordinal);
#else
        Assert.Fail(
            "R-019 not on this branch (base 29f4048): the admin-only card signal does not exist yet. "
            + "Build with -p:DefineConstants=R019_IMPLEMENTED after the merge and align the Update "
            + "signature per the file header. Spec string to honour: "
            + "\"Admin key connected · Claude Code not signed in\".");
#endif
    }

    [Fact]
    public void Update_ClaudeNormalOAuth_DoesNotReadAdminKeyConnected()
    {
#if R019_IMPLEMENTED
        // Negative leg of the same rule: signed-in Claude Code must not claim admin-only status,
        // even if a key happens to be saved (R-019 must not repaint existing states — E10).
        var card = new ConnectionCardViewModel(ProviderMeta.Claude);

        card.Update(
            new DetectionInfo("claude", DetectionState.Connected, "claude_code", "Claude Code"),
            adminOnly: false);

        var status = string.Join(" | ", card.StateWord, card.Account, card.SourceLine);
        Assert.DoesNotContain("Admin key connected", status, StringComparison.Ordinal);
        Assert.Equal("Connected", card.StateWord);
#else
        Assert.Fail(
            "R-019 not on this branch (base 29f4048): the admin-only card signal does not exist yet. "
            + "Activate with -p:DefineConstants=R019_IMPLEMENTED after the merge.");
#endif
    }
}
