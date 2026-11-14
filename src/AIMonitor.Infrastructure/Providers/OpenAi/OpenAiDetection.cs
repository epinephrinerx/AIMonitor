using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.OpenAI;

/// <summary>
/// Maps an <see cref="OpenAiCredentialSelectionInput"/> to the provider-neutral
/// <see cref="DetectionInfo"/> contract, applying OpenAI's fixed precedence (PAR-008): a discovered
/// Codex ChatGPT OAuth login always wins over saved/environment/CLI keys, even when that OAuth login
/// is currently expired or missing an account ID — existing keys must never silently take over from
/// a login that is present but unusable right now. Only when no Codex OAuth login was discovered at
/// all (missing or unparseable auth file) does selection fall through to saved Admin key, then
/// environment key, then Codex CLI's own API key, in that order.
/// </summary>
public static class OpenAiDetection
{
    public const string ProviderId = "openai";

    public const string CodexSourceId = "codex_cli";
    public const string CodexSourceLabel = "Codex CLI login (ChatGPT)";
    public const string CodexRefreshHint = "Run `codex` and sign in again.";

    public static DetectionInfo Detect(OpenAiCredentialSelectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return input.CodexOAuth.Credential is { } credential
            ? DetectFromCodexOAuth(input.CodexOAuth.Status, credential)
            : DetectFromInjectedCandidates(input);
    }

    /// <summary>
    /// A Codex OAuth login was discovered (kind OAuth in the baseline's vocabulary), regardless of
    /// whether it is currently usable. This is terminal by design: selection never consults
    /// <see cref="OpenAiCredentialSelectionInput.SavedAdminKey"/>,
    /// <see cref="OpenAiCredentialSelectionInput.EnvironmentKey"/>, or
    /// <see cref="OpenAiCredentialSelectionInput.CliApiKey"/> once a Codex OAuth credential exists —
    /// an expired or account-less login must surface as an actionable Codex error, not a silent
    /// switch to API spend.
    /// </summary>
    private static DetectionInfo DetectFromCodexOAuth(CodexOAuthStatus status, CodexOAuthCredential credential)
    {
        var candidates = new[] { new DetectionCandidate(CodexSourceId, CodexSourceLabel) };

        if (status == CodexOAuthStatus.Expired)
        {
            return new DetectionInfo(
                ProviderId,
                DetectionState.Expired,
                CodexSourceId,
                CodexSourceLabel,
                account: credential.Account,
                hint: CodexRefreshHint,
                candidates: candidates);
        }

        return credential.IsUsageCapable
            ? new DetectionInfo(
                ProviderId,
                DetectionState.Connected,
                CodexSourceId,
                CodexSourceLabel,
                account: credential.Account,
                hint: string.Empty,
                candidates: candidates)
            : new DetectionInfo(
                ProviderId,
                DetectionState.Limited,
                CodexSourceId,
                CodexSourceLabel,
                account: credential.Account,
                hint: credential.LimitedReason,
                candidates: candidates);
    }

    /// <summary>
    /// No Codex OAuth login was discovered at all: walk saved Admin key, then environment key, then
    /// CLI API key, in that fixed order. The first candidate shaped like a usable Admin key
    /// (<see cref="OpenAiApiKeyCandidate.IsUsageCapable"/>) wins as
    /// <see cref="DetectionState.Connected"/>; otherwise the first candidate present at all becomes a
    /// <see cref="DetectionState.Limited"/> result, and every candidate seen along the way (up to and
    /// including whichever one was returned) is recorded so the connections page can show every login
    /// that was found, not just the one that won.
    /// </summary>
    private static DetectionInfo DetectFromInjectedCandidates(OpenAiCredentialSelectionInput input)
    {
        List<DetectionCandidate> seen = [];
        OpenAiApiKeyCandidate? fallback = null;

        foreach (var candidate in new[] { input.SavedAdminKey, input.EnvironmentKey, input.CliApiKey })
        {
            if (candidate is null)
            {
                continue;
            }

            seen.Add(new DetectionCandidate(candidate.SourceId, candidate.SourceLabel));

            if (candidate.IsUsageCapable)
            {
                return new DetectionInfo(
                    ProviderId,
                    DetectionState.Connected,
                    candidate.SourceId,
                    candidate.SourceLabel,
                    account: candidate.Account,
                    hint: string.Empty,
                    candidates: seen);
            }

            fallback ??= candidate;
        }

        if (fallback is null)
        {
            return new DetectionInfo(ProviderId, DetectionState.NotConnected);
        }

        return new DetectionInfo(
            ProviderId,
            DetectionState.Limited,
            fallback.SourceId,
            fallback.SourceLabel,
            account: fallback.Account,
            hint: fallback.LimitedReason,
            candidates: seen);
    }
}
