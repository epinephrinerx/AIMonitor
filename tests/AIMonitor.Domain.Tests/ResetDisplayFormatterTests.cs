using System.Globalization;
using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class ResetDisplayFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Format_WhenResetIsAbsent_ReturnsNull()
    {
        Assert.Null(ResetDisplayFormatter.Format(null, Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void FormatCountdown_Elapsed_IsNow()
    {
        Assert.Equal("now", ResetDisplayFormatter.FormatCountdown(TimeSpan.Zero));
        Assert.Equal("now", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void FormatCountdown_SubSecondElapsed_IsNow()
    {
        Assert.Equal("now", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromMilliseconds(-1)));
        Assert.Equal("now", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void FormatCountdown_UnderSixtySeconds_IsUnderAMinute()
    {
        Assert.Equal("under a minute", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromSeconds(1)));
        Assert.Equal("under a minute", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromSeconds(59)));
    }

    [Fact]
    public void FormatCountdown_MinutesOnly()
    {
        Assert.Equal("1m", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromSeconds(60)));
        Assert.Equal("4m", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromMinutes(4)));
        Assert.Equal("59m", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromMinutes(59)));
    }

    [Fact]
    public void FormatCountdown_HoursWithAndWithoutMinutes()
    {
        Assert.Equal("1h", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromHours(1)));
        Assert.Equal("1h 12m", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromMinutes(72)));
        Assert.Equal("23h 59m", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59)));
    }

    [Fact]
    public void FormatCountdown_DaysWithAndWithoutHours()
    {
        Assert.Equal("2d", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromDays(2)));
        Assert.Equal("2d 8h", ResetDisplayFormatter.FormatCountdown(TimeSpan.FromHours(56)));
    }

    [Fact]
    public void Format_ExpiredReset_StillProducesADisplay()
    {
        var resetsAt = Now - TimeSpan.FromMinutes(5);

        var display = ResetDisplayFormatter.Format(resetsAt, Now, TimeZoneInfo.Utc);

        Assert.NotNull(display);
        Assert.Equal("now", display!.Countdown);
    }

    [Fact]
    public void Format_LocalTime_UsesShortFormatForToday()
    {
        var resetsAt = Now + TimeSpan.FromHours(2);

        var display = ResetDisplayFormatter.Format(resetsAt, Now, TimeZoneInfo.Utc);

        Assert.Equal("12:00", display!.LocalTime);
    }

    [Fact]
    public void Format_LocalTime_UsesLongFormatForAnotherDay()
    {
        var resetsAt = Now + TimeSpan.FromDays(3);

        var display = ResetDisplayFormatter.Format(resetsAt, Now, TimeZoneInfo.Utc);

        Assert.Equal("Sep 21, 10:00", display!.LocalTime);
    }

    [Fact]
    public void Format_UsesTheInjectedTimeZoneNotTheMachineZone()
    {
        var bangkok = TimeZoneInfo.CreateCustomTimeZone("Bangkok-Test", TimeSpan.FromHours(7), "Bangkok", "Bangkok");
        var resetsAt = new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero);

        var display = ResetDisplayFormatter.Format(resetsAt, Now, bangkok);

        Assert.Equal("8h", display!.Countdown);
        Assert.Equal("Sep 19, 01:00", display.LocalTime);
    }

    [Fact]
    public void Format_AcrossADstTransition_UsesTheOffsetAtTheResetMoment()
    {
        // A synthetic zone rather than a system-database id, so the test is deterministic and
        // portable regardless of which OS/tzdata version runs it.
        var transitionToDaylight = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 1);
        var transitionToStandard = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            transitionToDaylight,
            transitionToStandard);
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Dst-Test", TimeSpan.Zero, "Dst Test", "Standard", "Daylight", [rule]);

        var beforeTransition = new DateTimeOffset(2026, 2, 28, 10, 0, 0, TimeSpan.Zero);
        var afterTransition = beforeTransition + TimeSpan.FromDays(3);

        var display = ResetDisplayFormatter.Format(afterTransition, beforeTransition, zone);

        Assert.Equal("3d", display!.Countdown);
        Assert.Equal("Mar 3, 11:00", display.LocalTime);
    }

    [Fact]
    public void Format_IsIndependentOfCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var resetsAt = Now + TimeSpan.FromDays(3);

            var display = ResetDisplayFormatter.Format(resetsAt, Now, TimeZoneInfo.Utc);

            Assert.Equal("Sep 21, 10:00", display!.LocalTime);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
