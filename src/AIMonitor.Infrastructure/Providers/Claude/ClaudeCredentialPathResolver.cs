namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Resolves the on-disk location of Claude Code's credentials file. Deterministic from its explicit
/// inputs only — it never reads an environment variable or the real user profile itself, and never
/// expands (<c>~</c>, environment tokens) or writes the path it computes. The caller supplies
/// whatever <c>CLAUDE_CONFIG_DIR</c> override and profile directory are appropriate for its context
/// (production composition root vs. a test's temp directory).
/// </summary>
public static class ClaudeCredentialPathResolver
{
    private const string CredentialsFileName = ".credentials.json";
    private const string DefaultConfigFolderName = ".claude";

    /// <summary>
    /// Returns <c>&lt;claudeConfigDirOverride&gt;/.credentials.json</c> when
    /// <paramref name="claudeConfigDirOverride"/> is non-blank; otherwise
    /// <c>&lt;userProfileDirectory&gt;/.claude/.credentials.json</c>.
    /// </summary>
    public static string Resolve(string? claudeConfigDirOverride, string userProfileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

        var configDirectory = string.IsNullOrWhiteSpace(claudeConfigDirOverride)
            ? Path.Combine(userProfileDirectory, DefaultConfigFolderName)
            : claudeConfigDirOverride;

        return Path.Combine(configDirectory, CredentialsFileName);
    }
}
