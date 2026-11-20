using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.MenuBar;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.MenuBar;

public sealed class MenuBarReadingsTests
{
    private static Dictionary<string, IReadOnlyList<MeterViewModel>> Cache() => new(StringComparer.Ordinal);

    [Fact]
    public void Build_TakesTheFiveHourWindowAsTheOneTheIconDraws()
    {
        var provider = TestData.Provider(snapshot: TestData.Snapshot(meters:
        [
            TestData.Meter(key: "weekly", kind: "weekly", title: "Weekly", subtitle: "7-day window", percent: 80),
            TestData.Meter(key: "session", percent: 12),
        ]));

        var reading = Assert.Single(MenuBarReadings.Build([provider], Cache()));

        Assert.Equal(12, reading.Percent);
        Assert.Equal(2, reading.Windows.Count);
    }

    [Fact]
    public void Build_WithNoFiveHourWindow_DrawsNothingRatherThanPickingAnArbitraryMeter()
    {
        var provider = TestData.Provider(snapshot: TestData.Snapshot(meters:
            [TestData.Meter(key: "weekly", kind: "weekly", title: "Weekly", subtitle: "7-day window", percent: 80)]));

        var reading = Assert.Single(MenuBarReadings.Build([provider], Cache()));

        Assert.False(reading.HasData);
        Assert.Null(reading.Percent);
        Assert.Single(reading.Windows);
    }

    [Fact]
    public void Build_KeepsTheLastGoodWindowsWhenARefreshComesBackEmpty_AndMarksThemStale()
    {
        var cache = Cache();
        var provider = TestData.Provider();
        _ = MenuBarReadings.Build([provider], cache);

        provider.Apply(TestData.Snapshot(configured: false, meters: [], error: "network unreachable"));
        var reading = Assert.Single(MenuBarReadings.Build([provider], cache));

        // Vanishing from the menu bar would leave the user with no reading at all, which is worse
        // than an openly stale one.
        Assert.True(reading.IsStale);
        Assert.Single(reading.Windows);
        Assert.Equal(12, reading.Percent);
    }

    [Fact]
    public void Build_WithNothingEverRead_IsNotStale_BecauseThereIsNoPreviousReadingToBeStale()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        var reading = Assert.Single(MenuBarReadings.Build([provider], Cache()));

        Assert.False(reading.IsStale);
        Assert.Empty(reading.Windows);
        Assert.False(reading.HasData);
    }

    [Fact]
    public void Build_AGoodRefreshAfterAFailedOne_IsNoLongerStale()
    {
        var cache = Cache();
        var provider = TestData.Provider();
        _ = MenuBarReadings.Build([provider], cache);
        provider.Apply(TestData.Snapshot(configured: false, meters: [], error: "network unreachable"));
        _ = MenuBarReadings.Build([provider], cache);

        provider.Apply(TestData.Snapshot(meters: [TestData.Meter(percent: 34)]));
        var reading = Assert.Single(MenuBarReadings.Build([provider], cache));

        Assert.False(reading.IsStale);
        Assert.Equal(34, reading.Percent);
    }

    [Fact]
    public void Build_KeepsTheServicesInTheOrderTheDashboardShowsThem()
    {
        var readings = MenuBarReadings.Build(
            [TestData.Provider("claude", "Claude"), TestData.Provider("openai", "OpenAI / Codex")],
            Cache());

        Assert.Equal(["claude", "openai"], readings.Select(reading => reading.ProviderId));
        Assert.Equal(["Claude", "OpenAI / Codex"], readings.Select(reading => reading.ProviderName));
    }

    [Fact]
    public void Build_CarriesTheSeverityTheServiceReported()
    {
        var provider = TestData.Provider(snapshot: TestData.Snapshot(meters:
            [TestData.Meter(percent: 95, severity: Severity.Critical)]));

        Assert.Equal(Severity.Critical, Assert.Single(MenuBarReadings.Build([provider], Cache())).Severity);
    }
}
