using System.Diagnostics;
using System.Runtime.Versioning;

namespace AIMonitor.Infrastructure.Linux;

/// <summary>
/// An exclusive cross-process lock held by an open file stream (R-024).
///
/// Opening a file with <see cref="FileShare.None"/> takes the OS advisory lock that .NET applies on
/// Linux, so a second opener fails at once; releasing is just <see cref="FileStream.DisposeAsync"/>
/// or <see cref="FileStream.Dispose"/>. The lock file itself is never deleted, because deleting a
/// lock whose file another process still holds open silently breaks the lock for everyone after.
///
/// Existing files are normalized to 0600 before the open attempt — the owner can chmod a file
/// regardless of its mode, but cannot open a 0400 or 0000 file for writing — and normalized again
/// after the lock is granted, covering a fresh file whose mode the umask trimmed. Lock files are
/// empty, so the mode is their whole protection.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxFileLock
{
    /// <summary>How long to wait between open attempts while another process holds the lock.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private const UnixFileMode PermissionMask =
        UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit
        | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Acquires the lock at <paramref name="lockPath"/>, or returns <see langword="null"/> once
    /// <paramref name="timeout"/> has passed without it becoming free (zero tries once). The caller
    /// releases the lock by disposing the returned stream. The lock file is created if missing but
    /// its parent directory is not; a <see cref="DirectoryNotFoundException"/> therefore reaches the
    /// caller. A lock file whose mode cannot be normalized fails loudly rather than locking without
    /// the protection the mode provides.
    /// </summary>
    public static FileStream? TryAcquire(string lockPath, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        if (!Path.IsPathFullyQualified(lockPath))
        {
            throw new ArgumentException($"The lock path must be fully qualified: '{lockPath}'.", nameof(lockPath));
        }

        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The lock timeout must not be negative.");
        }

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (new FileInfo(lockPath).LinkTarget is not null)
            {
                throw new IOException($"The lock path is a symbolic link: '{lockPath}'.");
            }

            try
            {
                if (File.Exists(lockPath) && (File.GetUnixFileMode(lockPath) & PermissionMask) != OwnerReadWrite)
                {
                    File.SetUnixFileMode(lockPath, OwnerReadWrite);
                }
            }
            catch (FileNotFoundException)
            {
                // Gone between the existence check and the chmod; opening recreates it.
            }

            FileStream stream;
            try
            {
                stream = new FileStream(
                    lockPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.OpenOrCreate,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.None,
                        UnixCreateMode = OwnerReadWrite,
                    });
            }
            catch (IOException exception) when (exception is not (FileNotFoundException
                or DirectoryNotFoundException or PathTooLongException))
            {
                // Held by another process: the exception is the busy signal, not an error.
                if (stopwatch.Elapsed >= timeout)
                {
                    return null;
                }

                Thread.Sleep(PollInterval);
                continue;
            }

            try
            {
                if (File.GetUnixFileMode(lockPath) != OwnerReadWrite)
                {
                    File.SetUnixFileMode(lockPath, OwnerReadWrite);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Release before failing: the caller has no stream to dispose.
                stream.Dispose();
                throw;
            }

            return stream;
        }
    }
}
