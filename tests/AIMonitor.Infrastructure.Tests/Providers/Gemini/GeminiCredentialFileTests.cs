using AIMonitor.Infrastructure.Providers.Gemini;

namespace AIMonitor.Infrastructure.Tests.Providers.Gemini;

/// <summary>
/// The shared bounded/cancellable/strict-UTF-8 primitive every Gemini credential candidate reads
/// through. Every test uses a disposable temp directory unique to itself and synthetic content only -
/// never the real filesystem outside that directory.
/// </summary>
[Trait("Category", "Contract")]
public sealed class GeminiCredentialFileTests : IDisposable
{
    [Fact]
    public async Task ReadTextAsync_MalformedPath_ReturnsUnreadable()
    {
        var result = await GeminiCredentialFile.ReadTextAsync("bad\0path.json", CancellationToken.None);

        Assert.Equal(GeminiCredentialFile.ReadStatus.Unreadable, result.Status);
        Assert.Null(result.Text);
    }

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-gemini-file-tests-" + Guid.NewGuid().ToString("N"));

    public GeminiCredentialFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteFile(string content)
    {
        var path = Path.Combine(_directory, "credential.json");
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteBytes(byte[] bytes)
    {
        var path = Path.Combine(_directory, "credential.json");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task ReadTextAsync_MissingFile_ReturnsMissing()
    {
        var path = Path.Combine(_directory, "does-not-exist.json");

        var result = await GeminiCredentialFile.ReadTextAsync(path, CancellationToken.None);

        Assert.Equal(GeminiCredentialFile.ReadStatus.Missing, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task ReadTextAsync_MissingContainingDirectory_ReturnsMissing()
    {
        var path = Path.Combine(_directory, "no-such-subdir", "credential.json");

        var result = await GeminiCredentialFile.ReadTextAsync(path, CancellationToken.None);

        Assert.Equal(GeminiCredentialFile.ReadStatus.Missing, result.Status);
    }

    [Fact]
    public async Task ReadTextAsync_ValidUtf8File_ReturnsSuccessWithExactText()
    {
        var path = WriteFile("""{"hello":"world"}""");

        var result = await GeminiCredentialFile.ReadTextAsync(path, CancellationToken.None);

        Assert.Equal(GeminiCredentialFile.ReadStatus.Success, result.Status);
        Assert.Equal("""{"hello":"world"}""", result.Text);
    }

    [Fact]
    public async Task ReadTextAsync_NonUtf8Bytes_ReturnsUnreadableRatherThanThrowing()
    {
        // 0xFF is never valid at the start of a UTF-8 sequence; a strict decoder must reject it.
        var path = WriteBytes([0xFF, 0xFE, 0x00, 0x01]);

        var result = await GeminiCredentialFile.ReadTextAsync(path, CancellationToken.None);

        Assert.Equal(GeminiCredentialFile.ReadStatus.Unreadable, result.Status);
    }

    [Fact]
    public async Task ReadTextAsync_FileLargerThanCap_ReturnsUnreadableRatherThanReadingItAll()
    {
        var oversized = "{\"key\":\"" + new string('a', 2 * 1024 * 1024) + "\"}";
        var path = WriteFile(oversized);

        var result = await GeminiCredentialFile.ReadTextAsync(path, CancellationToken.None);

        Assert.Equal(GeminiCredentialFile.ReadStatus.Unreadable, result.Status);
    }

    [Fact]
    public async Task ReadTextAsync_AlreadyCancelledToken_ThrowsWithoutTouchingTheFile()
    {
        var path = Path.Combine(_directory, "never-read.json");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GeminiCredentialFile.ReadTextAsync(path, cts.Token));

        Assert.False(File.Exists(path), "a cancelled read must not create or touch the file");
    }

    [Fact]
    public async Task ReadTextAsync_CancellationDuringChunkedRead_PropagatesWithoutFinishing()
    {
        var path = Path.Combine(_directory, "grows-forever.json");
        using var cts = new CancellationTokenSource();
        var stream = new UnboundedGrowingStream(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GeminiCredentialFile.ReadTextAsync(path, _ => stream, cts.Token));
    }

    [Fact]
    public async Task ReadTextAsync_StreamKeepsGrowingPastTheCap_ReturnsUnreadableRatherThanReadingUnbounded()
    {
        var path = Path.Combine(_directory, "grows-forever.json");
        var stream = new UnboundedGrowingStream();

        var result = await GeminiCredentialFile.ReadTextAsync(path, _ => stream, CancellationToken.None);

        Assert.Equal(GeminiCredentialFile.ReadStatus.Unreadable, result.Status);
        Assert.True(stream.TotalBytesServed <= (1024 * 1024) + 8192, "must stop well before an unbounded read");
    }

    [Fact]
    public void TryParseObject_ValidObject_ReturnsDocument()
    {
        using var document = GeminiCredentialFile.TryParseObject("""{"a":1}""");

        Assert.NotNull(document);
        Assert.Equal(1, document!.RootElement.GetProperty("a").GetInt32());
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("true")]
    public void TryParseObject_InvalidJsonOrNonObjectRoot_ReturnsNull(string json)
    {
        using var document = GeminiCredentialFile.TryParseObject(json);

        Assert.Null(document);
    }

    /// <summary>Hand-written stream that never reaches end-of-stream and under-reports its own
    /// <see cref="Length"/>, standing in for a credential file that grows (or is replaced with a
    /// larger one) after the reader opened it - without any real filesystem timing race.</summary>
    private sealed class UnboundedGrowingStream : Stream
    {
        private readonly Action<int>? _afterRead;

        public UnboundedGrowingStream(Action<int>? afterRead = null)
        {
            _afterRead = afterRead;
        }

        public int TotalBytesServed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 1;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'a', offset, count);
            TotalBytesServed += count;
            _afterRead?.Invoke(TotalBytesServed);
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
