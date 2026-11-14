namespace AIMonitor.Infrastructure.Providers.OpenAI;

/// <summary>
/// Resolves the on-disk location of Codex CLI's auth file. Deterministic from its explicit inputs
/// only — it never reads an environment variable or the real user profile itself, and never expands
/// (<c>~</c>, environment tokens) or writes the path it computes. The caller supplies whatever
/// <c>CODEX_HOME</c> override and profile directory are appropriate for its context (production
/// composition root vs. a test's temp directory).
/// </summary>
public static class CodexAuthPathResolver
{
    private const string AuthFileName = "auth.json";
    private const string DefaultConfigFolderName = ".codex";

    /// <summary>
    /// Returns <c>&lt;codexHomeOverride&gt;/auth.json</c> when <paramref name="codexHomeOverride"/> is
    /// non-blank; otherwise <c>&lt;userProfileDirectory&gt;/.codex/auth.json</c>.
    /// </summary>
    public static string Resolve(string? codexHomeOverride, string userProfileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

        var configDirectory = string.IsNullOrWhiteSpace(codexHomeOverride)
            ? Path.Combine(userProfileDirectory, DefaultConfigFolderName)
            : codexHomeOverride;

        return Path.Combine(configDirectory, AuthFileName);
    }
}
