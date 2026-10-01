using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class UsageCountersTests
{
    [Fact]
    public void Zero_HasNoTokensCostOrMessages()
    {
        var zero = UsageCounters.Zero;

        Assert.Equal(0, zero.Messages);
        Assert.Equal(0, zero.TotalTokens);
        Assert.Equal(0m, zero.CostUsd);
        Assert.Equal(0, zero.UnpricedTokens);
        Assert.Equal(0, zero.EstimatedTokens);
        Assert.Equal(0.0, zero.CacheHitRate);
    }

    [Fact]
    public void ForMessage_CountsAsOneMessageAndCarriesEveryField()
    {
        var counters = UsageCounters.ForMessage(100, 200, 30, 40, 1.23m, unpricedTokens: 5, estimatedTokens: 6);

        Assert.Equal(1, counters.Messages);
        Assert.Equal(100, counters.InputTokens);
        Assert.Equal(200, counters.OutputTokens);
        Assert.Equal(30, counters.CacheWriteTokens);
        Assert.Equal(40, counters.CacheReadTokens);
        Assert.Equal(1.23m, counters.CostUsd);
        Assert.Equal(5, counters.UnpricedTokens);
        Assert.Equal(6, counters.EstimatedTokens);
        Assert.Equal(370, counters.TotalTokens);
    }

    [Fact]
    public void ForMessage_UnpricedAndEstimatedDefaultToZero()
    {
        var counters = UsageCounters.ForMessage(1, 1, 0, 0, 1.0m);

        Assert.Equal(0, counters.UnpricedTokens);
        Assert.Equal(0, counters.EstimatedTokens);
    }

    [Fact]
    public void Combine_AddsEveryFieldAcrossBothOperands()
    {
        var left = UsageCounters.ForMessage(1, 2, 3, 4, 0.0m, unpricedTokens: 10);
        var right = UsageCounters.ForMessage(1, 1, 0, 0, 5.0m);

        var combined = left.Combine(right);

        Assert.Equal(2, combined.Messages);
        Assert.Equal(2, combined.InputTokens);
        Assert.Equal(3, combined.OutputTokens);
        Assert.Equal(3, combined.CacheWriteTokens);
        Assert.Equal(4, combined.CacheReadTokens);
        Assert.Equal(5.0m, combined.CostUsd);
        Assert.Equal(10, combined.UnpricedTokens);
    }

    [Fact]
    public void Combine_DoesNotMutateEitherOperand()
    {
        var left = UsageCounters.ForMessage(1, 0, 0, 0, 0m);
        var right = UsageCounters.ForMessage(1, 0, 0, 0, 0m);

        _ = left.Combine(right);

        Assert.Equal(1, left.Messages);
        Assert.Equal(1, right.Messages);
    }

    [Fact]
    public void Combine_NullOther_Throws() =>
        Assert.Throws<ArgumentNullException>(() => UsageCounters.Zero.Combine(null!));

    [Fact]
    public void CacheHitRate_NoCacheableTokens_IsZeroRatherThanDividingByZero()
    {
        var counters = UsageCounters.ForMessage(0, 100, 0, 0, 0m);
        Assert.Equal(0.0, counters.CacheHitRate);
    }

    [Fact]
    public void CacheHitRate_IsShareOfCacheableTokensServedFromCache()
    {
        // input=10, cacheWrite=0, cacheRead=30 -> cacheable=40, hit rate = 30/40 = 0.75.
        var counters = UsageCounters.ForMessage(10, 0, 0, 30, 0m);
        Assert.Equal(0.75, counters.CacheHitRate);
    }

    [Fact]
    public void Constructor_NegativeMessages_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageCounters(messages: -1));

    [Fact]
    public void Constructor_NegativeInputTokens_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageCounters(inputTokens: -1));

    [Fact]
    public void Constructor_NegativeOutputTokens_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageCounters(outputTokens: -1));

    [Fact]
    public void Constructor_NegativeCacheWriteTokens_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageCounters(cacheWriteTokens: -1));

    [Fact]
    public void Constructor_NegativeCacheReadTokens_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageCounters(cacheReadTokens: -1));

    [Fact]
    public void Constructor_NegativeCost_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageCounters(costUsd: -0.01m));

    [Fact]
    public void Constructor_NegativeUnpricedTokens_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageCounters(unpricedTokens: -1));

    [Fact]
    public void Constructor_NegativeEstimatedTokens_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageCounters(estimatedTokens: -1));

    [Fact]
    public void TotalTokens_SumsAllFourTokenKinds()
    {
        var counters = new UsageCounters(inputTokens: 1, outputTokens: 2, cacheWriteTokens: 3, cacheReadTokens: 4);
        Assert.Equal(10, counters.TotalTokens);
    }

    [Fact]
    public void TotalTokens_FieldsIndividuallyValidButSumOneMoreThanLongMaxValue_ThrowsInsteadOfWrapping()
    {
        // Each field alone passes the constructor's non-negativity check; only their sum overflows.
        var counters = new UsageCounters(inputTokens: long.MaxValue, outputTokens: 1);

        Assert.Throws<OverflowException>(() => { _ = counters.TotalTokens; });
    }

    [Fact]
    public void Combine_ResultingTokenFieldOneMoreThanLongMaxValue_ThrowsInsteadOfWrappingNegative()
    {
        var left = UsageCounters.ForMessage(long.MaxValue, 0, 0, 0, 0m);
        var right = UsageCounters.ForMessage(1, 0, 0, 0, 0m);

        Assert.Throws<OverflowException>(() => left.Combine(right));
    }

    [Fact]
    public void Combine_MessagesOneMoreThanIntMaxValue_ThrowsInsteadOfWrapping()
    {
        var left = new UsageCounters(messages: int.MaxValue);
        var right = new UsageCounters(messages: 1);

        Assert.Throws<OverflowException>(() => left.Combine(right));
    }
}
