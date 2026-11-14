using AIMonitor.Application.Providers;
using AIMonitor.Application.Settings;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Tray;
using AIMonitor.Presentation.Wpf.ViewModels;
using AIMonitor.TestSupport;

namespace AIMonitor.Presentation.Wpf.Tests;

/// <summary>Tray rows, tooltip and reading selection per 1.3.3 <c>tray.py</c>.</summary>
public sealed class TrayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static ProviderTabViewModel Tab(string id, string name, params Meter[] meters)
    {
        var tab = new ProviderTabViewModel(id, name);
        tab.UpdateFromSnapshot(new ProviderSnapshot(id, configured: true, meters: meters, fetchedAt: Now), Now);
        return tab;
    }

    private static Meter Session(double percent, int resetsInMinutes = 125) =>
        new("session", "session", "Session", "5-hour window", percent, resetsAt: Now.AddMinutes(resetsInMinutes));

    private static Meter Weekly(double percent) =>
        new("weekly_all", "weekly_all", "Weekly", "7-day window", percent, resetsAt: Now.AddDays(3));

    [Fact]
    public void Icon_UsesTheFiveHourWindow_NotTheFullestOne()
    {
        var tab = Tab("claude", "Claude", Weekly(80), Session(12));
        var readings = TrayReadings.Build([tab], new Dictionary<string, IReadOnlyList<MeterDisplayItem>>());

        var reading = Assert.Single(readings);
        Assert.True(reading.HasData);
        Assert.Equal(12, reading.Percentage);
        Assert.Equal("Session", reading.IconMeter!.Title);
        Assert.Equal(2, reading.Windows.Count); // the menu still lists both windows
    }

    [Fact]
    public void ServiceWithoutFiveHourWindow_IsListedButNotDrawn()
    {
        var tab = Tab("gemini", "Gemini", Weekly(30));
        var reading = Assert.Single(TrayReadings.Build([tab], new Dictionary<string, IReadOnlyList<MeterDisplayItem>>()));

        Assert.False(reading.HasData);
        Assert.Null(reading.IconMeter);
        Assert.Single(reading.Windows);
    }

    [Fact]
    public void FailedRefresh_KeepsPreviousWindows_MarkedStale()
    {
        var cache = new Dictionary<string, IReadOnlyList<MeterDisplayItem>>();
        var tab = Tab("claude", "Claude", Session(40));
        Assert.False(Assert.Single(TrayReadings.Build([tab], cache)).IsStale);

        tab.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true, error: "rate limited"), Now);
        var reading = Assert.Single(TrayReadings.Build([tab], cache));

        Assert.True(reading.IsStale);
        Assert.Equal(40, reading.Percentage);
        Assert.Equal("Claude  (last refresh failed)", TrayText.Header(reading));
    }

    [Fact]
    public void QuotaLine_ShowsNameValueAndLiveCountdown()
    {
        var item = Tab("claude", "Claude", Session(12)).Meters[0];
        var line = TrayText.QuotaLine(item, Now, TimeZoneInfo.Utc);
        Assert.StartsWith("Session · 5-hour window — 12% · resets in 2h 5m (", line);

        var later = TrayText.QuotaLine(item, Now.AddMinutes(60), TimeZoneInfo.Utc);
        Assert.Contains("resets in 1h 5m", later); // recomputed each time the menu opens
    }

    [Fact]
    public void QuotaLine_FallsBackToLockedReasonOrNoReset()
    {
        var locked = Tab("claude", "Claude", new Meter("weekly_all", "w", "Weekly", "", 10, lockedReason: "Locked until upgrade")).Meters[0];
        Assert.EndsWith("Locked until upgrade", TrayText.QuotaLine(locked, Now, TimeZoneInfo.Utc));

        var none = Tab("claude", "Claude", new Meter("weekly_all", "w", "Weekly", "", 10)).Meters[0];
        Assert.Equal("Weekly — 10% · no reset scheduled", TrayText.QuotaLine(none, Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Tooltip_NamesServiceWindowSeverityReset_AndPosition()
    {
        var tab = Tab("claude", "Claude", Session(92));
        var reading = Assert.Single(TrayReadings.Build([tab], new Dictionary<string, IReadOnlyList<MeterDisplayItem>>()));

        var text = TrayText.Tooltip(reading, 0, 2, Now, TimeZoneInfo.Utc);

        Assert.StartsWith("Claude — Session (5-hour window)\n92% · ", text);
        Assert.Contains("resets in 2h 5m", text);
        Assert.EndsWith("1 of 2 services", text);
        Assert.True(text.Length <= TrayText.MaxTooltipLength);
    }

    [Fact]
    public void Tooltip_NeverExceedsTheShellLimit()
    {
        var lines = Enumerable.Repeat(new string('x', 60), 4).ToList();
        Assert.True(TrayText.Fit(lines).Length <= TrayText.MaxTooltipLength);
        Assert.True(TrayText.Fit([new string('y', 400)]).Length <= TrayText.MaxTooltipLength);
    }

    [Fact]
    public void NoData_SaysSo()
    {
        var empty = new TrayReading("claude", "Claude", "CL", 0, Severity.Normal, "");
        Assert.Equal("AI Usage Monitor — no quota data yet", TrayText.Tooltip(empty, 0, 1, Now, TimeZoneInfo.Utc));
    }

    private sealed class BalloonSink : ITrayReadingsSink, ITrayNotifier
    {
        public List<(string Title, string Message)> Balloons { get; } = [];

        public void UpdateReadings(IEnumerable<TrayReading> readings)
        {
        }

        public void ShowBalloon(string title, string message) => Balloons.Add((title, message));
    }

    [Fact]
    public async Task ParkedInTray_ShowsTheStillWatchingBalloon_OnlyTheFirstTime()
    {
        await using var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase([]));
        var store = new BlockingSettingsStore(new AppSettings());
        store.Release();
        using var session = new SettingsSession(store, new AppSettings());
        var vm = new MainWindowViewModel(coordinator, session);
        var sink = new BalloonSink();
        vm.AttachTray(sink);

        vm.NotifyParkedInTray();
        await session.FlushAsync();
        vm.NotifyParkedInTray();

        var balloon = Assert.Single(sink.Balloons);
        Assert.Equal("Still watching", balloon.Title);
        Assert.True(session.Current.TrayHintShown);
    }
}
