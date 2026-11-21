using System.Text;
using AIMonitor.Application.Windows;

namespace AIMonitor.Infrastructure.Linux;

/// <summary>
/// The Linux counterpart of <c>LaunchAgentStartupRegistrar</c>: a <c>.desktop</c> file in the
/// per-user <c>$XDG_CONFIG_HOME/autostart</c> (or <c>~/.config/autostart</c>) directory is what
/// desktop environments list under "Autostart" / "Login Items". Like the macOS side, it is per
/// user and needs no admin rights (PAR-024).
///
/// Path composition uses <c>/</c> directly rather than <see cref="Path.Combine"/>, so the same
/// test vectors produce the same result on every host (Inspector §2 ข้อ 3).
/// </summary>
internal sealed class XdgAutostartStartupRegistrar : IStartupRegistrar
{
    /// <summary>The unscoped file id: <c>~/.config/autostart/net.apichart.aiusagemonitor.desktop</c>.</summary>
    public const string DefaultFileId = "net.apichart.aiusagemonitor";

    private readonly string _autostartDirectory;
    private readonly string _fileId;

    public XdgAutostartStartupRegistrar(string autostartDirectory, string fileId)
    {
        // A10 / G-ARG: null → ArgumentNullException (exact ParamName); empty/whitespace → ArgumentException.
        if (autostartDirectory is null)
        {
            throw new ArgumentNullException(nameof(autostartDirectory));
        }

        if (string.IsNullOrWhiteSpace(autostartDirectory))
        {
            throw new ArgumentException(
                "Autostart directory must not be empty or whitespace.",
                nameof(autostartDirectory));
        }

        if (autostartDirectory[0] != '/' || autostartDirectory.Contains('\0'))
        {
            throw new ArgumentException(
                "Autostart directory must be an absolute path without embedded nulls.",
                nameof(autostartDirectory));
        }

        if (fileId is null)
        {
            throw new ArgumentNullException(nameof(fileId));
        }

        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new ArgumentException(
                "File ID must not be empty or whitespace.",
                nameof(fileId));
        }

        if (fileId.Contains('/') || fileId.Contains('\0'))
        {
            throw new ArgumentException(
                "File ID must not contain '/' or embedded nulls.",
                nameof(fileId));
        }

        _autostartDirectory = autostartDirectory;
        _fileId = fileId;
    }

    /// <summary>The autostart directory as supplied at construction.</summary>
    public string AutostartDirectory => _autostartDirectory;

    /// <summary>The file id (no extension) as supplied at construction.</summary>
    public string FileId => _fileId;

    /// <summary>
    /// Full path to the <c>.desktop</c> file. Trailing slashes on the directory are collapsed
    /// so the same file is addressed whether or not the caller kept one.
    /// </summary>
    public string DesktopFilePath => _autostartDirectory.TrimEnd('/') + "/" + _fileId + ".desktop";

    /// <summary>
    /// Picks the autostart directory from the host environment, without touching disk. A valid
    /// <paramref name="xdgConfigHome"/> (absolute and no embedded null) is used as-is; otherwise
    /// the fallback is <c>&lt;home&gt;/.config/autostart</c>.
    /// </summary>
    public static string ResolveAutostartDirectory(string? xdgConfigHome, string homeDirectory)
    {
        // A11 / G-ARG: null → ArgumentNullException (exact ParamName); empty/whitespace/relative/NUL → ArgumentException.
        if (homeDirectory is null)
        {
            throw new ArgumentNullException(nameof(homeDirectory));
        }

        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            throw new ArgumentException(
                "Home directory must not be empty or whitespace.",
                nameof(homeDirectory));
        }

        if (homeDirectory[0] != '/' || homeDirectory.Contains('\0'))
        {
            throw new ArgumentException(
                "Home directory must be an absolute path without embedded nulls.",
                nameof(homeDirectory));
        }

        // A11: xdgConfigHome is used verbatim when it already looks absolute; the spec rule is
        // "starts with / and no \0" without trimming, so a relative or NUL-bearing value falls
        // through to the home-based fallback.
        if (xdgConfigHome is not null
            && xdgConfigHome.Length > 0
            && xdgConfigHome[0] == '/'
            && !xdgConfigHome.Contains('\0'))
        {
            return xdgConfigHome.TrimEnd('/') + "/autostart";
        }

        return homeDirectory.TrimEnd('/') + "/.config/autostart";
    }

    /// <summary><see cref="GetRegistrationState"/> collapsed to a boolean: unreadable counts as off.</summary>
    public bool IsRegistered() => GetRegistrationState() ?? false;

    /// <summary>
    /// Reads the on-disk state. Missing file → <see langword="false"/> (this also covers the
    /// directory being absent, the path being a directory, or the existence check failing:
    /// <see cref="File.Exists"/> returns false in all of those). Unreadable → <see langword="null"/>
    /// so the caller preserves existing intent. Otherwise the parsed value.
    /// </summary>
    public bool? GetRegistrationState()
    {
        var path = DesktopFilePath;

        if (!File.Exists(path))
        {
            return false;
        }

        string contents;
        try
        {
            contents = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Same rule as the macOS registrar: a file we cannot read is not proof the user
            // turned it off, so callers must preserve the saved intent.
            return null;
        }

        return XdgDesktopEntry.ReadEnabledState(contents);
    }

    /// <summary>
    /// Writes the autostart entry atomically: a temp file in the same directory is filled first,
    /// then renamed over the target so the previous contents survive intact until the instant of
    /// replacement (A13). A symlink at the target path is replaced by a regular file — its
    /// destination is never touched.
    /// </summary>
    public void Register(string executablePath, string arguments = "")
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw executablePath is null
                ? new ArgumentNullException(nameof(executablePath))
                : new ArgumentException(
                    "Executable path must not be empty or whitespace.",
                    nameof(executablePath));
        }

        // A13(2): build before touching disk so a validation failure leaves nothing behind.
        var content = XdgDesktopEntry.Build(executablePath, arguments);

        var dir = _autostartDirectory;
        var desktopPath = DesktopFilePath;
        var tmpPath = dir.TrimEnd('/') + "/." + _fileId + ".desktop." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(tmpPath, content, new UTF8Encoding(false));
            File.Move(tmpPath, desktopPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: remove the temp file so normal operation leaves no residue.
            try
            {
                File.Delete(tmpPath);
            }
            catch
            {
                // Swallowed: the original exception is the one the caller needs.
            }

            throw new InvalidOperationException(
                $"Cannot write the autostart entry at '{desktopPath}'.",
                exception);
        }
    }

    /// <summary>
    /// Deletes the <c>.desktop</c> file and nothing else. A missing file or missing directory is
    /// a quiet success; any other IO failure wraps into <see cref="InvalidOperationException"/>
    /// so the original entry on disk is preserved (A14).
    /// </summary>
    public void Unregister()
    {
        try
        {
            File.Delete(DesktopFilePath);
        }
        catch (DirectoryNotFoundException)
        {
            // No autostart directory at all, so there is no entry to remove. File.Delete would
            // throw for a missing directory but quietly succeed for a missing file.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Cannot remove the autostart entry at '{DesktopFilePath}'.",
                exception);
        }
    }
}
