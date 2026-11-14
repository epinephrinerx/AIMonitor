using AIMonitor.Infrastructure.Providers.Claude;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Every test uses a disposable temp directory unique to itself and synthetic credential JSON only
/// — never <c>%USERPROFILE%</c>, the real <c>CLAUDE_CONFIG_DIR</c>, or the real <c>.claude</c>
/// directory. Exercises <see cref="ClaudeCredentialFileReader"/> — the async, size-capped,
/// strict-UTF-8 production boundary — end to end, and <see cref="ClaudeCredentialReader"/>'s pure
/// parsing rules through it.
/// </summary>
[Trait("Category", "Contract")]
public sealed class ClaudeCredentialReaderTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-claude-cred-tests-" + Guid.NewGuid().ToString("N"));

    private readonly ClaudeCredentialFileReader _reader = new();

    public ClaudeCredentialReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteCredentials(string json)
    {
        var path = Path.Combine(_directory, ".credentials.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string WriteCredentialsBytes(byte[] bytes)
    {
        var path = Path.Combine(_directory, ".credentials.json");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static FakeClock Clock() => new(FixedNow);

    private Task<ClaudeCredentialReadResult> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        _reader.ReadAsync(path, Clock(), cancellationToken);

    private static string CredentialsJson(
        string accessTokenLiteral, string? expiresAtLiteral = null, string? subscriptionType = "\"pro\"")
    {
        var expires = expiresAtLiteral is null ? string.Empty : ",\"expiresAt\":" + expiresAtLiteral;
        return "{\"claudeAiOauth\":{\"accessToken\":" + accessTokenLiteral + expires
            + ",\"subscriptionType\":" + subscriptionType + ",\"rateLimitTier\":\"default_claude_pro\"}}";
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsMissing()
    {
        var path = Path.Combine(_directory, ".credentials.json");

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Missing, result.Status);
        Assert.Null(result.Credentials);
    }

    [Fact]
    public async Task ReadAsync_MissingContainingDirectory_ReturnsMissing()
    {
        var path = Path.Combine(_directory, "no-such-subdir", ".credentials.json");

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Missing, result.Status);
    }

    [Fact]
    public async Task ReadAsync_ValidUnexpiredCredential_ReturnsFound()
    {
        var expiresAtSeconds = FixedNow.AddHours(1).ToUnixTimeSeconds();
        var path = WriteCredentials(
            CredentialsJson("\"synthetic-token\"", expiresAtSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Found, result.Status);
        Assert.Equal("synthetic-token", result.Credentials!.AccessToken);
        Assert.Equal("pro", result.Credentials.SubscriptionType);
        Assert.Equal("default_claude_pro", result.Credentials.RateLimitTier);
        Assert.Equal(FixedNow.AddHours(1), result.Credentials.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_ExpiryInEpochSeconds_IsNormalizedToUtc()
    {
        var expiresAtSeconds = FixedNow.AddMinutes(30).ToUnixTimeSeconds();
        var path = WriteCredentials(
            CredentialsJson("\"t\"", expiresAtSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var result = await ReadAsync(path);

        Assert.Equal(FixedNow.AddMinutes(30), result.Credentials!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_ExpiryInEpochMilliseconds_IsNormalizedToUtc()
    {
        var expiresAtMilliseconds = FixedNow.AddMinutes(30).ToUnixTimeMilliseconds();
        Assert.True(expiresAtMilliseconds > 10_000_000_000, "fixture must exercise the millisecond branch");
        var path = WriteCredentials(
            CredentialsJson("\"t\"", expiresAtMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var result = await ReadAsync(path);

        Assert.Equal(FixedNow.AddMinutes(30), result.Credentials!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_FractionalEpochSeconds_RoundsToNearestMillisecond()
    {
        var baseSeconds = FixedNow.AddMinutes(30).ToUnixTimeSeconds();
        var path = WriteCredentials(CredentialsJson("\"t\"", $"{baseSeconds}.5"));

        var result = await ReadAsync(path);

        Assert.Equal(FixedNow.AddMinutes(30).AddMilliseconds(500), result.Credentials!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_ExpiryExactlyAtMillisecondThreshold_IsTreatedAsMilliseconds()
    {
        // 10_000_000_000 seconds would be year ~2286; anything above the threshold is milliseconds,
        // so the threshold value itself is still seconds (~ year 2286), one past it is milliseconds.
        var path = WriteCredentials(CredentialsJson("\"t\"", "10000000001"));

        var result = await ReadAsync(path);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(10000000001), result.Credentials!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_MissingExpiresAt_IsUnknownNotExpired()
    {
        var path = WriteCredentials("""{"claudeAiOauth":{"accessToken":"t"}}""");

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Found, result.Status);
        Assert.Null(result.Credentials!.ExpiresAtUtc);
    }

    [Theory]
    [InlineData("\"not-a-number\"")]
    [InlineData("0")]
    [InlineData("-100")]
    [InlineData("null")]
    public async Task ReadAsync_InvalidOrNonPositiveExpiry_IsUnknown(string expiresAtLiteral)
    {
        var path = WriteCredentials(CredentialsJson("\"t\"", expiresAtLiteral));

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Found, result.Status);
        Assert.Null(result.Credentials!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_ExpiryExactlyAtNow_IsExpired()
    {
        var path = WriteCredentials(
            CredentialsJson("\"t\"", FixedNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Expired, result.Status);
        Assert.NotNull(result.Credentials);
    }

    [Fact]
    public async Task ReadAsync_ExpiryOneSecondBeforeNow_IsExpired()
    {
        var path = WriteCredentials(
            CredentialsJson("\"t\"", FixedNow.AddSeconds(-1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Expired, result.Status);
    }

    [Fact]
    public async Task ReadAsync_ExpiryOneSecondAfterNow_IsFound()
    {
        var path = WriteCredentials(
            CredentialsJson("\"t\"", FixedNow.AddSeconds(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Found, result.Status);
    }

    [Fact]
    public async Task ReadAsync_FractionalExpiryExactlyStraddlingNow_IsExpiredOnlyAtOrBelow()
    {
        var beforeMillis = FixedNow.ToUnixTimeMilliseconds() - 1;
        var path = WriteCredentials(CredentialsJson("\"t\"", $"{beforeMillis / 1000.0:F3}"));

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Expired, result.Status);
    }

    [Fact]
    public async Task ReadAsync_ExpiryPointFourMillisecondsBeforeNow_IsExpiredWithTickPrecision()
    {
        // FixedNow falls exactly on a whole second, so basing the fractional literal on the
        // previous whole second plus 0.9996s lands exactly 0.4ms (4000 ticks) before FixedNow,
        // entirely below double's reliable fractional-millisecond precision at this magnitude.
        var baseSeconds = FixedNow.ToUnixTimeSeconds() - 1;
        var path = WriteCredentials(CredentialsJson("\"t\"", $"{baseSeconds}.9996"));

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Expired, result.Status);
        Assert.Equal(FixedNow.AddTicks(-4000), result.Credentials!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_ExpiryPointFourMillisecondsAfterNow_IsFoundWithTickPrecision()
    {
        var baseSeconds = FixedNow.ToUnixTimeSeconds();
        var path = WriteCredentials(CredentialsJson("\"t\"", $"{baseSeconds}.0004"));

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Found, result.Status);
        Assert.Equal(FixedNow.AddTicks(4000), result.Credentials!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_FractionalExpiryAtMillisecondThreshold_IsTreatedAsMillisecondsWithTickPrecision()
    {
        // One past MillisecondEpochThreshold selects the milliseconds branch even when fractional;
        // the fractional part here (0.4ms) must survive as ticks rather than being dropped.
        var path = WriteCredentials(CredentialsJson("\"t\"", "10000000001.4"));

        var result = await ReadAsync(path);

        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(10000000001L * TimeSpan.TicksPerMillisecond + 4000), result.Credentials!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_CorruptJson_ReturnsInvalid()
    {
        var path = WriteCredentials("{not-json");

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_NonObjectRoot_ReturnsInvalid()
    {
        var path = WriteCredentials("[1, 2, 3]");

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_MissingOauthObject_ReturnsInvalid()
    {
        var path = WriteCredentials("""{"somethingElse": true}""");

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_MissingAccessToken_ReturnsInvalid()
    {
        var path = WriteCredentials("""{"claudeAiOauth":{"subscriptionType":"pro"}}""");

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task ReadAsync_BlankOrNonStringAccessToken_ReturnsInvalid(string tokenLiteral)
    {
        var path = WriteCredentials(CredentialsJson(tokenLiteral));

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_AccessTokenContainingControlCharacter_ReturnsInvalid()
    {
        var path = WriteCredentials(CredentialsJson("\"line1\\nline2\""));

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_NonUtf8Bytes_ReturnsInvalidRatherThanThrowing()
    {
        // 0xFF is never valid at the start of a UTF-8 sequence; a strict decoder must reject it.
        var path = WriteCredentialsBytes([0xFF, 0xFE, 0x00, 0x01]);

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_FileLargerThanCap_ReturnsInvalidRatherThanReadingItAll()
    {
        var oversized = "{\"claudeAiOauth\":{\"accessToken\":\"" + new string('a', 2 * 1024 * 1024) + "\"}}";
        var path = WriteCredentials(oversized);

        var result = await ReadAsync(path);

        Assert.Equal(ClaudeCredentialStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_StreamKeepsGrowingPastTheCap_ReturnsInvalidRatherThanReadingUnbounded()
    {
        // Simulates a file that grows after being opened (or a stream whose Length lies): Length
        // reports a small, in-cap value, but reads never end. The cumulative per-chunk cap check
        // must catch this even though it never trusted Length in the first place.
        var path = Path.Combine(_directory, ".credentials.json");
        var stream = new UnboundedGrowingStream();
        var reader = new ClaudeCredentialFileReader(_ => stream);

        var result = await reader.ReadAsync(path, Clock(), CancellationToken.None);

        Assert.Equal(ClaudeCredentialStatus.Invalid, result.Status);
        Assert.True(stream.TotalBytesServed <= (1024 * 1024) + 8192, "must stop well before an unbounded read");
    }

    [Fact]
    public async Task ReadAsync_CancellationDuringChunkedRead_PropagatesWithoutFinishing()
    {
        var path = Path.Combine(_directory, ".credentials.json");
        using var cts = new CancellationTokenSource();
        var stream = new UnboundedGrowingStream(_ => cts.Cancel());
        var reader = new ClaudeCredentialFileReader(_ => stream);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(path, Clock(), cts.Token));
    }

    /// <summary>Hand-written stream that never reaches end-of-stream and under-reports its own
    /// <see cref="Length"/>, standing in for a credentials file that grows (or is replaced with a
    /// larger one) after the reader opened it — without any real filesystem timing race.</summary>
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

    [Fact]
    public async Task ReadAsync_AlreadyCancelledToken_ThrowsWithoutTouchingTheFile()
    {
        var path = Path.Combine(_directory, "never-read.credentials.json");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(path, cts.Token));

        Assert.False(File.Exists(path), "a cancelled read must not create or touch the file");
    }
}
