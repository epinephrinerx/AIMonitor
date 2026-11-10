using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Decision C25(e) (P2b Inspector B mutant M3) — <see cref="ClaudeTranscriptStore"/> must scan
/// transcript files in <see cref="StringComparison.Ordinal"/> full-path order: SPEC R-014
/// "P2b detailed" §P2b.2 — "all files of all roots sorted <c>StringComparer.Ordinal</c> by full
/// path". Temp directories only (Docs/TESTING.md §5); synthetic records only.
/// </summary>
[Trait("Category", "Contract")]
public sealed class ClaudeTranscriptStoreScanOrderTests : IDisposable
{
    /// <summary>Same fixed "now" as the file tests: the builder's default record timestamp
    /// (<see cref="TranscriptRecordBuilder.DefaultWhen"/>, 2026-09-14) is 6 days before it, so a
    /// 7-day window includes the survivor.</summary>
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aimonitor-claude-transcript-scanorder-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _project;

    public ClaudeTranscriptStoreScanOrderTests()
    {
        _project = Path.Combine(_root, "C--work-project");
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_UppercaseAndLowercaseFileNames_ScanInOrdinalFullPathOrder_SurvivorIsTheUppercaseFile()
    {
        // Decision C25(e), mutant M3 ("Ordinal" replaced by OrdinalIgnoreCase or a culture
        // comparer). The handoff phrases it as "files whose names differ only in case", but no
        // NTFS directory can hold two sibling files whose names differ only in case — creating
        // the second one fails as a duplicate — so the pair is realised the only way Windows
        // allows while keeping CASE the deciding character of the full-path comparison: the two
        // paths are equal up to the file name, whose first characters are an uppercase letter
        // versus a lowercase letter. Ordinal puts "Zebra.jsonl" first ('Z' = 90 < 'a' = 97);
        // OrdinalIgnoreCase and every culture comparer put "apple.jsonl" first ("a…" before "Z…").
        //
        // Scan order becomes observable through the de-duplication tie: both files carry one
        // record with the SAME exact key (P2b.4: requestId present → id + "\n" + req — sessionId
        // is held identical anyway) and the records tie on every survivor criterion of P2b.5
        // (neither is a sidechain; identical total tokens 100+50; both models unpriced → equal
        // cost 0). "equal → keep the existing one" makes the kept model name a direct witness of
        // which file was scanned first. B19/P2b.5: the tied second record is not "added".
        var zebraLine = TranscriptRecordBuilder.Create("msg-order-tie", "z-model")
            .RequestId("req-1")
            .SessionId("sess-1")
            .Tokens(input: 100, output: 50)
            .Line();
        var appleLine = TranscriptRecordBuilder.Create("msg-order-tie", "a-model")
            .RequestId("req-1")
            .SessionId("sess-1")
            .Tokens(input: 100, output: 50)
            .Line();
        File.WriteAllText(Path.Combine(_project, "Zebra.jsonl"), zebraLine + "\n");
        File.WriteAllText(Path.Combine(_project, "apple.jsonl"), appleLine + "\n");

        var store = new ClaudeTranscriptStore(_root, new FakeClock(FixedNow), TimeZoneInfo.Utc);
        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, store.FilesScanned);
        Assert.Equal(1, added); // the second-scanned tied duplicate is not counted as added (B19)
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(100, store.AllTotals.InputTokens);
        Assert.Equal(0, store.MalformedRecordCount);

        // Ordinal: "…\C--work-project\Zebra.jsonl" sorts before "…\C--work-project\apple.jsonl",
        // so the survivor is Zebra's record — the only unpriced model name left in the window
        // (P2b.7 "all files of all roots sorted StringComparer.Ordinal by full path").
        Assert.Equal(["z-model"], store.UnpricedInRange(7));
    }
}
