using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

public sealed class WidgetViewModelTests
{
    private static WidgetViewModel Widget(params string[] providerIds)
    {
        var providers = providerIds.Length == 0
            ? [TestData.Provider("claude", "Claude"), TestData.Provider("openai", "OpenAI / Codex"), TestData.Provider("agy", "Gemini / AGY")]
            : providerIds.Select(id => TestData.Provider(id, id)).ToList();
        return new WidgetViewModel(providers);
    }

    [Fact]
    public void WithNoServices_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new WidgetViewModel([]));
    }

    [Fact]
    public void StartsOnTheFirstService()
    {
        Assert.Equal("claude", Widget().Current.ProviderId);
    }

    [Fact]
    public void Next_WrapsAroundRatherThanStopping()
    {
        var widget = Widget();

        widget.NextCommand.Execute(null);
        widget.NextCommand.Execute(null);
        widget.NextCommand.Execute(null);

        Assert.Equal("claude", widget.Current.ProviderId);
    }

    [Fact]
    public void Previous_FromTheFirstService_GoesToTheLast()
    {
        var widget = Widget();

        widget.PreviousCommand.Execute(null);

        Assert.Equal("agy", widget.Current.ProviderId);
    }

    [Fact]
    public void WithOneService_ThereIsNowhereToPage()
    {
        var widget = Widget("claude");

        Assert.False(widget.CanPage);
        widget.NextCommand.Execute(null);
        Assert.Equal("claude", widget.Current.ProviderId);
    }

    [Fact]
    public void WithOneService_ItNeverRotates_BecauseThereIsNothingToRotateTo()
    {
        var widget = Widget("claude");
        widget.ApplySettings(new AppSettings { WidgetRotationEnabled = true });

        Assert.False(widget.IsRotating);
    }

    [Fact]
    public void Advance_MovesOnWhileRotating()
    {
        var widget = Widget();
        widget.ApplySettings(new AppSettings { WidgetRotationEnabled = true });

        widget.Advance();

        Assert.Equal("openai", widget.Current.ProviderId);
    }

    [Fact]
    public void Advance_DoesNothingWhenRotationIsSwitchedOff()
    {
        var widget = Widget();
        widget.ApplySettings(new AppSettings { WidgetRotationEnabled = false });

        widget.Advance();

        Assert.Equal("claude", widget.Current.ProviderId);
    }

    [Fact]
    public void Advance_DoesNothingWhileAServiceIsPinned()
    {
        var widget = Widget();
        widget.ApplySettings(new AppSettings { WidgetRotationEnabled = true });
        widget.IsPinned = true;

        widget.Advance();

        // The user asked to look at this one; having it slide away would undo the request.
        Assert.Equal("claude", widget.Current.ProviderId);
    }

    [Fact]
    public void PagingByHandWhilePinned_PinsWhereItLands()
    {
        var widget = Widget();
        widget.ApplySettings(new AppSettings { WidgetRotationEnabled = true });
        widget.IsPinned = true;

        widget.NextCommand.Execute(null);

        Assert.Equal("openai", widget.PinnedProviderId);
        Assert.True(widget.IsPinned);
    }

    [Fact]
    public void Unpinning_LetsTheRotationResume()
    {
        var widget = Widget();
        widget.ApplySettings(new AppSettings { WidgetRotationEnabled = true });
        widget.IsPinned = true;
        widget.IsPinned = false;

        Assert.True(widget.IsRotating);
        widget.Advance();
        Assert.Equal("openai", widget.Current.ProviderId);
    }

    [Fact]
    public void AShowMenuChoice_PinsThatService_AndStopsTheTurns()
    {
        var widget = Widget();

        widget.ShowProvider("agy");

        Assert.Equal("agy", widget.Current.ProviderId);
        Assert.False(widget.IsRotatingAll);
        Assert.False(widget.IsRotating);
    }

    [Fact]
    public void AllServices_StartsTheTurnsAgain()
    {
        var widget = Widget();
        widget.ShowProvider("agy");

        widget.ShowAllProviders();

        Assert.True(widget.IsRotatingAll);
        Assert.True(widget.IsRotating);
        Assert.Null(widget.PinnedProviderId);
    }

    [Fact]
    public void WithRotationOff_AndNothingPinned_ItFollowsTheDashboardsTab()
    {
        var active = "openai";
        var widget = Widget();
        widget.ActiveProviderSource = () => active;
        widget.ApplySettings(new AppSettings { WidgetRotationEnabled = false });

        Assert.Equal("openai", widget.Current.ProviderId);
        active = "agy";
        widget.OnActiveProviderChanged();
        Assert.Equal("agy", widget.Current.ProviderId);
    }

    [Fact]
    public void TheTurns_SkipServicesThatAreNotConfigured()
    {
        var widget = new WidgetViewModel(
        [
            TestData.Provider("claude", "Claude"),
            TestData.Provider("openai", "OpenAI", TestData.Snapshot("openai", configured: false, meters: [])),
            TestData.Provider("agy", "AGY", TestData.Snapshot("agy")),
        ]);

        widget.Advance();
        Assert.Equal("agy", widget.Current.ProviderId);
        widget.Advance();
        Assert.Equal("claude", widget.Current.ProviderId);
    }

    [Fact]
    public void WithOnlyOneConfiguredService_ThereIsNothingToPageTo()
    {
        var widget = new WidgetViewModel(
        [
            TestData.Provider("claude", "Claude"),
            TestData.Provider("openai", "OpenAI", TestData.Snapshot("openai", configured: false, meters: [])),
        ]);

        Assert.False(widget.CanPage);
        Assert.False(widget.IsRotating);
    }

    [Fact]
    public void TheTurnsTakeFourSeconds_AsOnWindows()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), WidgetViewModel.RotationInterval);
    }

    [Fact]
    public void PagingByHand_AsksTheWindowToRestartTheInterval()
    {
        var widget = Widget();
        var restarts = 0;
        widget.RotationRestarted += () => restarts++;

        widget.NextCommand.Execute(null);

        Assert.Equal(1, restarts);
    }

    [Fact]
    public void ApplySettings_TakesOpacityAndAlwaysOnTopFromTheUsersChoice()
    {
        var widget = Widget();

        widget.ApplySettings(new AppSettings { WidgetOpacity = 0.5, WidgetAlwaysOnTop = false });

        Assert.Equal(0.5, widget.Opacity);
        Assert.False(widget.AlwaysOnTop);
    }

    [Theory]
    [InlineData(0, 0.25)]
    [InlineData(-1, 0.25)]
    [InlineData(5, 1.0)]
    public void ApplySettings_KeepsOpacityWhereTheWidgetIsStillVisibleAndClickable(double stored, double expected)
    {
        var widget = Widget();

        widget.ApplySettings(new AppSettings { WidgetOpacity = stored });

        Assert.Equal(expected, widget.Opacity);
    }

    [Fact]
    public void TheLeadLine_ComesFromTheFiveHourWindow_AndNamesItsSeverity()
    {
        var provider = TestData.Provider(snapshot: TestData.Snapshot(meters:
        [
            TestData.Meter(key: "weekly", kind: "weekly", title: "Weekly", subtitle: "7-day window", percent: 20),
            TestData.Meter(key: "session", percent: 95),
        ]));
        var widget = new WidgetViewModel([provider]);
        widget.UpdateLayout(300, 300, 16, 13.5);

        Assert.True(widget.ShowLeadHead);
        Assert.Equal("⚠ Session Critical", widget.LeadHeadText);
        Assert.True(widget.IsLeadCritical);
    }

    [Theory]
    [InlineData(300, 5)]
    [InlineData(230, 4)]
    [InlineData(150, 2)]
    public void HowManyGaugesFit_IsDecidedByTheWidth(double width, int expected)
    {
        var meters = Enumerable.Range(0, 6)
            .Select(i => TestData.Meter(key: $"w{i}", kind: "weekly", title: $"Window {i}", subtitle: "", percent: i * 10))
            .ToList();
        var widget = new WidgetViewModel([TestData.Provider(snapshot: TestData.Snapshot(meters: meters))]);

        widget.UpdateLayout(width, 300, 16, 13.5);

        Assert.Equal(expected, widget.VisibleMeters.Count);
    }

    [Fact]
    public void TheHeight_NeverTakesAGaugeAway()
    {
        var meters = Enumerable.Range(0, 3).Select(i => TestData.Meter(key: $"w{i}", percent: 10)).ToList();
        var widget = new WidgetViewModel([TestData.Provider(snapshot: TestData.Snapshot(meters: meters))]);

        widget.UpdateLayout(300, 300, 16, 13.5);
        var tall = widget.VisibleMeters.Count;
        widget.UpdateLayout(300, 140, 16, 13.5);

        Assert.Equal(tall, widget.VisibleMeters.Count);
    }

    [Fact]
    public void TooShortForAGauge_SaysSo()
    {
        var widget = new WidgetViewModel([TestData.Provider()]);

        widget.UpdateLayout(200, 60, 16, 13.5);

        Assert.True(widget.IsTooSmall);
        Assert.False(widget.HasMeters);
    }

    [Fact]
    public void BeforeTheFirstAnswer_ItSaysItIsWaiting()
    {
        var widget = new WidgetViewModel([new ProviderViewModel("claude", "Claude")]);

        Assert.False(widget.HasMeters);
        Assert.Equal("Waiting for data…", widget.EmptyMessage);
    }

    [Fact]
    public void WhenTheServiceReportedAProblem_TheEmptyMessageIsThatProblem()
    {
        var provider = TestData.Provider(snapshot:
            TestData.Snapshot(configured: false, meters: [], error: "network unreachable"));
        var widget = new WidgetViewModel([provider]);

        Assert.Equal("network unreachable", widget.EmptyMessage);
    }

    [Fact]
    public void AServiceThatIsNotSetUp_SaysSo()
    {
        var provider = TestData.Provider(snapshot: TestData.Snapshot(configured: false, meters: []));
        var widget = new WidgetViewModel([provider]);

        Assert.Equal("Claude is not configured yet.", widget.EmptyMessage);
    }

    [Fact]
    public void TheFooter_SaysHowOldTheReadingIs()
    {
        var now = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal("", WidgetViewModel.FormatStatus(null, now));
        Assert.Equal("updated just now", WidgetViewModel.FormatStatus(now.AddSeconds(-3), now));
        Assert.Equal("updated 5m ago", WidgetViewModel.FormatStatus(now.AddMinutes(-5), now));
    }

    [Fact]
    public void NewReadingsForTheServiceOnScreen_ReachTheWidgetWithoutPagingAway()
    {
        var provider = TestData.Provider();
        var widget = new WidgetViewModel([provider]);

        provider.Apply(TestData.Snapshot(meters: [TestData.Meter(percent: 77)]));

        Assert.Equal(77, widget.VisibleMeters[0].Percent);
    }
}
