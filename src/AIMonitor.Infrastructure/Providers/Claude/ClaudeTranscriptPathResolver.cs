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
    private const string DefaultXdgFolderName = ".config";

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

    /// <summary>
    /// Returns every <c>projects</c> directory to scan, in priority order and without duplicates.
    /// A non-blank <paramref name="claudeConfigDir"/> is a comma-separated list of Claude config
    /// directories; each entry becomes its <c>projects</c> directory, or is used as-is when its last
    /// segment already is <c>projects</c>. When that list is absent (or yields no entries), the
    /// defaults are <c>&lt;xdg&gt;/claude/projects</c> - where <c>&lt;xdg&gt;</c> is
    /// <paramref name="xdgConfigHome"/> when non-blank, else
    /// <c>&lt;userProfileDirectory&gt;/.config</c> - followed by
    /// <c>&lt;userProfileDirectory&gt;/.claude/projects</c>. Pure: no environment or disk access.
    /// </summary>
    public static IReadOnlyList<string> ResolveAll(
        string? claudeConfigDir, string? xdgConfigHome, string userProfileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(claudeConfigDir))
        {
            foreach (var rawEntry in claudeConfigDir.Split(','))
            {
                var entry = rawEntry.Trim();
                if (entry.Length == 0)
                {
                    continue;
                }

                candidates.Add(IsProjectsDirectory(entry) ? entry : Path.Combine(entry, ProjectsFolderName));
            }
        }

        if (candidates.Count == 0)
        {
            var xdgDirectory = string.IsNullOrWhiteSpace(xdgConfigHome)
                ? Path.Combine(userProfileDirectory, DefaultXdgFolderName)
                : xdgConfigHome;

            candidates.Add(Path.Combine(xdgDirectory, "claude", ProjectsFolderName));
            candidates.Add(Path.Combine(userProfileDirectory, DefaultConfigFolderName, ProjectsFolderName));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var fullPath = TrimTrailingSeparators(Path.GetFullPath(candidate));
            if (seen.Add(fullPath))
            {
                roots.Add(fullPath);
            }
        }

        return roots;
    }

    private static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? path : trimmed;
    }

    private static bool IsProjectsDirectory(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmed), ProjectsFolderName, StringComparison.OrdinalIgnoreCase);
    }
}
