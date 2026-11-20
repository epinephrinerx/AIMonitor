using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.Controls;

namespace AIMonitor.Presentation.Avalonia.Tests.Controls;

/// <summary>
/// The rules the charts apply before anything is drawn: how many rows survive, how day labels are
/// thinned, which colour a series gets, and what the pointer says.
/// </summary>
public sealed class ChartControlTests
{
    private static IReadOnlyList<UsageHistoryBreakdown> Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new UsageHistoryBreakdown($"model-{i}", 100 - i))];

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public void UpToEightRowsAreShownAsTheyAre(int count)
    {
        Assert.Equal(count, HorizontalBarChartControl.Fold(Rows(count)).Count);
    }

    [Fact]
    public void BeyondEightRowsTheRemainderIsFoldedIntoOne()
    {
        var folded = HorizontalBarChartControl.Fold(Rows(20));

        Assert.Equal(HorizontalBarChartControl.MaxRows, folded.Count);
        Assert.Equal("Other", folded[^1].Label);
    }

    [Fact]
    public void FoldingKeepsEveryValue_SoTheTotalIsStillTheTotal()
    {
        var rows = Rows(20);

        Assert.Equal(rows.Sum(row => row.Value), HorizontalBarChartControl.Fold(rows).Sum(row => row.Value));
    }

    [Fact]
    public void NoRowsAtAllIsNotAnError()
    {
        Assert.Empty(HorizontalBarChartControl.Fold(null));
    }

    [Theory]
    [InlineData(30.0, 60.0, 1)]
    [InlineData(30.0, 20.0, 3)]
    [InlineData(30.0, 1.0, 44)]
    public void DayLabelsAreThinnedUntilTheyStopColliding(double labelWidth, double bandWidth, int expected)
    {
        Assert.Equal(expected, StackedColumnChartControl.LabelStride(labelWidth, bandWidth));
    }

    [Fact]
    public void EachSeriesKeepsItsOwnColour()
    {
        var palette = ChartPalette.Light;
        string[] series = ["sonnet", "opus", "haiku"];

        Assert.Equal(palette.Series(0), StackedColumnChartControl.ColourFor(palette, series, "sonnet"));
        Assert.Equal(palette.Series(2), StackedColumnChartControl.ColourFor(palette, series, "haiku"));
    }

    [Fact]
    public void TheFoldedRemainderIsDrawnInTheQuietColour_NotAsAnotherModel()
    {
        var palette = ChartPalette.Light;

        Assert.Equal(palette.InkMuted, StackedColumnChartControl.ColourFor(palette, ["sonnet"], "Other"));
    }

    [Fact]
    public void ASeriesNobodyDeclaredFallsBackRatherThanThrowing()
    {
        var palette = ChartPalette.Light;

        Assert.Equal(palette.InkMuted, StackedColumnChartControl.ColourFor(palette, ["sonnet"], "a model nobody listed"));
    }

    [Fact]
    public void ThePointerShowsThePerModelSplitAndTheTotal()
    {
        var bucket = new UsageHistoryBucket(
            new DateOnly(2026, 9, 9),
            new Dictionary<string, double> { ["sonnet"] = 1200, ["opus"] = 800 });

        var lines = StackedColumnChartControl.TooltipText(bucket, ["sonnet", "opus"], "Total tokens").Split('\n');

        Assert.Equal("Sep 9", lines[0]);
        Assert.Equal("sonnet: 1,200", lines[1]);
        Assert.Equal("opus: 800", lines[2]);
        Assert.Equal("Total: 2,000", lines[3]);
    }

    [Fact]
    public void ADayWithNothingOnItSaysSoRatherThanShowingZeroes()
    {
        var bucket = new UsageHistoryBucket(new DateOnly(2026, 9, 9));

        var lines = StackedColumnChartControl.TooltipText(bucket, ["sonnet"], "Total tokens").Split('\n');

        Assert.Equal(["Sep 9", "No usage"], lines);
    }

    [Fact]
    public void AModelWithNothingOnADayIsLeftOutOfThatDaysTooltip()
    {
        var bucket = new UsageHistoryBucket(
            new DateOnly(2026, 9, 9),
            new Dictionary<string, double> { ["sonnet"] = 1200, ["opus"] = 0 });

        var tooltip = StackedColumnChartControl.TooltipText(bucket, ["sonnet", "opus"], "Total tokens");

        Assert.DoesNotContain("opus", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueMetricTooltipsAreWrittenAsMoney()
    {
        var bucket = new UsageHistoryBucket(
            new DateOnly(2026, 9, 9),
            new Dictionary<string, double> { ["sonnet"] = 12.5 });

        var tooltip = StackedColumnChartControl.TooltipText(bucket, ["sonnet"], ChartFormat.ValueMetric);

        Assert.Contains("$12.50", tooltip, StringComparison.Ordinal);
    }
}
