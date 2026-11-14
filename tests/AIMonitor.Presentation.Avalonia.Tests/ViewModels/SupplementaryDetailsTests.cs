using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

/// <summary>The supplementary section on the Claude and Codex tabs, with the Windows edition's rules.</summary>
public sealed class SupplementaryDetailsTests
{
    private static ProviderViewModel Tab(string providerId, params Stat[] stats)
    {
        var tab = new ProviderViewModel(providerId, providerId);
        tab.Apply(new ProviderSnapshot(providerId, configured: true, meters: [TestData.Meter()], stats: stats));
        return tab;
    }

    [Fact]
    public void Claude_ResetStatus_IsNotVerified_WithTheWayToCheck()
    {
        var details = Tab("claude").Supplementary!;

        Assert.Equal("Limit Resets (Claude)", details.ResetTitle);
        Assert.Equal("Not verified", details.ResetBadge);
        Assert.Contains("Settings > Usage", details.ResetDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_WithNoResetFigure_SaysNotVerified_NotZero()
    {
        var details = Tab("openai").Supplementary!;

        Assert.Equal("Not verified", details.ResetBadge);
        Assert.Contains("(not zero)", details.ResetSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_WithZeroResets_SaysSoPlainly()
    {
        var details = Tab("openai", new Stat("Rate limit resets", "0")).Supplementary!;

        Assert.Equal("0 Credits", details.ResetBadge);
    }

    [Fact]
    public void Codex_WithResetsAvailable_ShowsTheCountAndExpiry()
    {
        var details = Tab("openai", new Stat("Rate limit resets", "2", "next expires in 3d")).Supplementary!;

        Assert.Equal("Available: 2", details.ResetBadge);
        Assert.StartsWith("2 reset rights available · next expires in 3d", details.ResetSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreportedPlanAndCredits_AreSaidToBeUnreported_NotLeftOut()
    {
        var details = Tab("claude").Supplementary!;

        Assert.Equal(["Plan", "Credits"], details.Rows.Select(row => row.Badge));
        Assert.Equal("Not reported", details.Rows[0].Value);
        Assert.Equal("Not verified", details.Rows[1].Value);
    }

    [Fact]
    public void ReportedPlanCreditsAndLimitReason_MoveFromTheTilesIntoTheSection()
    {
        var tab = Tab("openai",
            new Stat("Plan", "Pro"),
            new Stat("Credits", "$12.00"),
            new Stat("Limit reason", "Weekly cap"),
            new Stat("Tokens today", "1.2M"));

        Assert.True(tab.ShowSupplementary);
        Assert.Equal(["Plan", "Credits", "Limit"], tab.Supplementary!.Rows.Select(row => row.Badge));
        Assert.Equal(["Tokens today"], tab.StatTiles.Select(tile => tile.Label));
    }

    [Fact]
    public void CodexActivity_BecomesPills()
    {
        var details = Tab("openai", new Stat("Current streak", "4 days"), new Stat("Longest turn", "12m")).Supplementary!;

        Assert.Equal("Codex Activity", details.ActivityTitle);
        Assert.Equal(["Current streak: 4 days", "Longest turn: 12m"], details.ActivityPills.Select(pill => pill.DisplayText));
    }

    [Fact]
    public void AGY_HasNoSupplementarySection()
    {
        var tab = Tab("agy", new Stat("Plan", "Pro"));

        Assert.Null(tab.Supplementary);
        Assert.False(tab.ShowSupplementary);
        Assert.Equal(["Plan"], tab.StatTiles.Select(tile => tile.Label));
    }

    [Fact]
    public void AnOpenResetHowTo_StaysOpenAcrossARefresh()
    {
        var tab = Tab("claude");
        tab.Supplementary!.IsResetDetailsExpanded = true;

        tab.Apply(new ProviderSnapshot("claude", configured: true, meters: [TestData.Meter()]));

        Assert.True(tab.Supplementary!.IsResetDetailsExpanded);
    }
}
