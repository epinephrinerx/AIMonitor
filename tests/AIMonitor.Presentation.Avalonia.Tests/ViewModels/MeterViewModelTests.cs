using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

/// <summary>What one dial says beside the ring: the severity in words, and when the window resets.</summary>
public sealed class MeterViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 15, 45, 0, TimeSpan.Zero);

    private static MeterViewModel Meter(
        double? percent = 49,
        Severity severity = Severity.Normal,
        DateTimeOffset? resetsAt = null,
        string? lockedReason = null) =>
        new(TestData.Meter(percent: percent, severity: severity, resetsAt: resetsAt) with
        {
        }, Now, TimeZoneInfo.Utc);

    [Theory]
    [InlineData(Severity.Normal)]
    [InlineData(Severity.High)]
    [InlineData(Severity.VeryHigh)]
    [InlineData(Severity.Critical)]
    public void SeverityIsSpelledOutBesideTheDial_NotLeftToColourAlone(Severity severity)
    {
        var described = SeverityLabels.Describe(severity);

        Assert.Equal($"{described.Glyph} {described.Label}", Meter(severity: severity).SeverityText);
    }

    [Fact]
    public void AWindowWithAResetSaysHowLongAndAtWhatTime()
    {
        var text = Meter(resetsAt: Now.AddHours(2).AddMinutes(5)).ResetText;

        Assert.StartsWith("Resets in ", text, StringComparison.Ordinal);
        Assert.Contains(" · ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowWithNoResetSaysSo_RatherThanShowingAnEmptyLine()
    {
        Assert.Equal("No reset scheduled", Meter().ResetText);
    }

    [Fact]
    public void ALockedWindowGivesTheReasonWhereTheResetWouldGo()
    {
        var meter = new MeterViewModel(
            new Meter("weekly", "weekly", "Weekly", "Opus only", percent: null, lockedReason: "Upgrade to unlock"),
            Now,
            TimeZoneInfo.Utc);

        Assert.Equal("Upgrade to unlock", meter.ResetText);
    }

    [Fact]
    public void AWindowWithNoDenominatorHasNoPercentage_SoTheDialDrawsNoneEither()
    {
        var meter = Meter(percent: null);

        Assert.False(meter.HasPercent);
        // The two dashes the Windows build shows for a window with no denominator.
        Assert.Equal("--", meter.ValueText);
        Assert.Equal(0, meter.PercentOrZero);
    }

    [Theory]
    [InlineData(49.0, "49%")]
    [InlineData(49.4, "49%")]
    [InlineData(49.6, "50%")]
    public void ThePercentageIsRoundedTheWayTheDialPrintsIt(double percent, string expected)
    {
        Assert.Equal(expected, Meter(percent: percent).ValueText);
    }

    [Theory]
    [InlineData(0, Severity.Normal)]
    [InlineData(95, Severity.Critical)]
    public void TheSeverityIsTheWorseOfTheLocalThresholdAndWhatTheServiceSaid(double percent, Severity expected)
    {
        // A window sitting at 95% has to read as critical even when the service reports nothing
        // unusual; taking the service's word alone would show it as normal.
        Assert.Equal(expected, Meter(percent: percent, severity: Severity.Normal).Severity);
    }

    [Fact]
    public void TheServicesOwnSeverityWinsWhenItIsTheWorseOfTheTwo()
    {
        Assert.Equal(Severity.Critical, Meter(percent: 1, severity: Severity.Critical).Severity);
    }
}
