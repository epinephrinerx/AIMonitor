namespace AIMonitor.Infrastructure;

/// <summary>
/// What a command-line tool is called on the host running this build.
///
/// Windows gives executables a <c>.exe</c> suffix and macOS does not, so a build that searches
/// <c>PATH</c> for a hard-coded <c>name.exe</c> finds nothing on a Mac where the tool is installed
/// and working — and then tells the user to install something they already have.
/// </summary>
public static class ExecutableNames
{
    /// <summary>The AGY command-line tool.</summary>
    public const string Agy = "agy";

    /// <summary>The Codex command-line tool.</summary>
    public const string Codex = "codex";

    /// <summary><paramref name="command"/> as this operating system spells it on disk.</summary>
    public static string OnThisPlatform(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return OperatingSystem.IsWindows() ? command + ".exe" : command;
    }
}
