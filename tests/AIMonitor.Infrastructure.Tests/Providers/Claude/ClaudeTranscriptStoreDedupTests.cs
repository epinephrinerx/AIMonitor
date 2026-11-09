using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Covers SPEC R-014 "P2b detailed" §P2b.3 (record filter), §P2b.4 (dedup keys) and §P2b.5 (survivor and
/// replacement) against <see cref="ClaudeTranscriptStore.TryIngest"/> and <see cref="ClaudeTranscriptStore
/// .RefreshAsync"/>, plus the decisions that override them: B1 (session aliases), B3
/// (<c>&lt;synthetic&gt;</c> is counted), B4 (only the day subtraction is skipped for a pruned bucket),
/// B5 (the <c>version</c> filter), B6 (no <c>-fast</c> label), B7 (seen-set eviction), B8 (exact-key match
/// beats a replay-key match), B10 (the cost oracles), B12 (matching by key content, no memory test), B13
/// (a replacement is never malformed and the day subtraction floors at zero), B19 (a replacement is not
/// counted as "added") and OP4 (cost is computed and subtracted per record); B20 fixes the "2.1" case of
/// the B5 filter.
/// </summary>
/// <remarks>
/// <para>
/// Every expected number cites the spec or shows the hand calculation. Rates come from the P2a table
/// (USD per million tokens): claude-opus-5 5 / 25 / 0.50 with a 5m cache write at input x 1.25 = 6.25,
/// claude-sonnet-5 2 / 10 / 0.20, claude-haiku-5-5 0.10 / 0.50 / 0.01 rising to 0.50 / 2.50 / 0.05 when
/// that one record's prompt - input + cache creation + cache read - exceeds 100,000 (A8, OP4).
/// </para>
/// <para>
/// Where an incoming record <em>replaces</em> a kept one, B19 settled the question this file first raised
/// as open: "a record that replaces a kept one is not 'added': <see cref="TryIngest"/> returns
/// <see langword="false"/> for it and <see cref="RefreshAsync"/>'s count excludes it (its counters still
/// change)". The replacement tests therefore assert the return value and the refresh count next to the
/// counters - see the B19 test, the B10 oracles and the RefreshAsync tests. A record that loses a
/// survivor comparison without replacing is likewise not added, as the exact-duplicate tests have always
/// asserted.
/// </para>
/// </remarks>
[Trait("Category", "Contract")]
public sealed class ClaudeTranscriptStoreDedupTests : IDisposable
{
    /// <summary>Fixed "now" and UTC, so a record's local calendar day is its UTC day: every record dated
    /// <see cref="TranscriptRecordBuilder.DefaultWhen"/> (2026-09-14) is 6 days back, inside
    /// <c>WindowTotals(7)</c> and outside <c>WindowTotals(6)</c>.</summary>
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    private const string Opus = "claude-opus-5";
    private const string Sonnet = "claude-sonnet-5";
    private const string Haiku55 = "claude-haiku-5-5";

    private static readonly string[] UnusedRoot = ["unused-root"];

    private readonly string _base =
        Path.Combine(Path.GetTempPath(), "aimonitor-claude-dedup-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _root;
    private readonly string _sessionPath;

    public ClaudeTranscriptStoreDedupTests()
    {
        Directory.CreateDirectory(_base);
        _root = Path.Combine(_base, "projects");
        Directory.CreateDirectory(Path.Combine(_root, "C--work-project"));
        _sessionPath = Path.Combine(_root, "C--work-project", "session.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    private static ClaudeTranscriptStore CreateIngestStore() =>
        new(UnusedRoot, new FakeClock(FixedNow), TimeZoneInfo.Utc);

    /// <summary>The same store with the seen-set capacity that B7 turns into an internal ctor seam. The
    /// <c>openFile</c> seam gets a stream these tests never read, because they only call
    /// <see cref="TryIngest"/>.</summary>
    private static ClaudeTranscriptStore CreateIngestStore(int seenCapacity) =>
        new(UnusedRoot, new FakeClock(FixedNow), TimeZoneInfo.Utc, _ => Stream.Null, seenCapacity);

    private ClaudeTranscriptStore CreateFileStore(DateTimeOffset? now = null) =>
        new(new[] { _root }, new FakeClock(now ?? FixedNow), TimeZoneInfo.Utc);

    private void WriteLines(params string[] lines) => File.WriteAllText(_sessionPath, string.Join("\n", lines) + "\n");

    private void AppendLines(params string[] lines) => File.AppendAllText(_sessionPath, string.Join("\n", lines) + "\n");

    // -- §P2b.3 record filter: skipped, never malformed --------------------------------------------

    [Theory]
    [InlineData("sessionId")]
    [InlineData("requestId")]
    [InlineData("messageId")]
    [InlineData("model")]
    public void TryIngest_EmptyStringKeyField_IsSkippedWithoutBeingMalformed(string field)
    {
        // P2b.3: "skip a record if ... any of sessionId, requestId, message.id, message.model is present
        // as an empty string", and the section heading is explicit that this is "not counted as malformed".
        var store = CreateIngestStore();
        var builder = TranscriptRecordBuilder.WithTokens(100, 0);
        var record = field switch
        {
            "sessionId" => builder.SessionId(string.Empty),
            "requestId" => builder.RequestId(string.Empty),
            "messageId" => builder.Id(string.Empty),
            _ => builder.Model(string.Empty),
        };

        var counted = store.TryIngest(record.Build(), "fallback");

        Assert.False(counted);
        Assert.Equal(0, store.AllTotals.Messages);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_FilteredRecord_DoesNotClaimItsKey_SoACorrectedCopyStillCounts()
    {
        // The filter runs "before any key is claimed" (P2b.3 heading), so a skipped record must not strand
        // the key its corrected copy needs - the same guarantee P2a gives for a malformed record.
        var store = CreateIngestStore();
        store.TryIngest(TranscriptRecordBuilder.WithTokens(100, 0, id: "msg-keep").SessionId(string.Empty).Build(), "fallback");

        var counted = store.TryIngest(TranscriptRecordBuilder.WithTokens(100, 0, id: "msg-keep").SessionId("s1").Build(), "fallback");

        Assert.True(counted);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_MissingModel_IsStillCounted_PricedAsUnknown()
    {
        // P2b.3: "A missing model stays as today (priced as unknown)". Tokens 100 + 50 = 150, all of it
        // unpriced, and no money claimed.
        var store = CreateIngestStore();

        var counted = store.TryIngest(TranscriptRecordBuilder.WithTokens(100, 50).Model(null).Build(), "fallback");

        Assert.True(counted);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(0m, store.AllTotals.CostUsd);
        Assert.Equal(150, store.AllTotals.UnpricedTokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2.1.5")]
    [InlineData("10.0.1")]
    public void TryIngest_VersionAbsentOrSemverPrefix_IsCounted(string? version)
    {
        // B5 filters "a record whose version is present and is not a semver prefix", so a real prefix is
        // untouched; null here means the field is absent, which B5 also leaves alone.
        var store = CreateIngestStore();

        var counted = store.TryIngest(TranscriptRecordBuilder.WithTokens(100, 0, id: "msg-v").Version(version).Build(), "fallback");

        Assert.True(counted);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("beta-2026-09")]
    [InlineData("v2.1.5")]
    [InlineData("2.1")]
    public void TryIngest_VersionNotSemverPrefix_IsSkippedWithoutBeingMalformed(string version)
    {
        var store = CreateIngestStore();

        var counted = store.TryIngest(TranscriptRecordBuilder.WithTokens(100, 0, id: "msg-bad-v").Version(version).Build(), "fallback");

        // B5: "skipped, not counted as malformed". The "2.1" row is B20: a two-component version is not a
        // semver prefix - ccusage needs <digits>.<digits>.<digit> - so the record is skipped.
        Assert.False(counted);
        Assert.Equal(0, store.AllTotals.Messages);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_FilteredOutVersion_DoesNotClaimItsKey_SoAVersionedCopyStillCounts()
    {
        var store = CreateIngestStore();
        store.TryIngest(TranscriptRecordBuilder.WithTokens(100, 0, id: "msg-rec").Version("latest").Build(), "fallback");

        var counted = store.TryIngest(TranscriptRecordBuilder.WithTokens(100, 0, id: "msg-rec").Version("2.1.5").Build(), "fallback");

        Assert.True(counted);
        Assert.Equal(1, store.AllTotals.Messages);
    }

    // -- B3: <synthetic> is counted, free, under its own label --------------------------------------

    [Fact]
    public void TryIngest_SyntheticModel_IsCountedUnderTheSyntheticLabelAtZeroCost()
    {
        var store = CreateIngestStore();

        var counted = store.TryIngest(
            TranscriptRecordBuilder.WithTokens(100, 50, id: "msg-syn").Model("<synthetic>").Build(), "fallback");

        // B3 replaces P2b.3's skip: "ccusage counts their tokens: model label (synthetic), cost 0, not
        // added to unpriced or estimated tokens".
        Assert.True(counted);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(150, store.AllTotals.TotalTokens);
        Assert.Equal(0m, store.AllTotals.CostUsd);
        Assert.Equal(0, store.AllTotals.UnpricedTokens);
        Assert.Equal(0, store.AllTotals.EstimatedTokens);

        var models = store.Breakdown("model", UsageMetric.TotalTokens, 7);
        Assert.Equal(["(synthetic)"], models.Select(entry => entry.Label));
        Assert.Equal(150.0, Assert.Single(models).Value);
    }

    [Fact]
    public void TryIngest_SyntheticModel_IsDeduplicatedLikeAnyOtherRecord()
    {
        var store = CreateIngestStore();
        var record = TranscriptRecordBuilder
            .WithTokens(100, 0, id: "msg-syn2")
            .Model("<synthetic>")
            .RequestId("req-s")
            .Build();

        Assert.True(store.TryIngest(record, "fallback"));
        store.TryIngest(record, "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(100, store.AllTotals.InputTokens);
    }

    // -- B6: no -fast suffix ------------------------------------------------------------------------

    [Fact]
    public void TryIngest_SpeedFast_KeepsThePlainModelLabelAndThePlainRates()
    {
        // B6 (accepted divergence): ccusage renames the model when usage.speed == "fast"; P2b does not,
        // because A10 compares daily token sums only. So the record folds under the plain label at the
        // plain rate: 100 input x $2 / 1,000,000 = $0.0002.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("msg-fast", Sonnet).Tokens(100, 0).Speed("fast").Build(), "fallback"));

        Assert.Equal(0.0002m, store.AllTotals.CostUsd);
        Assert.Equal(["Sonnet 5"], store.Breakdown("model", UsageMetric.TotalTokens, 7).Select(entry => entry.Label));
    }

    // -- §P2b.4 exact keys --------------------------------------------------------------------------

    [Fact]
    public void TryIngest_NoMessageId_IsNeverDeduplicated_EvenWithAStableRequestId()
    {
        // P2b.4: "No id -> no dedup (always counted; offsets still prevent re-reading)". A requestId
        // cannot carry a key by itself. The offset that keeps a real transcript from being re-read is
        // covered through RefreshAsync in ClaudeTranscriptStoreFileTests.
        var store = CreateIngestStore();
        var record = TranscriptRecordBuilder.WithTokens(100, 0).Id(null).RequestId("req-1").Build();

        Assert.True(store.TryIngest(record, "fallback"));
        Assert.True(store.TryIngest(record, "fallback"));

        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(200, store.AllTotals.InputTokens);
    }

    [Fact]
    public void TryIngest_MessageIdPairedWithRequestId_MatchesOnTheExactKey()
    {
        var store = CreateIngestStore();
        var record = TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-1").RequestId("req-1").Build();

        Assert.True(store.TryIngest(record, "fallback"));
        store.TryIngest(record, "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
    }

    [Fact]
    public void TryIngest_SameMessageIdDifferentRequestId_AreBothCounted()
    {
        // The exact key is id + "\n" + requestId (P2b.4), so two calls under one message id are two
        // records - and the second is not a survivor of the first.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-1").RequestId("req-1").Build(), "fallback"));
        Assert.True(store.TryIngest(TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-1").RequestId("req-2").Build(), "fallback"));

        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(200, store.AllTotals.InputTokens);
    }

    [Theory]
    [InlineData("msg_")]
    [InlineData("resp_")]
    public void TryIngest_ServerStyleIdWithoutRequestId_CollapsesAcrossTimestampsInOneSession(string prefix)
    {
        // P2b.4: with no requestId, an id starting with msg_/resp_ keys as id + "\n\n" + sid - so a
        // different timestamp cannot tell the two copies apart (the transcript re-sends the same message).
        var store = CreateIngestStore();
        var id = prefix + "001";

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.WithTokens(100, 0, id: id).SessionId("s1").At("2026-09-14T10:00:00Z").Build(), "fallback"));
        store.TryIngest(
            TranscriptRecordBuilder.WithTokens(100, 0, id: id).SessionId("s1").At("2026-09-14T10:00:05Z").Build(), "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
    }

    [Theory]
    [InlineData("msg_")]
    [InlineData("resp_")]
    public void TryIngest_ServerStyleIdWithoutRequestId_StillSeparatesSessions(string prefix)
    {
        // The key is id + "\n\n" + sid, not the id alone: the P2b outline's "collapse on message.id" is
        // made precise by P2b.4's formula, which keeps the session in the key.
        var store = CreateIngestStore();
        var id = prefix + "001";

        Assert.True(store.TryIngest(TranscriptRecordBuilder.WithTokens(100, 0, id: id).SessionId("s1").Build(), "fallback"));
        Assert.True(store.TryIngest(TranscriptRecordBuilder.WithTokens(100, 0, id: id).SessionId("s2").Build(), "fallback"));

        Assert.Equal(2, store.AllTotals.Messages);
    }

    [Fact]
    public void TryIngest_OtherIdWithoutRequestId_NeedsTheTimestampToMatch()
    {
        // P2b.4's last branch: id + "\n\n" + sid + "\n" + ts. Repeating one timestamp is a duplicate; a
        // second timestamp is a second record.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.WithTokens(100, 0, id: "local-1").SessionId("s1").At("2026-09-14T10:00:00Z").Build(), "fallback"));
        store.TryIngest(
            TranscriptRecordBuilder.WithTokens(100, 0, id: "local-1").SessionId("s1").At("2026-09-14T10:00:00Z").Build(), "fallback");
        Assert.Equal(1, store.AllTotals.Messages);

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.WithTokens(100, 0, id: "local-1").SessionId("s1").At("2026-09-14T10:00:05Z").Build(), "fallback"));
        Assert.Equal(2, store.AllTotals.Messages);
    }

    [Fact]
    public void TryIngest_SessionIdAbsent_TwoIdenticalRecordsStillShareOneKey()
    {
        // P2b.4: "sid = sessionId (else the file name without extension)". Through TryIngest there is no
        // file, so the fallback is whatever the store holds for the call - constant across two calls, and
        // the invariant the spec states is that identical records get one key. The file-derived spelling
        // is B2's rule, covered in ClaudeTranscriptStoreRootsTests.
        var store = CreateIngestStore();
        var record = TranscriptRecordBuilder.WithTokens(100, 0, id: "msg_nosid").SessionId(null).Build();

        Assert.True(store.TryIngest(record, "fallback"));
        store.TryIngest(record, "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
    }

    // -- §P2b.4 replay key, B8 precedence, B1 session aliases --------------------------------------

    [Fact]
    public void TryIngest_ReplayKey_DoesNotJoinTwoNonSidechainRecordsThatShareIdAndSession()
    {
        // P2b.4 grants the replay key (id + "\n" + sid) only "when either of the two is a sidechain
        // record", and B8 states the consequence: "Two non-sidechain records with the same replay key and
        // different exact keys both survive." Same id, same session, different requestId -> two exact
        // keys -> two records.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-1").SessionId("s1").RequestId("req-1").Sidechain(false).Build(), "fallback"));
        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-1").SessionId("s1").RequestId("req-2").Sidechain(false).Build(), "fallback"));

        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(200, store.AllTotals.InputTokens);
    }

    [Fact]
    public void TryIngest_ReplayKey_JoinsASidechainNewRecordToTheNonSidechainKeptRecord_WhichSurvives()
    {
        // Same id, same session, different requestId: only the replay rule can join them, and it applies
        // because the incoming record is a sidechain. P2b.5 branch 2 then keeps the non-sidechain survivor
        // - visible because the discarded record is 90x the size.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-1").SessionId("s1").RequestId("req-1").Sidechain(false).Build(), "fallback"));
        store.TryIngest(
            TranscriptRecordBuilder.WithTokens(9_000, 0, id: "gen-1").SessionId("s1").RequestId("req-2").Sidechain(true).Build(), "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(100, store.AllTotals.InputTokens);
    }

    [Fact]
    public void TryIngest_SessionAlias_TheSurvivorIsRegisteredUnderBothSessionsReplayKeys()
    {
        // B1: "when a match joins two records with different sid, the survivor is registered under the
        // replay key of both sids, so a later replay from either session finds it."
        var store = CreateIngestStore();

        // Records 1 and 2 share the exact key (id + "\n" + req-1) but sit in different sessions, and
        // record 2 is larger (200 > 100 total tokens) -> it replaces record 1 and takes over its keys.
        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-1").SessionId("s1").RequestId("req-1").Build(), "fallback"));
        store.TryIngest(
            TranscriptRecordBuilder.WithTokens(200, 0, id: "gen-1").SessionId("s2").RequestId("req-1").Build(), "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(200, store.AllTotals.InputTokens);

        // A replay from the REPLACED record's session (s1): its exact key (id + req-2) is new, and s1 is
        // no longer any kept record's own sid - only B1's alias can match it. Without the alias this would
        // count as a fresh record (Messages 2, Input 9,200).
        store.TryIngest(
            TranscriptRecordBuilder.WithTokens(9_000, 0, id: "gen-1").SessionId("s1").RequestId("req-2").Sidechain(true).Build(), "fallback");

        // And the survivor's own session (s2) still matches, so gaining the alias did not lose it.
        store.TryIngest(
            TranscriptRecordBuilder.WithTokens(9_000, 0, id: "gen-1").SessionId("s2").RequestId("req-3").Sidechain(true).Build(), "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(200, store.AllTotals.InputTokens);
    }

    [Fact]
    public void TryIngest_ExactKeyMatchWinsOverReplayKeyMatch_PointingAtDifferentKeptRecords()
    {
        // B8: "Exact-key match wins over replay-key match even when they point to different kept records
        // (ccusage order)." The data makes both matches live:
        //   kept 1: id gen-1, sid s1, req req-1, SIDECHAIN,     input 10
        //   kept 2: id gen-1, sid s2, req req-2, not sidechain, input 20   (kept 1 and kept 2 share no
        //           exact key and no replay key, so both survive on their own)
        //   new   : id gen-1, sid s1, req req-2, SIDECHAIN,     input 300
        //           exact key "gen-1 + req-2" = kept 2's; replay key "gen-1 + s1" = kept 1's, and that
        //           replay match is live because kept 1 and the new record are both sidechain.
        // The exact match must decide: the new record is a sidechain against kept 2's non-sidechain, so
        // P2b.5 branch 2 keeps kept 2 and discards the new record. Input stays 10 + 20 = 30. Had the
        // replay match been used, kept 1 would have lost to 300 > 10 tokens and Input would be 320.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.WithTokens(10, 0, id: "gen-1").SessionId("s1").RequestId("req-1").Sidechain(true).Build(), "fallback"));
        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.WithTokens(20, 0, id: "gen-1").SessionId("s2").RequestId("req-2").Sidechain(false).Build(), "fallback"));
        store.TryIngest(
            TranscriptRecordBuilder.WithTokens(300, 0, id: "gen-1").SessionId("s1").RequestId("req-2").Sidechain(true).Build(), "fallback");

        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(30, store.AllTotals.InputTokens);
    }

    // -- §P2b.5 survivor branches --------------------------------------------------------------------

    [Fact]
    public void TryIngest_SurvivorRule_NonSidechainNewRecordBeatsSidechainKeptRecord_RegardlessOfTokens()
    {
        // P2b.5 branch 1, decided before any token comparison: "kept is sidechain and new is not ->
        // replace". B10(c)'s shape: kept sidechain A (total 1,000) against new non-sidechain B (total 10)
        // -> B wins.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.WithTokens(1_000, 0, id: "gen-1").SessionId("s1").RequestId("req-1").Sidechain(true).Build(), "fallback"));
        store.TryIngest(
            TranscriptRecordBuilder.WithTokens(10, 0, id: "gen-1").SessionId("s2").RequestId("req-1").Sidechain(false).Build(), "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(10, store.AllTotals.InputTokens);
    }

    [Fact]
    public void TryIngest_SurvivorRule_LargerTotalTokensWins_InBothDirections()
    {
        // Branch 3, with the sidechain test not deciding (neither record is one):
        //   A = 100 input + 50 output + 20 cache read = 170 total tokens
        //   B = 60 total               -> A survives
        //   C = 180 total              -> C replaces A, and "Messages stays 1 per surviving record".
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1").Tokens(100, 50, cacheRead: 20).Build(), "fallback"));
        Assert.Equal(170, store.AllTotals.TotalTokens);

        store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1").Tokens(60, 0).Build(), "fallback");
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(170, store.AllTotals.TotalTokens);

        store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1").Tokens(180, 0).Build(), "fallback");
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(180, store.AllTotals.TotalTokens);
    }

    [Fact]
    public void TryIngest_SurvivorRule_EqualTokensAndEqualCost_KeepsTheExistingRecord()
    {
        // Branch 5: "equal -> keep the existing one". Both records are 100 input tokens of claude-opus-5,
        // so 100 x 5 / 1,000,000 = $0.0005 each - a tie on tokens and on cost - but they name different
        // projects, which is where the tie's outcome stays visible: the first project survives and the
        // second never enters the counters.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1")
                .Tokens(100, 0).Cwd(@"C:\work\alpha").Build(), "fallback"));
        store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1")
                .Tokens(100, 0).Cwd(@"C:\work\beta").Build(), "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(0.0005m, store.AllTotals.CostUsd);
        Assert.Equal(["alpha"], store.Breakdown("project", UsageMetric.TotalTokens, 7).Select(entry => entry.Label));
    }

    [Fact]
    public void TryIngest_SurvivorRule_EqualTotalTokens_LargerCostWins()
    {
        // Branch 4: "larger total tokens ... equal -> larger cost wins". Both records total 100 tokens but
        // price differently at claude-opus-5:
        //   A = 100 input  x 5  =   500/1e6 = $0.0005
        //   B = 100 output x 25 = 2,500/1e6 = $0.0025   -> B replaces A.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1").Tokens(100, 0).Build(), "fallback"));
        store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1").Tokens(0, 100).Build(), "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(0, store.AllTotals.InputTokens);
        Assert.Equal(100, store.AllTotals.OutputTokens);
        Assert.Equal(0.0025m, store.AllTotals.CostUsd);
        Assert.Equal(100.0, Assert.Single(store.Breakdown("model", UsageMetric.TotalTokens, 7)).Value);
    }

    // -- B19: a replacement is decided, not added ----------------------------------------------------

    [Fact]
    public void TryIngest_B19_ReplacingRecordReturnsFalse_WhileTheCountersStillChange()
    {
        // B19: "A record that replaces a kept one is not 'added': TryIngest returns false for it and
        // RefreshAsync's count excludes it (its counters still change)". Exercised across the survivor
        // branches that can reach a replacement - branch 1 (non-sidechain beats sidechain), branch 3
        // (larger total tokens) and branch 4 (equal tokens, larger cost) - plus branch 5's tie, where the
        // existing record is kept and nothing is added either.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-b19", Opus).SessionId("s1")
                .Tokens(1_000, 0).Sidechain(true).Build(), "fallback"));

        // Branch 1: 10 tokens beat the kept 1,000 because the kept record is a sidechain. Replaces -> false.
        Assert.False(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-b19", Opus).SessionId("s1")
                .Tokens(10, 0).Sidechain(false).Build(), "fallback"));
        Assert.Equal(10, store.AllTotals.InputTokens);

        // Branch 3: the larger non-sidechain record wins. Replaces -> false.
        Assert.False(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-b19", Opus).SessionId("s1")
                .Tokens(20, 0).Sidechain(false).Build(), "fallback"));
        Assert.Equal(20, store.AllTotals.InputTokens);

        // Branch 4: equal total tokens (20), larger cost (20 output x $25 beats 20 input x $5). Replaces.
        Assert.False(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-b19", Opus).SessionId("s1")
                .Tokens(0, 20).Sidechain(false).Build(), "fallback"));
        Assert.Equal(0, store.AllTotals.InputTokens);
        Assert.Equal(20, store.AllTotals.OutputTokens);

        // Branch 5: an exact tie keeps the existing record - no change, so this is not added either.
        Assert.False(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-b19", Opus).SessionId("s1")
                .Tokens(0, 20).Sidechain(false).Build(), "fallback"));

        // "Messages stays 1 per surviving record" (P2b.5): four survivor decisions on one key, one message.
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    // -- B10 oracles: the cost that survives a replacement -----------------------------------------

    [Fact]
    public void TryIngest_B10a_SonnetReplacement_LeavesTheNewRecordsCostAndCounters()
    {
        // B10(a) verbatim, at claude-sonnet-5's 2 / 10 / 0.20 (P2a table):
        //   A: input 100, output 50, cache read 20
        //      100x2 + 50x10 + 20x0.20 = 200 + 500 + 4 = 704      -> $0.000704
        //   B: same key, input 200
        //      200x2 + 50x10 + 20x0.20 = 400 + 500 + 4 = 904      -> $0.000904
        //      (the spec's corrected figure: "WO3 caught the earlier 1,104")
        // B totals 200 + 50 + 20 = 270 against A's 100 + 50 + 20 = 170, so branch 3 makes B the survivor.
        // "After B replaces A, day x model = B's counters, Messages = 1."
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Sonnet).SessionId("s1")
                .Tokens(100, 50, cacheRead: 20).Build(), "fallback"));
        Assert.Equal(0.000704m, store.AllTotals.CostUsd);

        // B19: B replaces A, so it is not "added" - the counters below move but the return says false.
        Assert.False(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Sonnet).SessionId("s1")
                .Tokens(200, 50, cacheRead: 20).Build(), "fallback"));

        Assert.Equal(0.000904m, store.AllTotals.CostUsd);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(200, store.AllTotals.InputTokens);
        Assert.Equal(50, store.AllTotals.OutputTokens);
        Assert.Equal(20, store.AllTotals.CacheReadTokens);

        // One day, one model: the window query reads the same day x model bucket the survivor owns.
        var day = store.WindowTotals(7);
        Assert.Equal(1, day.Messages);
        Assert.Equal(0.000904m, day.CostUsd);
        Assert.Equal(270, day.TotalTokens);

        var models = store.Breakdown("model", UsageMetric.TotalTokens, 7);
        Assert.Equal(["Sonnet 5"], models.Select(entry => entry.Label));
        Assert.Equal(270.0, Assert.Single(models).Value);

        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_B10b_HaikuHighTierSidechain_IsReplacedByLowTierNonSidechain_LeavingOnlyTheSmallCost()
    {
        // B10(b) verbatim, at claude-haiku-5-5's low tier 0.10 / 0.50 / 0.01 and high tier
        // 0.50 / 2.50 / 0.05 (P2a table, A8's per-record 100,000-token prompt test):
        //   A: input 60,000, output 1,000, cache read 50,000; prompt = 60,000 + 0 + 50,000 = 110,000
        //      > 100,000 -> high tier: 60,000x0.50 + 1,000x2.50 + 50,000x0.05 = 30,000 + 2,500 + 2,500
        //      = 35,000/1e6 -> $0.035. A is a sidechain record.
        //   B: same key, NOT sidechain, so branch 1 makes it the survivor despite 1,600 total tokens
        //      against A's 111,000. Prompt = 1,000 + 0 + 100 = 1,100 -> low tier: 1,000x0.10 + 500x0.50
        //      + 100x0.01 = 100 + 250 + 1 = 351/1e6 -> $0.000351.
        //   "after replacement AllTotals.CostUsd = $0.000351" holds only if A's $0.035 came back out
        //   exactly as it went in - OP4's per-record cost counter, which Reviewer1's MAJOR-1 case needs.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Haiku55).SessionId("s1")
                .Tokens(60_000, 1_000, cacheRead: 50_000).Sidechain(true).Build(), "fallback"));
        Assert.Equal(0.035m, store.AllTotals.CostUsd);

        // B19: A's replacement is not an addition (branch 1), even though every number below changes.
        Assert.False(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Haiku55).SessionId("s1")
                .Tokens(1_000, 500, cacheRead: 100).Sidechain(false).Build(), "fallback"));

        Assert.Equal(0.000351m, store.AllTotals.CostUsd);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(1_000, store.AllTotals.InputTokens);
        Assert.Equal(500, store.AllTotals.OutputTokens);
        Assert.Equal(100, store.AllTotals.CacheReadTokens);
        Assert.Equal(0, store.AllTotals.CacheWriteTokens);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_Op4_HaikuTierIsDecidedPerRecord_NotOnTheDayAggregate()
    {
        // OP4: "This forces cost to be computed per record at ingest, not per day/model aggregate". Two
        // distinct records, one day, one model, each with a 60,000-token prompt: each is under the
        // threshold, so each costs 60,000 x 0.10 / 1e6 = $0.006 -> $0.012 for the day. A day-level tier
        // (prompt 120,000) would have charged the high rate: 60,000 x 0.50 / 1e6 = $0.03 each -> $0.06.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(TranscriptRecordBuilder.WithTokens(60_000, 0, id: "gen-a", model: Haiku55).Build(), "fallback"));
        Assert.True(store.TryIngest(TranscriptRecordBuilder.WithTokens(60_000, 0, id: "gen-b", model: Haiku55).Build(), "fallback"));

        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(0.012m, store.AllTotals.CostUsd);
    }

    // -- §P2b.5 replacement subtraction --------------------------------------------------------------

    [Fact]
    public void TryIngest_ReplacementSubtractsTheWholeStoredContribution_FromEveryBucket()
    {
        // A stored contribution is the kept record's day, model label, project label and UsageCounters -
        // "tokens and cost" (P2b.5, OP4) - so a replacement moves every bucket it touched and leaves
        // nothing behind.
        //   A: 1,000x5 + 500x25 + 100x6.25 (5m write = input x 1.25) + 200x0.50
        //      = 5,000 + 12,500 + 625 + 100 = 18,225/1e6 -> $0.018225; total tokens 1,800; project alpha.
        //   B: 3,000x5 = 15,000/1e6 -> $0.015; total tokens 3,000 > 1,800, so B replaces A; project beta.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1")
                .Tokens(1_000, 500, cacheRead: 200, cacheWrite: 100)
                .Cwd(@"C:\work\alpha").Build(), "fallback"));
        Assert.Equal(0.018225m, store.AllTotals.CostUsd);
        Assert.Equal(1_800, store.AllTotals.TotalTokens);

        store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1")
                .Tokens(3_000, 0).Cwd(@"C:\work\beta").Build(), "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(3_000, store.AllTotals.TotalTokens);
        Assert.Equal(0.015m, store.AllTotals.CostUsd);
        Assert.Equal(0, store.AllTotals.CacheWriteTokens);
        Assert.Equal(0, store.AllTotals.CacheReadTokens);
        Assert.Equal(0, store.AllTotals.OutputTokens);

        Assert.Equal(["Opus 5"], store.Breakdown("model", UsageMetric.TotalTokens, 7).Select(entry => entry.Label));
        Assert.Equal(3_000.0, Assert.Single(store.Breakdown("model", UsageMetric.TotalTokens, 7)).Value);

        // A's project is gone: "a label whose counters reach all-zero with Messages == 0 is removed"
        // (P2b.5), so the project breakdown holds B's project alone.
        Assert.Equal(["beta"], store.Breakdown("project", UsageMetric.TotalTokens, 7).Select(entry => entry.Label));

        var history = store.BuildHistory(7, UsageMetric.TotalTokens);
        Assert.Equal(3_000.0, Assert.Single(history.Buckets, bucket => bucket.Total > 0).Total);
        Assert.Equal(["beta"], history.ByProject.Select(entry => entry.Label));
    }

    [Fact]
    public void TryIngest_ReplacementByTheSmallerRecord_StillRemovesTheLargerOnesCounters()
    {
        // The same subtraction with the winner decided by branch 1 instead of by size: the kept record is
        // a 5,000-token sidechain, the survivor a 10-token non-sidechain. Code that "subtracts the smaller
        // from the larger" cannot produce these numbers.
        //   kept: 5,000 x 5 / 1e6 = $0.025;  survivor: 10 x 5 / 1e6 = $0.00005.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1")
                .Tokens(5_000, 0).Sidechain(true).Build(), "fallback"));
        store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1")
                .Tokens(10, 0).Sidechain(false).Build(), "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(10, store.AllTotals.InputTokens);
        Assert.Equal(0.00005m, store.AllTotals.CostUsd);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_ReplacementLeavesNoCostInEitherBucketOfTheReplacedRecord()
    {
        // OP4 puts a cost counter "per day x model and day x project (decimal), next to the token
        // counters". With the day retained, the equivalent-value metric shows the survivor's $0.015 -
        // neither the replaced record's $0.018225 nor a sum of the two.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1")
                .Tokens(1_000, 500, cacheRead: 200, cacheWrite: 100).Build(), "fallback"));
        store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1")
                .Tokens(3_000, 0).Build(), "fallback");

        Assert.Equal(0.015, Assert.Single(store.Breakdown("model", UsageMetric.EquivalentValue, 7)).Value);
        Assert.Equal(0.015, Assert.Single(store.Breakdown("project", UsageMetric.EquivalentValue, 7)).Value);

        var day = store.WindowTotals(7);
        Assert.Equal(0.015m, day.CostUsd);
        Assert.Equal(3_000, day.InputTokens);
        Assert.Equal(0, day.CacheWriteTokens);
    }

    // -- B4 / B13: a pruned day bucket, through RefreshAsync ---------------------------------------

    [Fact]
    public async Task RefreshAsync_ReplacementAfterAPrunedDayBucket_KeepsAllTotalsExactAndCountsNothingMalformed()
    {
        // B4: "a pruned day bucket makes only the day x model / day x project subtraction a no-op; the
        // AllTotals subtraction always happens".
        // B13: "subtract day x model and day x project with each field floored at zero (covers a bucket
        // recreated after pruning), then subtract AllTotals last (exact). A record is not counted as
        // malformed because of its replacement".
        //
        // The handoff's oracle: now = 2027-06-01; X(100) and Y(10) on 2026-01-01; prune; then Y'(20) and
        // X'(200) -> no malformed, AllTotals.Input = 220, Messages 2. Why that day is pruned: the store
        // retains 400 days, so at now = 2027-06-01 the cut-off is 2026-04-27 and 2026-01-01 - 516 days
        // earlier - leaves the day tables while AllTotals keeps it (that is what AllTotals is for).
        //
        // X and Y are different message ids, so both count; Y' carries Y's key with more tokens (10 -> 20)
        // and X' carries X's key with more tokens (100 -> 200), so each replaces. X' is the step the floor
        // exists for: the day x model bucket for 2026-01-01 had just been recreated by Y' holding 20 input
        // tokens, so subtracting X's stored 100 would otherwise go negative - and UsageCounters.Subtract
        // throws on a negative field.
        var store = CreateFileStore(new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero));

        WriteLines(
            TranscriptRecordBuilder.Create("msg-x", Opus).At("2026-01-01T10:00:00Z").SessionId("s1").Tokens(100, 0).Line(),
            TranscriptRecordBuilder.Create("msg-y", Opus).At("2026-01-01T11:00:00Z").SessionId("s1").Tokens(10, 0).Line());

        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, added);
        Assert.Equal(110, store.AllTotals.InputTokens);
        Assert.Equal(2, store.AllTotals.Messages);

        AppendLines(
            TranscriptRecordBuilder.Create("msg-y", Opus).At("2026-01-01T11:00:00Z").SessionId("s1").Tokens(20, 0).Line(),
            TranscriptRecordBuilder.Create("msg-x", Opus).At("2026-01-01T10:00:00Z").SessionId("s1").Tokens(200, 0).Line());

        // B19: Y' and X' each replace a kept record, so RefreshAsync's count excludes them - the refresh
        // that produces every number below still reports zero added records.
        Assert.Equal(0, await store.RefreshAsync(CancellationToken.None));

        // 110 - 10 + 20 - 100 + 200 = 220, one message per surviving record, and nothing read as broken.
        Assert.Equal(220, store.AllTotals.InputTokens);
        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(0, store.MalformedRecordCount);
        Assert.Null(store.LastDirectoryError);

        // Cost, hand-checked at claude-opus-5's $5 per million input tokens: X' 200x5 = 1,000/1e6 =
        // $0.001 and Y' 20x5 = 100/1e6 = $0.0001, after the exact subtraction of the original X ($0.0005)
        // and Y ($0.00005) -> $0.0011.
        Assert.Equal(0.0011m, store.AllTotals.CostUsd);
    }

    [Fact]
    public async Task RefreshAsync_ReplacementInsideARetainedDayBucket_ShowsTheSameNumbersInTheDayTable()
    {
        // The same two replacements while the day is still retained (now = 2026-09-20, records dated
        // 2026-09-14: 6 days back, far inside the 400-day retention), so the day subtraction is NOT a
        // no-op and a window query can show it: 110 - 10 + 20 - 100 + 200 = 220 in the day buckets as
        // well as in AllTotals. This is what stops the pruned-bucket test above passing by accident.
        var store = CreateFileStore();

        WriteLines(
            TranscriptRecordBuilder.Create("msg-x", Opus).SessionId("s1").Tokens(100, 0).Line(),
            TranscriptRecordBuilder.Create("msg-y", Opus).SessionId("s1").Tokens(10, 0).Line());
        Assert.Equal(2, await store.RefreshAsync(CancellationToken.None)); // X and Y: two new records.

        Assert.Equal(110, store.WindowTotals(7).InputTokens);
        Assert.Equal(2, store.WindowTotals(7).Messages);

        AppendLines(
            TranscriptRecordBuilder.Create("msg-y", Opus).SessionId("s1").Tokens(20, 0).Line(),
            TranscriptRecordBuilder.Create("msg-x", Opus).SessionId("s1").Tokens(200, 0).Line());
        // B19: both appended lines replace, so the count excludes them even though the day table below
        // is what changes.
        Assert.Equal(0, await store.RefreshAsync(CancellationToken.None));

        var day = store.WindowTotals(7);
        Assert.Equal(220, day.InputTokens);
        Assert.Equal(2, day.Messages);
        Assert.Equal(220, store.AllTotals.InputTokens);
        Assert.Equal(0.0011m, day.CostUsd);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public async Task RefreshAsync_ReplacementIsNeverCountedAsMalformed()
    {
        // B13's last sentence through the real file path: a duplicate arriving on a later refresh is a
        // survivor decision, not a read failure.
        var store = CreateFileStore();

        WriteLines(TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1").Tokens(10, 0).Line());
        Assert.Equal(1, await store.RefreshAsync(CancellationToken.None));
        Assert.Equal(10, store.AllTotals.InputTokens);

        AppendLines(TranscriptRecordBuilder.Create("gen-1", Opus).SessionId("s1").Tokens(500, 0).Line());
        // B19: the appended line replaces the kept one, so the count excludes it...
        Assert.Equal(0, await store.RefreshAsync(CancellationToken.None));
        // ...while its counters still change and nothing is read as broken (B13).

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(500, store.AllTotals.InputTokens);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    // -- B7: the bounded seen-set evicts -------------------------------------------------------------

    [Fact]
    public void TryIngest_SeenCapacityOne_AnEvictedKeyIsCountedAgain()
    {
        // B7: "with capacity 1, record A, then B (other key), then a duplicate of A -> A is counted
        // twice". The eviction limitation is accepted and tested, not hidden. Eviction takes a record's
        // whole entry with it ("oldest evicted together with its keys"), so A's exact and replay keys are
        // both gone and the duplicate is new information.
        var store = CreateIngestStore(seenCapacity: 1);

        var a = TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-a").SessionId("s1").Build();
        var b = TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-b").SessionId("s1").Build();

        Assert.True(store.TryIngest(a, "fallback"));
        Assert.True(store.TryIngest(b, "fallback"));
        Assert.True(store.TryIngest(a, "fallback"));

        Assert.Equal(3, store.AllTotals.Messages);
        Assert.Equal(300, store.AllTotals.InputTokens);
    }

    [Fact]
    public void TryIngest_SeenCapacityOne_TheHeldRecordIsStillDeduplicated()
    {
        var store = CreateIngestStore(seenCapacity: 1);
        var a = TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-a").SessionId("s1").Build();

        Assert.True(store.TryIngest(a, "fallback"));
        store.TryIngest(a, "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(100, store.AllTotals.InputTokens);
    }

    [Fact]
    public void TryIngest_DefaultCapacity_HoldsFarMoreRecordsThanTheseTestsUse()
    {
        // P2b.5 and B7 put the default at 400,000, so 2,000 distinct keys must all stay held - the
        // opposite failure mode to B7's eviction (a capacity mistyped as, say, 400).
        var store = CreateIngestStore();

        for (var n = 0; n < 2_000; n++)
        {
            Assert.True(store.TryIngest(
                TranscriptRecordBuilder.WithTokens(1, 0, id: $"gen-{n}").SessionId("s1").Build(), "fallback"));
        }

        Assert.Equal(2_000, store.AllTotals.Messages);

        store.TryIngest(TranscriptRecordBuilder.WithTokens(1, 0, id: "gen-0").SessionId("s1").Build(), "fallback");

        Assert.Equal(2_000, store.AllTotals.Messages);
    }

    // -- B12 as behaviour: matching is by key content ----------------------------------------------

    [Fact]
    public void TryIngest_TwoRecordsDifferingInEveryNonKeyField_StillMatch()
    {
        // B12 stores exact and replay keys as 64-bit hashes, shares the model/project label strings and
        // keeps aliases in a small array. None of that may change what matches: the key here (id +
        // requestId) is identical while session, project, timestamp and token counts all change. The
        // second record is larger, so it replaces the first rather than adding a message.
        //   B's cost: 900x5 + 900x25 = 4,500 + 22,500 = 27,000/1e6 -> $0.027.
        var store = CreateIngestStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).Tokens(100, 0).RequestId("req-1").SessionId("s1")
                .Cwd(@"C:\work\alpha").At("2026-09-14T10:00:00Z").Build(), "fallback"));
        store.TryIngest(
            TranscriptRecordBuilder.Create("gen-1", Opus).Tokens(900, 900).RequestId("req-1").SessionId("s2")
                .Cwd(@"C:\work\beta").At("2026-09-14T18:00:00Z").Build(), "fallback");

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(900, store.AllTotals.InputTokens);
        Assert.Equal(0.027m, store.AllTotals.CostUsd);
    }

    [Fact]
    public void TryIngest_KeysThatDifferByASingleCharacter_NeverMerge()
    {
        // The other side of B12: 200 ids that differ only in a digit are 200 separate records, so neither
        // hashing nor any key truncation may fold them together.
        var store = CreateIngestStore();

        for (var n = 0; n < 200; n++)
        {
            Assert.True(store.TryIngest(
                TranscriptRecordBuilder.WithTokens(1, 0, id: $"gen-{n}").RequestId("req-1").SessionId("s1").Build(), "fallback"));
        }

        Assert.Equal(200, store.AllTotals.Messages);
        Assert.Equal(200, store.AllTotals.InputTokens);
    }
}
