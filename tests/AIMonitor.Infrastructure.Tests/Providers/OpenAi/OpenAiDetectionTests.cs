using AIMonitor.Domain;
using AIMonitor.Infrastructure.Providers.OpenAI;

namespace AIMonitor.Infrastructure.Tests.Providers.OpenAI;

/// <summary>
/// Covers OpenAI's PAR-008 precedence: a discovered Codex ChatGPT OAuth login is exclusive and
/// terminal — connected, limited, or expired, it is never replaced by a saved/environment/CLI key —
/// and only when no Codex OAuth login was discovered at all does selection fall through to saved
/// Admin key, then environment key, then Codex CLI's own API key.
/// </summary>
public class OpenAiDetectionTests
{
    private static CodexOAuthCredential Credential(
        DateTimeOffset? expiresAtUtc = null, string account = "", string accountId = "acct-123") =>
        new("synthetic-access-token", expiresAtUtc, account, accountId);

    private static readonly OpenAiApiKeyCandidate UsageCapableAdmin = OpenAiApiKeyCandidate.SavedAdmin("sk-admin-saved")!;
    private static readonly OpenAiApiKeyCandidate LimitedAdmin = OpenAiApiKeyCandidate.SavedAdmin("sk-proj-saved")!;
    private static readonly OpenAiApiKeyCandidate UsageCapableEnv = OpenAiApiKeyCandidate.Environment("sk-admin-env", "OPENAI_ADMIN_KEY")!;
    private static readonly OpenAiApiKeyCandidate LimitedEnv = OpenAiApiKeyCandidate.Environment("sk-env", "OPENAI_API_KEY")!;
    private static readonly OpenAiApiKeyCandidate UsageCapableCli = OpenAiApiKeyCandidate.CliKey("sk-admin-cli")!;
    private static readonly OpenAiApiKeyCandidate LimitedCli = OpenAiApiKeyCandidate.CliKey("sk-cli")!;

    // -- Codex OAuth is exclusive and terminal ---------------------------------------------------

    [Fact]
    public void Detect_CodexOAuthFoundAndUsageCapable_ReturnsConnectedWithCodexSourceIdentity()
    {
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Found(Credential(account: "user@example.com"), "path"),
            savedAdminKey: UsageCapableAdmin);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal("openai", detection.ProviderId);
        Assert.Equal(DetectionState.Connected, detection.State);
        Assert.Equal("codex_cli", detection.SourceId);
        Assert.Equal("Codex CLI login (ChatGPT)", detection.SourceLabel);
        Assert.Equal("user@example.com", detection.Account);
        Assert.Equal(string.Empty, detection.Hint);
        Assert.Single(detection.Candidates);
        Assert.True(detection.Usable);
    }

    [Fact]
    public void Detect_CodexOAuthFoundButNoAccountId_ReturnsLimitedRatherThanFallingBackToSavedAdmin()
    {
        var noAccountIdCredential = new CodexOAuthCredential("token", expiresAtUtc: null, account: "user@example.com", accountId: "");
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Found(noAccountIdCredential, "path"),
            savedAdminKey: UsageCapableAdmin);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(DetectionState.Limited, detection.State);
        Assert.Equal("codex_cli", detection.SourceId);
        Assert.Equal("Codex login has no account ID. Sign in to Codex again.", detection.Hint);
        Assert.False(detection.Usable);
    }

    [Fact]
    public void Detect_CodexOAuthExpired_ReturnsExpiredRatherThanFallingBackToAnyInjectedCandidate()
    {
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Expired(Credential(account: "user@example.com"), "path"),
            savedAdminKey: UsageCapableAdmin,
            environmentKey: UsageCapableEnv,
            cliApiKey: UsageCapableCli);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(DetectionState.Expired, detection.State);
        Assert.Equal("codex_cli", detection.SourceId);
        Assert.Equal("Run `codex` and sign in again.", detection.Hint);
        Assert.Equal("user@example.com", detection.Account);
        Assert.False(detection.Usable);
        Assert.Single(detection.Candidates);
    }

    // -- No Codex OAuth discovered: Missing and Invalid fall through identically -----------------

    [Fact]
    public void Detect_CodexOAuthMissing_FallsThroughToInjectedCandidates()
    {
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Missing("path"),
            savedAdminKey: UsageCapableAdmin);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(DetectionState.Connected, detection.State);
        Assert.Equal("manual", detection.SourceId);
    }

    [Fact]
    public void Detect_CodexOAuthInvalid_FallsThroughIdenticallyToMissing()
    {
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Invalid("path"),
            savedAdminKey: UsageCapableAdmin);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(DetectionState.Connected, detection.State);
        Assert.Equal("manual", detection.SourceId);
    }

    [Fact]
    public void Detect_NoCodexAndNoInjectedCandidates_ReturnsNotConnected()
    {
        var input = new OpenAiCredentialSelectionInput(CodexOAuthReadResult.Missing("path"));

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(DetectionState.NotConnected, detection.State);
        Assert.Empty(detection.Candidates);
        Assert.False(detection.Usable);
    }

    // -- precedence among injected candidates: saved Admin > environment > CLI --------------------

    [Fact]
    public void Detect_AllThreeUsageCapable_SavedAdminWins()
    {
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Missing("path"),
            savedAdminKey: UsageCapableAdmin,
            environmentKey: UsageCapableEnv,
            cliApiKey: UsageCapableCli);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(DetectionState.Connected, detection.State);
        Assert.Equal("manual", detection.SourceId);
        Assert.Equal("saved in this app", detection.Account);
    }

    [Fact]
    public void Detect_SavedAdminNotCapable_EnvironmentCapable_EnvironmentWins()
    {
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Missing("path"),
            savedAdminKey: LimitedAdmin,
            environmentKey: UsageCapableEnv,
            cliApiKey: UsageCapableCli);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(DetectionState.Connected, detection.State);
        Assert.Equal("env", detection.SourceId);
    }

    [Fact]
    public void Detect_OnlyCliCapable_CliWins()
    {
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Missing("path"),
            savedAdminKey: LimitedAdmin,
            environmentKey: LimitedEnv,
            cliApiKey: UsageCapableCli);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(DetectionState.Connected, detection.State);
        Assert.Equal("codex_cli", detection.SourceId);
    }

    [Fact]
    public void Detect_NoneUsageCapable_FallsBackToFirstPresentCandidateAsLimited()
    {
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Missing("path"),
            savedAdminKey: LimitedAdmin,
            environmentKey: LimitedEnv,
            cliApiKey: LimitedCli);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(DetectionState.Limited, detection.State);
        Assert.Equal("manual", detection.SourceId);
        Assert.Equal(OpenAiApiKeyCandidate.NotAdminKeyReason, detection.Hint);
        Assert.Equal(3, detection.Candidates.Count);
    }

    [Fact]
    public void Detect_SavedAdminAbsent_FirstPresentNonCapableCandidateIsEnvironment()
    {
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Missing("path"),
            savedAdminKey: null,
            environmentKey: LimitedEnv,
            cliApiKey: LimitedCli);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(DetectionState.Limited, detection.State);
        Assert.Equal("env", detection.SourceId);
        Assert.Equal(2, detection.Candidates.Count);
    }

    // -- Candidates list content ------------------------------------------------------------------

    [Fact]
    public void Detect_CandidatesList_TruncatesAtTheWinningUsageCapableCandidate()
    {
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Missing("path"),
            savedAdminKey: LimitedAdmin,
            environmentKey: UsageCapableEnv,
            cliApiKey: UsageCapableCli);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(DetectionState.Connected, detection.State);
        Assert.Equal(2, detection.Candidates.Count);
        Assert.Equal("manual", detection.Candidates[0].SourceId);
        Assert.Equal("env", detection.Candidates[1].SourceId);
    }

    [Fact]
    public void Detect_CandidatesList_IncludesAllNonBlankWhenNoneUsageCapable()
    {
        var input = new OpenAiCredentialSelectionInput(
            CodexOAuthReadResult.Missing("path"),
            savedAdminKey: LimitedAdmin,
            environmentKey: null,
            cliApiKey: LimitedCli);

        var detection = OpenAiDetection.Detect(input);

        Assert.Equal(2, detection.Candidates.Count);
        Assert.Equal("manual", detection.Candidates[0].SourceId);
        Assert.Equal("codex_cli", detection.Candidates[1].SourceId);
    }

    [Fact]
    public void Detect_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => OpenAiDetection.Detect(null!));
    }
}
