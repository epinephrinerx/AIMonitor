using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class SupplementaryDetailsTests
{
    [Fact]
    public void Claude_ResetStatus_IsAlwaysNotVerifiedWithInstructions()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        var snapshot = new ProviderSnapshot(
            providerId: "claude",
            configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 50.0)],
            stats: [new Stat("Equivalent API value", "$5.00", "estimate")]);

        vm.UpdateFromSnapshot(snapshot);

        Assert.True(vm.ShowSupplementary);
        Assert.Equal("Limit Resets (Claude)", vm.ResetTitle);
        Assert.Equal("Not verified", vm.ResetBadge);
        Assert.Contains("Settings > Usage on Claude web/Desktop", vm.ResetSummary);
        Assert.Contains("support.claude.com", vm.ResetDetails);
        Assert.DoesNotContain("Available: ", vm.ResetBadge);
    }

    [Fact]
    public void Codex_WhenResetAbsentOrMalformed_IsMarkedNotVerifiedNotZero()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        var snapshot = new ProviderSnapshot(
            providerId: "openai",
            configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 20.0)],
            stats: [new Stat("Plan", "Plus", "ChatGPT plan")]); // No Rate limit reset stat

        vm.UpdateFromSnapshot(snapshot);

        Assert.True(vm.ShowSupplementary);
        Assert.Equal("Rate Limit Resets (Codex)", vm.ResetTitle);
        Assert.Equal("Not verified", vm.ResetBadge);
        Assert.NotEqual("0 Credits", vm.ResetBadge);
        Assert.Contains("Not verified", vm.ResetSummary);
        Assert.Contains("not zero", vm.ResetSummary);
    }

    [Fact]
    public void Codex_WhenResetZero_IsMarkedConfirmedZero()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        var snapshot = new ProviderSnapshot(
            providerId: "openai",
            configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 20.0)],
            stats: [new Stat("Rate limit resets", "0", "")]);

        vm.UpdateFromSnapshot(snapshot);

        Assert.True(vm.ShowSupplementary);
        Assert.Equal("Rate Limit Resets (Codex)", vm.ResetTitle);
        Assert.Equal("0 Credits", vm.ResetBadge);
        Assert.Contains("count = 0", vm.ResetSummary);
    }

    [Fact]
    public void Codex_WhenResetAvailable_ShowsCountAndEarliestExpiry()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        var snapshot = new ProviderSnapshot(
            providerId: "openai",
            configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 20.0)],
            stats: [new Stat("Rate limit resets", "2", "Next known expiry: 2026-10-20 18:00:00Z")]);

        vm.UpdateFromSnapshot(snapshot);

        Assert.True(vm.ShowSupplementary);
        Assert.Equal("Rate Limit Resets (Codex)", vm.ResetTitle);
        Assert.Equal("Available: 2", vm.ResetBadge);
        Assert.Contains("2 reset rights available", vm.ResetSummary);
        Assert.Contains("Next known expiry: 2026-10-20 18:00:00Z", vm.ResetSummary);
    }

    [Fact]
    public void Gemini_NeverShowsSupplementarySectionOrFakeContent()
    {
        var vm = new ProviderTabViewModel("gemini", "Gemini");
        var snapshot = new ProviderSnapshot(
            providerId: "gemini",
            configured: true,
            meters: [new Meter("requests", "requests", "Requests", "Daily", 15.0)],
            stats: [new Stat("Requests", "150", "")]);

        vm.UpdateFromSnapshot(snapshot);

        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);
        Assert.Empty(vm.SupplementaryRows);
        Assert.Empty(vm.ActivityPills);
    }

    [Fact]
    public void UpdateFromSnapshot_RemovesSupplementaryMetadataFromLegacyStatTiles_PreventingDuplicates()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        var snapshot = new ProviderSnapshot(
            providerId: "openai",
            configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 30.0)],
            stats:
            [
                new Stat("Plan", "Plus", "ChatGPT plan"),
                new Stat("Credits", "150", "Workspace credits"),
                new Stat("Limit reason", "Rate limit reached", "Reported by Codex"),
                new Stat("Rate limit resets", "1", "Next known expiry unknown"),
                new Stat("Current streak", "5 days", "Reported by Codex"),
                new Stat("Longest streak", "12 days", "Reported by Codex"),
                new Stat("Longest turn", "45s", "Reported by Codex"),
                new Stat("Lifetime tokens", "4.5M", "Reported by Codex"),
                new Stat("Peak daily tokens", "120K", "Reported by Codex"),
                new Stat("Spend in range", "$12.50", "Billed")
            ]);

        vm.UpdateFromSnapshot(snapshot);

        // Supplementary rows contain Plan, Credits, Limit reason
        Assert.Contains(vm.SupplementaryRows, r => r.Label == "Plan" && r.Value == "Plus");
        Assert.Contains(vm.SupplementaryRows, r => r.Label == "Credits" && r.Value == "150");
        Assert.Contains(vm.SupplementaryRows, r => r.Label == "Limit reason" && r.Value == "Rate limit reached");

        // Activity pills contain the streaks and turn duration
        Assert.Contains(vm.ActivityPills, p => p.Label == "Current streak" && p.Value == "5 days");
        Assert.Contains(vm.ActivityPills, p => p.Label == "Longest streak" && p.Value == "12 days");
        Assert.Contains(vm.ActivityPills, p => p.Label == "Longest turn" && p.Value == "45s");

        // Legacy StatTiles MUST NOT contain supplementary stats, preventing duplicate metadata
        Assert.DoesNotContain(vm.StatTiles, t => t.Label is "Plan" or "Credits" or "Limit reason" or "Rate limit resets"
            or "Current streak" or "Longest streak" or "Longest turn");

        // Legacy StatTiles MUST retain numeric tokens/spend stats unchanged
        Assert.Contains(vm.StatTiles, t => t.Label == "Lifetime tokens" && t.Value == "4.5M");
        Assert.Contains(vm.StatTiles, t => t.Label == "Peak daily tokens" && t.Value == "120K");
        Assert.Contains(vm.StatTiles, t => t.Label == "Spend in range" && t.Value == "$12.50");
    }

    [Fact]
    public void UpdateFromSnapshot_PreservesGroupedLabelsForMultipleBuckets()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        var snapshot = new ProviderSnapshot(
            providerId: "openai",
            configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 30.0)],
            stats:
            [
                new Stat("Codex · Plan", "Plus", "ChatGPT plan"),
                new Stat("Codex · Credits", "150", "Workspace credits"),
                new Stat("GPT-5 · Plan", "Team", "ChatGPT plan"),
                new Stat("GPT-5 · Credits", "Unlimited", "Workspace credits"),
                new Stat("GPT-5 · Limit reason", "Rate limit reached", "Reported by Codex")
            ]);

        vm.UpdateFromSnapshot(snapshot);

        Assert.Equal(5, vm.SupplementaryRows.Count);
        Assert.Equal("Codex · Plan", vm.SupplementaryRows[0].Label);
        Assert.Equal("Codex · Credits", vm.SupplementaryRows[1].Label);
        Assert.Equal("GPT-5 · Plan", vm.SupplementaryRows[2].Label);
        Assert.Equal("GPT-5 · Credits", vm.SupplementaryRows[3].Label);
        Assert.Equal("GPT-5 · Limit reason", vm.SupplementaryRows[4].Label);
    }

    [Fact]
    public void UpdateFromSnapshot_RefreshRemovesStaleRows()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");

        // First snapshot with full supplementary data
        var firstSnapshot = new ProviderSnapshot(
            providerId: "openai",
            configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 30.0)],
            stats:
            [
                new Stat("Plan", "Plus", "ChatGPT plan"),
                new Stat("Credits", "150", "Workspace credits"),
                new Stat("Rate limit resets", "2", "Next known expiry: 2026-10-20 18:00:00Z"),
                new Stat("Current streak", "5 days", "Reported by Codex")
            ]);

        vm.UpdateFromSnapshot(firstSnapshot);
        Assert.Equal("Available: 2", vm.ResetBadge);
        Assert.Single(vm.ActivityPills);

        // Second snapshot where metadata is absent
        var secondSnapshot = new ProviderSnapshot(
            providerId: "openai",
            configured: true,
            meters: [new Meter("session", "session", "Session", "5-hour window", 35.0)],
            stats:
            [
                new Stat("Lifetime tokens", "5.0M", "Reported by Codex")
            ]);

        vm.UpdateFromSnapshot(secondSnapshot);

        // Stale rows must be removed; reset must show Not verified fallback rather than previous "Available: 2"
        Assert.Equal("Not verified", vm.ResetBadge);
        Assert.Empty(vm.ActivityPills);
        Assert.DoesNotContain(vm.SupplementaryRows, r => r.Value == "150");
    }

    [Fact]
    public void UpdateFromSnapshot_MetersRemainIdenticalBeforeAndAfterSupplementaryIntegration()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI / Codex");
        var meter = new Meter("session", "codex_codex_primary", "Session", "5-hour window", 68.0);

        var snapshotWithoutMetadata = new ProviderSnapshot(
            providerId: "openai",
            configured: true,
            meters: [meter],
            stats: []);

        vm.UpdateFromSnapshot(snapshotWithoutMetadata);
        var initialMeter = Assert.Single(vm.Meters);
        Assert.Equal(68.0, initialMeter.Value);
        Assert.Equal("68%", initialMeter.ValueText);
        Assert.Equal("Session", initialMeter.Title);

        var snapshotWithMetadata = new ProviderSnapshot(
            providerId: "openai",
            configured: true,
            meters: [meter],
            stats:
            [
                new Stat("Plan", "Plus", "ChatGPT plan"),
                new Stat("Rate limit resets", "1", "")
            ]);

        vm.UpdateFromSnapshot(snapshotWithMetadata);
        var afterMeter = Assert.Single(vm.Meters);
        Assert.Equal(initialMeter.Value, afterMeter.Value);
        Assert.Equal(initialMeter.ValueText, afterMeter.ValueText);
        Assert.Equal(initialMeter.Title, afterMeter.Title);
        Assert.Equal(initialMeter.Severity, afterMeter.Severity);
    }
}
