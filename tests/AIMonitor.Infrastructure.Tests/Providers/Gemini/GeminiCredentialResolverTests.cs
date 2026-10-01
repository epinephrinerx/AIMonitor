using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.Gemini;
using AIMonitor.TestSupport;

namespace AIMonitor.Infrastructure.Tests.Providers.Gemini;

/// <summary>
/// PAR-011/PAR-012: Gemini's credential is resolved in the deterministic order the baseline's
/// <c>GEMINI_SOURCES</c> establishes - manual (saved service-account file) -&gt; env
/// (<c>GOOGLE_APPLICATION_CREDENTIALS</c>) -&gt; Gemini CLI login -&gt; gcloud ADC - and every candidate
/// necessary is scanned before concluding Limited/Not connected: a source that is present but
/// unusable (Limited or Expired) is remembered as a fallback without stopping the walk, so a usable
/// login later in the list is never hidden by one found first. Every test uses a disposable temp
/// directory standing in for the user profile directory and injects the environment reader - never
/// the real <c>%USERPROFILE%</c> or process environment.
/// </summary>
[Trait("Category", "Contract")]
public sealed class GeminiCredentialResolverTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string CompleteServiceAccountJson =
        """{"type":"service_account","client_email":"robot@example.iam.gserviceaccount.com","project_id":"my-project","private_key":"-----BEGIN PRIVATE KEY----- not-a-real-key"}""";

    private const string IncompleteServiceAccountJson =
        """{"type":"service_account","client_email":"robot@example.iam.gserviceaccount.com","project_id":"my-project"}""";

    private const string UserLoginJson =
        """{"type":"authorized_user","account":"someone@example.invalid"}""";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aimonitor-gemini-resolver-tests-" + Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new(FixedNow);

    public GeminiCredentialResolverTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string ManualPath => Path.Combine(_directory, "manual-key.json");

    private string EnvKeyPath => Path.Combine(_directory, "env-key.json");

    private string GeminiCliOAuthPath => Path.Combine(_directory, ".gemini", "oauth_creds.json");

    private string GcloudWindowsPath => Path.Combine(_directory, "AppData", "Roaming", "gcloud", "application_default_credentials.json");

    private void WriteManual(string json) => File.WriteAllText(ManualPath, json);

    private void WriteEnvKey(string json) => File.WriteAllText(EnvKeyPath, json);

    private void WriteGeminiCli(string accessToken = "cli-token", long? expiryDateMs = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GeminiCliOAuthPath)!);
        var expiryField = expiryDateMs is long value ? ",\"expiry_date\":" + value : string.Empty;
        var json = "{\"access_token\":\"" + accessToken + "\",\"scope\":\"https://www.googleapis.com/auth/cloud-platform\"" + expiryField + "}";
        File.WriteAllText(GeminiCliOAuthPath, json);
    }

    private void WriteGcloudWindows(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GcloudWindowsPath)!);
        File.WriteAllText(GcloudWindowsPath, json);
    }

    private static Func<string, string?> NoEnv() => _ => null;

    private static Func<string, string?> EnvPointingAt(string path) =>
        name => name == "GOOGLE_APPLICATION_CREDENTIALS" ? path : null;

    private Task<GeminiResolution> ResolveAsync(
        string? manualPath, Func<string, string?> environmentVariableReader, CancellationToken cancellationToken = default) =>
        GeminiCredentialResolver.ResolveAsync(manualPath, environmentVariableReader, _directory, _clock, cancellationToken);

    [Fact]
    public async Task ResolveAsync_NothingConfiguredAnywhere_ReturnsNotConnected()
    {
        var resolution = await ResolveAsync(null, NoEnv());

        Assert.Equal(DetectionState.NotConnected, resolution.Detection.State);
        Assert.Null(resolution.Credential);
        Assert.Empty(resolution.Detection.Candidates);
    }

    [Fact]
    public async Task ResolveAsync_ManualConnected_WinsOverEveryOtherConnectedSource()
    {
        WriteManual(CompleteServiceAccountJson);
        WriteEnvKey(CompleteServiceAccountJson);
        WriteGeminiCli();
        WriteGcloudWindows(CompleteServiceAccountJson);

        var resolution = await ResolveAsync(ManualPath, EnvPointingAt(EnvKeyPath));

        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
        Assert.Equal(GeminiCredentialResolver.ManualSourceId, resolution.Detection.SourceId);
        // Short-circuits on the first Connected source: nothing past manual is even probed.
        Assert.Single(resolution.Detection.Candidates);
        Assert.Equal(GeminiCredentialResolver.ManualSourceId, resolution.Detection.Candidates[0].SourceId);
    }

    [Fact]
    public async Task ResolveAsync_MalformedManual_DoesNotBlockConnectedEnv()
    {
        WriteManual(IncompleteServiceAccountJson);
        WriteEnvKey(CompleteServiceAccountJson);

        var resolution = await ResolveAsync(ManualPath, EnvPointingAt(EnvKeyPath));

        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
        Assert.Equal(GeminiCredentialResolver.EnvSourceId, resolution.Detection.SourceId);
        Assert.Equal(2, resolution.Detection.Candidates.Count);
        Assert.Equal(GeminiCredentialResolver.ManualSourceId, resolution.Detection.Candidates[0].SourceId);
        Assert.Equal(GeminiCredentialResolver.EnvSourceId, resolution.Detection.Candidates[1].SourceId);
    }

    [Fact]
    public async Task ResolveAsync_EnvConnected_WinsOverGeminiCliAndGcloudAdc()
    {
        WriteEnvKey(CompleteServiceAccountJson);
        WriteGeminiCli();
        WriteGcloudWindows(CompleteServiceAccountJson);

        var resolution = await ResolveAsync(null, EnvPointingAt(EnvKeyPath));

        Assert.Equal(GeminiCredentialResolver.EnvSourceId, resolution.Detection.SourceId);
        Assert.Single(resolution.Detection.Candidates);
    }

    [Fact]
    public async Task ResolveAsync_GeminiCliConnected_WinsOverGcloudAdc()
    {
        WriteGeminiCli();
        WriteGcloudWindows(CompleteServiceAccountJson);

        var resolution = await ResolveAsync(null, NoEnv());

        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
        Assert.Equal(GeminiCredentialResolver.GeminiCliSourceId, resolution.Detection.SourceId);
        Assert.Single(resolution.Detection.Candidates);
    }

    [Fact]
    public async Task ResolveAsync_OnlyGcloudAdcPresent_Wins()
    {
        WriteGcloudWindows(CompleteServiceAccountJson);

        var resolution = await ResolveAsync(null, NoEnv());

        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
        Assert.Equal(GeminiCredentialResolver.GcloudAdcSourceId, resolution.Detection.SourceId);
        Assert.Equal("robot@example.iam.gserviceaccount.com", resolution.Detection.Account);
    }

    [Fact]
    public async Task ResolveAsync_ExpiredGeminiCliDoesNotBlockLaterConnectedGcloudAdc()
    {
        WriteGeminiCli(expiryDateMs: FixedNow.AddHours(-1).ToUnixTimeMilliseconds());
        WriteGcloudWindows(CompleteServiceAccountJson);

        var resolution = await ResolveAsync(null, NoEnv());

        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
        Assert.Equal(GeminiCredentialResolver.GcloudAdcSourceId, resolution.Detection.SourceId);
        Assert.Equal(2, resolution.Detection.Candidates.Count);
        Assert.Equal(GeminiCredentialResolver.GeminiCliSourceId, resolution.Detection.Candidates[0].SourceId);
        Assert.Equal(GeminiCredentialResolver.GcloudAdcSourceId, resolution.Detection.Candidates[1].SourceId);
    }

    [Fact]
    public async Task ResolveAsync_OnlyGeminiCliExpired_ReturnsExpiredWithRefreshHint()
    {
        WriteGeminiCli(expiryDateMs: FixedNow.AddHours(-1).ToUnixTimeMilliseconds());

        var resolution = await ResolveAsync(null, NoEnv());

        Assert.Equal(DetectionState.Expired, resolution.Detection.State);
        Assert.Equal(GeminiCredentialResolver.GeminiCliSourceId, resolution.Detection.SourceId);
        Assert.NotEmpty(resolution.Detection.Hint);
        Assert.Equal(GeminiCredentialKind.OAuth, resolution.Credential!.Kind);
    }

    [Fact]
    public async Task ResolveAsync_GeminiCliExactlyAtExpiry_IsExpired()
    {
        WriteGeminiCli(expiryDateMs: FixedNow.ToUnixTimeMilliseconds());

        var resolution = await ResolveAsync(null, NoEnv());

        Assert.Equal(DetectionState.Expired, resolution.Detection.State);
    }

    [Fact]
    public async Task ResolveAsync_GeminiCliOneMillisecondAfterNow_IsConnected()
    {
        WriteGeminiCli(expiryDateMs: FixedNow.ToUnixTimeMilliseconds() + 1);

        var resolution = await ResolveAsync(null, NoEnv());

        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
    }

    [Fact]
    public async Task ResolveAsync_FallbackCandidatesAccumulateAcrossEveryNonNullSource()
    {
        // manual: Limited (incomplete). env: not configured (never becomes a candidate). gemini_cli:
        // present but not usable for Cloud Monitoring (missing scope -> null, never becomes a
        // candidate either). gcloud: a user login, Limited. The first non-connected source (manual)
        // is what is actually returned, but every source that yielded *something* is still listed.
        WriteManual(IncompleteServiceAccountJson);
        Directory.CreateDirectory(Path.GetDirectoryName(GeminiCliOAuthPath)!);
        File.WriteAllText(GeminiCliOAuthPath, """{"access_token":"t","scope":"https://www.googleapis.com/auth/userinfo.email"}""");
        WriteGcloudWindows(UserLoginJson);

        var resolution = await ResolveAsync(ManualPath, NoEnv());

        Assert.Equal(DetectionState.Limited, resolution.Detection.State);
        Assert.Equal(GeminiCredentialResolver.ManualSourceId, resolution.Detection.SourceId);
        Assert.Equal(2, resolution.Detection.Candidates.Count);
        Assert.Equal(GeminiCredentialResolver.ManualSourceId, resolution.Detection.Candidates[0].SourceId);
        Assert.Equal(GeminiCredentialResolver.GcloudAdcSourceId, resolution.Detection.Candidates[1].SourceId);
    }

    [Fact]
    public async Task ResolveAsync_ManualPathWithSurroundingQuotesAndWhitespace_IsTrimmed()
    {
        WriteManual(CompleteServiceAccountJson);

        var resolution = await ResolveAsync($"  \"{ManualPath}\"  ", NoEnv());

        Assert.Equal(DetectionState.Connected, resolution.Detection.State);
        Assert.Equal(GeminiCredentialResolver.ManualSourceId, resolution.Detection.SourceId);
    }

    [Fact]
    public async Task ResolveAsync_IsReadOnly_NeverModifiesAnyCandidateFile()
    {
        WriteManual(IncompleteServiceAccountJson);
        WriteEnvKey(CompleteServiceAccountJson);
        WriteGeminiCli();
        WriteGcloudWindows(UserLoginJson);

        await ResolveAsync(ManualPath, EnvPointingAt(EnvKeyPath));

        Assert.Equal(IncompleteServiceAccountJson, await File.ReadAllTextAsync(ManualPath));
        Assert.Equal(CompleteServiceAccountJson, await File.ReadAllTextAsync(EnvKeyPath));
    }

    [Fact]
    public async Task ResolveAsync_AlreadyCancelled_ThrowsOperationCanceledException()
    {
        WriteManual(CompleteServiceAccountJson);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResolveAsync(ManualPath, NoEnv(), cts.Token));
    }

    [Fact]
    public async Task ResolveAsync_NullEnvironmentReader_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => GeminiCredentialResolver.ResolveAsync(null, null!, _directory, _clock, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_NullClock_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => GeminiCredentialResolver.ResolveAsync(null, NoEnv(), _directory, null!, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAsync_BlankUserProfileDirectory_Throws(string? blank)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => GeminiCredentialResolver.ResolveAsync(null, NoEnv(), blank!, _clock, CancellationToken.None));
    }
}
