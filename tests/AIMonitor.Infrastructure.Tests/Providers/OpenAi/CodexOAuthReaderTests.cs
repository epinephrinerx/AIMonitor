using System.Text;
using AIMonitor.Infrastructure.Providers.OpenAI;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAI;

/// <summary>
/// Every test uses a disposable temp directory unique to itself and synthetic auth JSON/JWTs only —
/// never <c>%USERPROFILE%</c>, the real <c>CODEX_HOME</c>, or the real <c>.codex</c> directory.
/// Exercises <see cref="CodexOAuthFileReader"/> — the async, size-capped, strict-UTF-8 production
/// boundary — end to end, and <see cref="CodexOAuthParser"/>'s pure parsing/claim-extraction rules
/// through it. None of these tokens carry a real signature: this codebase never checks one (see
/// <see cref="JwtClaimsReader"/>), so tests build tokens with a fixed dummy header/signature segment.
/// </summary>
[Trait("Category", "Contract")]
public sealed class CodexOAuthReaderTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-codex-oauth-tests-" + Guid.NewGuid().ToString("N"));

    private readonly CodexOAuthFileReader _reader = new();

    public CodexOAuthReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteAuth(string json)
    {
        var path = Path.Combine(_directory, "auth.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string WriteAuthBytes(byte[] bytes)
    {
        var path = Path.Combine(_directory, "auth.json");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static FakeClock Clock() => new(FixedNow);

    private Task<CodexOAuthReadResult> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        _reader.ReadAsync(path, Clock(), cancellationToken);

    private static string Base64Url(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string Jwt(string payloadJson) => $"header.{Base64Url(payloadJson)}.signature";

    private static string ClaimsJson(
        long? exp = null,
        string? email = null,
        string? preferredUsername = null,
        string? name = null,
        string? sub = null,
        string? chatgptAccountId = null)
    {
        var parts = new List<string>();
        if (exp is { } expValue)
        {
            parts.Add("\"exp\":" + expValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (email is not null)
        {
            parts.Add("\"email\":\"" + email + "\"");
        }

        if (preferredUsername is not null)
        {
            parts.Add("\"preferred_username\":\"" + preferredUsername + "\"");
        }

        if (name is not null)
        {
            parts.Add("\"name\":\"" + name + "\"");
        }

        if (sub is not null)
        {
            parts.Add("\"sub\":\"" + sub + "\"");
        }

        if (chatgptAccountId is not null)
        {
            parts.Add("\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"" + chatgptAccountId + "\"}");
        }

        return "{" + string.Join(",", parts) + "}";
    }

    private static string AuthJson(string accessTokenLiteral, string? idToken = null, string? accountId = null)
    {
        var json = "{\"tokens\":{\"access_token\":" + accessTokenLiteral;
        if (idToken is not null)
        {
            json += ",\"id_token\":\"" + idToken + "\"";
        }

        if (accountId is not null)
        {
            json += ",\"account_id\":\"" + accountId + "\"";
        }

        return json + "}}";
    }

    private static string AuthJsonWithToken(string accessToken, string? idToken = null, string? accountId = null) =>
        AuthJson("\"" + accessToken + "\"", idToken, accountId);

    // -- missing --------------------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsMissing()
    {
        var path = Path.Combine(_directory, "auth.json");

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Missing, result.Status);
        Assert.Null(result.Credential);
    }

    [Fact]
    public async Task ReadAsync_MissingContainingDirectory_ReturnsMissing()
    {
        var path = Path.Combine(_directory, "no-such-subdir", "auth.json");

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Missing, result.Status);
    }

    // -- account extraction -----------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_ValidUnexpiredCredential_ReturnsFoundAndKeepsWholeAccessTokenVerbatim()
    {
        var accessToken = Jwt(ClaimsJson(exp: FixedNow.AddHours(1).ToUnixTimeSeconds(), chatgptAccountId: "acct"));
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Found, result.Status);
        Assert.Equal(accessToken, result.Credential!.AccessToken);
        Assert.Equal(FixedNow.AddHours(1), result.Credential.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_Account_PrefersEmailOverEveryOtherIdTokenClaim()
    {
        var idToken = Jwt(ClaimsJson(email: "e@x.com", preferredUsername: "pu", name: "n", sub: "s"));
        var accessToken = Jwt(ClaimsJson(chatgptAccountId: "acct"));
        var path = WriteAuth(AuthJsonWithToken(accessToken, idToken));

        var result = await ReadAsync(path);

        Assert.Equal("e@x.com", result.Credential!.Account);
    }

    [Fact]
    public async Task ReadAsync_Account_FallsBackToPreferredUsernameWhenNoEmail()
    {
        var idToken = Jwt(ClaimsJson(preferredUsername: "pu", name: "n", sub: "s"));
        var accessToken = Jwt(ClaimsJson(chatgptAccountId: "acct"));
        var path = WriteAuth(AuthJsonWithToken(accessToken, idToken));

        var result = await ReadAsync(path);

        Assert.Equal("pu", result.Credential!.Account);
    }

    [Fact]
    public async Task ReadAsync_Account_FallsBackToNameWhenNoEmailOrPreferredUsername()
    {
        var idToken = Jwt(ClaimsJson(name: "n", sub: "s"));
        var accessToken = Jwt(ClaimsJson(chatgptAccountId: "acct"));
        var path = WriteAuth(AuthJsonWithToken(accessToken, idToken));

        var result = await ReadAsync(path);

        Assert.Equal("n", result.Credential!.Account);
    }

    [Fact]
    public async Task ReadAsync_Account_FallsBackToSubWhenNoOtherIdTokenClaim()
    {
        var idToken = Jwt(ClaimsJson(sub: "s"));
        var accessToken = Jwt(ClaimsJson(chatgptAccountId: "acct"));
        var path = WriteAuth(AuthJsonWithToken(accessToken, idToken));

        var result = await ReadAsync(path);

        Assert.Equal("s", result.Credential!.Account);
    }

    [Fact]
    public async Task ReadAsync_Account_FallsBackToTokensAccountIdWhenIdTokenAbsent()
    {
        var accessToken = Jwt(ClaimsJson(chatgptAccountId: "claim-account-id"));
        var path = WriteAuth(AuthJsonWithToken(accessToken, idToken: null, accountId: "top-level-account-id"));

        var result = await ReadAsync(path);

        Assert.Equal("top-level-account-id", result.Credential!.Account);
    }

    [Fact]
    public async Task ReadAsync_Account_EmptyWhenNoClaimsAndNoTokensAccountId()
    {
        var accessToken = Jwt(ClaimsJson());
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(string.Empty, result.Credential!.Account);
    }

    // -- account id / usage-capable ------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_AccountId_PrefersTokensAccountIdOverJwtClaim()
    {
        var accessToken = Jwt(ClaimsJson(chatgptAccountId: "claim-account-id"));
        var path = WriteAuth(AuthJsonWithToken(accessToken, idToken: null, accountId: "tokens-account-id"));

        var result = await ReadAsync(path);

        Assert.Equal("tokens-account-id", result.Credential!.AccountId);
    }

    [Fact]
    public async Task ReadAsync_AccountId_FallsBackToJwtClaimWhenTokensAccountIdAbsent()
    {
        var accessToken = Jwt(ClaimsJson(chatgptAccountId: "claim-account-id"));
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal("claim-account-id", result.Credential!.AccountId);
        Assert.True(result.Credential.IsUsageCapable);
    }

    [Fact]
    public async Task ReadAsync_NoAccountIdAnywhere_IsUsageCapableFalseWithFixedHint_ButStatusIsStillFound()
    {
        var accessToken = Jwt(ClaimsJson(exp: FixedNow.AddHours(1).ToUnixTimeSeconds()));
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Found, result.Status);
        Assert.False(result.Credential!.IsUsageCapable);
        Assert.Equal("Codex login has no account ID. Sign in to Codex again.", result.Credential.LimitedReason);
    }

    [Fact]
    public async Task ReadAsync_AccessTokenNotAValidJwt_StillFoundWithUnknownExpiryAndFallsBackToTokensAccountId()
    {
        var path = WriteAuth(AuthJsonWithToken("not-a-jwt-at-all", idToken: null, accountId: "top-level-id"));

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Found, result.Status);
        Assert.Null(result.Credential!.ExpiresAtUtc);
        Assert.Equal("top-level-id", result.Credential.AccountId);
        Assert.True(result.Credential.IsUsageCapable);
    }

    // -- expiry -----------------------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_ExpiryInEpochSeconds_IsNormalizedToUtc()
    {
        var accessToken = Jwt(ClaimsJson(exp: FixedNow.AddMinutes(30).ToUnixTimeSeconds()));
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(FixedNow.AddMinutes(30), result.Credential!.ExpiresAtUtc);
        Assert.Equal(CodexOAuthStatus.Found, result.Status);
    }

    [Fact]
    public async Task ReadAsync_ExpiryInEpochMilliseconds_IsNormalizedToUtc()
    {
        var expMillis = FixedNow.AddMinutes(30).ToUnixTimeMilliseconds();
        Assert.True(expMillis > 10_000_000_000, "fixture must exercise the millisecond branch");
        var accessToken = Jwt(ClaimsJson(exp: expMillis));
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(FixedNow.AddMinutes(30), result.Credential!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_FractionalEpochSeconds_RoundsToNearestMillisecond()
    {
        var baseSeconds = FixedNow.AddMinutes(30).ToUnixTimeSeconds();
        var accessToken = Jwt("{\"exp\":" + baseSeconds + ".5}");
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(FixedNow.AddMinutes(30).AddMilliseconds(500), result.Credential!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_MissingExp_IsUnknownNotExpired()
    {
        var accessToken = Jwt(ClaimsJson(chatgptAccountId: "acct"));
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Found, result.Status);
        Assert.Null(result.Credential!.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task ReadAsync_NonPositiveExpiry_IsUnknown(long badExp)
    {
        var accessToken = Jwt(ClaimsJson(exp: badExp));
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Found, result.Status);
        Assert.Null(result.Credential!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_ExpNotANumber_IsUnknown()
    {
        var accessToken = Jwt("{\"exp\":\"not-a-number\"}");
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Found, result.Status);
        Assert.Null(result.Credential!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_ExpiryExactlyAtNow_IsExpired()
    {
        var accessToken = Jwt(ClaimsJson(exp: FixedNow.ToUnixTimeSeconds()));
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Expired, result.Status);
        Assert.NotNull(result.Credential);
    }

    [Fact]
    public async Task ReadAsync_ExpiryOneSecondBeforeNow_IsExpired()
    {
        var accessToken = Jwt(ClaimsJson(exp: FixedNow.AddSeconds(-1).ToUnixTimeSeconds()));
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Expired, result.Status);
    }

    [Fact]
    public async Task ReadAsync_ExpiryOneSecondAfterNow_IsFound()
    {
        var accessToken = Jwt(ClaimsJson(exp: FixedNow.AddSeconds(1).ToUnixTimeSeconds()));
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Found, result.Status);
    }

    [Fact]
    public async Task ReadAsync_ExpiredCredential_CanStillBeUsageCapable()
    {
        // Expired-but-usage-capable is exactly the shape OpenAiDetection must treat as terminal
        // Expired rather than falling back to saved/environment/CLI keys — this fixture proves the
        // reader reports both facts accurately, independent of how the detection layer uses them.
        var accessToken = Jwt(ClaimsJson(exp: FixedNow.AddSeconds(-1).ToUnixTimeSeconds(), chatgptAccountId: "acct"));
        var path = WriteAuth(AuthJsonWithToken(accessToken));

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Expired, result.Status);
        Assert.True(result.Credential!.IsUsageCapable);
    }

    // -- malformed --------------------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_CorruptJson_ReturnsInvalid()
    {
        var path = WriteAuth("{not-json");

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_NonObjectRoot_ReturnsInvalid()
    {
        var path = WriteAuth("[1, 2, 3]");

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_MissingTokensObject_ReturnsInvalid()
    {
        var path = WriteAuth("""{"somethingElse": true}""");

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_TokensNotAnObject_ReturnsInvalid()
    {
        var path = WriteAuth("""{"tokens": "oops"}""");

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_MissingAccessToken_ReturnsInvalid()
    {
        var path = WriteAuth("""{"tokens":{"account_id":"acct"}}""");

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task ReadAsync_BlankOrNonStringAccessToken_ReturnsInvalid(string tokenLiteral)
    {
        var path = WriteAuth(AuthJson(tokenLiteral));

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_AccessTokenContainingControlCharacter_ReturnsInvalid()
    {
        var path = WriteAuth("{\"tokens\":{\"access_token\":\"line1\\nline2\"}}");

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_NonUtf8Bytes_ReturnsInvalidRatherThanThrowing()
    {
        // 0xFF is never valid at the start of a UTF-8 sequence; a strict decoder must reject it.
        var path = WriteAuthBytes([0xFF, 0xFE, 0x00, 0x01]);

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_FileLargerThanCap_ReturnsInvalidRatherThanReadingItAll()
    {
        var oversized = "{\"tokens\":{\"access_token\":\"" + new string('a', 2 * 1024 * 1024) + "\"}}";
        var path = WriteAuth(oversized);

        var result = await ReadAsync(path);

        Assert.Equal(CodexOAuthStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ReadAsync_StreamKeepsGrowingPastTheCap_ReturnsInvalidRatherThanReadingUnbounded()
    {
        // Simulates a file that grows after being opened (or a stream whose Length lies): Length
        // reports a small, in-cap value, but reads never end. The cumulative per-chunk cap check
        // must catch this even though it never trusted Length in the first place.
        var path = Path.Combine(_directory, "auth.json");
        var stream = new UnboundedGrowingStream();
        var reader = new CodexOAuthFileReader(_ => stream);

        var result = await reader.ReadAsync(path, Clock(), CancellationToken.None);

        Assert.Equal(CodexOAuthStatus.Invalid, result.Status);
        Assert.True(stream.TotalBytesServed <= (1024 * 1024) + 8192, "must stop well before an unbounded read");
    }

    [Fact]
    public async Task ReadAsync_CancellationDuringChunkedRead_PropagatesWithoutFinishing()
    {
        var path = Path.Combine(_directory, "auth.json");
        using var cts = new CancellationTokenSource();
        var stream = new UnboundedGrowingStream(_ => cts.Cancel());
        var reader = new CodexOAuthFileReader(_ => stream);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(path, Clock(), cts.Token));
    }

    /// <summary>Hand-written stream that never reaches end-of-stream and under-reports its own
    /// <see cref="Length"/>, standing in for an auth file that grows (or is replaced with a larger
    /// one) after the reader opened it — without any real filesystem timing race.</summary>
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
        var path = Path.Combine(_directory, "never-read-auth.json");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(path, cts.Token));

        Assert.False(File.Exists(path), "a cancelled read must not create or touch the file");
    }
}
