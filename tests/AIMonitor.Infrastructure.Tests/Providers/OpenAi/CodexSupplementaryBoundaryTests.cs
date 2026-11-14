using System.Text.Json;
using AIMonitor.Infrastructure.Providers.OpenAi;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

public sealed class CodexSupplementaryBoundaryTests
{
    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("1e30")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("\"2\"")]
    public void InvalidIntegerMetadata_IsNotCoerced(string value)
    {
        using var reset = JsonDocument.Parse("{\"rateLimitResetCredits\":{\"availableCount\":" + value + "}}");
        Assert.Empty(CodexQuotaMapper.RateLimitStats(reset.RootElement));
        using var activity = JsonDocument.Parse("{\"summary\":{\"currentStreakDays\":" + value
            + ",\"longestStreakDays\":" + value + ",\"longestRunningTurnSec\":" + value + "}}");
        Assert.Empty(CodexQuotaMapper.History(activity.RootElement, 7, "Total tokens", new DateOnly(2026, 10, 15)).Stats);
    }

    [Theory]
    [InlineData("253402300800")]
    [InlineData("1893456000000")]
    [InlineData("-1")]
    [InlineData("null")]
    public void InvalidExpirySeconds_RemainUnknown(string expiry)
    {
        using var payload = JsonDocument.Parse("{\"rateLimitResetCredits\":{\"availableCount\":2,\"credits\":[{\"status\":\"available\",\"expiresAt\":" + expiry + "}]}}");
        var stat = Assert.Single(CodexQuotaMapper.RateLimitStats(payload.RootElement));
        Assert.Equal("2", stat.Value);
        Assert.Empty(stat.Detail);
    }
}
