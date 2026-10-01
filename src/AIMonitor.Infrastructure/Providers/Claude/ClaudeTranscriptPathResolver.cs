namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Resolves the on-disk location of Claude Code's local transcript history, read-only. Deterministic
/// from its explicit inputs only - it never reads an environment variable or the real user profile
/// itself, and never expands (<c>~</c>, environment tokens) or writes the path it computes. Mirrors
/// <see cref="ClaudeCredentialPathResolver"/>'s override rule so both agree on where Claude Code's
/// config directory is, without sharing mutable state between them.
/// </summary>
public static class ClaudeTranscriptPathResolver
{
    private const string ProjectsFolderName = "projects";
    private const string DefaultConfigFolderName = ".claude";

    /// <summary>
    /// Returns <c>&lt;claudeConfigDirOverride&gt;/projects</c> when
    /// <paramref name="claudeConfigDirOverride"/> is non-blank; otherwise
    /// <c>&lt;userProfileDirectory&gt;/.claude/projects</c>.
    /// </summary>
    public static string Resolve(string? claudeConfigDirOverride, string userProfileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

        var configDirectory = string.IsNullOrWhiteSpace(claudeConfigDirOverride)
            ? Path.Combine(userProfileDirectory, DefaultConfigFolderName)
            : claudeConfigDirOverride;

        return Path.Combine(configDirectory, ProjectsFolderName);
    }
}
