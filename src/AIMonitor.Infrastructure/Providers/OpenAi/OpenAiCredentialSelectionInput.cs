namespace AIMonitor.Infrastructure.Providers.OpenAI;

/// <summary>
/// Everything <see cref="OpenAiDetection.Detect"/> needs to choose OpenAI's active login: the result
/// of discovering a Codex ChatGPT OAuth login, plus the three key-shaped candidates this phase treats
/// as caller-injected rather than self-discovered (see <see cref="OpenAiApiKeyCandidate"/>). A
/// <see langword="null"/> candidate means that source had nothing to offer — not an error.
/// </summary>
public sealed record OpenAiCredentialSelectionInput
{
    public CodexOAuthReadResult CodexOAuth { get; }

    public OpenAiApiKeyCandidate? SavedAdminKey { get; }

    public OpenAiApiKeyCandidate? EnvironmentKey { get; }

    public OpenAiApiKeyCandidate? CliApiKey { get; }

    public OpenAiCredentialSelectionInput(
        CodexOAuthReadResult codexOAuth,
        OpenAiApiKeyCandidate? savedAdminKey = null,
        OpenAiApiKeyCandidate? environmentKey = null,
        OpenAiApiKeyCandidate? cliApiKey = null)
    {
        ArgumentNullException.ThrowIfNull(codexOAuth);

        CodexOAuth = codexOAuth;
        SavedAdminKey = savedAdminKey;
        EnvironmentKey = environmentKey;
        CliApiKey = cliApiKey;
    }
}
