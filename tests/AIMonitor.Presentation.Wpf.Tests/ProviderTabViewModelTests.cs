using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public class ProviderTabViewModelTests
{
    [Fact]
    public void Constructor_InitializesProperties()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");

        Assert.Equal("claude", vm.ProviderId);
        Assert.Equal("Claude", vm.DisplayName);
        Assert.Equal("Checking...", vm.Status);
        Assert.Empty(vm.Meters);
        Assert.Empty(vm.ChartBuckets);
    }

    [Fact]
    public void UpdateFromSnapshot_NullSnapshot_ThrowsArgumentNullException()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        Assert.Throws<ArgumentNullException>(() => vm.UpdateFromSnapshot(null!));
    }

    [Fact]
    public void UpdateFromSnapshot_WithDetectionState_UpdatesStatusAndDetail()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        var detection = new DetectionInfo("openai", DetectionState.Connected, "env", "API Key", account: "user@example.com", hint: "Direct key detected");
        var snapshot = new ProviderSnapshot("openai", configured: true, detection: detection, account: "user@example.com");

        vm.UpdateFromSnapshot(snapshot);

        Assert.Equal("Connected", vm.Status);
        Assert.Equal("Direct key detected", vm.StatusDetail);
        Assert.Equal("Account: user@example.com", vm.AccountInfo);
        Assert.Equal(string.Empty, vm.ErrorMessage);
    }

    [Fact]
    public void UpdateFromSnapshot_WithError_UpdatesErrorProperties()
    {
        var vm = new ProviderTabViewModel("gemini", "Gemini");
        var snapshot = new ProviderSnapshot("gemini", configured: false, error: "Authentication failed 401");

        vm.UpdateFromSnapshot(snapshot);

        Assert.Equal("Error", vm.Status);
        Assert.Equal("Authentication failed 401", vm.ErrorMessage);
    }

    [Fact]
    public void UpdateFromSnapshot_WithMeters_PopulatesMeterDisplayItems()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        var meter = new Meter("session", "session", "5-hour session", "Resets soon", percent: 65.4, serverSeverity: Severity.Normal);
        var snapshot = new ProviderSnapshot("claude", configured: true, meters: [meter]);

        vm.UpdateFromSnapshot(snapshot);

        Assert.Single(vm.Meters);
        var item = vm.Meters[0];
        Assert.Equal("5-hour session", item.Title);
        Assert.Equal(65.4, item.Value);
        Assert.Equal("65%", item.ValueText);
        Assert.Equal(Severity.Normal, item.Severity);
    }

    [Fact]
    public void UpdateFromSnapshot_MetersWithoutSection_FormOneUnheadedRow()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true, meters:
        [
            new Meter("session", "session", "Session", "5-hour window", percent: 10),
            new Meter("weekly_all", "weekly_all", "Weekly", "All models", percent: 20),
        ]));

        var section = Assert.Single(vm.MeterSections);
        Assert.Equal("", section.Heading);
        Assert.Equal(2, section.Columns);
        Assert.Equal(["Session", "Weekly"], section.Meters.Select(m => m.GaugeTitle));
    }

    [Fact]
    public void UpdateFromSnapshot_SectionedMeters_GroupInFirstSeenOrderWithSharedColumnsAndShortTitles()
    {
        var vm = new ProviderTabViewModel("agy", "Gemini / AGY");
        vm.UpdateFromSnapshot(new ProviderSnapshot("agy", configured: true, meters:
        [
            new Meter("session", "g5", "Gemini · Session", "5-hour window", percent: 0, section: "Gemini"),
            new Meter("weekly", "gw", "Gemini · Weekly", "Weekly window", percent: 10, section: "Gemini"),
            new Meter("session", "c5", "Claude / GPT · Session", "5-hour window", percent: 0, section: "Claude / GPT"),
            new Meter("weekly", "cw", "Claude / GPT · Weekly", "Weekly window", percent: 0, section: "Claude / GPT"),
            new Meter("weekly", "aw", "Gemini 4 Argon · Weekly", "Weekly window", percent: 0, section: "Gemini"),
        ]));

        Assert.Equal(["Gemini", "Claude / GPT"], vm.MeterSections.Select(s => s.Heading));
        Assert.All(vm.MeterSections, s => Assert.Equal(3, s.Columns));
        Assert.Equal(["Session", "Weekly", "Gemini 4 Argon · Weekly"], vm.MeterSections[0].Meters.Select(m => m.GaugeTitle));
        Assert.Equal(["Session", "Weekly"], vm.MeterSections[1].Meters.Select(m => m.GaugeTitle));

        // Widget and tray read the flat list and keep the full, self-describing titles.
        Assert.Equal("Gemini · Session", vm.Meters[0].Title);
        Assert.Equal(5, vm.Meters.Count);
    }

    [Fact]
    public void UpdateFromSnapshot_WithHistoryBuckets_PopulatesChartData()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var bucket = new UsageHistoryBucket(today, new Dictionary<string, double> { ["gpt-4"] = 2500 });
        var history = new UsageHistory(buckets: [bucket]);
        var snapshot = new ProviderSnapshot("openai", configured: true, history: history);

        vm.UpdateFromSnapshot(snapshot);

        Assert.True(vm.HasHistory);
        var day = Assert.Single(vm.ChartBuckets);
        Assert.Equal(today, day.Day);
        Assert.Equal(2500, day.Total);
        Assert.Equal("Usage per day · last 14 days", vm.ChartTitle);
    }

    [Fact]
    public void UpdateFromSnapshot_WithStats_FormatsStatsSummary()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        var stat1 = new Stat("Tokens Today", "15,200");
        var stat2 = new Stat("Cost", "$0.45");
        var snapshot = new ProviderSnapshot("claude", configured: true, stats: [stat1, stat2]);

        vm.UpdateFromSnapshot(snapshot);

        Assert.Equal("Tokens Today: 15,200  |  Cost: $0.45", vm.StatsSummary);
    }

    [Fact]
    public void UpdateFromSnapshot_WithCodexRateLimitStats_PopulatesSupplementaryRowsAndClearsDuplicatesFromStatTiles()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        var stats = new List<Stat>
        {
            new("Plan", "Plus", "ChatGPT plan"),
            new("Credits", "150", "Workspace credits"),
            new("Limit reason", "Workspace owner credits depleted", "Reported by Codex"),
            new("Lifetime tokens", "4.5M", "Reported by Codex")
        };
        var snapshot = new ProviderSnapshot("openai", configured: true, stats: stats);

        vm.UpdateFromSnapshot(snapshot);

        // Plan, Credits, and Limit reason are moved to SupplementaryRows; only Lifetime tokens remains in StatTiles
        Assert.Single(vm.StatTiles);
        Assert.Equal("Lifetime tokens", vm.StatTiles[0].Label);
        Assert.Equal("4.5M", vm.StatTiles[0].Value);

        Assert.True(vm.ShowSupplementary);
        Assert.NotNull(vm.Supplementary);
        Assert.Equal(3, vm.SupplementaryRows.Count);
        Assert.Equal("Plan", vm.SupplementaryRows[0].Label);
        Assert.Equal("Plus", vm.SupplementaryRows[0].Value);
        Assert.Equal("Credits", vm.SupplementaryRows[1].Label);
        Assert.Equal("150", vm.SupplementaryRows[1].Value);
        Assert.Equal("Limit reason", vm.SupplementaryRows[2].Label);
        Assert.Equal("Workspace owner credits depleted", vm.SupplementaryRows[2].Value);
        Assert.Equal("Plan: Plus  |  Credits: 150  |  Limit reason: Workspace owner credits depleted  |  Lifetime tokens: 4.5M", vm.StatsSummary);
    }

    [Fact]
    public void UpdateFromSnapshot_WhenUIRefresh_RemovesStaleRows()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        var stats1 = new List<Stat>
        {
            new("Codex · Plan", "Plus", "ChatGPT plan"),
            new("GPT-5 · Plan", "Team", "ChatGPT plan"),
            new("Credits", "150", "Workspace credits")
        };
        vm.UpdateFromSnapshot(new ProviderSnapshot("openai", configured: true, stats: stats1));

        Assert.Equal(3, vm.SupplementaryRows.Count);

        // Refresh with a single bucket plan
        var stats2 = new List<Stat>
        {
            new("Plan", "Pro", "ChatGPT plan")
        };
        vm.UpdateFromSnapshot(new ProviderSnapshot("openai", configured: true, stats: stats2));

        // Stale rows from the first snapshot are completely replaced
        Assert.Equal(2, vm.SupplementaryRows.Count); // 1 plan + 1 fallback credits (none reported)
        Assert.Equal("Plan", vm.SupplementaryRows[0].Label);
        Assert.Equal("Pro", vm.SupplementaryRows[0].Value);
        Assert.DoesNotContain(vm.SupplementaryRows, r => r.Label == "GPT-5 · Plan");
    }

    [Fact]
    public void UpdateFromSnapshot_WhenMultiBucket_PreservesGroupedLabels()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        var stats = new List<Stat>
        {
            new("Codex · Plan", "Plus", "ChatGPT plan"),
            new("Codex · Credits", "150", "Workspace credits"),
            new("GPT-5 · Plan", "Team", "ChatGPT plan"),
            new("GPT-5 · Credits", "Unlimited", "Workspace credits"),
            new("GPT-5 · Limit reason", "Rate limit reached", "Reported by Codex")
        };
        var snapshot = new ProviderSnapshot("openai", configured: true, stats: stats);

        vm.UpdateFromSnapshot(snapshot);

        Assert.Equal(5, vm.SupplementaryRows.Count);
        Assert.Equal("Codex · Plan", vm.SupplementaryRows[0].Label);
        Assert.Equal("Codex · Credits", vm.SupplementaryRows[1].Label);
        Assert.Equal("GPT-5 · Plan", vm.SupplementaryRows[2].Label);
        Assert.Equal("GPT-5 · Credits", vm.SupplementaryRows[3].Label);
        Assert.Equal("GPT-5 · Limit reason", vm.SupplementaryRows[4].Label);
    }

    [Fact]
    public void UpdateFromSnapshot_WhenClaude_ResetAlwaysUnknownAndNoDuplicates()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        var stats = new List<Stat>
        {
            new("Tokens Today", "15,200"),
            new("Cost", "$0.45")
        };
        var snapshot = new ProviderSnapshot("claude", configured: true, stats: stats);

        vm.UpdateFromSnapshot(snapshot);

        Assert.True(vm.ShowSupplementary);
        Assert.Equal("Not verified", vm.ResetBadge);
        Assert.Contains("Check Settings > Usage on Claude web/Desktop", vm.ResetSummary);
        Assert.Contains("Official Claude Limit Reset Procedure", vm.ResetDetails);
        Assert.DoesNotContain(vm.ResetSummary, "available");

        // StatTiles has only numeric legacy stats, no duplicate metadata
        Assert.Equal(2, vm.StatTiles.Count);
        Assert.Equal("Tokens Today", vm.StatTiles[0].Label);
        Assert.Equal("Cost", vm.StatTiles[1].Label);
    }

    [Fact]
    public void UpdateFromSnapshot_WhenGemini_NoSupplementarySection()
    {
        var vm = new ProviderTabViewModel("gemini", "Gemini");
        var stats = new List<Stat>
        {
            new("Requests today", "128")
        };
        var snapshot = new ProviderSnapshot("gemini", configured: true, stats: stats);

        vm.UpdateFromSnapshot(snapshot);

        Assert.False(vm.ShowSupplementary);
        Assert.Null(vm.Supplementary);
        Assert.Empty(vm.SupplementaryRows);
        Assert.Single(vm.StatTiles);
        Assert.Equal("Requests today", vm.StatTiles[0].Label);
    }

    [Fact]
    public void UpdateFromSnapshot_MetersUnchangedBeforeAndAfterSupplementaryChanges()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        var meter1 = new Meter("session", "session", "Session", "Codex quota", 42.0);
        var meter2 = new Meter("weekly", "weekly", "Weekly", "Codex quota", 78.0);
        var stats = new List<Stat>
        {
            new("Plan", "Plus", "ChatGPT plan"),
            new("Rate limit resets", "2", "Next expiry: 2026-10-20 18:00:00Z"),
            new("Current streak", "8 days", "Reported by Codex")
        };
        var snapshot = new ProviderSnapshot("openai", configured: true, meters: [meter1, meter2], stats: stats);

        vm.UpdateFromSnapshot(snapshot);

        Assert.Equal(2, vm.Meters.Count);
        Assert.Equal(42.0, vm.Meters[0].Value);
        Assert.Equal("42%", vm.Meters[0].ValueText);
        Assert.Equal("Session", vm.Meters[0].Title);
        Assert.Equal(78.0, vm.Meters[1].Value);
        Assert.Equal("78%", vm.Meters[1].ValueText);
        Assert.Equal("Weekly", vm.Meters[1].Title);
    }

    [Fact]
    public void UpdateFromSnapshot_WhenCodexResetAbsentOrMalformed_FallsBackToNotVerified()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        var snapshot = new ProviderSnapshot("openai", configured: true, stats: []);

        vm.UpdateFromSnapshot(snapshot);

        Assert.True(vm.ShowSupplementary);
        Assert.Equal("Not verified", vm.ResetBadge);
        Assert.Contains("No confirmed reset data", vm.ResetSummary);
    }

    [Fact]
    public void UpdateFromSnapshot_WhenCodexResetZero_ConfirmedZero()
    {
        var vm = new ProviderTabViewModel("openai", "OpenAI");
        var snapshot = new ProviderSnapshot("openai", configured: true, stats: [new Stat("Rate limit resets", "0")]);

        vm.UpdateFromSnapshot(snapshot);

        Assert.True(vm.ShowSupplementary);
        Assert.Equal("0 Credits", vm.ResetBadge);
        Assert.Contains("Confirmed no reset credits available on this account (count = 0)", vm.ResetSummary);
    }
}
