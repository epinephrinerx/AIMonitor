using AIMonitor.Application.Providers;

namespace AIMonitor.Application.Tests.Providers;

public class ProviderSnapshotRequestTests
{
    [Fact]
    public void Constructor_NoArguments_UsesDefaultHistoryDaysEmptyMetricAndExcludesHistory()
    {
        var request = new ProviderSnapshotRequest();

        Assert.Equal(ProviderSnapshotRequest.DefaultHistoryDays, request.HistoryDays);
        Assert.Equal(string.Empty, request.Metric);
        Assert.False(request.IncludeHistory);
    }

    [Fact]
    public void Default_IsEquivalentToParameterlessConstruction()
    {
        Assert.Equal(new ProviderSnapshotRequest(), ProviderSnapshotRequest.Default);
    }

    [Fact]
    public void Constructor_NullMetric_BecomesEmptyStringRatherThanNull()
    {
        var request = new ProviderSnapshotRequest(metric: null);

        Assert.Equal(string.Empty, request.Metric);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_NonPositiveHistoryDays_Throws(int historyDays)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderSnapshotRequest(historyDays));
    }

    [Fact]
    public void Constructor_ExplicitValues_AreCarriedThroughUnchanged()
    {
        var request = new ProviderSnapshotRequest(historyDays: 30, metric: "Total tokens", includeHistory: true);

        Assert.Equal(30, request.HistoryDays);
        Assert.Equal("Total tokens", request.Metric);
        Assert.True(request.IncludeHistory);
    }
}
