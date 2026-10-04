using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Controls;
using AIMonitor.Presentation.Wpf.Theme;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.Presentation.Wpf.Tests;

public sealed class ChartTests
{
    [Theory]
    [InlineData(1284, "1,284")]
    [InlineData(12900, "12.9K")]
    [InlineData(4_200_000, "4.2M")]
    [InlineData(3_000_000, "3M")]
    [InlineData(1_300_000_000, "1.3B")]
    public void Compact_MatchesLegacyFormat(double value, string expected) =>
        Assert.Equal(expected, ChartFormat.Compact(value));

    [Theory]
    [InlineData(0.004, "<$0.01")]
    [InlineData(12.5, "$12.50")]
    [InlineData(1500, "$1,500")]
    [InlineData(12000, "$12K")]
    public void Money_MatchesLegacyFormat(double value, string expected) =>
        Assert.Equal(expected, ChartFormat.Money(value));

    [Theory]
    [InlineData(2000, "Equivalent value", "$2K")]
    [InlineData(40, "Equivalent value", "$40")]
    [InlineData(2.5, "Equivalent value", "$2.5")]
    [InlineData(6_000_000, "Total tokens", "6M")]
    public void AxisTick_UsesMetric(double value, string metric, string expected) =>
        Assert.Equal(expected, ChartFormat.AxisTick(value, metric));

    [Theory]
    [InlineData(7_100_000, 8_000_000)]
    [InlineData(0, 4)]
    [InlineData(95, 100)]
    public void NiceAxisMax_LandsOnRoundTicks(double peak, double expected) =>
        Assert.Equal(expected, ChartFormat.NiceAxisMax(peak, 4), 6);

    [Fact]
    public void DayLabel_HasNoLeadingZero() =>
        Assert.Equal("Sep 9", ChartFormat.DayLabel(new DateOnly(2026, 9, 9)));

    [Fact]
    public void ColourFor_UsesSeriesPositionAndMutedOther()
    {
        var palette = ThemePalette.Light;
        var series = new[] { "Opus", "Sonnet", "Other" };
        Assert.Equal(palette.Series(1), StackedColumnChartControl.ColourFor(palette, series, "Sonnet"));
        Assert.Equal(palette.InkMuted, StackedColumnChartControl.ColourFor(palette, series, "Other"));
        Assert.Equal(palette.Accent, StackedColumnChartControl.ColourFor(palette, series, "Unknown"));
    }

    [Fact]
    public void LabelStride_ThinsCrowdedLabels()
    {
        Assert.Equal(1, StackedColumnChartControl.LabelStride(30, 60));
        Assert.Equal(4, StackedColumnChartControl.LabelStride(30, 12));
    }

    [Fact]
    public void Tooltip_ListsModelsAndTotal()
    {
        var bucket = new UsageHistoryBucket(new DateOnly(2026, 9, 9),
            new Dictionary<string, double> { ["Opus"] = 1500, ["Sonnet"] = 500 });
        var text = StackedColumnChartControl.TooltipText(bucket, ["Opus", "Sonnet"], "Total tokens");
        Assert.Contains("Sep 9", text);
        Assert.Contains("Opus: 1,500", text);
        Assert.Contains("Total: 2,000", text);
    }

    [Fact]
    public void Fold_CapsAtEightRows_AndSumsTheTailIntoOther()
    {
        var rows = Enumerable.Range(1, 10).Select(i => new UsageHistoryBreakdown($"m{i}", 100 - i)).ToList();
        var folded = HorizontalBarChartControl.Fold(rows);
        Assert.Equal(8, folded.Count);
        Assert.Equal("Other", folded[^1].Label);
        Assert.Equal(rows.Skip(7).Sum(r => r.Value), folded[^1].Value);
    }

    [Fact]
    public void Snapshot_WithHistory_FillsBreakdownsAndTitles()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        var history = new UsageHistory(
            buckets: [new UsageHistoryBucket(new DateOnly(2026, 9, 9), new Dictionary<string, double> { ["Opus"] = 10 })],
            series: ["Opus"],
            byModel: [new UsageHistoryBreakdown("Opus", 10)],
            byProject: [new UsageHistoryBreakdown("app", 10)],
            days: 30,
            projectLabel: "By project");
        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true, history: history, valueNote: "note"));

        Assert.Equal("By model · last 30 days", vm.ModelTitle);
        Assert.Equal("By project · last 30 days", vm.ProjectTitle);
        Assert.True(vm.HasModelRows);
        Assert.True(vm.HasProjectRows);
        Assert.Equal("note", vm.ValueNote);
    }

    [Fact]
    public void HistoryError_GoesToHistoryNote_NotTheTopBanner()
    {
        var vm = new ProviderTabViewModel("claude", "Claude");
        vm.UpdateFromSnapshot(new ProviderSnapshot("claude", configured: true, historyError: "no transcripts"));

        Assert.False(vm.HasError);
        Assert.True(vm.HasHistoryNote);
        Assert.Equal("⚠ no transcripts", vm.HistoryNote);
        Assert.False(vm.HasHistory);
    }
}
