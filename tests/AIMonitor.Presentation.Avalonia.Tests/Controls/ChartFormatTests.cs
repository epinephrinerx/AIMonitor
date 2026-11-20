using AIMonitor.Presentation.Avalonia.Controls;

namespace AIMonitor.Presentation.Avalonia.Tests.Controls;

/// <summary>
/// How the charts write numbers. The Windows build and the 1.3.3 Python baseline both read this
/// way, so a reading looks the same whichever one the user is looking at.
/// </summary>
public sealed class ChartFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1284, "1,284")]
    [InlineData(9999, "9,999")]
    [InlineData(12900, "12.9K")]
    [InlineData(13000, "13K")]
    [InlineData(4_200_000, "4.2M")]
    [InlineData(1_300_000_000, "1.3B")]
    public void CompactNumbersDropToOneDecimalAndKeepTheirUnit(double value, string expected)
    {
        Assert.Equal(expected, ChartFormat.Compact(value));
    }

    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(0.004, "<$0.01")]
    [InlineData(12.5, "$12.50")]
    [InlineData(4200, "$4,200")]
    public void MoneyNeverRoundsASmallAmountAwayToNothing(double value, string expected)
    {
        Assert.Equal(expected, ChartFormat.Money(value));
    }

    [Theory]
    [InlineData(1500, "Total tokens", "1,500")]
    [InlineData(1500, "Equivalent value", "$2K")]
    [InlineData(12, "Equivalent value", "$12")]
    [InlineData(1.5, "Equivalent value", "$1.5")]
    public void AnAxisTickReadsAsTheMetricItMeasures(double value, string metric, string expected)
    {
        Assert.Equal(expected, ChartFormat.AxisTick(value, metric));
    }

    [Fact]
    public void ADayLabelIsTheMonthAndAnUnpaddedDay()
    {
        Assert.Equal("Sep 9", ChartFormat.DayLabel(new DateOnly(2026, 9, 9)));
    }

    [Theory]
    [InlineData(7_100_000, 4, 8_000_000)]
    [InlineData(100, 4, 100)]
    [InlineData(0, 4, 4)]
    public void TheAxisMaximumLandsOnRoundTicks(double peak, int divisions, double expected)
    {
        // A peak of 7.1M over four divisions becomes 0 / 2M / 4M / 6M / 8M, not 0 / 1.9M / 3.8M.
        Assert.Equal(expected, ChartFormat.NiceAxisMax(peak, divisions));
    }

    [Fact]
    public void EveryTickOfANiceAxisIsAWholeStep()
    {
        var max = ChartFormat.NiceAxisMax(7_100_000, 4);
        var step = max / 4;

        Assert.Equal(2_000_000, step);
    }
}
