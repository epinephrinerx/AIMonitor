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
    /// <paramref name="fallbackDirectory"/> is returned unchanged. The runtime directory is only
    /// taken as given: it must be absolute, exist, not be a symlink, be owner-only (0700, sticky
    /// permitted), listable by this user, and leave room for the socket file name.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static string ResolveSocketDirectory(string? xdgRuntimeDir, string fallbackDirectory, string scopedAppId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopedAppId);

        return IsUsable(xdgRuntimeDir, scopedAppId)
            ? Path.Combine(xdgRuntimeDir!, scopedAppId)
            : fallbackDirectory;
    }

    [UnsupportedOSPlatform("windows")]
    private static bool IsUsable(string? xdgRuntimeDir, string scopedAppId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(xdgRuntimeDir) || xdgRuntimeDir.Contains('\0'))
            {
                return false;
            }

            if (!Path.IsPathFullyQualified(xdgRuntimeDir) || !Directory.Exists(xdgRuntimeDir))
            {
                return false;
            }

            if (new DirectoryInfo(xdgRuntimeDir).LinkTarget is not null)
            {
                return false;
            }

            var mode = File.GetUnixFileMode(xdgRuntimeDir);
            if ((mode & OwnerBits) != OwnerBits || (mode & GroupAndOtherBits) != 0)
            {
                return false;
            }

            // Listing proves this user can actually reach the contents, which stands in for an
            // ownership check: with owner-only mode the directory is this user's.
            _ = Directory.EnumerateFileSystemEntries(xdgRuntimeDir).GetEnumerator().MoveNext();

            var scopedPath = Path.Combine(xdgRuntimeDir, scopedAppId);
            var socketPathBytes = Encoding.UTF8.GetByteCount(scopedPath) + 1 + SocketFileNameBytes;
            return socketPathBytes <= MaxSocketPathBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
