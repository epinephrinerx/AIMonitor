using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.Controls;

namespace AIMonitor.Presentation.Avalonia.Tests.Controls;

/// <summary>The cost line on the daily chart (R-017), with the rules the Windows chart follows.</summary>
public sealed class ChartCostLineTests
{
    private static readonly DateOnly Day = new(2026, 11, 9);

    [Fact]
    public void NoCostsOrOnlyZeroCosts_LeaveTheChartAsItWas()
    {
        Assert.False(StackedColumnChartControl.ShowsCostAxis(null));
        Assert.False(StackedColumnChartControl.ShowsCostAxis([]));
        Assert.False(StackedColumnChartControl.ShowsCostAxis([new DailyCost(Day, 0, hasUnpricedModels: true)]));
        Assert.Equal(["sonnet"], StackedColumnChartControl.LegendEntries(["sonnet"], [new DailyCost(Day, 0, false)]));
    }

    [Fact]
    public void APricedDay_AddsTheCostLineToTheLegend()
    {
        IReadOnlyList<DailyCost> costs = [new DailyCost(Day, 1.25, false)];

        Assert.True(StackedColumnChartControl.ShowsCostAxis(costs));
        Assert.Equal(["sonnet", "opus", "Cost ($)"], StackedColumnChartControl.LegendEntries(["sonnet", "opus"], costs));
    }

    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(0.0125, "$0.0125")]
    [InlineData(12.5, "$12.50")]
    [InlineData(999.99, "$999.99")]
    public void CostTicks_KeepTwoToFourDecimalsBelowAThousand(double value, string expected)
    {
        Assert.Equal(expected, StackedColumnChartControl.CostTickLabel(value));
    }

    [Fact]
    public void CostTicks_FromAThousand_UseTheCompactFormat()
    {
        Assert.Equal("$" + ChartFormat.Compact(2500), StackedColumnChartControl.CostTickLabel(2500));
    }

    [Fact]
    public void TheTooltipAddsTheDaysCost_MatchedByDay()
    {
        var bucket = new UsageHistoryBucket(Day, new Dictionary<string, double> { ["sonnet"] = 1000 });
        IReadOnlyList<DailyCost> costs = [new DailyCost(Day.AddDays(-1), 9, false), new DailyCost(Day, 2.5, hasUnpricedModels: true)];

        var text = StackedColumnChartControl.TooltipText(bucket, ["sonnet"], "Total tokens", costs);

        Assert.EndsWith("Cost: $2.50 + unpriced", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTooltipWithoutACostForThatDay_HasNoCostLine()
    {
        var bucket = new UsageHistoryBucket(Day, new Dictionary<string, double> { ["sonnet"] = 1000 });

        var text = StackedColumnChartControl.TooltipText(bucket, ["sonnet"], "Total tokens", [new DailyCost(Day.AddDays(1), 3, false)]);

        Assert.DoesNotContain("Cost:", text, StringComparison.Ordinal);
    }
}
