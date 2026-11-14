using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

/// <summary>When the "not set up yet" card belongs on screen, and when it is in the way.</summary>
public sealed class ProviderViewModelTests
{
    [Fact]
    public void BeforeAnyRefresh_TheCardIsShown_BecauseThereIsNothingElseToShow()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        Assert.True(provider.ShowSetupHint);
        Assert.Equal("Claude is not set up yet", provider.SetupTitle);
    }

    [Fact]
    public void OnceAReadingArrives_TheCardGoesAway()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        provider.Apply(TestData.Snapshot("claude"));

        // Leaving it up would tell somebody who is signed in to go and sign in.
        Assert.False(provider.ShowSetupHint);
    }

    [Fact]
    public void AServiceThatIsSignedInButReportingNothingYet_StillHidesTheCard()
    {
        var provider = new ProviderViewModel("openai", "OpenAI / Codex");

        provider.Apply(TestData.Snapshot("openai", configured: true, meters: []));

        Assert.False(provider.ShowSetupHint);
    }

    [Fact]
    public void AServiceThatIsNotSignedInAndHasNoReadings_ShowsTheCard()
    {
        var provider = new ProviderViewModel("openai", "OpenAI / Codex");

        provider.Apply(TestData.Snapshot("openai", configured: false, meters: [], error: "Not signed in"));

        Assert.True(provider.ShowSetupHint);
    }

    [Fact]
    public void AFailedRefreshThatLeavesNothingOnScreen_ShowsTheCardAgain()
    {
        var provider = new ProviderViewModel("claude", "Claude");
        provider.Apply(TestData.Snapshot("claude"));

        provider.Apply(TestData.Snapshot("claude", configured: false, meters: [], error: "network unreachable"));

        // Matches the Windows rule exactly: nothing configured and nothing to read means the card
        // is the only thing left to say. The error banner says what went wrong above it.
        Assert.True(provider.ShowSetupHint);
        Assert.True(provider.HasError);
    }

    [Fact]
    public void TheTabCarriesTheLeadReading()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        provider.Apply(TestData.Snapshot("claude", meters: [TestData.Meter(percent: 58)]));

        Assert.Equal("Claude 58%", provider.TabTitle);
    }

    [Fact]
    public void TheTabIsJustTheNameWhenNoWindowReportsAPercentage()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        provider.Apply(TestData.Snapshot("claude", meters: [TestData.Meter(percent: null)]));

        Assert.Equal("Claude", provider.TabTitle);
    }

    [Fact]
    public void WindowsAreGroupedIntoTheSectionsTheServiceNamed()
    {
        var provider = new ProviderViewModel("agy", "Gemini / AGY");

        provider.Apply(TestData.Snapshot("agy", meters:
        [
            TestData.Meter(key: "g1", title: "Gemini · Pro", section: "Gemini"),
            TestData.Meter(key: "g2", title: "Gemini · Flash", section: "Gemini"),
            TestData.Meter(key: "c1", title: "Claude / GPT · All", section: "Claude / GPT"),
        ]));

        Assert.Equal(["Gemini", "Claude / GPT"], provider.MeterSections.Select(s => s.Heading));
        // One column count for every row, so a two-card row and a one-card row still line up.
        Assert.All(provider.MeterSections, section => Assert.Equal(2, section.Columns));
    }

    [Fact]
    public void AServiceWithNoSectionsYieldsOneUnheadedRow()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        provider.Apply(TestData.Snapshot("claude"));

        var section = Assert.Single(provider.MeterSections);
        Assert.False(section.HasHeading);
    }

    [Fact]
    public void ACardsOwnLabelDropsTheSectionPrefixTheHeadingAlreadyCarries()
    {
        var provider = new ProviderViewModel("agy", "Gemini / AGY");

        provider.Apply(TestData.Snapshot("agy", meters:
            [TestData.Meter(key: "g1", title: "Gemini · Pro", section: "Gemini")]));

        Assert.Equal("Gemini · Pro", provider.Meters[0].Title);
        Assert.Equal("Pro", provider.Meters[0].GaugeTitle);
    }

    [Fact]
    public void AHistoryFailureIsKeptSeparateFromTheServiceFailing()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        provider.Apply(TestData.Snapshot("claude", historyError: "could not read the transcripts"));

        // A history problem must never look like the live quota failed too.
        Assert.False(provider.HasError);
        Assert.True(provider.HasHistoryNote);
        Assert.StartsWith("⚠ ", provider.HistoryNote, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryFillsTheChartsAndSaysHowManyDaysItCovers()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        provider.Apply(TestData.Snapshot("claude", history: TestData.History()));

        Assert.True(provider.HasHistory);
        Assert.Equal("Usage per day · last 14 days", provider.ChartTitle);
        Assert.Equal("By model · last 14 days", provider.ModelTitle);
        Assert.Equal("By project · last 14 days", provider.ProjectTitle);
    }

    [Fact]
    public void TheDailyChartIsOldestFirst_SoTimeRunsLeftToRight()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        provider.Apply(TestData.Snapshot("claude", history: TestData.History()));

        var days = provider.ChartBuckets.Select(bucket => bucket.Day).ToList();
        Assert.Equal(days.OrderBy(day => day), days);
    }

    [Fact]
    public void OnlyTheDaysTheRangeCoversAreCharted()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        provider.Apply(TestData.Snapshot("claude", history: TestData.History(buckets: 30, days: 7)));

        Assert.Equal(7, provider.ChartBuckets.Count);
    }

    [Fact]
    public void WithoutHistory_TheChartsAreNotShownAtAll()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        provider.Apply(TestData.Snapshot("claude"));

        Assert.False(provider.HasHistory);
        Assert.Empty(provider.ChartBuckets);
        Assert.False(provider.HasModelRows);
        Assert.False(provider.HasProjectRows);
    }

    [Fact]
    public void AServiceThatUsesItsOwnWordForAProjectKeepsIt()
    {
        var provider = new ProviderViewModel("openai", "OpenAI / Codex");

        provider.Apply(TestData.Snapshot("openai", history: TestData.History(projectLabel: "By workspace")));

        Assert.Equal("By workspace · last 14 days", provider.ProjectTitle);
    }

    [Fact]
    public void HistoryThatArrivesEmptyIsTreatedAsNoHistory_NotAsAnEmptyChart()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        provider.Apply(TestData.Snapshot("claude", history: TestData.History(buckets: 0)));

        Assert.False(provider.HasHistory);
    }

    [Fact]
    public void AnAccountIsShownWhenThereIsOne()
    {
        var provider = new ProviderViewModel("claude", "Claude");

        provider.Apply(TestData.Snapshot("claude", account: "someone@example.com"));

        Assert.Equal("someone@example.com", provider.Account);
    }

    [Fact]
    public void WithNoAccountAndNothingSetUp_TheFooterSaysSo()
    {
        var provider = new ProviderViewModel("openai", "OpenAI / Codex");

        provider.Apply(TestData.Snapshot("openai", configured: false, meters: []));

        Assert.Equal("OpenAI / Codex is not configured yet", provider.Account);
    }

    [Fact]
    public void ASkippedRefreshKeepsWhatWasOnScreen()
    {
        var provider = new ProviderViewModel("openai", "OpenAI / Codex");
        provider.Apply(TestData.Snapshot("openai"));

        provider.Apply(null);

        Assert.Single(provider.Meters);
        Assert.False(provider.ShowSetupHint);
        Assert.False(provider.HasError);
    }

    [Fact]
    public void AServiceReplacesItsWindowsOnEachRefresh_RatherThanAccumulatingThem()
    {
        var provider = new ProviderViewModel("claude", "Claude");
        provider.Apply(TestData.Snapshot("claude", meters:
            [TestData.Meter(key: "session"), TestData.Meter(key: "weekly", kind: "weekly", title: "Weekly")]));

        provider.Apply(TestData.Snapshot("claude", meters: [TestData.Meter(key: "session")]));

        // A window the service stopped reporting must disappear, not linger with a stale reading.
        Assert.Single(provider.Meters);
    }
}
