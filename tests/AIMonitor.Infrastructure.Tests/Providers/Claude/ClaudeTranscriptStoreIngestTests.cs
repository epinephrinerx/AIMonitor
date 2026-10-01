using System.Text.Json;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Exercises <see cref="ClaudeTranscriptStore.TryIngest"/> directly - the pure "fold one already-parsed
/// record" boundary - without touching a real file. <see cref="ClaudeTranscriptStoreFileTests"/> covers
/// the same guarantees through the real <see cref="ClaudeTranscriptStore.RefreshAsync"/> file path.
/// </summary>
[Trait("Category", "Contract")]
public sealed class ClaudeTranscriptStoreIngestTests
{
    private const string When = "2026-09-14T10:00:00Z";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    private const string GoodUsageJson =
        """{"input_tokens":100,"output_tokens":200,"cache_read_input_tokens":300,"cache_creation_input_tokens":400}""";

    private static ClaudeTranscriptStore CreateStore() => new("unused-root", new FakeClock(FixedNow), TimeZoneInfo.Utc);

    private static JsonElement BuildRecord(
        string? messageId = "msg-1",
        string? requestId = null,
        string? type = "assistant",
        string? timestamp = When,
        bool includeTimestampField = true,
        string? cwd = "/work/project",
        string model = "claude-opus-4",
        string usageJson = GoodUsageJson)
    {
        var messageFields = new List<string> { $"\"model\":{JsonSerializer.Serialize(model)}", $"\"usage\":{usageJson}" };
        if (messageId is not null)
        {
            messageFields.Insert(0, $"\"id\":{JsonSerializer.Serialize(messageId)}");
        }

        var rootFields = new List<string>();
        if (type is not null)
        {
            rootFields.Add($"\"type\":{JsonSerializer.Serialize(type)}");
        }

        if (includeTimestampField)
        {
            rootFields.Add($"\"timestamp\":{(timestamp is null ? "null" : JsonSerializer.Serialize(timestamp))}");
        }

        if (cwd is not null)
        {
            rootFields.Add($"\"cwd\":{JsonSerializer.Serialize(cwd)}");
        }

        rootFields.Add("\"message\":{" + string.Join(",", messageFields) + "}");
        if (requestId is not null)
        {
            rootFields.Add($"\"requestId\":{JsonSerializer.Serialize(requestId)}");
        }

        var json = "{" + string.Join(",", rootFields) + "}";
        return JsonDocument.Parse(json).RootElement;
    }

    private static JsonElement UsageOverride(string fieldName, string rawJsonValue)
    {
        var fields = new Dictionary<string, string>
        {
            ["input_tokens"] = "100",
            ["output_tokens"] = "200",
            ["cache_read_input_tokens"] = "300",
            ["cache_creation_input_tokens"] = "400",
            [fieldName] = rawJsonValue,
        };
        var usageJson = "{" + string.Join(",", fields.Select(kv => $"\"{kv.Key}\":{kv.Value}")) + "}";
        return BuildRecord(usageJson: usageJson);
    }

    [Fact]
    public void TryIngest_GoodRecord_Counts()
    {
        var store = CreateStore();

        var counted = store.TryIngest(BuildRecord(), "fallback");

        Assert.True(counted);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(100, store.AllTotals.InputTokens);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_MalformedTokenCount_NeverThrowsAndIsSkipped()
    {
        var store = CreateStore();

        var counted = store.TryIngest(UsageOverride("input_tokens", "\"not-a-number\""), "fallback");

        Assert.False(counted);
        Assert.Equal(0, store.AllTotals.Messages);
        Assert.Equal(1, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_MalformedRecord_DoesNotClaimItsId_SoACorrectedCopyCanStillCount()
    {
        var store = CreateStore();
        var badFields = new Dictionary<string, string>
        {
            ["input_tokens"] = "100",
            ["output_tokens"] = "\"oops\"",
            ["cache_read_input_tokens"] = "300",
            ["cache_creation_input_tokens"] = "400",
        };
        var badUsage = "{" + string.Join(",", badFields.Select(kv => $"\"{kv.Key}\":{kv.Value}")) + "}";
        store.TryIngest(BuildRecord(messageId: "msg-7", usageJson: badUsage), "fallback");

        var countedAfterFix = store.TryIngest(BuildRecord(messageId: "msg-7"), "fallback");

        Assert.True(countedAfterFix);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(200, store.AllTotals.OutputTokens);
    }

    [Fact]
    public void TryIngest_TokenSumOneMoreThanLongMaxValue_IsTreatedAsMalformedWithoutClaimingTheId()
    {
        var store = CreateStore();
        var overflowFields = new Dictionary<string, string>
        {
            ["input_tokens"] = "9223372036854775807", // long.MaxValue
            ["output_tokens"] = "1",
            ["cache_read_input_tokens"] = "0",
            ["cache_creation_input_tokens"] = "0",
        };
        var overflowUsage = "{" + string.Join(",", overflowFields.Select(kv => $"\"{kv.Key}\":{kv.Value}")) + "}";

        var counted = store.TryIngest(BuildRecord(messageId: "msg-overflow", usageJson: overflowUsage), "fallback");

        Assert.False(counted);
        Assert.Equal(1, store.MalformedRecordCount);
        Assert.Equal(0, store.AllTotals.Messages);

        // The overflowing record must not have claimed its id - a corrected retry with the same id
        // (valid, non-overflowing token counts) must still be able to count.
        var countedAfterFix = store.TryIngest(BuildRecord(messageId: "msg-overflow"), "fallback");

        Assert.True(countedAfterFix);
        Assert.Equal(1, store.AllTotals.Messages);
    }

    [Fact]
    public void TryIngest_GoodRecord_StillDeduplicatesById()
    {
        var store = CreateStore();

        Assert.True(store.TryIngest(BuildRecord(messageId: "msg-9"), "fallback"));
        Assert.False(store.TryIngest(BuildRecord(messageId: "msg-9"), "fallback"));
        Assert.Equal(1, store.AllTotals.Messages);
    }

    [Fact]
    public void TryIngest_DeduplicatesByRequestIdWhenMessageIdIsAbsent()
    {
        var store = CreateStore();

        Assert.True(store.TryIngest(BuildRecord(messageId: null, requestId: "req-1"), "fallback"));
        Assert.False(store.TryIngest(BuildRecord(messageId: null, requestId: "req-1"), "fallback"));
        Assert.Equal(1, store.AllTotals.Messages);
    }

    [Fact]
    public void TryIngest_NoIdOrRequestId_NeverDeduplicates()
    {
        var store = CreateStore();

        Assert.True(store.TryIngest(BuildRecord(messageId: null), "fallback"));
        Assert.True(store.TryIngest(BuildRecord(messageId: null), "fallback"));
        Assert.Equal(2, store.AllTotals.Messages);
    }

    [Fact]
    public void TryIngest_BadTimestamp_IsSkippedWithoutClaimingTheId()
    {
        var store = CreateStore();

        var counted = store.TryIngest(BuildRecord(messageId: "msg-3", timestamp: "yesterday"), "fallback");

        Assert.False(counted);
        Assert.Equal(1, store.MalformedRecordCount);
        Assert.True(store.TryIngest(BuildRecord(messageId: "msg-3"), "fallback"));
    }

    [Fact]
    public void TryIngest_MissingTimestamp_IsSkippedWithoutClaimingTheId()
    {
        var store = CreateStore();

        var counted = store.TryIngest(BuildRecord(messageId: "msg-4", includeTimestampField: false), "fallback");

        Assert.False(counted);
        Assert.True(store.TryIngest(BuildRecord(messageId: "msg-4"), "fallback"));
    }

    [Fact]
    public void TryIngest_NegativeTokenCount_IsTreatedAsMalformed()
    {
        var store = CreateStore();

        var counted = store.TryIngest(UsageOverride("cache_read_input_tokens", "-5"), "fallback");

        Assert.False(counted);
        Assert.Equal(1, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_NonAssistantType_IsNotCountedAsMalformed()
    {
        var store = CreateStore();

        Assert.False(store.TryIngest(BuildRecord(type: "user"), "fallback"));
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_MessageFieldIsNotAnObject_IsNotCountedAsMalformed()
    {
        var store = CreateStore();
        var record = JsonDocument.Parse(
            """{"type":"assistant","timestamp":"2026-09-14T10:00:00Z","message":"text"}""").RootElement;

        Assert.False(store.TryIngest(record, "fallback"));
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_UsageFieldIsNotAnObject_IsNotCountedAsMalformed()
    {
        var store = CreateStore();
        var record = JsonDocument.Parse(
            """{"type":"assistant","timestamp":"2026-09-14T10:00:00Z","message":{"id":"x","model":"claude-opus-4","usage":"not-an-object"}}""")
            .RootElement;

        Assert.False(store.TryIngest(record, "fallback"));
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_RootIsNotAnObject_ReturnsFalse()
    {
        var store = CreateStore();
        var record = JsonDocument.Parse("[1,2,3]").RootElement;

        Assert.False(store.TryIngest(record, "fallback"));
    }

    [Fact]
    public void TryIngest_EphemeralCacheSplit_IsValidatedToo()
    {
        var store = CreateStore();
        const string usage =
            """{"input_tokens":100,"output_tokens":200,"cache_read_input_tokens":300,"cache_creation":{"ephemeral_5m_input_tokens":10,"ephemeral_1h_input_tokens":"bad"}}""";

        var counted = store.TryIngest(BuildRecord(usageJson: usage), "fallback");

        Assert.False(counted);
        Assert.Equal(1, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_EphemeralCacheSplit_SumsBothWriteTypesIntoCacheWriteTokens()
    {
        var store = CreateStore();
        const string usage =
            """{"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":10,"ephemeral_1h_input_tokens":20}}""";

        Assert.True(store.TryIngest(BuildRecord(usageJson: usage), "fallback"));
        Assert.Equal(30, store.AllTotals.CacheWriteTokens);
    }

    [Fact]
    public void TryIngest_NoCacheCreationObject_TreatsAggregateFieldAsFiveMinuteWrite()
    {
        var store = CreateStore();
        const string usage =
            """{"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":15}""";

        Assert.True(store.TryIngest(BuildRecord(usageJson: usage), "fallback"));
        Assert.Equal(15, store.AllTotals.CacheWriteTokens);
    }

    [Fact]
    public void TryIngest_CwdAbsent_UsesFallbackProjectForTheProjectBreakdown()
    {
        var store = CreateStore();
        store.TryIngest(BuildRecord(cwd: null), "fallback-project");

        var breakdown = store.Breakdown("project", UsageMetric.TotalTokens, 7);

        Assert.Contains(breakdown, entry => entry.Label == "fallback-project");
    }

    [Fact]
    public void TryIngest_CwdPresent_UsesItsLastPathSegmentAsTheProject()
    {
        var store = CreateStore();
        store.TryIngest(BuildRecord(cwd: "/work/my-project"), "fallback-project");

        var breakdown = store.Breakdown("project", UsageMetric.TotalTokens, 7);

        Assert.Contains(breakdown, entry => entry.Label == "my-project");
    }

    [Fact]
    public void TryIngest_UnknownModel_IsGroupedUnderItsOwnIdInTheModelBreakdown()
    {
        var store = CreateStore();
        store.TryIngest(BuildRecord(model: "gpt-5"), "fallback");

        var breakdown = store.Breakdown("model", UsageMetric.TotalTokens, 7);

        Assert.Contains(breakdown, entry => entry.Label == "gpt-5");
    }
}
