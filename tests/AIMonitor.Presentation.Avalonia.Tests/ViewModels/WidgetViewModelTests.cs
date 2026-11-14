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
    public void Show_JumpsToTheNamedService()
    {
        var widget = Widget();

        widget.Show("agy");

        Assert.Equal("agy", widget.Current.ProviderId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nothing-of-ours")]
    public void Show_WithAServiceItDoesNotHave_StaysPut(string? providerId)
    {
        var widget = Widget();

        widget.Show(providerId);

        Assert.Equal("claude", widget.Current.ProviderId);
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
    [InlineData(0, 0.2)]
    [InlineData(-1, 0.2)]
    [InlineData(5, 1.0)]
    public void ApplySettings_KeepsOpacityWhereTheWidgetIsStillVisibleAndClickable(double stored, double expected)
    {
        var widget = Widget();

        widget.ApplySettings(new AppSettings { WidgetOpacity = stored });

        Assert.Equal(expected, widget.Opacity);
    }

    [Fact]
    public void ShowsTheFiveHourWindowFirst_BecauseThatIsTheOnePeopleWatch()
    {
        var provider = TestData.Provider(snapshot: TestData.Snapshot(meters:
        [
            TestData.Meter(key: "weekly", kind: "weekly", title: "Weekly", subtitle: "7-day window", percent: 90),
            TestData.Meter(key: "session", percent: 12),
        ]));
        var widget = new WidgetViewModel([provider]);

        Assert.Equal("Session", widget.VisibleMeters[0].Title);
    }

    [Fact]
    public void ShowsAtMostThreeWindows_SoItStaysGlanceable()
    {
        var meters = Enumerable.Range(0, 6)
            .Select(i => TestData.Meter(key: $"w{i}", kind: "weekly", title: $"Window {i}", subtitle: "", percent: i * 10))
            .ToList();
        var widget = new WidgetViewModel([TestData.Provider(snapshot: TestData.Snapshot(meters: meters))]);

        Assert.Equal(3, widget.VisibleMeters.Count);
    }

    [Fact]
    public void WithNoWindowsAtAll_SaysWhyRatherThanShowingAnEmptyCard()
    {
        var widget = new WidgetViewModel([new ProviderViewModel("claude", "Claude")]);

        Assert.False(widget.HasMeters);
        Assert.Equal("No quota data yet", widget.EmptyMessage);
    }

    [Fact]
    public void WhenTheServiceReportedAProblem_TheEmptyMessageSaysSo()
    {
        var provider = TestData.Provider(snapshot:
            TestData.Snapshot(configured: false, meters: [], error: "network unreachable"));
        var widget = new WidgetViewModel([provider]);

        Assert.Equal("Last refresh failed", widget.EmptyMessage);
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
