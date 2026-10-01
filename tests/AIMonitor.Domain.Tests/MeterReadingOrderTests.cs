using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class MeterReadingOrderTests
{
    private static Meter Session(double percent) => new("session", "session", "Session", "5-hour window", percent);

    private static Meter WeeklyAll(double percent) =>
        new("weekly_all", "weekly_all", "Weekly", "All models", percent);

    private static Meter Scoped(string modelName, double percent) =>
        new("weekly_scoped", $"weekly_scoped_{modelName.ToLowerInvariant()}", "Weekly", $"{modelName} only", percent);

    private static IReadOnlyList<(string Title, string Subtitle)> Order(params Meter[] meters) =>
        MeterReadingOrder.Sort(meters).Select(m => (m.Title, m.Subtitle)).ToArray();

    [Fact]
    public void FableAheadOfAllModelsOnUsage_DoesNotMoveItUpTheRow()
    {
        var result = Order(Scoped("Fable", 10.0), WeeklyAll(7.0), Session(56.0));

        Assert.Equal(
            new[]
            {
                ("Session", "5-hour window"),
                ("Weekly", "All models"),
                ("Weekly", "Fable only"),
            },
            result);
    }

    [Theory]
    [InlineData(56.0, 24.0, 5.0)]
    [InlineData(56.0, 7.0, 10.0)]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(1.0, 99.0, 50.0)]
    [InlineData(99.0, 1.0, 100.0)]
    public void OrderDoesNotDependOnThePercentages(double session, double weekly, double fable)
    {
        var expected = new[]
        {
            ("Session", "5-hour window"),
            ("Weekly", "All models"),
            ("Weekly", "Fable only"),
        };

        var result = Order(Scoped("Fable", fable), WeeklyAll(weekly), Session(session));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void OrderDoesNotDependOnInputOrder()
    {
        var expected = new[]
        {
            ("Session", "5-hour window"),
            ("Weekly", "All models"),
            ("Weekly", "Opus only"),
        };

        var first = Order(Session(5.0), WeeklyAll(5.0), Scoped("Opus", 5.0));
        var second = Order(Scoped("Opus", 5.0), Session(5.0), WeeklyAll(5.0));

        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
    }

    [Fact]
    public void SessionWindowIsAlwaysFirst()
    {
        var sorted = MeterReadingOrder.Sort([WeeklyAll(90.0), Session(1.0)]);

        Assert.Equal("session", sorted[0].Kind);
    }

    [Fact]
    public void PerModelWindows_KeepFixedOrderAmongThemselves()
    {
        var result = Order(Scoped("Sonnet", 30.0), Scoped("Fable", 20.0), Scoped("Opus", 10.0), WeeklyAll(40.0));

        Assert.Equal(
            new[]
            {
                ("Weekly", "All models"),
                ("Weekly", "Fable only"),
                ("Weekly", "Opus only"),
                ("Weekly", "Sonnet only"),
            },
            result);
    }

    [Fact]
    public void UnknownKind_SortsLastRatherThanDisplacingAKnownOne()
    {
        var unknown = new Meter("some_new_window", "some_new_window", "Some new window", "Some new window", 99.0);
        var result = MeterReadingOrder.Sort([unknown, WeeklyAll(2.0), Session(1.0)]);

        Assert.Equal("session", result[0].Kind);
        Assert.Equal("weekly_all", result[1].Kind);
        Assert.Equal("some_new_window", result[2].Kind);
    }

    [Fact]
    public void NothingIsDropped()
    {
        var result = MeterReadingOrder.Sort(
            [Session(1.0), WeeklyAll(2.0), Scoped("Fable", 3.0), new Meter("weekly_oauth_apps", "weekly_oauth_apps", "Weekly", "OAuth apps", 4.0)]);

        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void GroupSetToSession_PromotesAWindowEvenWhenItsKindIsNotLiterallySession()
    {
        var sessionLinked = new Meter(
            "weekly_scoped", "weekly_scoped_session_linked", "Weekly", "Session-linked", 5.0, group: "session");
        var result = MeterReadingOrder.Sort([WeeklyAll(90.0), sessionLinked]);

        Assert.Equal("Session-linked", result[0].Subtitle);
    }

    [Fact]
    public void SameKindAndSubtitle_BreaksTiesOnKey_IndependentOfInputOrder()
    {
        var first = new Meter("weekly_scoped", "weekly_scoped_a", "Weekly", "Fable only", 5.0);
        var second = new Meter("weekly_scoped", "weekly_scoped_b", "Weekly", "Fable only", 5.0);

        var forward = MeterReadingOrder.Sort([first, second]).Select(m => m.Key).ToArray();
        var reversed = MeterReadingOrder.Sort([second, first]).Select(m => m.Key).ToArray();

        Assert.Equal(new[] { "weekly_scoped_a", "weekly_scoped_b" }, forward);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void FullMeterSet_ReversedInput_ProducesTheSameOrder()
    {
        var meters = new[]
        {
            Session(56.0),
            WeeklyAll(7.0),
            Scoped("Fable", 10.0),
            Scoped("Opus", 20.0),
            new Meter("weekly_oauth_apps", "weekly_oauth_apps", "Weekly", "OAuth apps", 4.0),
        };

        var forward = MeterReadingOrder.Sort(meters).Select(m => m.Key).ToArray();
        var reversed = MeterReadingOrder.Sort(meters.Reverse()).Select(m => m.Key).ToArray();

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void DuplicateMeterKey_SameKindAndSubtitle_ThrowsArgumentException()
    {
        var first = new Meter("weekly_scoped", "weekly_scoped_dup", "Weekly", "Fable only", 5.0);
        var duplicate = new Meter("weekly_scoped", "weekly_scoped_dup", "Weekly", "Fable only", 9.0);

        Assert.Throws<ArgumentException>(() => MeterReadingOrder.Sort([first, duplicate]));
    }
}
