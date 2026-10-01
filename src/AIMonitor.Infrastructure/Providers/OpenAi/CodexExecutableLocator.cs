namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Finds the Codex CLI executable: first on <c>PATH</c> (<c>codex.exe</c>), then the newest
/// <c>%LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe</c> the Codex desktop app installs, matching the
/// Python baseline. Every filesystem/environment access is injected so this is testable without
/// touching the real machine's <c>PATH</c> or app-data folder.
/// </summary>
internal static class CodexExecutableLocator
{
    private const string ExecutableFileName = "codex.exe";

    /// <param name="pathDirectories">The directories on <c>PATH</c>, in order.</param>
    /// <param name="fileExists">Whether a given full path names an existing file.</param>
    /// <param name="localAppData">The value of <c>%LOCALAPPDATA%</c>, or <see langword="null"/>/empty
    /// when unset.</param>
    /// <param name="enumerateCodexBinCandidates">Lists candidate
    /// <c>codex.exe</c> paths under the Codex desktop app's install root (one per installed version);
    /// empty when the root does not exist.</param>
    /// <param name="lastWriteTimeUtc">Used to pick the most recently modified candidate when more than
    /// one version directory exists.</param>
    public static string? Locate(
        IEnumerable<string> pathDirectories,
        Func<string, bool> fileExists,
        string? localAppData,
        Func<string, IEnumerable<string>> enumerateCodexBinCandidates,
        Func<string, DateTimeOffset> lastWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(pathDirectories);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(enumerateCodexBinCandidates);
        ArgumentNullException.ThrowIfNull(lastWriteTimeUtc);

        foreach (var directory in pathDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, ExecutableFileName);
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        var root = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        string? newest = null;
        var newestWriteTime = DateTimeOffset.MinValue;
        foreach (var candidate in enumerateCodexBinCandidates(root))
        {
            var writeTime = lastWriteTimeUtc(candidate);
            if (newest is null || writeTime > newestWriteTime)
            {
                newest = candidate;
                newestWriteTime = writeTime;
            }
        }

        return newest;
    }

    /// <summary>Production entry point: searches the real <c>PATH</c> and
    /// <c>%LOCALAPPDATA%\OpenAI\Codex\bin</c>.</summary>
    public static string? LocateOnThisMachine()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return Locate(
            directories,
            File.Exists,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            root => Directory.Exists(root)
                ? Directory.EnumerateFiles(root, ExecutableFileName, SearchOption.AllDirectories)
                : [],
            path => File.GetLastWriteTimeUtc(path));
    }
}
