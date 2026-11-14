namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Builds the environment for the isolated Codex App Server child process: a copy of the caller's
/// environment with every credential-shaped variable for every provider this app knows about removed
/// (<c>OPENAI_</c>, <c>CODEX_</c>, <c>ANTHROPIC_</c>, <c>CLAUDE_</c>, <c>GEMINI_</c>, <c>GOOGLE_</c> -
/// so an unrelated provider's credentials, and the real <c>CODEX_HOME</c>, are never handed to the
/// isolated client), then <c>CODEX_HOME</c> set to the disposable temporary directory. Everything else
/// (<c>PATH</c>, <c>USERNAME</c>, etc.) is passed through unchanged - the child still needs a normal
/// environment to launch. A pure function of its inputs - no environment or process access of its own
/// - so it is testable without touching the real environment.
/// </summary>
internal static class CodexEnvironmentSanitizer
{
    private static readonly string[] StrippedPrefixes =
        ["OPENAI_", "CODEX_", "ANTHROPIC_", "CLAUDE_", "GEMINI_", "GOOGLE_"];

    public static IReadOnlyDictionary<string, string?> Sanitize(
        IReadOnlyDictionary<string, string?> source, string temporaryCodexHome)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryCodexHome);

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            if (!HasStrippedPrefix(key))
            {
                result[key] = value;
            }
        }

        result["CODEX_HOME"] = temporaryCodexHome;
        return result;
    }

    private static bool HasStrippedPrefix(string key)
    {
        foreach (var prefix in StrippedPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
