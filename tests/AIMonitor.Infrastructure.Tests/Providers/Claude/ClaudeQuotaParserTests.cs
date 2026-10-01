using System.Text;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

[Trait("Category", "Contract")]
public class ClaudeQuotaParserTests
{
    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Claude", fileName);

    private static string ReadFixture(string fileName) => File.ReadAllText(FixturePath(fileName));

    private static IReadOnlyList<Meter> ParseFixture(string fileName) => ClaudeQuotaParser.Parse(ReadFixture(fileName));

    [Fact]
    public void Parse_CurrentShape_MapsKnownKindsToFixedTitleAndSubtitle()
    {
        var meters = ParseFixture("current-shape.json");

        var byKind = meters.ToDictionary(m => m.Kind);
        Assert.Equal(("Session", "5-hour window"), (byKind["session"].Title, byKind["session"].Subtitle));
        Assert.Equal(("Weekly", "All models"), (byKind["weekly_all"].Title, byKind["weekly_all"].Subtitle));
        Assert.Equal(("Weekly", "Opus only"), (byKind["weekly_opus"].Title, byKind["weekly_opus"].Subtitle));
        Assert.Equal(("Weekly", "Sonnet only"), (byKind["weekly_sonnet"].Title, byKind["weekly_sonnet"].Subtitle));
        Assert.Equal(("Weekly", "API apps"), (byKind["weekly_oauth_apps"].Title, byKind["weekly_oauth_apps"].Subtitle));
    }

    [Fact]
    public void Parse_CurrentShape_PreservesGroupWhenPresentAndDefaultsToKindOtherwise()
    {
        var meters = ParseFixture("current-shape.json");
        var byKind = meters.ToDictionary(m => m.Kind);

        // The fixture sets an explicit "group" distinct from "kind" to prove it round-trips.
        Assert.Equal("primary-session", byKind["session"].Group);
        // weekly_all has no "group" field in the fixture, so it must default to its own kind.
        Assert.Equal("weekly_all", byKind["weekly_all"].Group);
    }

    [Fact]
    public void Parse_CurrentShape_PreservesPercentAndSeverityAndResetTimestamp()
    {
        var meters = ParseFixture("current-shape.json");
        var session = meters.Single(m => m.Kind == "session");

        Assert.Equal(42.5, session.Percent);
        Assert.Equal(Severity.Normal, session.ServerSeverity);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), session.ResetsAt);
    }

    [Fact]
    public void Parse_CurrentShape_MissingResetsAtBecomesNull()
    {
        var meters = ParseFixture("current-shape.json");
        var opus = meters.Single(m => m.Kind == "weekly_opus");

        Assert.Null(opus.ResetsAt);
    }

    [Fact]
    public void Parse_LegacyOnlyShape_ParsesAllFiveBlocksInSemanticOrder()
    {
        var meters = ParseFixture("legacy-only.json");

        // Rank ties among weekly windows break on subtitle, case-insensitive: "API apps" < "Opus
        // only" < "Sonnet only" — not the declaration order of the legacy block names.
        Assert.Equal(
            new[] { "session", "weekly_all", "weekly_oauth_apps", "weekly_opus", "weekly_sonnet" },
            meters.Select(m => m.Kind));
        Assert.Equal(
            new[]
            {
                ("Session", "5-hour window"),
                ("Weekly", "All models"),
                ("Weekly", "API apps"),
                ("Weekly", "Opus only"),
                ("Weekly", "Sonnet only"),
            },
            meters.Select(m => (m.Title, m.Subtitle)));
    }

    [Fact]
    public void Parse_LegacyOnlyShape_ReadsUtilizationAndLockedReason()
    {
        var meters = ParseFixture("legacy-only.json");
        var sonnet = meters.Single(m => m.Kind == "weekly_sonnet");

        Assert.Equal(1.5, sonnet.Percent);
        Assert.Equal("example-lock", sonnet.LockedReason);
    }

    [Fact]
    public void Parse_LegacyBlockOverridesCurrentPercentAndCarriesLockedReason()
    {
        var meters = ParseFixture("legacy-precision-override.json");
        var session = meters.Single(m => m.Kind == "session");

        Assert.Equal(41.75, session.Percent);
        Assert.Equal("manual-hold-example", session.LockedReason);
    }

    [Fact]
    public void Parse_ScopedEntries_UseModelThenSurfaceThenFallbackForTitleAndKey()
    {
        var meters = ParseFixture("scoped-variants.json");

        Assert.Contains(meters, m => m.Subtitle == "Example Model only" && m.Key == "weekly_scoped:model:Example Model");
        Assert.Contains(meters, m => m.Subtitle == "Example Surface only" && m.Key == "weekly_scoped:surface:Example Surface");
        Assert.Contains(meters, m => m.Subtitle == "Scoped only" && m.Key == "weekly_scoped:fallback:Scoped");
        Assert.All(meters, m => Assert.Equal("Weekly", m.Title));
    }

    [Fact]
    public void Parse_ScopedEntries_ModelAndSurfaceWithSameDisplayName_ProduceDistinctKeys()
    {
        var meters = ParseFixture("scoped-model-surface-same-name.json");

        Assert.Equal(2, meters.Count);
        Assert.Contains(meters, m => m.Key == "weekly_scoped:model:Ambiguous Name");
        Assert.Contains(meters, m => m.Key == "weekly_scoped:surface:Ambiguous Name");
    }

    [Fact]
    public void Parse_ScopedEntries_SameTypeSameDisplayName_IsStillRejectedAsDuplicate()
    {
        var json = ReadFixture("duplicate-identity.json");

        Assert.Throws<ClaudeQuotaParseException>(() => ClaudeQuotaParser.Parse(json));
    }

    [Fact]
    public void Parse_UnknownKinds_AreRetainedAndWeeklyPrefixedGroupsUnderWeeklyTitle()
    {
        var meters = ParseFixture("unknown-kinds.json");

        Assert.Equal(4, meters.Count);
        var other = meters.Single(m => m.Kind == "some_new_window");
        Assert.Equal("Some new window", other.Title);
        Assert.Equal("Some new window", other.Subtitle);

        var weeklyUnknown = meters.Single(m => m.Kind == "weekly_new_window");
        Assert.Equal("Weekly", weeklyUnknown.Title);
        Assert.Equal("Weekly new window", weeklyUnknown.Subtitle);
    }

    [Fact]
    public void Parse_ReadingOrder_IsSemanticNotPercentageOrInputOrder()
    {
        var meters = ParseFixture("unknown-kinds.json");

        // "weekly_new_window" still starts with "weekly", so it ranks with the other weekly
        // windows (rank 2) ahead of "some_new_window", which has no recognised prefix (rank 3).
        Assert.Equal(
            new[] { "session", "weekly_all", "weekly_new_window", "some_new_window" },
            meters.Select(m => m.Kind));
    }

    [Fact]
    public void Parse_SkipsNonObjectMembersInsideLimitsArray()
    {
        var meters = ParseFixture("malformed-non-object-member.json");

        var meter = Assert.Single(meters);
        Assert.Equal("session", meter.Kind);
        Assert.Equal(7.0, meter.Percent);
    }

    [Fact]
    public void Parse_Timestamps_AcceptEmptyGarbageNullNaiveAndOffsetForms()
    {
        var meters = ParseFixture("malformed-timestamps.json");
        var byKind = meters.ToDictionary(m => m.Kind);

        Assert.Null(byKind["session"].ResetsAt); // empty string
        Assert.Null(byKind["weekly_all"].ResetsAt); // garbage text
        Assert.Null(byKind["weekly_opus"].ResetsAt); // explicit null
        var naive = byKind["weekly_sonnet"].ResetsAt!.Value;
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 12, 30, 0, TimeSpan.Zero), naive); // naive value treated as UTC
        Assert.Equal(TimeSpan.Zero, naive.Offset);

        var offset = byKind["weekly_oauth_apps"].ResetsAt!.Value;
        // "2026-01-05T12:30:00+02:00" is the same instant as "2026-01-05T10:30:00Z"; the parser
        // normalizes every valid timestamp to offset zero rather than keeping the original offset.
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 10, 30, 0, TimeSpan.Zero), offset);
        Assert.Equal(TimeSpan.Zero, offset.Offset);
    }

    [Fact]
    public void Parse_SeverityLabels_MapThroughDomainVocabulary()
    {
        var meters = ParseFixture("severity-mapping.json");
        var byKind = meters.ToDictionary(m => m.Kind);

        Assert.Equal(Severity.High, byKind["session"].ServerSeverity);
        Assert.Equal(Severity.VeryHigh, byKind["weekly_all"].ServerSeverity);
        Assert.Equal(Severity.Critical, byKind["weekly_opus"].ServerSeverity);
        Assert.Equal(Severity.Normal, byKind["weekly_sonnet"].ServerSeverity); // absent
        Assert.Equal(Severity.Normal, byKind["weekly_oauth_apps"].ServerSeverity); // unrecognised label
    }

    [Fact]
    public void Parse_PercentAbove100_IsPreservedRatherThanClamped()
    {
        var meters = ParseFixture("percent-above-100.json");

        Assert.Equal(142.5, meters.Single().Percent);
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsClaudeQuotaParseExceptionWithoutLeakingContent()
    {
        var invalidJson = ReadFixture("invalid-json.txt");

        var ex = Assert.Throws<ClaudeQuotaParseException>(() => ClaudeQuotaParser.Parse(invalidJson));
        Assert.DoesNotContain(invalidJson, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NonObjectRoot_ThrowsClaudeQuotaParseException()
    {
        var json = ReadFixture("invalid-root-array.json");

        Assert.Throws<ClaudeQuotaParseException>(() => ClaudeQuotaParser.Parse(json));
    }

    [Fact]
    public void Parse_NonNumericPercent_ThrowsClaudeQuotaParseException()
    {
        var json = ReadFixture("invalid-percent-type.json");

        Assert.Throws<ClaudeQuotaParseException>(() => ClaudeQuotaParser.Parse(json));
    }

    [Fact]
    public void Parse_NegativePercent_DoesNotBypassDomainValidation()
    {
        var json = ReadFixture("invalid-percent-negative.json");

        Assert.Throws<ClaudeQuotaParseException>(() => ClaudeQuotaParser.Parse(json));
    }

    [Fact]
    public void Parse_InfinitePercent_DoesNotBypassDomainValidation()
    {
        var json = ReadFixture("invalid-percent-infinite.json");

        Assert.Throws<ClaudeQuotaParseException>(() => ClaudeQuotaParser.Parse(json));
    }

    [Fact]
    public void Parse_DuplicateMeterIdentity_ThrowsClaudeQuotaParseExceptionRatherThanDroppingOrOverwriting()
    {
        var json = ReadFixture("duplicate-identity.json");

        Assert.Throws<ClaudeQuotaParseException>(() => ClaudeQuotaParser.Parse(json));
    }

    [Fact]
    public void Parse_AcceptsUtf8BytesAndStreamProducingTheSameResultAsString()
    {
        var json = ReadFixture("current-shape.json");
        var bytes = Encoding.UTF8.GetBytes(json);

        var fromString = ClaudeQuotaParser.Parse(json);
        var fromBytes = ClaudeQuotaParser.Parse(new ReadOnlyMemory<byte>(bytes));
        using var stream = new MemoryStream(bytes);
        var fromStream = ClaudeQuotaParser.Parse(stream);

        Assert.Equal(fromString.Select(m => m.Key), fromBytes.Select(m => m.Key));
        Assert.Equal(fromString.Select(m => m.Key), fromStream.Select(m => m.Key));
    }

    [Fact]
    public void Parse_LimitsAbsent_FallsBackToLegacyBlocks()
    {
        var meters = ParseFixture("legacy-only.json");

        Assert.Equal(5, meters.Count);
    }

    [Fact]
    public void Parse_ReturnedCollection_MutationThroughIListThrowsAndLeavesStateUnchanged()
    {
        var meters = ParseFixture("current-shape.json");
        var originalKeys = meters.Select(m => m.Key).ToArray();

        var mutable = Assert.IsAssignableFrom<IList<Meter>>(meters);

        Assert.Throws<NotSupportedException>(() => mutable.Add(meters[0]));
        Assert.Throws<NotSupportedException>(() => mutable.Insert(0, meters[0]));
        Assert.Throws<NotSupportedException>(() => mutable.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => mutable[0] = meters[0]);
        Assert.Throws<NotSupportedException>(() => mutable.Clear());

        Assert.Equal(originalKeys, meters.Select(m => m.Key));
    }

    [Fact]
    public void Parse_InvalidJson_ExceptionStringNeverContainsSentinelPayloadFragment()
    {
        var invalidJson = ReadFixture("invalid-json-sentinel.txt");

        var ex = Assert.Throws<ClaudeQuotaParseException>(() => ClaudeQuotaParser.Parse(invalidJson));

        Assert.DoesNotContain("SENTINEL_PAYLOAD_UNSAFE_9f3c", ex.ToString(), StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Parse_InvalidDomainValue_ExceptionStringNeverContainsSentinelKindFragment()
    {
        var json = ReadFixture("invalid-domain-value-sentinel.json");

        var ex = Assert.Throws<ClaudeQuotaParseException>(() => ClaudeQuotaParser.Parse(json));

        Assert.DoesNotContain("SENTINEL_KIND_UNSAFE_9f3c", ex.ToString(), StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Parse_DuplicateIdentity_ExceptionStringNeverContainsSentinelScopeDisplayFragment()
    {
        var json = ReadFixture("duplicate-identity-sentinel.json");

        var ex = Assert.Throws<ClaudeQuotaParseException>(() => ClaudeQuotaParser.Parse(json));

        Assert.DoesNotContain("SENTINEL_DISPLAY_UNSAFE_9f3c", ex.ToString(), StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Parse_NonFiniteOrOverflowingPercent_AlwaysBecomesClaudeQuotaParseExceptionWithSafeText()
    {
        var json = ReadFixture("invalid-percent-infinite.json");

        var ex = Assert.Throws<ClaudeQuotaParseException>(() => ClaudeQuotaParser.Parse(json));

        Assert.Equal("Claude quota entry has an invalid value.", ex.Message);
        Assert.Null(ex.InnerException);
    }
}
