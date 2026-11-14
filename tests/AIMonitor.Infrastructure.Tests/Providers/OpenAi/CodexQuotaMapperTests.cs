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

    // -- R-007: Rate limits metadata (plan, credits, limit reasons) --------

    [Fact]
    public void RateLimitStats_WhenMetadataPresent_ExtractsPlanCreditsAndReachedReason()
    {
        const string payload = """
            {
              "rateLimits": {
                "limitId": "codex",
                "planType": "plus",
                "credits": {
                  "hasCredits": true,
                  "unlimited": false,
                  "balance": "150"
                },
                "rateLimitReachedType": "workspace_owner_credits_depleted"
              }
            }
            """;

        var stats = CodexQuotaMapper.RateLimitStats(Parse(payload));

        Assert.Equal(3, stats.Count);
        Assert.Equal(new Stat("Plan", "Plus", "ChatGPT plan"), stats[0]);
        Assert.Equal(new Stat("Credits", "150", "Workspace credits"), stats[1]);
        Assert.Equal(new Stat("Limit reason", "Workspace owner credits depleted", "Reported by Codex"), stats[2]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"rateLimits":{}}""")]
    [InlineData("""{"rateLimits":{"primary":{"usedPercent":20}}}""")]
    [InlineData("""{"rateLimits":{"planType":null,"credits":null,"rateLimitReachedType":null}}""")]
    public void RateLimitStats_WhenMetadataAbsent_ReturnsEmptyStats(string json)
    {
        var stats = CodexQuotaMapper.RateLimitStats(Parse(json));

        Assert.Empty(stats);
    }

    [Theory]
    [InlineData("""{"rateLimits":{"planType":123}}""")] // planType not string
    [InlineData("""{"rateLimits":{"planType":""}}""")] // empty planType
    [InlineData("""{"rateLimits":{"credits":"not-an-object"}}""")] // credits not object
    [InlineData("""{"rateLimits":{"credits":{"balance":null,"unlimited":false}}}""")] // null balance and not unlimited
    [InlineData("""{"rateLimits":{"credits":{"balance":[]}}}""")] // malformed balance
    [InlineData("""{"rateLimits":{"rateLimitReachedType":456}}""")] // reachedType not string
    public void RateLimitStats_WhenMalformed_IgnoresInvalidValuesSafely(string json)
    {
        var stats = CodexQuotaMapper.RateLimitStats(Parse(json));

        Assert.Empty(stats);
    }

    [Theory]
    [InlineData("""{"rateLimits":{"credits":{"balance":"NaN"}}}""")]
    [InlineData("""{"rateLimits":{"credits":{"balance":"Infinity"}}}""")]
    [InlineData("""{"rateLimits":{"credits":{"balance":"+Infinity"}}}""")]
    [InlineData("""{"rateLimits":{"credits":{"balance":"-Infinity"}}}""")]
    [InlineData("""{"rateLimits":{"credits":{"balance":"not-a-number"}}}""")]
    [InlineData("""{"rateLimits":{"credits":{"balance":"1e9999"}}}""")]
    [InlineData("""{"rateLimits":{"credits":{"balance":1e9999}}}""")]
    [InlineData("""{"rateLimits":{"credits":{"balance":"   "}}}""")]
    public void RateLimitStats_WhenCreditBalanceMalformedOrOutOfRange_ReturnsEmptyStats(string json)
    {
        var stats = CodexQuotaMapper.RateLimitStats(Parse(json));

        Assert.Empty(stats);
    }

    [Theory]
    [InlineData("""{"rateLimits":{"credits":{"balance":"150"}}}""", "150")]
    [InlineData("""{"rateLimits":{"credits":{"balance":150}}}""", "150")]
    [InlineData("""{"rateLimits":{"credits":{"balance":"123.450"}}}""", "123.450")]
    [InlineData("""{"rateLimits":{"credits":{"balance":123.45}}}""", "123.45")]
    public void RateLimitStats_WhenValidFiniteCreditBalance_PreservesPrecisionAndValue(string json, string expected)
    {
        var stats = CodexQuotaMapper.RateLimitStats(Parse(json));

        var stat = Assert.Single(stats);
        Assert.Equal("Credits", stat.Label);
        Assert.Equal(expected, stat.Value);
    }

    [Fact]
    public void RateLimitStats_WhenHasCreditsTrueWithoutFiniteBalance_DoesNotInventValue()
    {
        const string payload = """
            {
              "rateLimits": {
                "credits": {
                  "hasCredits": true,
                  "unlimited": false,
                  "balance": null
                }
              }
            }
            """;

        var stats = CodexQuotaMapper.RateLimitStats(Parse(payload));

        Assert.Empty(stats);
    }

    [Theory]
    [InlineData("""{"rateLimits":{"credits":{"unlimited":false,"balance":"0"}}}""", "0")]
    [InlineData("""{"rateLimits":{"credits":{"unlimited":false,"balance":0}}}""", "0")]
    [InlineData("""{"rateLimits":{"credits":{"unlimited":false,"balance":"0.00"}}}""", "0.00")]
    public void RateLimitStats_WhenBalanceNumericZero_PreservesZeroWithoutInventingCurrency(string json, string expected)
    {
        var stats = CodexQuotaMapper.RateLimitStats(Parse(json));

        var stat = Assert.Single(stats);
        Assert.Equal("Credits", stat.Label);
        Assert.Equal(expected, stat.Value);
        Assert.Equal("Workspace credits", stat.Detail);
        Assert.DoesNotContain("$", stat.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("USD", stat.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void RateLimitStats_WhenUnlimitedCredits_ReturnsExplicitUnlimited()
    {
        const string payload = """
            {
              "rateLimits": {
                "credits": {
                  "hasCredits": true,
                  "unlimited": true,
                  "balance": null
                }
              }
            }
            """;

        var stats = CodexQuotaMapper.RateLimitStats(Parse(payload));

        var stat = Assert.Single(stats);
        Assert.Equal("Credits", stat.Label);
        Assert.Equal("Unlimited", stat.Value);
        Assert.Equal("Workspace credits", stat.Detail);
    }

    [Fact]
    public void RateLimitStats_WhenMultipleBuckets_EmitsGroupedLabels()
    {
        const string payload = """
            {
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": "codex",
                  "limitName": null,
                  "planType": "plus",
                  "credits": {
                    "hasCredits": true,
                    "unlimited": false,
                    "balance": "250"
                  }
                },
                "codex_other": {
                  "limitId": "codex_other",
                  "limitName": "Codex Other",
                  "planType": "team",
                  "credits": {
                    "hasCredits": true,
                    "unlimited": true,
                    "balance": null
                  },
                  "rateLimitReachedType": "rate_limit_reached"
                }
              }
            }
            """;

        var stats = CodexQuotaMapper.RateLimitStats(Parse(payload));

        Assert.Equal(5, stats.Count);
        Assert.Equal("Codex · Plan", stats[0].Label);
        Assert.Equal("Plus", stats[0].Value);
        Assert.Equal("Codex · Credits", stats[1].Label);
        Assert.Equal("250", stats[1].Value);

        Assert.Equal("Codex Other · Plan", stats[2].Label);
        Assert.Equal("Team", stats[2].Value);
        Assert.Equal("Codex Other · Credits", stats[3].Label);
        Assert.Equal("Unlimited", stats[3].Value);
        Assert.Equal("Codex Other · Limit reason", stats[4].Label);
        Assert.Equal("Rate limit reached", stats[4].Value);
    }

    [Theory]
    [InlineData("""{"rateLimits":{"credits":{"hasCredits":false,"unlimited":false,"balance":"0"}}}""")]
    [InlineData("""{"rateLimits":{"credits":{"hasCredits":false,"unlimited":false,"balance":"150"}}}""")]
    [InlineData("""{"rateLimits":{"credits":{"hasCredits":false,"unlimited":true,"balance":null}}}""")]
    [InlineData("""{"rateLimits":{"credits":{"hasCredits":false,"unlimited":true,"balance":"150"}}}""")]
    [InlineData("""{"rateLimits":{"credits":{"hasCredits":"false","unlimited":false,"balance":"0"}}}""")]
    public void RateLimitStats_WhenHasCreditsFalse_SuppressesCreditsTileEvenWithContradictoryBalanceOrUnlimited(string json)
    {
        var stats = CodexQuotaMapper.RateLimitStats(Parse(json));

        Assert.Empty(stats);
    }

    [Theory]
    [InlineData("""{"rateLimits":{"credits":{"hasCredits":true,"unlimited":false,"balance":"0"}}}""", "0")]
    [InlineData("""{"rateLimits":{"credits":{"unlimited":false,"balance":"0"}}}""", "0")]
    public void RateLimitStats_WhenHasCreditsTrueOrMissing_PreservesValidZero(string json, string expected)
    {
        var stats = CodexQuotaMapper.RateLimitStats(Parse(json));

        var stat = Assert.Single(stats);
        Assert.Equal("Credits", stat.Label);
        Assert.Equal(expected, stat.Value);
    }

    [Fact]
    public void RateLimitStats_WhenUnlimitedTrueWithNonNullBalance_PrecedenceFavorsUnlimited()
    {
        const string payload = """
            {
              "rateLimits": {
                "credits": {
                  "hasCredits": true,
                  "unlimited": true,
                  "balance": "150"
                }
              }
            }
            """;

        var stats = CodexQuotaMapper.RateLimitStats(Parse(payload));

        var stat = Assert.Single(stats);
        Assert.Equal("Credits", stat.Label);
        Assert.Equal("Unlimited", stat.Value);
    }

    [Fact]
    public void RateLimitStats_WhenConflictingLegacyAndByLimitIdMetadata_PrefersByLimitIdMetadata()
    {
        const string payload = """
            {
              "rateLimits": {
                "limitId": "codex",
                "planType": "free",
                "credits": {
                  "hasCredits": true,
                  "unlimited": false,
                  "balance": "10"
                },
                "rateLimitReachedType": "workspace_owner_credits_depleted"
              },
              "rateLimitsByLimitId": {
                "codex": {
                  "planType": "team",
                  "credits": {
                    "hasCredits": true,
                    "unlimited": true,
                    "balance": null
                  },
                  "rateLimitReachedType": "rate_limit_reached"
                }
              }
            }
            """;

        var stats = CodexQuotaMapper.RateLimitStats(Parse(payload));

        Assert.Equal(3, stats.Count);
        Assert.Equal(new Stat("Plan", "Team", "ChatGPT plan"), stats[0]);
        Assert.Equal(new Stat("Credits", "Unlimited", "Workspace credits"), stats[1]);
        Assert.Equal(new Stat("Limit reason", "Rate limit reached", "Reported by Codex"), stats[2]);
    }

    [Theory]
    [InlineData("enterprise_preview", "Enterprise Preview")]
    [InlineData("custom-partner-tier", "Custom Partner Tier")]
    [InlineData("unknown_future_plan", "Unknown Future Plan")]
    [InlineData("deep_research_preview", "Deep Research Preview")]
    public void RateLimitStats_WhenUnknownPlanType_FormatsAsReadableFallbackWithoutInventingAliases(string rawPlan, string expectedPlan)
    {
        var payload = $$"""
            {
              "rateLimits": {
                "planType": "{{rawPlan}}"
              }
            }
            """;

        var stats = CodexQuotaMapper.RateLimitStats(Parse(payload));

        var stat = Assert.Single(stats);
        Assert.Equal("Plan", stat.Label);
        Assert.Equal(expectedPlan, stat.Value);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void RateLimitStats_GoldenCodexRateLimitsR007Fixture_MapsGroupedMetadataInServerOrder()
    {
        var payload = Parse(ReadFixture("codex-rate-limits-r007.json"));

        var stats = CodexQuotaMapper.RateLimitStats(payload);

        Assert.Equal(5, stats.Count);
        Assert.Equal("Codex · Plan", stats[0].Label);
        Assert.Equal("Plus", stats[0].Value);
        Assert.Equal("Codex · Credits", stats[1].Label);
        Assert.Equal("150", stats[1].Value);
        Assert.Equal("GPT-5 · Plan", stats[2].Label);
        Assert.Equal("Team", stats[2].Value);
        Assert.Equal("GPT-5 · Credits", stats[3].Label);
        Assert.Equal("Unlimited", stats[3].Value);
        Assert.Equal("GPT-5 · Limit reason", stats[4].Label);
        Assert.Equal("Rate limit reached", stats[4].Value);
    }

    [Fact]
    public void RateLimitStats_WhenCountVsArray_CountIsAuthoritative()
    {
        var payload = """
        {
          "rateLimitResetCredits": {
            "availableCount": 3,
            "credits": [
              { "status": "available", "expiresAt": 1893456000 }
            ]
          }
        }
        """;

        var stats = CodexQuotaMapper.RateLimitStats(Parse(payload));

        var resetStat = Assert.Single(stats);
        Assert.Equal("Rate limit resets", resetStat.Label);
        Assert.Equal("3", resetStat.Value);
        Assert.Contains("Next known expiry", resetStat.Detail);
    }

    [Fact]
    public void RateLimitStats_WhenZeroCount_EmitsConfirmedZeroStat()
    {
        var payload = """
        {
          "rateLimitResetCredits": {
            "availableCount": 0,
            "credits": []
          }
        }
        """;

        var stats = CodexQuotaMapper.RateLimitStats(Parse(payload));

        var resetStat = Assert.Single(stats);
        Assert.Equal("Rate limit resets", resetStat.Label);
        Assert.Equal("0", resetStat.Value);
        Assert.Equal("", resetStat.Detail);
    }

    [Theory]
    [InlineData("""{"rateLimitResetCredits":{"availableCount":null}}""")]
    [InlineData("""{"rateLimitResetCredits":{"availableCount":-1}}""")]
    [InlineData("""{"rateLimitResetCredits":{"availableCount":"3"}}""")]
    [InlineData("""{"rateLimitResetCredits":{"availableCount":1.5}}""")]
    [InlineData("""{"rateLimitResetCredits":"malformed"}""")]
    public void RateLimitStats_WhenNullOrMalformedResetCredits_DoesNotEmitStat(string json)
    {
        var stats = CodexQuotaMapper.RateLimitStats(Parse(json));

        Assert.Empty(stats);
    }

    [Fact]
    public void RateLimitStats_WhenRootMetadataOnly_PreservesResetStatWithoutQuotaSuccess()
    {
        var payload = """
        {
          "rateLimitResetCredits": {
            "availableCount": 2
          }
        }
        """;

        var stats = CodexQuotaMapper.RateLimitStats(Parse(payload));
        var meters = CodexQuotaMapper.Meters(Parse(payload));

        var resetStat = Assert.Single(stats);
        Assert.Equal("Rate limit resets", resetStat.Label);
        Assert.Equal("2", resetStat.Value);
        Assert.Empty(meters);
    }

    [Theory]
    [InlineData("""{"rateLimitResetCredits":{"availableCount":1,"credits":null}}""")]
    [InlineData("""{"rateLimitResetCredits":{"availableCount":1,"credits":[]}}""")]
    [InlineData("""{"rateLimitResetCredits":{"availableCount":1,"credits":[{"status":"available","expiresAt":null}]}}""")]
    [InlineData("""{"rateLimitResetCredits":{"availableCount":1,"credits":[{"status":"available","expiresAt":-50}]}}""")]
    [InlineData("""{"rateLimitResetCredits":{"availableCount":1,"credits":[{"status":"available","expiresAt":"invalid"}]}}""")]
    [InlineData("""{"rateLimitResetCredits":{"availableCount":1,"credits":[{"status":"used","expiresAt":1893456000}]}}""")]
    public void RateLimitStats_WhenExpiryAbsentOrInvalid_EarliestExpiryUnknown(string json)
    {
        var stats = CodexQuotaMapper.RateLimitStats(Parse(json));

        var resetStat = Assert.Single(stats);
        Assert.Equal("Rate limit resets", resetStat.Label);
        Assert.Equal("1", resetStat.Value);
        Assert.Equal("", resetStat.Detail);
    }

    [Fact]
    public void RateLimitStats_WhenMultipleAvailableCredits_PicksEarliestExpiry()
    {
        var payload = """
        {
          "rateLimitResetCredits": {
            "availableCount": 2,
            "credits": [
              { "status": "available", "expiresAt": 1893500000 },
              { "status": "available", "expiresAt": 1893456000 },
              { "status": "expired", "expiresAt": 1000000000 }
            ]
          }
        }
        """;

        var stats = CodexQuotaMapper.RateLimitStats(Parse(payload));

        var resetStat = Assert.Single(stats);
        Assert.Equal("2", resetStat.Value);
        Assert.Equal($"Next known expiry: {DateTimeOffset.FromUnixTimeSeconds(1893456000):u}", resetStat.Detail);
    }

    [Fact]
    public void History_WhenActivitySummaryPresent_MapsStreakAndDurationAccurately()
    {
        var payload = """
        {
          "summary": {
            "lifetimeTokens": 1000000,
            "currentStreakDays": 8,
            "longestStreakDays": 14,
            "longestRunningTurnSec": 540
          }
        }
        """;

        var (_, stats) = CodexQuotaMapper.History(Parse(payload), 7, "Total tokens", new DateOnly(2026, 1, 5));

        Assert.Contains(stats, s => s.Label == "Current streak" && s.Value == "8 days");
        Assert.Contains(stats, s => s.Label == "Longest streak" && s.Value == "14 days");
        Assert.Contains(stats, s => s.Label == "Longest turn" && s.Value == "9 min (540s)");
    }

    [Fact]
    public void History_WhenActivitySummaryZero_ZeroIsValid()
    {
        var payload = """
        {
          "summary": {
            "currentStreakDays": 0,
            "longestStreakDays": 0,
            "longestRunningTurnSec": 0
          }
        }
        """;

        var (_, stats) = CodexQuotaMapper.History(Parse(payload), 7, "Total tokens", new DateOnly(2026, 1, 5));

        Assert.Contains(stats, s => s.Label == "Current streak" && s.Value == "0 days");
        Assert.Contains(stats, s => s.Label == "Longest streak" && s.Value == "0 days");
        Assert.Contains(stats, s => s.Label == "Longest turn" && s.Value == "0s");
    }

    [Theory]
    [InlineData("""{"summary":{"currentStreakDays":null}}""")]
    [InlineData("""{"summary":{"currentStreakDays":-1}}""")]
    [InlineData("""{"summary":{"currentStreakDays":"8"}}""")]
    [InlineData("""{"summary":{"longestStreakDays":"NaN"}}""")]
    [InlineData("""{"summary":{"longestRunningTurnSec":-60}}""")]
    public void History_WhenActivitySummaryNullOrInvalid_OmitsActivityStats(string json)
    {
        var (_, stats) = CodexQuotaMapper.History(Parse(json), 7, "Total tokens", new DateOnly(2026, 1, 5));

        Assert.DoesNotContain(stats, s => s.Label is "Current streak" or "Longest streak" or "Longest turn");
    }

    [Fact]
    public void History_WhenTurnSecExtremelyLarge_PreventsTimeSpanOverflow()
    {
        var payload = """
        {
          "summary": {
            "longestRunningTurnSec": 9223372036854775807
          }
        }
        """;

        var (_, stats) = CodexQuotaMapper.History(Parse(payload), 7, "Total tokens", new DateOnly(2026, 1, 5));

        var turnStat = Assert.Single(stats, s => s.Label == "Longest turn");
        Assert.Equal("9223372036854775807s", turnStat.Value);
    }
}
