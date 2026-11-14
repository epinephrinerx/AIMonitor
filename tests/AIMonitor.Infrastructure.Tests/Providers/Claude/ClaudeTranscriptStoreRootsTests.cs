using System.Diagnostics;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Covers SPEC R-014 "P2b detailed" §P2b.2 - the transcript store taking a list of roots, using only the
/// roots whose directory exists, scanning every <c>*.jsonl</c> under each at any depth, sorting all files
/// of all roots by full path (Ordinal), and labelling a record's fallback project from the first segment
/// below the root - plus decision B2 (the <c>sid</c> fallback derived from the file's position) and B11
/// (that first segment is still decoded by the existing rule).
/// </summary>
/// <remarks>
/// Real disposable temp directories, a fixed clock and a fixed zone, as <see cref="ClaudeTranscriptStoreFileTests"/>
/// does. Every record is written by <see cref="TranscriptRecordBuilder"/>; nothing here touches a real
/// transcript, credential, or network resource (Docs/TESTING.md §5).
///
/// One fixture is machine-specific by necessity: <see cref="CreateUnreadableRoot"/> needs a directory that
/// exists yet cannot be enumerated, and on Windows the only unprivileged, deterministic way to make one is
/// a junction whose target was never created (<c>mklink /J</c>, inside this test's own temp tree, removed
/// again in <see cref="Dispose"/>). An access-denied ACL on a directory is not used because the
/// <c>EnumerationOptions.IgnoreInaccessible</c> the spec mandates makes that condition invisible to the
/// store - the enumerator swallows it instead of throwing. The fixture asserts its own precondition, so if
/// a runner cannot produce the condition the test fails with a fixture message rather than passing vacuously.
/// </remarks>
[Trait("Category", "Contract")]
public sealed class ClaudeTranscriptStoreRootsTests : IDisposable
{
    /// <summary>P2b.2's exact wording for "no root exists" - a fixed, sanitized, actionable sentence with
    /// no path in it.</summary>
    private const string NoDirectoryError = "No Claude transcripts directory found.";

    /// <summary>P2b.2's exact wording for "a root exists but cannot be enumerated".</summary>
    private const string UnreadableDirectoryError = "Could not read a Claude transcripts directory.";

    private static readonly DateTimeOffset FixedNow = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    private readonly string _base =
        Path.Combine(Path.GetTempPath(), "aimonitor-claude-roots-tests-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _junctions = [];
    private readonly string _rootA;
    private readonly string _rootB;

    public ClaudeTranscriptStoreRootsTests()
    {
        Directory.CreateDirectory(_base);
        _rootA = Path.Combine(_base, "root-a");
        _rootB = Path.Combine(_base, "root-b");
        Directory.CreateDirectory(_rootA);
        Directory.CreateDirectory(_rootB);
    }

    public void Dispose()
    {
        // A junction to a path that was never created has to be removed as a reparse point first: a
        // recursive delete would try to follow it into a directory that does not exist.
        foreach (var junction in _junctions)
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction, recursive: false);
            }
        }

        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    private ClaudeTranscriptStore CreateStore(params string[] roots) =>
        new(roots, new FakeClock(FixedNow), TimeZoneInfo.Utc);

    private string MakeProject(string root, string encodedProjectFolder)
    {
        var project = Path.Combine(root, encodedProjectFolder);
        Directory.CreateDirectory(project);
        return project;
    }

    private static void WriteSessionFile(string directory, string fileName, params string[] lines)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), string.Join("\n", lines) + "\n");
    }

    /// <summary>A root that exists as a directory yet cannot be enumerated: a junction whose target was
    /// never created. <see cref="Directory.Exists"/> answers <see langword="true"/> for it while
    /// enumeration throws <see cref="DirectoryNotFoundException"/> (an <see cref="IOException"/>), which is
    /// exactly the condition P2b.2 separates from "does not exist". The fixture's own preconditions are
    /// asserted below, so a runner that cannot produce the condition fails here with a fixture message
    /// instead of letting the test pass for the wrong reason.</summary>
    private string CreateUnreadableRoot(string name)
    {
        var link = Path.Combine(_base, name);
        var missingTarget = Path.Combine(_base, name + "-never-created");
        CreateJunction(link, missingTarget);
        _junctions.Add(link);

        Assert.True(Directory.Exists(link), $"fixture: {link} should exist as a directory");
        Assert.ThrowsAny<IOException>(() => Directory.EnumerateFiles(link, "*.jsonl").ToList());

        return link;
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo(
            "cmd.exe",
            "/c mklink /J \"" + linkPath + "\" \"" + targetPath + "\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("fixture: could not start cmd.exe to create a junction");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"fixture: mklink /J exited {process.ExitCode}: {output}");
    }

    /// <summary>A record with its own session id and no <c>cwd</c>, so its dedup key never depends on the
    /// file it came from (the tests below vary the roots and the file layout, not the keys) while its
    /// project label does come from the file's position - which is what several assertions read.</summary>
    private static string Record(string id, long inputTokens) =>
        TranscriptRecordBuilder.WithTokens(inputTokens, 0, id: id).SessionId("session-" + id).Cwd(null).Line();

    // -- constructor rules (P2b.2) ----------------------------------------------------------------

    [Fact]
    public void Ctor_NullRootList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ClaudeTranscriptStore((IReadOnlyList<string>)null!, new FakeClock(FixedNow), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Ctor_EmptyRootList_Throws()
    {
        // P2b.2: the list must be "non-null, >= 1 entry".
        Assert.ThrowsAny<ArgumentException>(() =>
            new ClaudeTranscriptStore(new string[0], new FakeClock(FixedNow), TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_RootListWithABlankEntry_Throws(string blankRoot)
    {
        // P2b.2: "no blank entry".
        Assert.ThrowsAny<ArgumentException>(() =>
            new ClaudeTranscriptStore(new[] { blankRoot }, new FakeClock(FixedNow), TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task Ctor_SingleStringRoot_StillScans_AsAOneItemList()
    {
        // P2b.2: "the existing string root ctors delegate with a one-item list" - the pre-P2b call form
        // must keep working, unchanged.
        WriteSessionFile(MakeProject(_rootA, "C--work-project"), "session.jsonl", Record("msg-str", 100));

        var store = new ClaudeTranscriptStore(_rootA, new FakeClock(FixedNow), TimeZoneInfo.Utc);
        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, added);
        Assert.Equal(1, store.FilesScanned);
        Assert.Equal(1, store.AllTotals.Messages);
    }

    // -- which roots are used, and what is reported ------------------------------------------------

    [Fact]
    public async Task RefreshAsync_NoRootExists_ReportsTheFixedNoDirectoryErrorWithoutLeakingAPath()
    {
        var store = CreateStore(Path.Combine(_base, "missing-a"), Path.Combine(_base, "missing-b"));

        var added = await store.RefreshAsync(CancellationToken.None);

        // P2b.2: "None exists -> LastDirectoryError = \"No Claude transcripts directory found.\",
        // FilesScanned = 0, return 0."
        Assert.Equal(0, added);
        Assert.Equal(0, store.FilesScanned);
        Assert.Equal(NoDirectoryError, store.LastDirectoryError);
        Assert.DoesNotContain(_base, store.LastDirectoryError!, StringComparison.Ordinal);
        Assert.Equal(0, store.AllTotals.Messages);
    }

    [Fact]
    public async Task RefreshAsync_OneRootMissing_AnotherExists_NoDirectoryErrorIsReported()
    {
        // P2b.2: "a root is used only if its directory exists" - a missing root is not an error while
        // another root still gives a scan.
        WriteSessionFile(MakeProject(_rootA, "C--work-project"), "session.jsonl", Record("msg-one", 100));

        var store = CreateStore(Path.Combine(_base, "missing"), _rootA);
        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, added);
        Assert.Null(store.LastDirectoryError);
        Assert.Equal(1, store.FilesScanned);
        Assert.Equal(1, store.AllTotals.Messages);
    }

    [Fact]
    public async Task RefreshAsync_RootExistsButHoldsNoFiles_ReportsNoError()
    {
        // The root exists and is successfully enumerated; it simply has nothing in it.
        var store = CreateStore(_rootA, _rootB);

        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, added);
        Assert.Equal(0, store.FilesScanned);
        Assert.Null(store.LastDirectoryError);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    // -- combining roots and deduplicating across them ---------------------------------------------

    [WindowsOnlyFact("the fixture needs a directory that exists yet cannot be listed, which only a Windows junction to a missing target produces; elsewhere the only way is withholding permissions, which the spec's IgnoreInaccessible deliberately hides, as it does an access-denied ACL on Windows")]
    public async Task RefreshAsync_UnreadableRoot_ReportsTheDirectoryError_AndStillScansTheOtherRoots()
    {
        var unreadable = CreateUnreadableRoot("unreadable");
        WriteSessionFile(MakeProject(_rootA, "C--work-project"), "session.jsonl", Record("msg-good", 100));

        // The unreadable root comes first, so the test proves the scan continues past it to the next root.
        var store = CreateStore(unreadable, _rootA);
        var added = await store.RefreshAsync(CancellationToken.None);

        // P2b.2: "A root that exists but cannot be enumerated -> LastDirectoryError = \"Could not read a
        // Claude transcripts directory.\", other roots still scanned." FilesScanned is deliberately not
        // asserted: the spec defines it as the total count of files found and says nothing about what a
        // root whose enumeration aborted contributes.
        Assert.Equal(1, added);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(UnreadableDirectoryError, store.LastDirectoryError);
        Assert.DoesNotContain(_base, store.LastDirectoryError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_TwoRoots_CombineTheirRecords()
    {
        WriteSessionFile(MakeProject(_rootA, "C--work-alpha"), "session.jsonl", Record("msg-a", 100));
        WriteSessionFile(MakeProject(_rootB, "C--work-beta"), "session.jsonl", Record("msg-b", 50));

        var store = CreateStore(_rootA, _rootB);
        var added = await store.RefreshAsync(CancellationToken.None);

        // P2b.2/P2b outline: "data from all roots combined and deduplicated".
        Assert.Equal(2, added);
        Assert.Equal(2, store.FilesScanned);
        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(150, store.AllTotals.InputTokens);

        var projects = store.Breakdown("project", UsageMetric.TotalTokens, 7);
        Assert.Contains(projects, entry => entry.Label == "alpha");
        Assert.Contains(projects, entry => entry.Label == "beta");
    }

    [Fact]
    public async Task RefreshAsync_SameRecordInTwoRoots_CountedOnce()
    {
        // The dedup key (id + sessionId here, P2b.4) does not depend on the file, so a transcript that
        // appears under two roots - a copied or symlinked config directory - is counted once.
        var line = Record("msg-dup", 100);
        WriteSessionFile(MakeProject(_rootA, "C--work-alpha"), "session.jsonl", line);
        WriteSessionFile(MakeProject(_rootB, "C--work-beta"), "session.jsonl", line);

        var store = CreateStore(_rootA, _rootB);
        await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(100, store.AllTotals.InputTokens);
        Assert.Equal(2, store.FilesScanned);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    // -- the recursive scan (P2b.2) ----------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_ScansJsonlAtEveryDepthIncludingSubagents_AndCountsEveryFile()
    {
        var project = MakeProject(_rootA, "C--work-project");
        WriteSessionFile(project, "top-level.jsonl", Record("msg-t", 1));
        WriteSessionFile(Path.Combine(project, "subagents"), "agent-a.jsonl", Record("msg-sa", 2));
        WriteSessionFile(Path.Combine(project, "subagents", "nested"), "agent-b.jsonl", Record("msg-sn", 3));
        WriteSessionFile(_rootA, "loose.jsonl", Record("msg-loose", 4));

        var store = CreateStore(_rootA);
        var added = await store.RefreshAsync(CancellationToken.None);

        // P2b.2: "every *.jsonl under each root at any depth (including subagents\)... FilesScanned =
        // total count" - four files here, one of them directly in the root.
        Assert.Equal(4, store.FilesScanned);
        Assert.Equal(4, added);
        Assert.Equal(4, store.AllTotals.Messages);

        // 1 + 2 + 3 + 4 = 10 input tokens.
        Assert.Equal(10, store.AllTotals.InputTokens);
    }

    [Fact]
    public async Task RefreshAsync_NonJsonlFilesAtAnyDepth_AreIgnored()
    {
        var project = MakeProject(_rootA, "C--work-project");
        WriteSessionFile(project, "session.jsonl", Record("msg-1", 100));
        WriteSessionFile(Path.Combine(project, "subagents"), "notes.txt", "not a transcript");
        WriteSessionFile(project, "session.json", Record("msg-not-counted", 5_000));

        var store = CreateStore(_rootA);
        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, store.FilesScanned);
        Assert.Equal(1, added);
        Assert.Equal(100, store.AllTotals.InputTokens);
    }

    // -- fallback project label (P2b.2 + B11) ------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_FallbackProject_IsTheFirstSegmentBelowTheRoot_StillDecoded()
    {
        // P2b.2 + B11: the chosen segment is the first directory below the root (the encoded project
        // folder) and is decoded by the existing rule, so "C--work-app" reads as "app" whichever file
        // depth it came from. Before P2b the *immediate parent* was used, which would have labelled the
        // same records "subagents" and "deep".
        var project = MakeProject(_rootA, "C--work-app");
        WriteSessionFile(
            Path.Combine(project, "subagents"),
            "agent.jsonl",
            TranscriptRecordBuilder.WithTokens(100, 0, id: "msg-deep-1").Cwd(null).SessionId("s1").Line());
        WriteSessionFile(
            Path.Combine(project, "subagents", "deep"),
            "agent.jsonl",
            TranscriptRecordBuilder.WithTokens(60, 0, id: "msg-deep-2").Cwd(null).SessionId("s2").Line());

        var store = CreateStore(_rootA);
        await store.RefreshAsync(CancellationToken.None);

        var projects = store.Breakdown("project", UsageMetric.TotalTokens, 7);
        Assert.Equal(["app"], projects.Select(entry => entry.Label));
        Assert.Equal(160.0, Assert.Single(projects).Value);
    }

    [Fact]
    public async Task RefreshAsync_FileDirectlyInARoot_UsesTheUnknownProjectLabel()
    {
        // P2b.2: "a file directly in a root uses \"\" -> (unknown)".
        WriteSessionFile(_rootA, "loose.jsonl", TranscriptRecordBuilder.WithTokens(100, 0, id: "msg-loose").Cwd(null).SessionId("s1").Line());

        var store = CreateStore(_rootA);
        await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(["(unknown)"], store.Breakdown("project", UsageMetric.TotalTokens, 7).Select(entry => entry.Label));
    }

    [Fact]
    public async Task RefreshAsync_CwdWinsOverTheFallbackEvenInADeepFile()
    {
        // The first-segment rule is only the *fallback*; a record that carries its own cwd is still
        // labelled from that (unchanged from P2a).
        var project = MakeProject(_rootA, "C--work-app");
        WriteSessionFile(
            Path.Combine(project, "subagents"),
            "agent.jsonl",
            TranscriptRecordBuilder.WithTokens(100, 0, id: "msg-cwd").Cwd(@"C:\work\from-cwd").SessionId("s1").Line());

        var store = CreateStore(_rootA);
        await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(["from-cwd"], store.Breakdown("project", UsageMetric.TotalTokens, 7).Select(entry => entry.Label));
    }

    // -- scan order (P2b.2: sorted Ordinal by full path) ------------------------------------------

    [Fact]
    public async Task RefreshAsync_ScansFilesInOrdinalFullPathOrder_TheFirstFoldedRecordWinsATie()
    {
        // Two records with one and the same dedup key, tied on every survivor criterion: both
        // non-sidechain, 100 input tokens each, and the same cost (100 x 5 / 1,000,000 = $0.0005 at
        // claude-opus-5). P2b.5's last branch - "equal -> keep the existing one" - therefore keeps the
        // record folded first, and P2b.2 sorts all files of all roots with StringComparer.Ordinal.
        var project = MakeProject(_rootA, "C--work-project");
        WriteSessionFile(project, "a.jsonl", TranscriptRecordBuilder
            .WithTokens(100, 0, id: "gen-tie").SessionId("s1").RequestId("req-1").Cwd(@"C:\work\alpha").Line());
        WriteSessionFile(project, "b.jsonl", TranscriptRecordBuilder
            .WithTokens(100, 0, id: "gen-tie").SessionId("s1").RequestId("req-1").Cwd(@"C:\work\beta").Line());

        var store = CreateStore(_rootA);
        await store.RefreshAsync(CancellationToken.None);

        // "<root>\C--work-project\a.jsonl" sorts before "...\b.jsonl" ordinally, so alpha is the surviving
        // record's project and beta never enters the counters.
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(["alpha"], store.Breakdown("project", UsageMetric.TotalTokens, 7).Select(entry => entry.Label));
    }

    // -- sid fallback from the file position (B2) ---------------------------------------------------

    [Fact]
    public async Task RefreshAsync_SidFallbackForAFileInTheProjectFolder_IsTheFileName_TwoFilesDoNotDeduplicate()
    {
        // B2: "file at <root>\<project>\<file>.jsonl -> file name without extension". The id here has no
        // msg_/resp_ prefix and there is no requestId, so the exact key is id + "\n\n" + sid + "\n" + ts
        // (P2b.4) - two file names give two different sids, hence two records.
        var line = TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-1").Cwd(null).Line();
        var project = MakeProject(_rootA, "C--work-project");
        WriteSessionFile(project, "alpha.jsonl", line);
        WriteSessionFile(project, "beta.jsonl", line);

        var store = CreateStore(_rootA);
        await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(200, store.AllTotals.InputTokens);
    }

    [Fact]
    public async Task RefreshAsync_SidFallbackForADeeperFile_IsTheDirectoryBelowTheProject_TwoFilesDeduplicate()
    {
        // B2: "deeper file -> the directory segment directly below <project>" (ccusage
        // extract_session_parts). Both records sit in <root>\C--work-project\sess-1\, so both get
        // sid = "sess-1" and share one key even though the file names differ - the file name must NOT be
        // used at this depth.
        var sessionDirectory = Path.Combine(MakeProject(_rootA, "C--work-project"), "sess-1");
        var line = TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-1").Cwd(null).Line();
        WriteSessionFile(sessionDirectory, "agent-a.jsonl", line);
        WriteSessionFile(sessionDirectory, "agent-b.jsonl", line);

        var store = CreateStore(_rootA);
        await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(100, store.AllTotals.InputTokens);
    }

    [Fact]
    public async Task RefreshAsync_SameSessionInTwoProjectFoldersOfDifferentRoots_CountedOnce()
    {
        // A session id on the record makes the key independent of where the copy is found (P2b.4's sid is
        // sessionId when present), which is how the same session appearing under two roots stays one set
        // of records.
        var line = TranscriptRecordBuilder.WithTokens(100, 0, id: "gen-1").SessionId("shared-session").Line();
        WriteSessionFile(MakeProject(_rootA, "C--work-one"), "session.jsonl", line);
        WriteSessionFile(MakeProject(_rootB, "C--work-two"), "session.jsonl", line);

        var store = CreateStore(_rootA, _rootB);
        await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(100, store.AllTotals.InputTokens);
    }
}
