using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Covers SPEC R-014 decision B16 - advisor iterations found in <c>message.usage.iterations[]</c> ("found
/// in 2 real local transcripts"). One extra record per entry with <c>type == "advisor_message"</c> and a
/// non-empty model, carrying the parent's timestamp, sessionId, requestId, isSidechain, project and day,
/// an id of <c>&lt;id&gt;:advisor:&lt;n&gt;</c> where n counts <em>advisor</em> entries only (0, 1, ...),
/// the iteration's own model and token fields, priced like any record and deduplicated by the same rules,
/// while an invalid iteration is skipped alone.
/// </summary>
/// <remarks>
/// Rates for the hand calculations (spec P2a table, USD per million tokens): claude-opus-5 5/25/0.50,
/// claude-haiku-4-5 1/5/0.10. As in <see cref="ClaudeTranscriptStoreDedupTests"/>, the bool that
/// <see cref="ClaudeTranscriptStore.TryIngest"/> returns is only asserted where the spec settles it: a
/// record that matches nothing counts, and a record discarded by the survivor rule does not.
/// </remarks>
[Trait("Category", "Contract")]
public sealed class ClaudeTranscriptStoreAdvisorTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    private const string Opus = "claude-opus-5";
    private const string Haiku45 = "claude-haiku-4-5";

    private static readonly string[] UnusedRoot = ["unused-root"];

    private static ClaudeTranscriptStore CreateStore() =>
        new(UnusedRoot, new FakeClock(FixedNow), TimeZoneInfo.Utc);

    /// <summary>A parent assistant record with one advisor iteration: model claude-opus-5, input 100,
    /// output 50 (cost 100x5 + 50x25 = 500 + 1,250 = 1,750/1e6 = $0.00175) whose usage carries one
    /// <c>advisor_message</c> entry priced at input 10, output 1 (10x5 + 1x25 = 50 + 25 = 75/1e6 =
    /// $0.000075).</summary>
    private static TranscriptRecordBuilder ParentWithOneAdvisor(
        string id = "msg_p",
        string? requestId = "req-1",
        string? sessionId = "s1",
        string? timestamp = TranscriptRecordBuilder.DefaultWhen,
        string? cwd = TranscriptRecordBuilder.DefaultCwd,
        bool sidechain = false)
        => TranscriptRecordBuilder.Create(id, Opus)
            .RequestId(requestId)
            .SessionId(sessionId)
            .At(timestamp)
            .Cwd(cwd)
            .Sidechain(sidechain)
            .Tokens(100, 50)
            .Advisor(Opus, input: 10, output: 1);

    [Fact]
    public void TryIngest_AdvisorIteration_AddsOneExtraRecordWithTheIterationsOwnModelAndTokens()
    {
        var store = CreateStore();

        var counted = store.TryIngest(
            TranscriptRecordBuilder.Create("msg_p", Opus)
                .RequestId("req-1").SessionId("s1").Tokens(100, 50)
                .Advisor(Haiku45, input: 1_000, output: 10)
                .Build(), "fallback");

        // B16: the parent and the advisor record are two records. The advisor's tokens come from the
        // iteration, and it is priced like any record - here Haiku 4.5 at 1/5 per MTok:
        //   parent  100x5 + 50x25   = 500 + 1,250  = 1,750/1e6 = $0.00175
        //   advisor 1,000x1 + 10x5  = 1,000 + 50   = 1,050/1e6 = $0.00105
        //   total                                              $0.0028
        // Two records, so a new record was counted; TryIngest's bool is asserted only for that first fold.
        Assert.True(counted);
        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(1_100, store.AllTotals.InputTokens);
        Assert.Equal(60, store.AllTotals.OutputTokens);
        Assert.Equal(0.0028m, store.AllTotals.CostUsd);

        var labels = store.Breakdown("model", UsageMetric.TotalTokens, 7).Select(entry => entry.Label).ToArray();
        Assert.Contains("Opus 5", labels);
        Assert.Contains("Haiku 4.5", labels);
    }

    [Fact]
    public void TryIngest_AdvisorRecord_IsDeduplicatedByTheSameRules_OnARepeatOfTheParentLine()
    {
        var store = CreateStore();
        var parent = ParentWithOneAdvisor().Build();

        Assert.True(store.TryIngest(parent, "fallback"));
        store.TryIngest(parent, "fallback");

        // The parent's own key and the derived "<id>:advisor:0" key both already exist, so re-folding the
        // same line adds nothing (B16: "deduplicated by the same rules").
        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(110, store.AllTotals.InputTokens);
    }

    [Fact]
    public void TryIngest_AdvisorNumberingCountsAdvisorEntriesOnly_NotTheIterationIndex()
    {
        // B16: "message.id = <id>:advisor:<n> where n counts advisor entries only (0, 1, ...)". A literal
        // record carrying the id the first advisor entry must have derived proves the numbering, because
        // the entries before it are not advisors.
        var store = CreateStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("msg_p", Opus)
                .RequestId("req-1").SessionId("s1").Tokens(100, 50)
                .OtherIteration()                 // iterations[0] - not an advisor entry
                .Advisor(Opus, input: 10, output: 1) // iterations[1] - the FIRST advisor entry
                .Build(), "fallback"));

        // A plain record with exactly the id the first advisor entry should have derived. Same requestId
        // (the advisor inherits it), so same exact key, same tokens, same cost -> the survivor rule keeps
        // the existing one and nothing is added. Had n been the array index (1), the derived id would be
        // "msg_p:advisor:1", this record would be new, and Messages would be 3.
        store.TryIngest(
            TranscriptRecordBuilder.Create("msg_p:advisor:0", Opus)
                .RequestId("req-1").SessionId("s1").Tokens(10, 1)
                .Build(), "fallback");

        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(110, store.AllTotals.InputTokens);

        // parent 1,750/1e6 + advisor 75/1e6 = 1,825/1e6.
        Assert.Equal(0.001825m, store.AllTotals.CostUsd);
    }

    [Fact]
    public void TryIngest_TwoAdvisorEntries_DeriveConsecutiveIds_AndBothCount()
    {
        var store = CreateStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("msg_p", Opus)
                .RequestId("req-1").SessionId("s1").Tokens(100, 50)
                .Advisor(Opus, input: 10, output: 1)
                .Advisor(Opus, input: 20, output: 2)
                .Build(), "fallback"));

        // Parent + 2 advisor records; cost 1,750 + 75 + 150 = 1,975/1e6 -> $0.001975.
        Assert.Equal(3, store.AllTotals.Messages);
        Assert.Equal(130, store.AllTotals.InputTokens);
        Assert.Equal(0.001975m, store.AllTotals.CostUsd);

        // And the ids really are ":advisor:0" and ":advisor:1": a copy of the second entry's derived
        // record (same key, same tokens) adds nothing. With 1-based numbering this copy would have hit the
        // FIRST advisor instead and replaced it, taking the input total to 140 ($0.00205).
        store.TryIngest(
            TranscriptRecordBuilder.Create("msg_p:advisor:1", Opus)
                .RequestId("req-1").SessionId("s1").Tokens(20, 2)
                .Build(), "fallback");
        Assert.Equal(3, store.AllTotals.Messages);
        Assert.Equal(130, store.AllTotals.InputTokens);
        Assert.Equal(0.001975m, store.AllTotals.CostUsd);
    }

    [Fact]
    public void TryIngest_ParentWithoutId_LeavesItsAdvisorRecordsWithoutADedupKey()
    {
        // B16: "<n> ... omitted dedup if the parent has no id". Two folds of one id-less parent add both
        // its records, exactly as P2b.4 treats an id-less record itself.
        var store = CreateStore();
        var parent = TranscriptRecordBuilder.Create(null, Opus)
            .RequestId("req-1").SessionId("s1").Tokens(100, 50)
            .Advisor(Opus, input: 10, output: 1)
            .Build();

        Assert.True(store.TryIngest(parent, "fallback"));
        Assert.True(store.TryIngest(parent, "fallback"));

        Assert.Equal(4, store.AllTotals.Messages);
        Assert.Equal(220, store.AllTotals.InputTokens);
    }

    [Fact]
    public void TryIngest_AdvisorEntryWithBlankModel_IsSkippedAlone_TheParentAndItsSiblingsStillCount()
    {
        var store = CreateStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("msg_p", Opus)
                .RequestId("req-1").SessionId("s1").Tokens(100, 50)
                .Advisor(string.Empty, input: 700, output: 700) // B16 requires a non-empty model
                .Advisor(Opus, input: 10, output: 1)
                .Build(), "fallback"));

        // The blank-model advisor is dropped, the valid one still folds, and skipping the bad entry is
        // not a malformed record. Had it been counted at its own 700 + 700 tokens, InputTokens would be
        // 810 and Messages 3.
        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(110, store.AllTotals.InputTokens);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Theory]
    [InlineData("\"a bare string\"")]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("{}")]
    public void TryIngest_MalformedAdvisorEntry_IsSkippedAlone(string rawIteration)
    {
        // B16: "An invalid iteration is skipped alone." An entry that is not an object, or is an object
        // without a type/model, cannot be an advisor entry - so the parent and the good advisor entry
        // still fold. Here the good entry is always the survivor of the two, so the numbers never change.
        var store = CreateStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("msg_p", Opus)
                .RequestId("req-1").SessionId("s1").Tokens(100, 50)
                .RawIteration(rawIteration)
                .Advisor(Opus, input: 10, output: 1)
                .Build(), "fallback"));

        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(110, store.AllTotals.InputTokens);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public void TryIngest_IterationsThatAreNotAdvisorEntries_AddNothing()
    {
        var store = CreateStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("msg_p", Opus)
                .RequestId("req-1").SessionId("s1").Tokens(100, 50)
                .OtherIteration(model: "claude-opus-5", input: 9_999, output: 9_999)
                .RawIteration("""{"type":"tool_use","model":"claude-opus-5","input_tokens":5,"output_tokens":5}""")
                .Build(), "fallback"));

        // Only the parent folds: 100 input + 50 output, cost 1,750/1e6 -> $0.00175.
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(100, store.AllTotals.InputTokens);
        Assert.Equal(0.00175m, store.AllTotals.CostUsd);
    }

    [Fact]
    public void TryIngest_AdvisorEntry_InheritsTheParentSidechainFlag()
    {
        // B16: the extra record carries the parent's isSidechain. Proved through P2b.5: the derived
        // advisor record of a sidechain parent is itself a sidechain, so a later NON-sidechain record
        // with that id replaces it despite being smaller.
        var store = CreateStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("msg_p", Opus)
                .RequestId("req-1").SessionId("s1").Sidechain(true).Tokens(100, 50)
                .Advisor(Opus, input: 10, output: 1)
                .Build(), "fallback"));

        // Parent (100/50, sidechain) + advisor (10/1, sidechain) = 110 input, 51 output.
        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(110, store.AllTotals.InputTokens);

        store.TryIngest(
            TranscriptRecordBuilder.Create("msg_p:advisor:0", Opus)
                .RequestId("req-1").SessionId("s1").Sidechain(false).Tokens(1, 0)
                .Build(), "fallback");

        // The advisor's 10 input + 1 output are gone and 1 input takes their place: 100 + 1 = 101 input,
        // 50 output. Had the advisor not inherited isSidechain, the kept (non-sidechain, 11 tokens) would
        // have beaten this 1-token record and the numbers would have stayed 110/51.
        // Cost: parent 1,750/1e6 + challenger 1x5/1e6 = 5/1e6 -> 1,755/1e6 = $0.001755 (the advisor's
        // 75/1e6 was subtracted with it).
        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(101, store.AllTotals.InputTokens);
        Assert.Equal(50, store.AllTotals.OutputTokens);
        Assert.Equal(0.001755m, store.AllTotals.CostUsd);
    }

    [Fact]
    public void TryIngest_AdvisorEntry_InheritsTheParentProjectAndDay()
    {
        // B16: "...with the same timestamp, sessionId, requestId, isSidechain, project and day". With no
        // cwd on the record, the project can only come from the parent's fallback; and the record dated
        // 2026-09-18 (now = 2026-09-20) must land on that day, not on today.
        var store = CreateStore();

        Assert.True(store.TryIngest(
            ParentWithOneAdvisor(timestamp: "2026-09-18T10:00:00Z", cwd: null).Build(), "fallback-project"));

        // Both records on 2026-09-18: a 1-day window (today) sees neither, a 3-day window sees both.
        Assert.Equal(0, store.WindowTotals(1).Messages);
        Assert.Equal(2, store.WindowTotals(3).Messages);

        // And one project row carrying both records' tokens: parent 100 + 50 = 150, advisor 10 + 1 = 11.
        var projects = store.Breakdown("project", UsageMetric.TotalTokens, 3);
        Assert.Equal(["fallback-project"], projects.Select(entry => entry.Label));
        Assert.Equal(161.0, Assert.Single(projects).Value);
    }

    [Fact]
    public void TryIngest_AdvisorEntry_ReadsBothCacheWriteSpellingsLikeAnyRecord()
    {
        // B16: an advisor entry's token fields are the same ones a record reads, including the
        // cache_creation split (A8). Here the entry writes 100 tokens of 5m cache and reads 200:
        //   advisor: 100 input x 5 + 0 output + 100 write5m x 6.25 + 200 read x 0.50
        //          = 500 + 0 + 625 + 100 = 1,225/1e6 -> $0.001225
        //   parent : $0.00175 -> total $0.002975.
        var store = CreateStore();

        Assert.True(store.TryIngest(
            TranscriptRecordBuilder.Create("msg_p", Opus)
                .RequestId("req-1").SessionId("s1").Tokens(100, 50)
                .AdvisorSplitCacheWrite(Opus, input: 100, output: 0, cacheRead: 200, fiveMinuteWrite: 100, oneHourWrite: 0)
                .Build(), "fallback"));

        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(200, store.AllTotals.CacheReadTokens);
        Assert.Equal(100, store.AllTotals.CacheWriteTokens);
        Assert.Equal(0.002975m, store.AllTotals.CostUsd);
    }
}
