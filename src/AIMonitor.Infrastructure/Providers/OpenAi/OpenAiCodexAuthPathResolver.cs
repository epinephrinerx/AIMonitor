namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Resolves the on-disk location of Codex CLI's own login file (<c>auth.json</c>), read for
/// credential detection only. Deterministic from its explicit inputs only - it never reads an
/// environment variable or the real user profile itself, and never expands or writes the path it
/// computes.
/// <para>
/// This is unrelated to the disposable temporary <c>CODEX_HOME</c> the App Server subprocess is
/// launched with (see <c>CodexAppServerLauncher</c>), which is always a fresh, empty directory that
/// never contains this file.
/// </para>
/// </summary>
internal static class OpenAiCodexAuthPathResolver
{
    private const string AuthFileName = "auth.json";
    private const string DefaultCodexFolderName = ".codex";

    /// <summary>
    /// Returns <c>&lt;codexHomeOverride&gt;/auth.json</c> when <paramref name="codexHomeOverride"/>
    /// (the value of <c>CODEX_HOME</c>) is non-blank; otherwise
    /// <c>&lt;userProfileDirectory&gt;/.codex/auth.json</c>.
    /// </summary>
    public static string Resolve(string? codexHomeOverride, string userProfileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

        var codexHome = string.IsNullOrWhiteSpace(codexHomeOverride)
            ? Path.Combine(userProfileDirectory, DefaultCodexFolderName)
            : codexHomeOverride;

        return Path.Combine(codexHome, AuthFileName);
    }
}
