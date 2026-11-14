using System.Globalization;
using System.Text;
using System.Text.Json;
using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.OpenAi;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAi;

/// <summary>
/// PAR-008: Codex ChatGPT OAuth always wins over a saved Admin key, an environment variable, or a key
/// in Codex CLI's own login file, and an expired Codex OAuth login is reported as such rather than
/// silently falling back. Every test uses a synthetic <c>auth.json</c> under a unique temp directory;
/// none inspect the real <c>%USERPROFILE%</c>, <c>CODEX_HOME</c>, or environment.
/// </summary>
[Trait("Category", "Contract")]
public sealed class OpenAiCredentialResolverTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-openai-resolver-tests-" + Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new(FixedNow);

    public OpenAiCredentialResolverTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string AuthPath => Path.Combine(_directory, "auth.json");

    private void WriteAuth(string json) => File.WriteAllText(AuthPath, json);

    private static string Jwt(Dictionary<string, object?> claims)
    {
        var json = JsonSerializer.Serialize(claims);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"header.{payload}.signature";
    }

    private static string AccessTokenJwt(string accountId, double? expEpochSeconds)
    {
        var claims = new Dictionary<string, object?>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object?> { ["chatgpt_account_id"] = accountId },
        };
        if (expEpochSeconds is double exp)
        {
            claims["exp"] = exp;
        }

        return Jwt(claims);
    }

    private static string IdTokenJwt(string email) => Jwt(new Dictionary<string, object?> { ["email"] = email });

    /// <summary>Builds a synthetic Codex CLI <c>auth.json</c> with a ChatGPT OAuth login.</summary>
    private static string BuildOAuthAuthJson(
        string accountId, double? expEpochSeconds = null, bool includeTokensAccountId = true, string idTokenEmail = "user@example.com")
    {
        var tokens = new Dictionary<string, object?>
        {
            ["access_token"] = AccessTokenJwt(accountId, expEpochSeconds),
            ["id_token"] = IdTokenJwt(idTokenEmail),
        };
        if (includeTokensAccountId)
        {
            tokens["account_id"] = accountId;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["tokens"] = tokens });
    }

    private static Func<string, string?> NoEnv() => _ => null;

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(pair => pair.Name == name).Value;

    [Fact]
    public async Task DetectAsync_ValidCodexOAuth_TakesPriorityOverSavedAdminKeyAndEnvironment()
    {
        WriteAuth(BuildOAuthAuthJson("account-1"));

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: "sk-admin-saved",
            environmentVariableReader: Env(("OPENAI_ADMIN_KEY", "sk-admin-env")),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
        Assert.Equal(OpenAiCredentialResolver.CodexCliSourceId, resolution.Detection.SourceId);
        Assert.Equal(OpenAiCredentialKind.OAuth, resolution.Credential!.Kind);
        Assert.Equal("account-1", resolution.Credential.AccountId);
    }

    [Fact]
    public async Task DetectAsync_ExpiredCodexOAuth_ReturnsExpiredWithoutFallingBackToAdminKey()
    {
        var expired = FixedNow.AddHours(-1).ToUnixTimeSeconds();
        WriteAuth(BuildOAuthAuthJson("account-1", expired));

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: "sk-admin-saved",
            environmentVariableReader: NoEnv(),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(DetectionState.Expired, resolution.Detection.State);
        Assert.Equal(OpenAiCredentialResolver.CodexCliSourceId, resolution.Detection.SourceId);
        Assert.Equal(OpenAiCredentialKind.OAuth, resolution.Credential!.Kind);
        Assert.NotEmpty(resolution.Detection.Hint);
    }

    [Fact]
    public async Task DetectAsync_CodexOAuthExactlyAtExpiry_IsExpired()
    {
        WriteAuth(BuildOAuthAuthJson("account-1", FixedNow.ToUnixTimeSeconds()));

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            null, NoEnv(), AuthPath, _clock, CancellationToken.None);

        Assert.Equal(DetectionState.Expired, resolution.Detection.State);
    }

    [Fact]
    public async Task DetectAsync_NoCodexLogin_FallsBackToSavedAdminKey()
    {
        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: "sk-admin-saved",
            environmentVariableReader: Env(("OPENAI_ADMIN_KEY", "sk-admin-env")),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
        Assert.Equal(OpenAiCredentialResolver.ManualSourceId, resolution.Detection.SourceId);
        Assert.Equal("sk-admin-saved", resolution.Credential!.Value);
    }

    [Fact]
    public async Task DetectAsync_NoCodexLoginAndNoSavedKey_FallsBackToEnvironmentAdminKeyOverApiKey()
    {
        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: null,
            environmentVariableReader: Env(("OPENAI_ADMIN_KEY", "sk-admin-env"), ("OPENAI_API_KEY", "sk-project")),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
        Assert.Equal(OpenAiCredentialResolver.EnvSourceId, resolution.Detection.SourceId);
        Assert.Equal("sk-admin-env", resolution.Credential!.Value);
    }

    [Fact]
    public async Task DetectAsync_OnlyOrdinaryEnvironmentKey_IsLimitedWithActionableReason()
    {
        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: null,
            environmentVariableReader: Env(("OPENAI_API_KEY", "sk-project")),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(DetectionState.Limited, resolution.Detection.State);
        Assert.Equal(OpenAiCredentialResolver.EnvSourceId, resolution.Detection.SourceId);
        Assert.NotEmpty(resolution.Detection.Hint);
        Assert.Contains("Admin key", resolution.Detection.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectAsync_OrdinarySavedKey_IsLimitedEvenWhenEnvironmentHasAdminKey()
    {
        // The saved key is probed first; being Limited (not Connected) means probing continues, and
        // the environment Admin key further down the list should win instead.
        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: "sk-project-saved",
            environmentVariableReader: Env(("OPENAI_ADMIN_KEY", "sk-admin-env")),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
        Assert.Equal(OpenAiCredentialResolver.EnvSourceId, resolution.Detection.SourceId);
    }

    [Fact]
    public async Task DetectAsync_CodexLoginStoresRawApiKey_CompetesOnEqualFootingNotPrioritized()
    {
        // `codex login --api-key` writes a top-level OPENAI_API_KEY field rather than ChatGPT OAuth
        // tokens - this is API-key-kind, not OAuth-kind, so it must not jump the queue.
        WriteAuth("""{"OPENAI_API_KEY":"sk-project-from-codex"}""");

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: "sk-admin-saved",
            environmentVariableReader: NoEnv(),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(OpenAiCredentialResolver.ManualSourceId, resolution.Detection.SourceId);
    }

    [Fact]
    public async Task DetectAsync_CodexLoginStoresRawApiKey_UsedWhenNothingElseIsPresent()
    {
        WriteAuth("""{"OPENAI_API_KEY":"sk-project-from-codex"}""");

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: null,
            environmentVariableReader: NoEnv(),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(DetectionState.Limited, resolution.Detection.State);
        Assert.Equal(OpenAiCredentialResolver.CodexCliSourceId, resolution.Detection.SourceId);
        Assert.Equal(OpenAiCredentialKind.ApiKey, resolution.Credential!.Kind);
    }

    [Fact]
    public async Task DetectAsync_CodexLoginMissingAccountId_IsLimitedWithReason()
    {
        WriteAuth(BuildOAuthAuthJson(accountId: string.Empty, includeTokensAccountId: false));

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            null, NoEnv(), AuthPath, _clock, CancellationToken.None);

        Assert.Equal(DetectionState.Limited, resolution.Detection.State);
        Assert.Equal(OpenAiCredentialResolver.CodexCliSourceId, resolution.Detection.SourceId);
        Assert.Equal(OpenAiCredentialKind.OAuth, resolution.Credential!.Kind);
        Assert.Equal(string.Empty, resolution.Credential.AccountId);
        Assert.Contains("account ID", resolution.Detection.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectAsync_AccountIdPrefersTokensFieldOverJwtClaim()
    {
        var json = BuildOAuthAuthJson(accountId: "from-claim", includeTokensAccountId: false);
        using var document = JsonDocument.Parse(json);
        var tokens = new Dictionary<string, object?>
        {
            ["access_token"] = document.RootElement.GetProperty("tokens").GetProperty("access_token").GetString(),
            ["id_token"] = document.RootElement.GetProperty("tokens").GetProperty("id_token").GetString(),
            ["account_id"] = "from-tokens-field",
        };
        WriteAuth(JsonSerializer.Serialize(new Dictionary<string, object?> { ["tokens"] = tokens }));

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            null, NoEnv(), AuthPath, _clock, CancellationToken.None);

        Assert.Equal("from-tokens-field", resolution.Credential!.AccountId);
    }

    [Fact]
    public async Task DetectAsync_AccountIdFallsBackToJwtClaimWhenTokensFieldMissing()
    {
        WriteAuth(BuildOAuthAuthJson(accountId: "from-claim", includeTokensAccountId: false));

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            null, NoEnv(), AuthPath, _clock, CancellationToken.None);

        Assert.Equal("from-claim", resolution.Credential!.AccountId);
    }

    [Fact]
    public async Task DetectAsync_ValidCodexOAuthCoexistingWithTopLevelApiKey_OAuthWinsNotTheApiKey()
    {
        // Codex CLI normally writes only one or the other, but a stale OPENAI_API_KEY left over from
        // an earlier `codex login --api-key` can end up alongside a fresh ChatGPT OAuth login in the
        // same auth.json. The OAuth login must still win (PAR-008), never the API key.
        using var oauthDocument = JsonDocument.Parse(BuildOAuthAuthJson("account-1"));
        var tokens = oauthDocument.RootElement.GetProperty("tokens");
        var merged = new Dictionary<string, object?>
        {
            ["OPENAI_API_KEY"] = "sk-stale-from-codex",
            ["tokens"] = new Dictionary<string, object?>
            {
                ["access_token"] = tokens.GetProperty("access_token").GetString(),
                ["id_token"] = tokens.GetProperty("id_token").GetString(),
                ["account_id"] = tokens.GetProperty("account_id").GetString(),
            },
        };
        WriteAuth(JsonSerializer.Serialize(merged));

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: "sk-admin-saved",
            environmentVariableReader: NoEnv(),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(OpenAiCredentialResolver.CodexCliSourceId, resolution.Detection.SourceId);
        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
        Assert.Equal(OpenAiCredentialKind.OAuth, resolution.Credential!.Kind);
        Assert.Equal("account-1", resolution.Credential.AccountId);
    }

    [Fact]
    public async Task DetectAsync_ExpiredCodexOAuthCoexistingWithTopLevelApiKey_StaysExpiredNeverFallsBackToApiKey()
    {
        var expired = FixedNow.AddHours(-1).ToUnixTimeSeconds();
        using var oauthDocument = JsonDocument.Parse(BuildOAuthAuthJson("account-1", expired));
        var tokens = oauthDocument.RootElement.GetProperty("tokens");
        var merged = new Dictionary<string, object?>
        {
            ["OPENAI_API_KEY"] = "sk-stale-from-codex",
            ["tokens"] = new Dictionary<string, object?>
            {
                ["access_token"] = tokens.GetProperty("access_token").GetString(),
                ["id_token"] = tokens.GetProperty("id_token").GetString(),
                ["account_id"] = tokens.GetProperty("account_id").GetString(),
            },
        };
        WriteAuth(JsonSerializer.Serialize(merged));

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: "sk-admin-saved",
            environmentVariableReader: NoEnv(),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        // Terminal: an expired-but-present Codex OAuth login must never be silently replaced by the
        // coexisting API key or the saved Admin key, even though both would otherwise be usable.
        Assert.Equal(DetectionState.Expired, resolution.Detection.State);
        Assert.Equal(OpenAiCredentialResolver.CodexCliSourceId, resolution.Detection.SourceId);
        Assert.Equal(OpenAiCredentialKind.OAuth, resolution.Credential!.Kind);
    }

    [Fact]
    public async Task DetectAsync_CodexOAuthMissingAccessTokenWithTopLevelApiKey_FallsBackToApiKey()
    {
        // `tokens` is a well-formed object but has no usable access token: only then may the
        // coexisting top-level API key be used.
        WriteAuth("""{"tokens":{"account_id":"account-1"},"OPENAI_API_KEY":"sk-project-from-codex"}""");

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: null,
            environmentVariableReader: NoEnv(),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(OpenAiCredentialResolver.CodexCliSourceId, resolution.Detection.SourceId);
        Assert.Equal(OpenAiCredentialKind.ApiKey, resolution.Credential!.Kind);
    }

    [Fact]
    public async Task DetectAsync_MalformedTokensField_IsTreatedAsNoCodexLoginEvenWithTopLevelApiKey()
    {
        // A non-object `tokens` invalidates the whole login, checked before OPENAI_API_KEY.
        WriteAuth("""{"tokens":"not-an-object","OPENAI_API_KEY":"sk-project"}""");

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            savedAdminKey: "sk-admin-saved",
            environmentVariableReader: NoEnv(),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(OpenAiCredentialResolver.ManualSourceId, resolution.Detection.SourceId);
    }

    [Fact]
    public async Task DetectAsync_MissingAuthFile_FallsBackAsIfNoCodexLoginExists()
    {
        var resolution = await OpenAiCredentialResolver.DetectAsync(
            "sk-admin-saved", NoEnv(), AuthPath, _clock, CancellationToken.None);

        Assert.Equal(OpenAiCredentialResolver.ManualSourceId, resolution.Detection.SourceId);
    }

    [Fact]
    public async Task DetectAsync_CorruptAuthFile_FallsBackAsIfNoCodexLoginExists()
    {
        WriteAuth("{not-json");

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            "sk-admin-saved", NoEnv(), AuthPath, _clock, CancellationToken.None);

        Assert.Equal(OpenAiCredentialResolver.ManualSourceId, resolution.Detection.SourceId);
    }

    [Fact]
    public async Task DetectAsync_OversizedAuthFile_IsTreatedAsUnreadable()
    {
        var huge = "{\"OPENAI_API_KEY\":\"sk-" + new string('a', 2 * 1024 * 1024) + "\"}";
        WriteAuth(huge);

        var resolution = await OpenAiCredentialResolver.DetectAsync(
            "sk-admin-saved", NoEnv(), AuthPath, _clock, CancellationToken.None);

        Assert.Equal(OpenAiCredentialResolver.ManualSourceId, resolution.Detection.SourceId);
    }

    [Fact]
    public async Task DetectAsync_NothingConfiguredAnywhere_ReturnsNotConnected()
    {
        var resolution = await OpenAiCredentialResolver.DetectAsync(
            null, NoEnv(), AuthPath, _clock, CancellationToken.None);

        Assert.Equal(DetectionState.NotConnected, resolution.Detection.State);
        Assert.Null(resolution.Credential);
    }

    [Fact]
    public async Task DetectAsync_IsReadOnly_NeverWritesTheAuthFile()
    {
        var original = BuildOAuthAuthJson("account-1");
        WriteAuth(original);

        await OpenAiCredentialResolver.DetectAsync(null, NoEnv(), AuthPath, _clock, CancellationToken.None);

        Assert.Equal(original, File.ReadAllText(AuthPath));
    }

    [Fact]
    public async Task DetectAsync_AlreadyCancelled_ThrowsWithoutFileAccess()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenAiCredentialResolver.DetectAsync(
            "sk-admin-saved", NoEnv(), AuthPath, _clock, cts.Token));

        Assert.False(File.Exists(AuthPath));
    }

    [Fact]
    public async Task ResolveAsync_FallbackCandidatesAccumulateAcrossAllThreeSources()
    {
        WriteAuth(BuildOAuthAuthJson(accountId: string.Empty, includeTokensAccountId: false));

        var resolution = await OpenAiCredentialResolver.ResolveAsync(
            savedAdminKey: "sk-project-saved",
            environmentVariableReader: Env(("OPENAI_API_KEY", "sk-project-env")),
            codexAuthPath: AuthPath,
            clock: _clock,
            cancellationToken: CancellationToken.None);

        Assert.Equal(3, resolution.Detection.Candidates.Count);
        Assert.Equal(OpenAiCredentialResolver.ManualSourceId, resolution.Detection.Candidates[0].SourceId);
        Assert.Equal(OpenAiCredentialResolver.EnvSourceId, resolution.Detection.Candidates[1].SourceId);
        Assert.Equal(OpenAiCredentialResolver.CodexCliSourceId, resolution.Detection.Candidates[2].SourceId);
        // The first non-connected source (manual) is the fallback that is actually returned.
        Assert.Equal(OpenAiCredentialResolver.ManualSourceId, resolution.Detection.SourceId);
    }

    [Fact]
    public void Credential_ToStringNeverExposesTheSecretValue()
    {
        var credential = new OpenAiCredential(OpenAiCredentialKind.ApiKey, "sk-super-secret-value");

        var text = credential.ToString();

        Assert.DoesNotContain("sk-super-secret-value", text, StringComparison.Ordinal);
    }
}
