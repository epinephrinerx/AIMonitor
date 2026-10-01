using System.Text.Json;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.OpenAi;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

/// <summary>
/// Pure mapping tests for <see cref="CodexQuotaMapper"/> against synthetic
/// <c>account/rateLimits/read</c>/<c>account/usage/read</c> payloads - no process, no HTTP.
/// </summary>
public sealed class CodexQuotaMapperTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenAi", fileName);

    private static string ReadFixture(string fileName) => File.ReadAllText(FixturePath(fileName));

    [Fact]
    public void Meters_DeduplicatesLegacyAndCurrentShapes_AndPreservesServerOrderNotPercentOrder()
    {
        const string payload = """
            {
              "rateLimits": {"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":2000000000},
                              "secondary":{"usedPercent":80,"windowDurationMins":10080,"resetsAt":2000500000}},
              "rateLimitsByLimitId": {
                "codex": {"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":2000000000},
                          "secondary":{"usedPercent":80,"windowDurationMins":10080,"resetsAt":2000500000}},
                "model": {"limitName":"Model","primary":{"usedPercent":95,"windowDurationMins":60}}
              }
            }
            """;

        var meters = CodexQuotaMapper.Meters(Parse(payload));

        Assert.Equal(3, meters.Count);
        Assert.Equal([25, 80, 95], meters.Select(m => m.Percent).ToArray());
        Assert.Equal("Weekly", meters[1].Title);
        Assert.Equal("Model · Session", meters[2].Title);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000), meters[0].ResetsAt);
        Assert.Equal(Severity.Critical, meters[2].ServerSeverity);
        Assert.Equal(Severity.Normal, meters[0].ServerSeverity);
        Assert.Equal(Severity.High, meters[1].ServerSeverity);
    }

    [Theory]
    [InlineData("""{"rateLimits":{"primary":{}}}""")] // missing usedPercent
    [InlineData("""{"rateLimits":{"primary":{"usedPercent":"50"}}}""")] // string, not a number
    [InlineData("""{"rateLimits":{"primary":{"usedPercent":true}}}""")] // JSON bool, not a number
    [InlineData("""{"rateLimits":{"primary":{"usedPercent":-1}}}""")] // negative
    [InlineData("""{"rateLimits":{"primary":{"usedPercent":1e400}}}""")] // parses to +Infinity
    public void Meters_RejectsInvalidPercentValues(string payload)
    {
        var meters = CodexQuotaMapper.Meters(Parse(payload));

        Assert.Empty(meters);
    }

    [Fact]
    public void Meters_ZeroPercentIsIncludedNotTreatedAsMissing()
    {
        var meters = CodexQuotaMapper.Meters(Parse("""{"rateLimits":{"primary":{"usedPercent":0}}}"""));

        Assert.Equal(0, Assert.Single(meters).Percent);
    }

    [Fact]
    public void Meters_PercentAboveOneHundredIsClamped()
    {
        var meters = CodexQuotaMapper.Meters(Parse("""{"rateLimits":{"primary":{"usedPercent":140}}}"""));

        Assert.Equal(100, Assert.Single(meters).Percent);
    }

    [Fact]
    public void Meters_LockedReasonForcesCriticalRegardlessOfPercent()
    {
        var meters = CodexQuotaMapper.Meters(
            Parse("""{"rateLimits":{"primary":{"usedPercent":10,"rateLimitReachedType":"primary_window"}}}"""));

        var meter = Assert.Single(meters);
        Assert.Equal(Severity.Critical, meter.ServerSeverity);
        Assert.Equal("primary window", meter.LockedReason);
    }

    [Fact]
    public void Meters_MissingRateLimitData_ReturnsEmptyList()
    {
        Assert.Empty(CodexQuotaMapper.Meters(Parse("{}")));
    }

    [Fact]
    public void Meters_KeyIsUniquePerLimitGroupAndWindow()
    {
        const string payload = """
            {"rateLimitsByLimitId": {
                "codex": {"primary":{"usedPercent":1},"secondary":{"usedPercent":2}},
                "extra": {"primary":{"usedPercent":3}}
            }}
            """;

        var meters = CodexQuotaMapper.Meters(Parse(payload));

        Assert.Equal(["codex_codex_primary", "codex_codex_secondary", "codex_extra_primary"], meters.Select(m => m.Key).ToArray());
    }

    [Fact]
    public void History_DoesNotInventModelsSpendOrMissingDays()
    {
        const string payload = """
            {"summary":{"lifetimeTokens":1000,"peakDailyTokens":null},
             "dailyUsageBuckets":[{"startDate":"2026-01-05","tokens":100},{"startDate":"bad","tokens":123}]}
            """;
        var today = new DateOnly(2026, 1, 5);

        var (history, stats) = CodexQuotaMapper.History(Parse(payload), 14, UsageMetric.TotalTokens, today);

        Assert.NotNull(history);
        Assert.Single(history!.Buckets);
        Assert.Equal(100, history.Buckets[0].Total);
        Assert.Empty(history.ByModel);
        Assert.Equal(2, stats.Count);
    }

    [Theory]
    [InlineData(UsageMetric.OutputTokens)]
    [InlineData(UsageMetric.EquivalentValue)]
    public void History_NonTotalTokensMetric_ReturnsNoHistory(string metric)
    {
        const string payload = """{"dailyUsageBuckets":[{"startDate":"2026-01-05","tokens":100}]}""";
        var today = new DateOnly(2026, 1, 5);

        var (history, _) = CodexQuotaMapper.History(Parse(payload), 14, metric, today);

        Assert.Null(history);
    }

    [Fact]
    public void History_DayOutsideRequestedWindow_IsDropped()
    {
        const string payload = """{"dailyUsageBuckets":[{"startDate":"2025-01-01","tokens":100}]}""";
        var today = new DateOnly(2026, 1, 5);

        var (history, _) = CodexQuotaMapper.History(Parse(payload), 14, UsageMetric.TotalTokens, today);

        Assert.Null(history);
    }

    [Fact]
    public void History_NegativeTokenCount_IsSkipped()
    {
        const string payload = """{"dailyUsageBuckets":[{"startDate":"2026-01-05","tokens":-5}]}""";
        var today = new DateOnly(2026, 1, 5);

        var (history, _) = CodexQuotaMapper.History(Parse(payload), 14, UsageMetric.TotalTokens, today);

        Assert.Null(history);
    }

    [Fact]
    public void History_NoSummaryAndNoBuckets_ReturnsNullHistoryAndEmptyStats()
    {
        var (history, stats) = CodexQuotaMapper.History(Parse("{}"), 14, UsageMetric.TotalTokens, new DateOnly(2026, 1, 5));

        Assert.Null(history);
        Assert.Empty(stats);
    }

    // -- Golden fixture contract tests (finding #10): guard against drift from the real
    // account/rateLimits/read and account/usage/read response shapes. --------------------------------

    [Fact]
    [Trait("Category", "Contract")]
    public void Meters_GoldenCodexRateLimitsFixture_MapsEveryWindowInServerOrder()
    {
        var payload = Parse(ReadFixture("codex-rate-limits.json"));

        var meters = CodexQuotaMapper.Meters(payload);

        Assert.Equal(3, meters.Count);
        Assert.Equal("codex_codex_primary", meters[0].Key);
        Assert.Equal(42.5, meters[0].Percent);
        Assert.Equal("codex_codex_secondary", meters[1].Key);
        Assert.Equal("Weekly", meters[1].Title);
        Assert.Equal(61.0, meters[1].Percent);
        Assert.Equal("codex_model-gpt-5_primary", meters[2].Key);
        Assert.Equal("GPT-5 · Session", meters[2].Title);
        Assert.Equal(15.0, meters[2].Percent);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void History_GoldenCodexUsageFixture_MapsDailyBucketsAndLifetimeStats()
    {
        var payload = Parse(ReadFixture("codex-usage.json"));
        var today = new DateOnly(2026, 1, 5);

        var (history, stats) = CodexQuotaMapper.History(payload, 14, UsageMetric.TotalTokens, today);

        Assert.NotNull(history);
        Assert.Equal(3, history!.Buckets.Count);
        Assert.Equal(82000 + 95500 + 61200, history.Buckets.Sum(b => b.Total));
        Assert.Contains(stats, s => s.Label == "Lifetime tokens" && s.Value == "4.5M");
        Assert.Contains(stats, s => s.Label == "Peak daily tokens" && s.Value == "120K");
    }
}
