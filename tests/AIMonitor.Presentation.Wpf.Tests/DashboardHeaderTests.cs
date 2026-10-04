using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class DashboardHeaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Options_MatchLegacyChoices()
    {
        Assert.Equal(["Total tokens", "Output tokens", "Equivalent value"], DashboardOptions.Metrics.Select(o => o.Label));
        Assert.Equal([7, 14, 30, 90], DashboardOptions.Ranges.Select(o => o.Value));
        Assert.Equal([30, 60, 180, 300, 600, 1800, 0], DashboardOptions.Intervals.Select(o => o.Value));
        Assert.Equal("Every 3 minutes · default", DashboardOptions.Intervals[2].Label);
        Assert.Equal("Manual only", DashboardOptions.Intervals[^1].Label);
    }

    [Fact]
    public void Find_UnknownValue_FallsBackToIndex()
    {
        Assert.Equal(180, DashboardOptions.Find(DashboardOptions.Intervals, 45, 2).Value);
        Assert.Equal(30, DashboardOptions.Find(DashboardOptions.Ranges, 30, 1).Value);
    }

    [Fact]
    public void Normalize_KeepsManualOnlyInterval_AndStillClampsOthers()
    {
        Assert.Equal(0, new AppSettings { RefreshIntervalSeconds = 0 }.Normalize().RefreshIntervalSeconds);
        Assert.Equal(30, new AppSettings { RefreshIntervalSeconds = 5 }.Normalize().RefreshIntervalSeconds);
    }

    [Fact]
    public void Meter_ShowsSeverityWordSubtitleAndResetLine()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        var meters = new[]
        {
            new Meter("session", "session", "Session", "5-hour window", 95, resetsAt: Now.AddHours(2).AddMinutes(5)),
            new Meter("weekly", "weekly", "Weekly", "All models", 12),
        };

        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true, meters: meters), Now);

        Assert.Equal("5-hour window", vm.Meters[0].Subtitle);
        Assert.Equal("⚠ Critical", vm.Meters[0].SeverityText);
        Assert.StartsWith("Resets in 2h 5m · ", vm.Meters[0].ResetText);
        Assert.Equal("✓ Normal", vm.Meters[1].SeverityText);
        Assert.Equal("No reset scheduled", vm.Meters[1].ResetText);
    }

    [Fact]
    public void Meter_ElapsedReset_ReadsResettingNow_AndLockReasonReplacesMissingReset()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        var meters = new[]
        {
            new Meter("session", "session", "Session", "", 10, resetsAt: Now.AddMinutes(-1)),
            new Meter("weekly", "weekly", "Weekly", "", 10, lockedReason: "Locked until billing resumes"),
        };

        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true, meters: meters), Now);

        Assert.Equal("Resetting now", vm.Meters[0].ResetText);
        Assert.Equal("Locked until billing resumes", vm.Meters[1].ResetText);
    }

    [Fact]
    public void TabTitle_ShowsLeadPercentage_AndFallsBackToName()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        Assert.Equal("Claude", vm.TabTitle);

        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true,
            meters: [new Meter("session", "session", "Session", "", 12)]), Now);

        Assert.Equal("Claude 12%", vm.TabTitle);
    }

    [Fact]
    public void NotConfigured_ShowsSetupCardAndAccountLabel()
    {
        var vm = new ProviderTabViewModel("gemini", "Gemini");
        var detection = new DetectionInfo("gemini", DetectionState.NotConnected, "", "");

        vm.UpdateFromSnapshot(new ProviderSnapshot("gemini", configured: false, detection: detection), Now);

        Assert.True(vm.ShowSetupCard);
        Assert.Equal("Gemini is not set up yet", vm.SetupTitle);
        Assert.Contains("Cloud Monitoring", vm.SetupHint);
        Assert.Equal("Gemini is not configured yet", vm.AccountLabel);
    }

    [Fact]
    public void Error_SetsHasError_AndStatsBecomeTiles()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        var snapshot = new ProviderSnapshot("claude", configured: true,
            stats: [new Stat("Tokens in range", "2.3B", "2.3B all time")],
            error: "service failed",
            meters: [new Meter("session", "session", "Session", "", 1)]);

        vm.UpdateFromSnapshot(snapshot, Now);

        Assert.True(vm.HasError);
        Assert.Equal("service failed", vm.ErrorMessage);
        var tile = Assert.Single(vm.StatTiles);
        Assert.Equal(new StatTile("Tokens in range", "2.3B", "2.3B all time"), tile);
    }

    [Fact]
    public async Task ChangingMetric_SavesSetting_AndSyncsFromExternalChange()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);

        vm.SelectedRange = DashboardOptions.Ranges[2];
        await session.FlushAsync();
        Assert.Equal(30, session.Current.ChartRangeDays);

        await session.UpdateAsync(s => s with { ChartMetric = "Output tokens", RefreshIntervalSeconds = 0 });
        Assert.Equal("Output tokens", vm.SelectedMetric.Value);
        Assert.Equal("Manual only", vm.SelectedInterval.Label);
    }

    [Fact]
    public async Task SelectingTab_PersistsActiveProvider_AndInitialTabIsRestored()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings { ActiveProvider = "gemini" });
        store.Release();
        using var session = new SettingsSession(store, new AppSettings { ActiveProvider = "gemini" });
        var vm = new MainWindowViewModel(coordinator, session);

        Assert.Equal("gemini", vm.SelectedTab.ProviderId);

        vm.SelectedTab = vm.OpenAiTab;
        await session.FlushAsync();

        Assert.Equal("openai", session.Current.ActiveProvider);
    }

    [Fact]
    public void StatusBar_BeforeFirstFetch_SaysSo()
    {
        var text = StatusBarText.Compose(null, Now, 180, null, TimeZoneInfo.Utc);

        Assert.Equal("No successful fetch yet  ·  auto-refresh every 3 minutes · default", text);
    }

    [Fact]
    public void StatusBar_ShowsAgeClockIntervalAndMemory()
    {
        var text = StatusBarText.Compose(Now.AddMinutes(-90), Now, 180, 70.4, TimeZoneInfo.Utc);

        Assert.Equal("Updated 1h 30m ago (07:30:00)  ·  auto-refresh every 3 minutes · default  ·  70 MB resident", text);
    }

    [Fact]
    public void StatusBar_RecentFetchIsJustNow_AndManualOnlyIsNamed()
    {
        var text = StatusBarText.Compose(Now.AddSeconds(-2), Now, 0, null, TimeZoneInfo.Utc);

        Assert.Equal("Updated just now (08:59:58)  ·  manual refresh", text);
    }

    [Fact]
    public void StatusBar_UnlistedInterval_IsSpelledOutInSeconds()
    {
        Assert.Contains("auto-refresh every 45 seconds", StatusBarText.Compose(Now, Now, 45, null, TimeZoneInfo.Utc));
    }
}
