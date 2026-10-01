using System.Text;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Exercises <see cref="ClaudeTranscriptStore.RefreshAsync"/> end to end against real (temp-directory)
/// <c>*.jsonl</c> files: the incremental checkpoint, de-duplication across refreshes/files, malformed
/// line/record handling, and cancellation. Every test uses a disposable temp directory unique to
/// itself and synthetic content only.
/// </summary>
[Trait("Category", "Contract")]
public sealed class ClaudeTranscriptStoreFileTests : IDisposable
{
    private const string When = "2026-09-14T10:00:00Z";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    private const string GoodUsage =
        """{"input_tokens":100,"output_tokens":200,"cache_read_input_tokens":300,"cache_creation_input_tokens":400}""";

    private const string BadInputTokensUsage =
        """{"input_tokens":"not-a-number","output_tokens":200,"cache_read_input_tokens":300,"cache_creation_input_tokens":400}""";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aimonitor-claude-transcript-file-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _project;
    private readonly string _sessionPath;

    public ClaudeTranscriptStoreFileTests()
    {
        _project = Path.Combine(_root, "C--work-project");
        Directory.CreateDirectory(_project);
        _sessionPath = Path.Combine(_project, "session.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ClaudeTranscriptStore CreateStore(string? root = null) =>
        new(root ?? _root, new FakeClock(FixedNow), TimeZoneInfo.Utc);

    /// <summary>Test-only seam via <see cref="ClaudeTranscriptStore"/>'s internal constructor: lets a
    /// test substitute a controlled stream in place of the real file open, to exercise cancellation
    /// and I/O failure deterministically instead of racing a real file handle.</summary>
    private ClaudeTranscriptStore CreateStore(Func<string, Stream> openFile) =>
        new(_root, new FakeClock(FixedNow), TimeZoneInfo.Utc, openFile);

    private static Stream OpenRealFile(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string RecordJson(string messageId, string usageJson = GoodUsage) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + When + "\",\"cwd\":\"/work/project\",\"message\":{\"id\":\""
        + messageId + "\",\"model\":\"claude-opus-4\",\"usage\":" + usageJson + "}}";

    /// <summary>A well-formed assistant record with neither a <c>message.id</c> nor a <c>requestId</c> -
    /// the one shape <see cref="ClaudeTranscriptStore.TryIngest"/> can never de-duplicate (see
    /// <c>TryIngest_NoIdOrRequestId_NeverDeduplicates</c>), so it only stays safe from a double count
    /// via the checkpoint offset itself.</summary>
    private static string IdLessRecordJson(string usageJson = GoodUsage) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + When + "\",\"cwd\":\"/work/project\",\"message\":{"
        + "\"model\":\"claude-opus-4\",\"usage\":" + usageJson + "}}";

    private void WriteLines(params string[] lines) => File.WriteAllText(_sessionPath, string.Join("\n", lines) + "\n");

    private void AppendLines(params string[] lines) => File.AppendAllText(_sessionPath, string.Join("\n", lines) + "\n");

    [Fact]
    public async Task RefreshAsync_OneBadLine_DoesNotStopTheGoodOnes()
    {
        WriteLines(RecordJson("good-1"), RecordJson("bad-1", BadInputTokensUsage), RecordJson("good-2"));
        var store = CreateStore();

        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, added);
        Assert.Equal(2, store.AllTotals.Messages);
        Assert.Equal(1, store.MalformedRecordCount);
        Assert.Null(store.LastDirectoryError);
    }

    [Fact]
    public async Task RefreshAsync_NeverThrowsOnABadLine()
    {
        WriteLines(RecordJson("bad-1", BadInputTokensUsage));
        var store = CreateStore();

        var exception = await Record.ExceptionAsync(() => store.RefreshAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RefreshAsync_OffsetAdvancesPastABadLine_SoARepeatRefreshDoesNotReprocessIt()
    {
        WriteLines(RecordJson("bad-1", BadInputTokensUsage));
        var store = CreateStore();

        await store.RefreshAsync(CancellationToken.None);
        var addedOnSecondRefresh = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, addedOnSecondRefresh);
        Assert.Equal(1, store.MalformedRecordCount);
    }

    [Fact]
    public async Task RefreshAsync_ACorrectedRecordCountsOnALaterRefresh()
    {
        WriteLines(RecordJson("msg-1", BadInputTokensUsage));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);
        Assert.Equal(0, store.AllTotals.Messages);

        AppendLines(RecordJson("msg-1"));
        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, added);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(100, store.AllTotals.InputTokens);
    }

    [Fact]
    public async Task RefreshAsync_SecondCall_OnlyReadsWhatWasAppendedSinceTheFirst()
    {
        WriteLines(RecordJson("msg-1"));
        var store = CreateStore();
        Assert.Equal(1, await store.RefreshAsync(CancellationToken.None));

        AppendLines(RecordJson("msg-2"));
        var secondAdded = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, secondAdded);
        Assert.Equal(2, store.AllTotals.Messages);
    }

    [Fact]
    public async Task RefreshAsync_NothingAppended_ReturnsZeroOnTheSecondCall()
    {
        WriteLines(RecordJson("msg-1"));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        var secondAdded = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, secondAdded);
        Assert.Equal(1, store.AllTotals.Messages);
    }

    [Fact]
    public async Task RefreshAsync_FileRotatedSmaller_ResumesFromTheStartRatherThanTheOldOffset()
    {
        WriteLines(RecordJson("msg-1"), RecordJson("msg-2"));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, store.AllTotals.Messages);

        // Simulate rotation: the file is replaced with a shorter one carrying a fresh record.
        WriteLines(RecordJson("msg-3"));
        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, added);
        Assert.Equal(3, store.AllTotals.Messages);
    }

    [Fact]
    public async Task RefreshAsync_PartialTrailingLineWithoutNewline_IsNotConsumedYet()
    {
        File.WriteAllText(_sessionPath, RecordJson("msg-1")); // deliberately no trailing newline
        var store = CreateStore();

        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, added);
        Assert.Equal(0, store.AllTotals.Messages);

        File.AppendAllText(_sessionPath, "\n" + RecordJson("msg-2") + "\n");
        var addedAfterCompletion = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, addedAfterCompletion);
    }

    [Fact]
    public async Task RefreshAsync_BlankAndNonObjectLines_AreSkippedWithoutBeingMalformed()
    {
        WriteLines("", "   ", "[1,2,3]", RecordJson("msg-1"));
        var store = CreateStore();

        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, added);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public async Task RefreshAsync_CorruptJsonLine_IsSkippedWithoutBeingMalformed()
    {
        WriteLines("{\"type\":\"assistant\", this is not valid json", RecordJson("msg-1"));
        var store = CreateStore();

        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, added);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public async Task RefreshAsync_TotalsLandOnTheRecordsLocalDay()
    {
        WriteLines(RecordJson("msg-1"));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);

        // The fixture timestamp (2026-09-14T10:00:00Z) is 6 days before "today" (2026-09-20 per
        // FixedNow, UTC zone): a 7-day window must include it, and a narrower window that excludes
        // Sep 14 must not - pinning down the exact day the record landed on, not just "somewhere".
        Assert.Equal(1, store.WindowTotals(7).Messages);
        Assert.Equal(0, store.WindowTotals(6).Messages);
    }

    [Fact]
    public async Task RefreshAsync_UsesTheInjectedLocalTimeZoneForTheCalendarDay_NotUtc()
    {
        // 2026-09-14T23:30:00Z is still September 14 in UTC, but September 15 in UTC+1.
        var path = Path.Combine(_project, "session.jsonl");
        var line = "{\"type\":\"assistant\",\"timestamp\":\"2026-09-14T23:30:00Z\",\"cwd\":\"/work/project\","
            + "\"message\":{\"id\":\"m1\",\"model\":\"claude-opus-4\",\"usage\":" + GoodUsage + "}}";
        File.WriteAllText(path, line + "\n");
        var utcPlusOne = TimeZoneInfo.CreateCustomTimeZone("Test/UTC+1", TimeSpan.FromHours(1), "UTC+1", "UTC+1");
        var store = new ClaudeTranscriptStore(_root, new FakeClock(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero)), utcPlusOne);

        await store.RefreshAsync(CancellationToken.None);

        // "Today" is Sep 20 in both zones. Sep15..Sep20 (6 days) must include the record; Sep16..Sep20
        // (5 days) must not - proving the record landed on Sep 15 (the UTC+1 local day), not Sep 14 (UTC).
        Assert.Equal(1, store.WindowTotals(6).Messages);
        Assert.Equal(0, store.WindowTotals(5).Messages);
    }

    [Fact]
    public async Task RefreshAsync_SameMessageIdAcrossTwoFiles_CountsOnlyOnce()
    {
        WriteLines(RecordJson("shared-id"));
        var otherProject = Path.Combine(_root, "C--work-other");
        Directory.CreateDirectory(otherProject);
        File.WriteAllText(Path.Combine(otherProject, "session-b.jsonl"), RecordJson("shared-id") + "\n");

        var store = CreateStore();
        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, added);
        Assert.Equal(1, store.AllTotals.Messages);
    }

    [Fact]
    public async Task RefreshAsync_MissingDirectory_ReportsASanitizedActionableErrorRatherThanThrowing()
    {
        var missingRoot = Path.Combine(_root, "does-not-exist");
        var store = CreateStore(missingRoot);

        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, added);
        Assert.NotNull(store.LastDirectoryError);
        Assert.Contains(missingRoot, store.LastDirectoryError!);
    }

    [Fact]
    public async Task RefreshAsync_EmptyDirectory_ReportsNoError()
    {
        var store = CreateStore();

        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, added);
        Assert.Null(store.LastDirectoryError);
        Assert.Equal(0, store.MalformedRecordCount);
    }

    [Fact]
    public async Task RefreshAsync_OneFileUnreadableAmongReadableOnes_CountsTheReadableFilesAndTracksTheFailureWithoutADirectoryError()
    {
        WriteLines(RecordJson("good-1"));
        var otherProject = Path.Combine(_root, "C--work-other");
        Directory.CreateDirectory(otherProject);
        var badPath = Path.Combine(otherProject, "session-b.jsonl");
        File.WriteAllText(badPath, RecordJson("would-have-counted") + "\n");

        var store = CreateStore(path =>
            path == badPath
                ? throw new UnauthorizedAccessException("synthetic: access denied")
                : OpenRealFile(path));

        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, added);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(1, store.FileReadFailureCount);
        Assert.Null(store.LastDirectoryError);
    }

    [Fact]
    public async Task RefreshAsync_UnreadableFile_RetriesFromTheSameOffsetOnceItBecomesReadable()
    {
        WriteLines(RecordJson("msg-1"));
        var shouldThrow = true;
        var store = CreateStore(path => shouldThrow ? throw new IOException("synthetic: locked") : OpenRealFile(path));

        var addedWhileLocked = await store.RefreshAsync(CancellationToken.None);
        Assert.Equal(0, addedWhileLocked);
        Assert.Equal(1, store.FileReadFailureCount);
        Assert.Equal(0, store.AllTotals.Messages);

        shouldThrow = false;
        var addedAfterUnlocked = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, addedAfterUnlocked);
        Assert.Equal(1, store.AllTotals.Messages);
        Assert.Equal(0, store.FileReadFailureCount);
    }

    [Fact]
    public async Task RefreshAsync_NonJsonlFilesInTheProjectFolder_AreIgnored()
    {
        WriteLines(RecordJson("msg-1"));
        File.WriteAllText(Path.Combine(_project, "notes.txt"), "not a transcript");
        var store = CreateStore();

        var added = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, added);
    }

    [Fact]
    public async Task RefreshAsync_AlreadyCancelledToken_ThrowsWithoutReadingAnyFile()
    {
        WriteLines(RecordJson("msg-1"));
        var store = CreateStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RefreshAsync(cts.Token));

        Assert.Equal(0, store.AllTotals.Messages);
        Assert.Equal(0, store.FilesScanned);
    }

    [Fact]
    public async Task RefreshAsync_CancelledBeforeASubsequentCall_ThrowsWithoutDisturbingPriorState()
    {
        WriteLines(RecordJson("msg-1"));
        var store = CreateStore();
        await store.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, store.AllTotals.Messages);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RefreshAsync(cts.Token));

        Assert.Equal(1, store.AllTotals.Messages);
    }

    [Fact]
    public async Task RefreshAsync_CancelledMidReadAfterAnIdLessLine_CheckpointsSoRetryDoesNotDoubleCountIt()
    {
        var idLessLine = IdLessRecordJson();
        WriteLines(idLessLine, RecordJson("msg-2"));
        var idLessLineBytes = Encoding.UTF8.GetBytes(idLessLine + "\n");

        using var cts = new CancellationTokenSource();
        var openCallCount = 0;
        var store = CreateStore(path =>
        {
            openCallCount++;
            return openCallCount == 1 ? new CancelDuringReadStream(idLessLineBytes, cts) : OpenRealFile(path);
        });

        // The token is cancelled by CancelDuringReadStream itself, from inside the read that hands
        // back the id-less line - simulating a caller cancellation observed between chunks, after
        // that line was already folded into the counters, not before any progress was made.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RefreshAsync(cts.Token));
        Assert.Equal(1, store.AllTotals.Messages);

        var addedOnRetry = await store.RefreshAsync(CancellationToken.None);

        // Without a checkpoint commit on the cancelled path, the retry would resume from offset 0,
        // re-read the id-less line (de-duplication cannot catch it), and double-count it.
        Assert.Equal(1, addedOnRetry);
        Assert.Equal(2, store.AllTotals.Messages);
    }

    [Fact]
    public async Task RefreshAsync_IOExceptionMidReadAfterAnIdLessLine_CheckpointsSoRetryDoesNotDoubleCountIt()
    {
        var idLessLine = IdLessRecordJson();
        WriteLines(idLessLine, RecordJson("msg-2"));
        var idLessLineBytes = Encoding.UTF8.GetBytes(idLessLine + "\n");

        var openCallCount = 0;
        var store = CreateStore(path =>
        {
            openCallCount++;
            return openCallCount == 1 ? new ThrowAfterFirstLineStream(idLessLineBytes) : OpenRealFile(path);
        });

        // ThrowAfterFirstLineStream hands back the id-less line on its first read, then raises
        // IOException on the next one - a transcript becoming briefly unreadable mid-file. The store
        // swallows this (RefreshAsync never throws for it), so it must not be mistaken for the
        // cancellation path above.
        var addedOnFirstCall = await store.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, addedOnFirstCall);
        Assert.Equal(1, store.AllTotals.Messages);

        var addedOnRetry = await store.RefreshAsync(CancellationToken.None);

        // Without a checkpoint commit on the handled-I/O-failure path, the retry would resume from
        // offset 0, re-read the id-less line (de-duplication cannot catch it), and double-count it.
        Assert.Equal(1, addedOnRetry);
        Assert.Equal(2, store.AllTotals.Messages);
    }

    /// <summary>Test-only <see cref="Stream"/> that returns exactly one line's bytes from a single
    /// <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> call and, as part of that same call,
    /// cancels <paramref name="cancelSource"/> - so the cancellation is observed by
    /// <c>ClaudeTranscriptStore.ReadFileAsync</c>'s own <c>ThrowIfCancellationRequested</c> check right
    /// after that line has already been folded into the counters, deterministically, with no reliance
    /// on real timing or threads.</summary>
    private sealed class CancelDuringReadStream(byte[] firstLine, CancellationTokenSource cancelSource) : Stream
    {
        private bool _served;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => firstLine.Length;

        public override long Position { get; set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served)
            {
                return ValueTask.FromResult(0);
            }

            _served = true;
            firstLine.AsSpan().CopyTo(buffer.Span);
            cancelSource.Cancel();
            return ValueTask.FromResult(firstLine.Length);
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = offset;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Test-only <see cref="Stream"/> that returns <paramref name="firstLine"/>'s bytes on its
    /// first <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> call and raises
    /// <see cref="IOException"/> on every call after that - standing in for a transcript that becomes
    /// briefly unreadable (rotation, antivirus lock, ...) partway through a read, without any real
    /// file-handle trickery.</summary>
    private sealed class ThrowAfterFirstLineStream(byte[] firstLine) : Stream
    {
        private int _readCount;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => firstLine.Length;

        public override long Position { get; set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readCount++;
            if (_readCount == 1)
            {
                firstLine.AsSpan().CopyTo(buffer.Span);
                return ValueTask.FromResult(firstLine.Length);
            }

            throw new IOException("synthetic mid-file read failure");
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = offset;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
