using System.Text;
using System.Text.Json;
using AIMonitor.Infrastructure.Providers.Gemini;

namespace AIMonitor.Infrastructure.Tests.Providers.Gemini;

/// <summary>
/// Gemini CLI's own Google OAuth login (<c>~/.gemini/oauth_creds.json</c>). Every test uses a
/// disposable temp directory standing in for the user profile directory - never the real
/// <c>%USERPROFILE%</c>.
/// </summary>
[Trait("Category", "Contract")]
public sealed class GeminiCliCredentialReaderTests : IDisposable
{
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    private readonly string _profileDirectory =
        Path.Combine(Path.GetTempPath(), "aimonitor-gemini-cli-tests-" + Guid.NewGuid().ToString("N"));

    public GeminiCliCredentialReaderTests() => Directory.CreateDirectory(_profileDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_profileDirectory))
        {
            Directory.Delete(_profileDirectory, recursive: true);
        }
    }

    private string GeminiHome => Path.Combine(_profileDirectory, ".gemini");

    private void WriteOAuthCreds(string json)
    {
        Directory.CreateDirectory(GeminiHome);
        File.WriteAllText(Path.Combine(GeminiHome, "oauth_creds.json"), json);
    }

    private void WriteGoogleAccounts(string json)
    {
        Directory.CreateDirectory(GeminiHome);
        File.WriteAllText(Path.Combine(GeminiHome, "google_accounts.json"), json);
    }

    private static string Jwt(Dictionary<string, object?> claims)
    {
        var json = JsonSerializer.Serialize(claims);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"header.{payload}.signature";
    }

    private Task<GeminiCredential?> ReadAsync(CancellationToken cancellationToken = default) =>
        GeminiCliCredentialReader.ReadAsync(_profileDirectory, cancellationToken);

    private static string OAuthJson(string accessToken = "gemini-access-token", string scope = CloudPlatformScope, long? expiryDateMs = null, string? idToken = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["access_token"] = accessToken,
            ["scope"] = scope,
        };
        if (expiryDateMs is long expiry)
        {
            payload["expiry_date"] = expiry;
        }

        if (idToken is not null)
        {
            payload["id_token"] = idToken;
        }

        return JsonSerializer.Serialize(payload);
    }

    [Fact]
    public async Task ReadAsync_MissingOAuthFile_ReturnsNull()
    {
        var credential = await ReadAsync();

        Assert.Null(credential);
    }

    [Fact]
    public async Task ReadAsync_MissingAccessToken_ReturnsNull()
    {
        WriteOAuthCreds("""{"scope":"https://www.googleapis.com/auth/cloud-platform"}""");

        var credential = await ReadAsync();

        Assert.Null(credential);
    }

    [Fact]
    public async Task ReadAsync_EmptyAccessToken_ReturnsNull()
    {
        WriteOAuthCreds(OAuthJson(accessToken: ""));

        var credential = await ReadAsync();

        Assert.Null(credential);
    }

    [Fact]
    public async Task ReadAsync_ScopeMissingCloudPlatform_ReturnsNullRatherThanLimited()
    {
        // Matches the Python baseline's actual code (not its aspirational docstring): a token that
        // cannot read Cloud Monitoring is treated as though nothing was found here.
        WriteOAuthCreds(OAuthJson(scope: "https://www.googleapis.com/auth/userinfo.email"));

        var credential = await ReadAsync();

        Assert.Null(credential);
    }

    [Fact]
    public async Task ReadAsync_MissingScopeField_ReturnsNull()
    {
        WriteOAuthCreds("""{"access_token":"t"}""");

        var credential = await ReadAsync();

        Assert.Null(credential);
    }

    [Fact]
    public async Task ReadAsync_CloudPlatformScopeAmongOthers_IsFound()
    {
        WriteOAuthCreds(OAuthJson(scope: $"https://www.googleapis.com/auth/userinfo.email {CloudPlatformScope}"));

        var credential = await ReadAsync();

        Assert.NotNull(credential);
        Assert.True(credential!.UsageCapable);
        Assert.Equal(GeminiCredentialKind.OAuth, credential.Kind);
        Assert.Equal("gemini-access-token", credential.Value);
    }

    [Fact]
    public async Task ReadAsync_NoAccountSourceAvailable_HasEmptyAccount()
    {
        WriteOAuthCreds(OAuthJson());

        var credential = await ReadAsync();

        Assert.NotNull(credential);
        Assert.Equal(string.Empty, credential!.Account);
    }

    [Fact]
    public async Task ReadAsync_GoogleAccountsActiveField_IsPreferredForAccount()
    {
        WriteOAuthCreds(OAuthJson(idToken: Jwt(new Dictionary<string, object?> { ["email"] = "from-jwt@example.com" })));
        WriteGoogleAccounts("""{"active":"from-active@example.com"}""");

        var credential = await ReadAsync();

        Assert.Equal("from-active@example.com", credential!.Account);
    }

    [Fact]
    public async Task ReadAsync_FallsBackToIdTokenClaimWhenGoogleAccountsMissing()
    {
        WriteOAuthCreds(OAuthJson(idToken: Jwt(new Dictionary<string, object?> { ["email"] = "from-jwt@example.com" })));

        var credential = await ReadAsync();

        Assert.Equal("from-jwt@example.com", credential!.Account);
    }

    [Fact]
    public async Task ReadAsync_FallsBackToIdTokenClaimWhenGoogleAccountsHasNoActiveField()
    {
        WriteOAuthCreds(OAuthJson(idToken: Jwt(new Dictionary<string, object?> { ["email"] = "from-jwt@example.com" })));
        WriteGoogleAccounts("""{"other":"field"}""");

        var credential = await ReadAsync();

        Assert.Equal("from-jwt@example.com", credential!.Account);
    }

    [Fact]
    public async Task ReadAsync_IdTokenClaimPriority_PrefersEmailOverSub()
    {
        WriteOAuthCreds(OAuthJson(idToken: Jwt(new Dictionary<string, object?>
        {
            ["sub"] = "subject-id",
            ["email"] = "preferred@example.com",
        })));

        var credential = await ReadAsync();

        Assert.Equal("preferred@example.com", credential!.Account);
    }

    [Fact]
    public async Task ReadAsync_ExpiryDateInMilliseconds_IsNormalizedToUtc()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        WriteOAuthCreds(OAuthJson(expiryDateMs: expiry.ToUnixTimeMilliseconds()));

        var credential = await ReadAsync();

        Assert.NotNull(credential!.ExpiresAtUtc);
        Assert.Equal(expiry.ToUnixTimeMilliseconds(), credential.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ReadAsync_MissingExpiryDate_IsUnknownNotExpired()
    {
        WriteOAuthCreds(OAuthJson());

        var credential = await ReadAsync();

        Assert.Null(credential!.ExpiresAtUtc);
    }

    [Fact]
    public async Task ReadAsync_CorruptOAuthFile_ReturnsNull()
    {
        WriteOAuthCreds("{not-json");

        var credential = await ReadAsync();

        Assert.Null(credential);
    }

    [Fact]
    public async Task ReadAsync_NonObjectOAuthFile_ReturnsNull()
    {
        WriteOAuthCreds("[1,2,3]");

        var credential = await ReadAsync();

        Assert.Null(credential);
    }

    [Fact]
    public async Task ReadAsync_IsReadOnly_NeverModifiesEitherFile()
    {
        var oauthJson = OAuthJson();
        var accountsJson = """{"active":"someone@example.com"}""";
        WriteOAuthCreds(oauthJson);
        WriteGoogleAccounts(accountsJson);

        await ReadAsync();

        Assert.Equal(oauthJson, await File.ReadAllTextAsync(Path.Combine(GeminiHome, "oauth_creds.json")));
        Assert.Equal(accountsJson, await File.ReadAllTextAsync(Path.Combine(GeminiHome, "google_accounts.json")));
    }

    [Fact]
    public async Task ReadAsync_AlreadyCancelledToken_ThrowsWithoutFileAccess()
    {
        WriteOAuthCreds(OAuthJson());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(cts.Token));
    }

    [Fact]
    public void Credential_ToStringNeverExposesTheAccessToken()
    {
        var credential = new GeminiCredential(GeminiCredentialKind.OAuth, "sentinel-access-token-unsafe");

        Assert.DoesNotContain("sentinel-access-token-unsafe", credential.ToString(), StringComparison.Ordinal);
    }
}
