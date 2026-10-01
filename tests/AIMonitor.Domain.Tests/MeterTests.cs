using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class MeterTests
{
    private static Meter Meter(double? percent, Severity serverSeverity = Severity.Normal) =>
        new("session", "session", "Session", "5-hour window", percent, serverSeverity);

    [Theory]
    [InlineData(74.9, Severity.Normal)]
    [InlineData(75.0, Severity.High)]
    [InlineData(89.9, Severity.High)]
    [InlineData(90.0, Severity.Critical)]
    public void EffectiveSeverity_AppliesLocalThresholds(double percent, Severity expected)
    {
        Assert.Equal(expected, Meter(percent).EffectiveSeverity);
    }

    [Fact]
    public void EffectiveSeverity_WhenServerSeverityIsWorse_IsNeverMasked()
    {
        Assert.Equal(Severity.Critical, Meter(5.0, Severity.Critical).EffectiveSeverity);
    }

    [Fact]
    public void EffectiveSeverity_WhenPercentIsNull_PreservesServerSeverity()
    {
        Assert.Equal(Severity.High, Meter(null, Severity.High).EffectiveSeverity);
    }

    [Fact]
    public void Percent_CanBeAbsentWithoutInventingAValue()
    {
        var meter = Meter(null);

        Assert.Null(meter.Percent);
    }

    [Fact]
    public void ResetsAt_CanBeAbsent()
    {
        var meter = new Meter("session", "session", "Session", "5-hour window", 10.0);

        Assert.Null(meter.ResetsAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankKind(string kind)
    {
        Assert.Throws<ArgumentException>(() => new Meter(kind, "session", "Session", "5-hour window"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankKey(string key)
    {
        Assert.Throws<ArgumentException>(() => new Meter("session", key, "Session", "5-hour window"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankTitle(string title)
    {
        Assert.Throws<ArgumentException>(() => new Meter("session", "session", title, "5-hour window"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_PermitsEmptyOrBlankSubtitle(string subtitle)
    {
        var meter = new Meter("session", "session", "Session", subtitle);

        Assert.Equal(subtitle, meter.Subtitle);
    }

    [Fact]
    public void Constructor_RejectsNullSubtitle()
    {
        Assert.Throws<ArgumentNullException>(() => new Meter("session", "session", "Session", null!));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.1)]
    public void Constructor_RejectsStructurallyInvalidPercent(double percent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Meter(percent));
    }

    [Fact]
    public void Constructor_RejectsUndefinedServerSeverity()
    {
        var invalid = (Severity)999;

        Assert.Throws<ArgumentOutOfRangeException>(() => Meter(50.0, invalid));
    }

    [Fact]
    public void EffectiveSeverity_CannotBeOutrankedByAnUndefinedServerSeverity()
    {
        // The constructor is the boundary that rejects this, so callers cannot ever observe an
        // EffectiveSeverity computed from an out-of-range enum value.
        Assert.Throws<ArgumentOutOfRangeException>(() => Meter(5.0, (Severity)999));
    }

    [Fact]
    public void Key_IsAnEnforcedIdentity_NotDefaultedFromKind()
    {
        var meter = new Meter("weekly_scoped", "weekly_scoped_fable", "Weekly", "Fable only", 10.0);

        Assert.Equal("weekly_scoped_fable", meter.Key);
    }

    [Fact]
    public void Key_DistinguishesTwoWindowsOfTheSameKind()
    {
        var first = new Meter("weekly_scoped", "weekly_scoped_fable", "Weekly", "Fable only", 10.0);
        var second = new Meter("weekly_scoped", "weekly_scoped_opus", "Weekly", "Opus only", 20.0);

        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal(first.Kind, second.Kind);
    }

    [Fact]
    public void Group_DefaultsToKind_WhenNotSupplied()
    {
        var meter = new Meter("weekly_scoped", "weekly_scoped_fable", "Weekly", "Fable only", 10.0);

        Assert.Equal("weekly_scoped", meter.Group);
    }

    [Fact]
    public void Group_CanBeSetExplicitlyToClassifyAWindowAsSession()
    {
        var meter = new Meter(
            "weekly_scoped", "weekly_scoped_fable", "Weekly", "Session-linked", 10.0, group: "session");

        Assert.Equal("session", meter.Group);
    }
}
