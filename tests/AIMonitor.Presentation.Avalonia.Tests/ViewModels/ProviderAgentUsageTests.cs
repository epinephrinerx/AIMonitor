using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

/// <summary>What a tab does with ccusage figures, the Overview's own rules, and chart costs.</summary>
public sealed class ProviderAgentUsageTests
{
    private static AgentUsageLine Line(string tokens, string cost) => new("claude", "Claude Code", 1, tokens, cost);

    [Theory]
    [InlineData("claude")]
    [InlineData("openai")]
    [InlineData("agy")]
    [InlineData("opencode")]
    public void AnAgentTab_ShowsTodayAndThisMonth(string providerId)
    {
        var tab = new ProviderViewModel(providerId, "Tab");

        tab.SetAgentUsage(new AgentUsageSummary(Line("1.2M", "$3.40"), Line("9.9M", "$20.00")));

        Assert.Equal(
            [("Today (ccusage)", "1.2M · $3.40"), ("This month (ccusage)", "9.9M · $20.00")],
            tab.AgentUsageRows.Select(row => (row.Label, row.Value)));
        Assert.True(tab.HasAgentUsage);
    }

    [Fact]
    public void TheOverview_IgnoresAnAgentSummary()
    {
        var tab = new ProviderViewModel(OverviewSnapshotBuilder.ProviderId, "Overview");

        tab.SetAgentUsage(new AgentUsageSummary(Line("1", "$1.00"), null));

        Assert.False(tab.HasAgentUsage);
    }

    [Fact]
    public void ClearingTheSummary_RemovesTheRows()
    {
        var tab = new ProviderViewModel("claude", "Claude");
        tab.SetAgentUsage(new AgentUsageSummary(Line("1", "$1.00"), null));

        tab.SetAgentUsage(null);

        Assert.Empty(tab.AgentUsageRows);
        Assert.False(tab.HasAgentUsage);
    }

    [Fact]
    public void OpenCodeAndTheOverview_HaveNoSetupCard()
    {
        Assert.False(new ProviderViewModel(OpenCodeSnapshotBuilder.ProviderId, "OpenCode").ShowSetupHint);
        Assert.False(new ProviderViewModel(OverviewSnapshotBuilder.ProviderId, "Overview").ShowSetupHint);
    }

    [Fact]
    public void TheOverview_NeverLaysOutMoreThanThreeGaugesInARow()
    {
        var overview = new ProviderViewModel(OverviewSnapshotBuilder.ProviderId, "Overview");

        overview.Apply(TestData.Snapshot(
            OverviewSnapshotBuilder.ProviderId,
            meters: [.. Enumerable.Range(0, 5).Select(i => TestData.Meter(key: $"agent-{i}"))]));

        Assert.Equal(3, overview.MeterSections[0].Columns);
    }

    [Fact]
    public void AServiceTab_KeepsEveryGaugeOnOneRow()
    {
        var claude = new ProviderViewModel("claude", "Claude");

        claude.Apply(TestData.Snapshot(meters: [.. Enumerable.Range(0, 5).Select(i => TestData.Meter(key: $"w-{i}"))]));

        Assert.Equal(5, claude.MeterSections[0].Columns);
    }

    [Fact]
    public void TheOverviewEmptyMessage_IsTakenOnlyByTheOverview()
    {
        var overview = new ProviderViewModel(OverviewSnapshotBuilder.ProviderId, "Overview");
        var claude = new ProviderViewModel("claude", "Claude");

        overview.UpdateOverviewEmptyMessage("No agent usage recorded this month");
        claude.UpdateOverviewEmptyMessage("No agent usage recorded this month");
        overview.UpdateOverviewEmptyMessage(null);

        Assert.False(overview.HasEmptyMessage);
        Assert.False(claude.HasEmptyMessage);
    }

    [Fact]
    public void ChartCosts_FollowTheChartDays_AndSkipDaysWithoutAPrice()
    {
        var history = TestData.History(buckets: 3, days: 3);
        var days = history.Buckets.Select(bucket => bucket.Day).ToList();
        var priced = new UsageHistory(
            history.Buckets, history.Series, history.ByModel, history.ByProject, history.Days, history.Metric, history.ProjectLabel,
            dailyCosts: [new DailyCost(days[2], 3, false), new DailyCost(days[0], 1, false), new DailyCost(days[0].AddDays(-30), 9, false)]);
        var claude = new ProviderViewModel("claude", "Claude");

        claude.Apply(TestData.Snapshot(history: priced));

        Assert.Equal([days[0], days[2]], claude.ChartCosts.Select(cost => cost.Day));
    }
}
