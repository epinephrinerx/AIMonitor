using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class UsageMetricTests
{
    private static readonly UsageCounters Sample = new(
        inputTokens: 100, outputTokens: 50, cacheWriteTokens: 10, cacheReadTokens: 5, costUsd: 2.5m);

    [Fact]
    public void ValueOf_OutputTokens_ReturnsOutputTokenCount() =>
        Assert.Equal(50.0, UsageMetric.ValueOf(Sample, UsageMetric.OutputTokens));

    [Fact]
    public void ValueOf_EquivalentValue_ReturnsCostAsDouble() =>
        Assert.Equal(2.5, UsageMetric.ValueOf(Sample, UsageMetric.EquivalentValue));

    [Fact]
    public void ValueOf_TotalTokens_ReturnsSumOfAllTokenKinds() =>
        Assert.Equal(165.0, UsageMetric.ValueOf(Sample, UsageMetric.TotalTokens));

    [Theory]
    [InlineData("")]
    [InlineData("unrecognised-metric")]
    public void ValueOf_UnrecognisedOrEmptyMetric_DefaultsToTotalTokens(string metric) =>
        Assert.Equal(165.0, UsageMetric.ValueOf(Sample, metric));

    [Fact]
    public void ValueOf_NullCounters_Throws() =>
        Assert.Throws<ArgumentNullException>(() => UsageMetric.ValueOf(null!, UsageMetric.TotalTokens));
}
