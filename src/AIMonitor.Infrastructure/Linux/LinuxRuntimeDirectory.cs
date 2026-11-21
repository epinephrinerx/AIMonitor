using System.Runtime.Versioning;
using System.Text;

namespace AIMonitor.Infrastructure.Linux;

/// <summary>
/// Chooses the directory the Linux single-instance socket lives in (R-024).
///
/// A desktop session usually provides <c>XDG_RUNTIME_DIR</c>, a per-user directory on <c>tmpfs</c>
/// that the session itself protects, so the socket belongs there. The directory has to prove
/// itself before it is used — right mode, no symlinks, no characters a Unix domain socket cannot
/// carry in its address — and anything that does not hold up falls back to the same per-user data
/// folder the other platforms use. Nothing is created or changed here: deciding is all it does.
/// </summary>
internal static class LinuxRuntimeDirectory
{
    /// <summary>The session's runtime directory, the first choice for the socket.</summary>
    public const string EnvironmentVariable = "XDG_RUNTIME_DIR";

    /// <summary>
    /// <c>sockaddr_un.sun_path</c> holds 104 bytes on Linux, including the terminator; the macOS
    /// adapter enforces the same limit.
    /// </summary>
    public const int MaxSocketPathBytes = 103;

    /// <summary>The hashed socket file name the shared adapter builds: "si-" plus 12 hex plus ".sock".</summary>
    public const int SocketFileNameBytes = 20;

    private const UnixFileMode OwnerBits =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode GroupAndOtherBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Returns the directory to build a scoped socket under, without creating or changing anything.
    ///
    /// A usable <paramref name="xdgRuntimeDir"/> yields the scope inside it; otherwise the caller's
    /// <paramref name="fallbackDirectory"/> is returned unchanged. Every trailing slash is stripped
    /// from <paramref name="xdgRuntimeDir"/> first (a value of only slashes keeps the root) and the
    /// trimmed value drives both the checks and the returned path. The runtime directory is only
    /// taken as given: it must be absolute, exist, not be a symlink, be owner-only (0700, sticky
    /// permitted), listable by this user, and leave room for the socket file name.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static string ResolveSocketDirectory(string? xdgRuntimeDir, string fallbackDirectory, string scopedAppId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopedAppId);

        return IsUsable(xdgRuntimeDir, out var directory, scopedAppId)
            ? Path.Combine(directory, scopedAppId)
            : fallbackDirectory;
    }

    [UnsupportedOSPlatform("windows")]
    private static bool IsUsable(string? xdgRuntimeDir, out string directory, string scopedAppId)
    {
        directory = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(xdgRuntimeDir) || xdgRuntimeDir.Contains('\0'))
            {
                return false;
            }

            // A trailing slash makes DirectoryInfo report a null LinkTarget in .NET 10, so every
            // trailing slash is stripped before any check; only slashes keep the root.
            directory = xdgRuntimeDir.TrimEnd('/');
            if (directory.Length == 0)
            {
                directory = "/";
            }

            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                return false;
            }

            var mode = File.GetUnixFileMode(directory);
            if ((mode & OwnerBits) != OwnerBits || (mode & GroupAndOtherBits) != 0)
            {
                return false;
            }

            // Listing proves this user can actually reach the contents, which stands in for an
            // ownership check: with owner-only mode the directory is this user's.
            _ = Directory.EnumerateFileSystemEntries(directory).GetEnumerator().MoveNext();

            var scopedPath = Path.Combine(directory, scopedAppId);
            var socketPathBytes = Encoding.UTF8.GetByteCount(scopedPath) + 1 + SocketFileNameBytes;
            return socketPathBytes <= MaxSocketPathBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
