using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>
/// R-019 (Docs/SPEC_R019.md): E2/E12a's tab rule — with a working Admin key and OAuth missing,
/// expired or rejected, the Claude tab shows NO setup card (it shows the Admin stats and the E2 note
/// instead), and it hides the setup card ONLY then. E12a: <c>ProviderSnapshot.AdminOnly</c> is the
/// single signal the tab keys on; <c>AdminApiConnected</c> keeps meaning "Admin call succeeded" and
/// must not by itself change the setup card.
///
/// <para><b>Aligned API (E11/E12a, read from the merged <c>src/</c>):</b>
/// <c>ProviderSnapshot(…, adminApiConnected:, adminOnly:)</c>; the tab reads <c>snapshot.AdminOnly</c>
/// in <c>UpdateFromSnapshot</c>.</para>
/// </summary>
public sealed class ProviderTabViewModelR019Tests
{
    // E2 note, verbatim.
    private const string AdminOnlyNote =
        "Claude Code not signed in — quota unavailable; API usage from Admin key";

    // E10 stat labels, verbatim.
    private const string CostTodayLabel = "API cost today (UTC)";
    private const string CostMonthLabel = "API cost this month (UTC)";
    private const string TokensMonthLabel = "API tokens this month (UTC)";

    private static ProviderSnapshot AdminOnlySnapshot(DetectionState oAuthDetectionState) =>
        new(
            "claude",
            configured: true,
            stats:
            [
                new Stat(CostTodayLabel, "$1.23", "Anthropic Admin API · billed"),
                new Stat(CostMonthLabel, "$2.23", "Anthropic Admin API · billed"),
                new Stat(TokensMonthLabel, "15.1K", "Anthropic Admin API · billed"),
            ],
            error: null,
            unauthorized: false,
            setupHint: "",
            valueNote: AdminOnlyNote,
            fetchedAt: new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero),
            detection: new DetectionInfo("claude", oAuthDetectionState),
            adminApiConnected: true,
            adminOnly: true);

    [Theory]
    // E2/E12a: OAuth missing (NotConnected) or expired, Admin succeeded → AdminOnly → no setup card,
    // the Admin stats and the note, and no quota meters.
    [InlineData(DetectionState.NotConnected)]
    [InlineData(DetectionState.Expired)]
    public void UpdateFromSnapshot_ClaudeAdminOnly_NoSetupCardStatsAndNote(DetectionState oAuthState)
    {
        var tab = new ProviderTabViewModel("claude", "Claude");

        tab.UpdateFromSnapshot(AdminOnlySnapshot(oAuthState));

        Assert.False(tab.ShowSetupCard);
        Assert.Equal("", tab.ErrorMessage);
        Assert.Equal(AdminOnlyNote, tab.ValueNote);
        Assert.Empty(tab.Meters);
        Assert.Equal(
            [CostTodayLabel, CostMonthLabel, TokensMonthLabel],
            tab.StatTiles.Select(t => t.Label).ToArray());
    }

    [Fact]
    public void UpdateFromSnapshot_ClaudeNotConnectedWithoutAdminOnly_StillShowsSetupCard()
    {
        // E12a "only then": without the AdminOnly signal the Claude tab keeps R-004's setup card —
        // nothing here is repainted by R-019 (E10).
        var tab = new ProviderTabViewModel("claude", "Claude");

        tab.UpdateFromSnapshot(new ProviderSnapshot(
            "claude",
            configured: false,
            error: "No Claude Code login found.",
            detection: new DetectionInfo("claude", DetectionState.NotConnected)));

        Assert.True(tab.ShowSetupCard);
    }

    [Fact]
    public void UpdateFromSnapshot_ClaudeAdminApiConnectedButNotAdminOnly_SetupCardStays()
    {
        // E12a: the tab keys on AdminOnly ALONE. AdminApiConnected=true ("Admin call succeeded")
        // with AdminOnly=false must not hide the setup card. (A reachable shape: Admin succeeded
        // while the snapshot is otherwise unconfigured — the flag, not the connection, decides.)
        var tab = new ProviderTabViewModel("claude", "Claude");

        tab.UpdateFromSnapshot(new ProviderSnapshot(
            "claude",
            configured: false,
            error: "No Claude Code login found.",
            detection: new DetectionInfo("claude", DetectionState.NotConnected),
            adminApiConnected: true,
            adminOnly: false));

        Assert.True(tab.ShowSetupCard);
    }

    [Fact]
    public void UpdateFromSnapshot_OAuthConnectedClaudeTab_KeepsExistingPresentation()
    {
        // E2/E10: a normal signed-in tab (AdminOnly=false) is unchanged — no admin note, no setup
        // card, and no admin-only presentation even though the Admin call succeeded as well.
        var tab = new ProviderTabViewModel("claude", "Claude");

        tab.UpdateFromSnapshot(new ProviderSnapshot(
            "claude",
            configured: true,
            valueNote: "Equivalent API value at list price - a subscription is not billed per token.",
            detection: new DetectionInfo("claude", DetectionState.Connected, "claude_code", "Claude Code"),
            adminApiConnected: true,
            adminOnly: false));

        Assert.False(tab.ShowSetupCard);
        Assert.DoesNotContain("not signed in", tab.ValueNote, StringComparison.OrdinalIgnoreCase);
    }
}
