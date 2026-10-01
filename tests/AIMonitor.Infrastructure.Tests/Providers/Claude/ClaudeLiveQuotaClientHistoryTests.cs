using System.Net;
using System.Text;
using AIMonitor.Application.Providers;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// PAR-004 (partial success): a live-quota success/failure and a local-transcript-history
/// success/failure are independent outcomes within one <see cref="ClaudeLiveQuotaClient.GetSnapshotAsync"/>
/// call - neither one can suppress the other. Every test uses a disposable temp directory unique to
/// itself and synthetic content only; never a real credential, profile, or network call.
/// </summary>
[Trait("Category", "Contract")]
public sealed class ClaudeLiveQuotaClientHistoryTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-claude-live-history-tests-" + Guid.NewGuid().ToString("N"));

    public ClaudeLiveQuotaClientHistoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void WriteCredentials(DateTimeOffset expiresAtUtc)
    {
        var path = Path.Combine(_directory, ".credentials.json");
        var json = "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-token\",\"expiresAt\":"
            + expiresAtUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"subscriptionType\":\"pro\"}}";
        File.WriteAllText(path, json);
    }

    private void WriteTranscript(string model, long inputTokens, string messageId, DateTimeOffset timestamp)
    {
        var project = Path.Combine(_directory, "projects", "C--work-project");
        Directory.CreateDirectory(project);
        var json = "{\"type\":\"assistant\",\"timestamp\":\""
            + timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            + "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"" + model
            + "\",\"usage\":{\"input_tokens\":" + inputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"output_tokens\":0}}}";
        File.AppendAllText(Path.Combine(project, "session.jsonl"), json + "\n");
    }

    private const string ValidUsagePayload = """{"limits":[{"kind":"session","percent":12.5,"severity":"normal"}]}""";

    private static HttpResponseMessage SuccessResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private ClaudeLiveQuotaClient CreateClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            _directory,
            _directory,
            new FakeClock(FixedNow),
            timeout: TimeSpan.FromSeconds(5),
            transcriptTimeZone: TimeZoneInfo.Utc);

    private static ProviderSnapshotRequest HistoryRequest(int days = 14) =>
        new(historyDays: days, metric: UsageMetric.TotalTokens, includeHistory: true);

    [Fact]
    public async Task GetSnapshotAsync_IncludeHistoryFalse_NeverTouchesTranscriptsAndStaysExactlyAsBefore()
    {
        WriteCredentials(FixedNow.AddHours(1));
        WriteTranscript("claude-opus-5", 1_000, "m1", FixedNow);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(ProviderSnapshotRequest.Default, CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Null(snapshot.History);
        Assert.Empty(snapshot.Stats);
        Assert.Null(snapshot.HistoryError);
    }

    [Fact]
    public async Task GetSnapshotAsync_QuotaSucceeds_NoTranscriptsDirectory_QuotaStaysOkAndHistoryErrorIsSet()
    {
        WriteCredentials(FixedNow.AddHours(1));
        // Deliberately no `projects` directory under _directory.
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.True(snapshot.Ok, "a refresh with live quota data is a success even when history is not");
        Assert.Null(snapshot.Error);
        Assert.Single(snapshot.Meters);
        Assert.NotNull(snapshot.HistoryError);
        Assert.Null(snapshot.History);
        Assert.Empty(snapshot.Stats);
    }

    [Fact]
    public async Task GetSnapshotAsync_QuotaSucceeds_TranscriptsDirectoryExistsButEmpty_ProducesValidEmptyHistoryAndZeroStat()
    {
        WriteCredentials(FixedNow.AddHours(1));
        // The `projects` directory exists and is successfully scanned - it just has nothing in it,
        // which is a real (not invented) empty result, unlike the missing-directory case above.
        Directory.CreateDirectory(Path.Combine(_directory, "projects"));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Null(snapshot.HistoryError);
        Assert.NotNull(snapshot.History);
        Assert.Empty(snapshot.History!.ByModel);
        var stat = Assert.Single(snapshot.Stats);
        Assert.Equal("$0.00", stat.Value);
    }

    [Fact]
    public async Task GetSnapshotAsync_OneTranscriptFileUnreadableAmongReadableOnes_HistoryErrorSetButReadableDataAndQuotaSurvive()
    {
        WriteCredentials(FixedNow.AddHours(1));
        WriteTranscript("claude-opus-5", 1_000, "good-1", FixedNow);
        var badProject = Path.Combine(_directory, "projects", "C--work-bad");
        Directory.CreateDirectory(badProject);
        var badPath = Path.Combine(badProject, "session.jsonl");
        File.WriteAllText(badPath, "irrelevant - the open itself fails\n");

        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = new ClaudeLiveQuotaClient(
            new HttpClient(handler),
            _directory,
            _directory,
            new FakeClock(FixedNow),
            transcriptOpenFile: path => path == badPath
                ? throw new IOException("synthetic: locked")
                : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            timeout: TimeSpan.FromSeconds(5),
            transcriptTimeZone: TimeZoneInfo.Utc);

        var snapshot = await client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.NotNull(snapshot.HistoryError);
        // Sanitized: the failing file's path/name never appears in the note shown to the user.
        Assert.DoesNotContain(badPath, snapshot.HistoryError);
        Assert.DoesNotContain("session.jsonl", snapshot.HistoryError);
        Assert.NotNull(snapshot.History);
        Assert.NotEmpty(snapshot.History!.ByModel);
    }

    [Fact]
    public async Task GetSnapshotAsync_QuotaSucceeds_TranscriptsPresent_BothPortionsArePopulated()
    {
        WriteCredentials(FixedNow.AddHours(1));
        WriteTranscript("claude-opus-5", 1_000_000, "m1", FixedNow);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Single(snapshot.Meters);
        Assert.Null(snapshot.HistoryError);
        Assert.NotNull(snapshot.History);
        Assert.NotEmpty(snapshot.History!.ByModel);
        var stat = Assert.Single(snapshot.Stats);
        Assert.Equal("Equivalent API value", stat.Label);
        Assert.Equal("$5.00", stat.Value);
        Assert.Equal("at API list price", stat.Detail);
    }

    [Fact]
    public async Task GetSnapshotAsync_UnpricedModelInHistory_StatDetailNamesItAndExcludesItFromTheValue()
    {
        WriteCredentials(FixedNow.AddHours(1));
        WriteTranscript("gpt-5", 500, "m1", FixedNow);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        var stat = Assert.Single(snapshot.Stats);
        Assert.Equal("$0.00", stat.Value);
        Assert.Contains("no published price", stat.Detail);
        Assert.Contains("gpt-5", stat.Detail);
    }

    [Fact]
    public async Task GetSnapshotAsync_MissingCredentials_HistoryIsStillReadAndReported()
    {
        // No credentials file written at all - the quota portion must fail.
        WriteTranscript("claude-opus-5", 1_000, "m1", FixedNow);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.Error);
        Assert.True(snapshot.Unauthorized);
        Assert.Equal(0, handler.CallCount);
        // The quota failure must not suppress history that is otherwise readable.
        Assert.Null(snapshot.HistoryError);
        Assert.NotEmpty(snapshot.Stats);
        Assert.NotNull(snapshot.History);
        Assert.NotEmpty(snapshot.History!.ByModel);
    }

    [Fact]
    public async Task GetSnapshotAsync_ExpiredCredentials_HistoryIsStillReadAndReported()
    {
        WriteCredentials(FixedNow.AddSeconds(-1));
        WriteTranscript("claude-opus-5", 1_000, "m1", FixedNow);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.True(snapshot.Unauthorized);
        Assert.NotNull(snapshot.Error);
        Assert.Equal(0, handler.CallCount);
        Assert.NotEmpty(snapshot.History!.ByModel);
    }

    [Fact]
    public async Task GetSnapshotAsync_QuotaFailsWithHttpError_HistoryIsStillReadAndReported()
    {
        WriteCredentials(FixedNow.AddHours(1));
        WriteTranscript("claude-opus-5", 1_000, "m1", FixedNow);
        var handler = new FakeHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = CreateClient(handler);

        var snapshot = await client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.NotNull(snapshot.Error);
        Assert.Null(snapshot.HistoryError);
        Assert.NotEmpty(snapshot.History!.ByModel);
    }

    [Fact]
    public async Task GetSnapshotAsync_AlreadyCancelled_ThrowsBeforeTouchingHistoryOrQuota()
    {
        WriteCredentials(FixedNow.AddHours(1));
        WriteTranscript("claude-opus-5", 1_000, "m1", FixedNow);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSnapshotAsync(HistoryRequest(), cts.Token));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_HistoryDaysControlsTheWindow()
    {
        WriteCredentials(FixedNow.AddHours(1));
        WriteTranscript("claude-opus-5", 1_000, "old", FixedNow.AddDays(-40));
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var client = CreateClient(handler);

        var narrowSnapshot = await client.GetSnapshotAsync(HistoryRequest(days: 7), CancellationToken.None);
        var wideSnapshot = await client.GetSnapshotAsync(HistoryRequest(days: 90), CancellationToken.None);

        Assert.Empty(narrowSnapshot.History!.ByModel);
        Assert.NotEmpty(wideSnapshot.History!.ByModel);
    }

    private void WriteSingleRecordTranscript(out byte[] recordBytes)
    {
        var project = Path.Combine(_directory, "projects", "C--work-project");
        Directory.CreateDirectory(project);
        var json = "{\"type\":\"assistant\",\"timestamp\":\""
            + FixedNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            + "\",\"message\":{\"id\":\"m1\",\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":1000,\"output_tokens\":0}}}";
        File.WriteAllText(Path.Combine(project, "session.jsonl"), json + "\n");
        recordBytes = Encoding.UTF8.GetBytes(json + "\n");
    }

    [Fact]
    public async Task GetSnapshotAsync_OverlappingHistoryCalls_SerializesTheSharedStoreSoNeitherInterleavesWithTheOther()
    {
        WriteCredentials(FixedNow.AddHours(1));
        WriteSingleRecordTranscript(out var recordBytes);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var openFileCallCount = 0;
        var gate = new GatedStream(recordBytes);
        var client = new ClaudeLiveQuotaClient(
            new HttpClient(handler),
            _directory,
            _directory,
            new FakeClock(FixedNow),
            transcriptOpenFile: _ =>
            {
                Interlocked.Increment(ref openFileCallCount);
                return gate;
            },
            timeout: TimeSpan.FromSeconds(5),
            transcriptTimeZone: TimeZoneInfo.Utc);

        var callA = client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);
        var callB = client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);

        // callA's refresh is now stuck mid-read, inside the gate, holding it. Give callB a generous
        // window to run as far as it possibly can: without serialization it would reach the
        // transcript store's own file-open step too (a second call to the factory above) instead of
        // waiting behind the semaphore callA holds.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, Volatile.Read(ref openFileCallCount));
        Assert.False(callB.IsCompleted, "callB must wait for the gate callA is holding, not interleave with it");

        gate.Release();
        var snapshotA = await callA;
        var snapshotB = await callB;

        Assert.True(snapshotA.Ok);
        Assert.True(snapshotB.Ok);
        Assert.Null(snapshotA.HistoryError);
        Assert.Null(snapshotB.HistoryError);
        Assert.NotEmpty(snapshotA.History!.ByModel);
        Assert.NotEmpty(snapshotB.History!.ByModel);
    }

    [Fact]
    public async Task GetSnapshotAsync_CancelledWhileWaitingForAnOverlappingHistoryCall_ThrowsWithoutDisturbingTheInFlightCallOrLeakingTheGate()
    {
        WriteCredentials(FixedNow.AddHours(1));
        WriteSingleRecordTranscript(out var recordBytes);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(SuccessResponse(ValidUsagePayload)));
        var gate = new GatedStream(recordBytes);
        var client = new ClaudeLiveQuotaClient(
            new HttpClient(handler),
            _directory,
            _directory,
            new FakeClock(FixedNow),
            transcriptOpenFile: _ => gate,
            timeout: TimeSpan.FromSeconds(5),
            transcriptTimeZone: TimeZoneInfo.Utc);

        var callA = client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var callB = client.GetSnapshotAsync(HistoryRequest(), cts.Token);

        // callA holds the gate; callB must genuinely be waiting on it (not on file I/O of its own)
        // before it gets cancelled, so this proves cancellation of a *gate wait* propagates.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(callB.IsCompleted, "callB must be waiting on the gate callA holds before it is cancelled");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callB);

        gate.Release();
        var snapshotA = await callA;
        Assert.True(snapshotA.Ok, "callA must be unaffected by callB's cancellation");

        // A cancelled gate wait must not leave the gate permanently held - release was never paired
        // with an acquire for callB, so a fresh call afterward must still succeed normally.
        var snapshotC = await client.GetSnapshotAsync(HistoryRequest(), CancellationToken.None);
        Assert.True(snapshotC.Ok);
    }

    /// <summary>Test-only <see cref="Stream"/> that returns <paramref name="content"/> on its first
    /// <see cref="ReadAsync"/> call, then suspends every call after that until <see cref="Release"/>
    /// is called - letting a test hold a <see cref="ClaudeLiveQuotaClient.GetSnapshotAsync"/> call
    /// "in progress" inside its transcript refresh for as long as needed to prove a second, overlapping
    /// call genuinely waits rather than interleaving with it.</summary>
    private sealed class GatedStream(byte[] content) : Stream
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _served;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position { get; set; }

        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served)
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            _served = true;
            content.AsSpan().CopyTo(buffer.Span);
            return content.Length;
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
