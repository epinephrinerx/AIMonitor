using System.Net.Sockets;
using System.Runtime.Versioning;
using AIMonitor.Application.Windows;
using AIMonitor.Infrastructure.MacOS;

namespace AIMonitor.Infrastructure.Linux;

/// <summary>
/// The Linux single-instance guard: the shared listening-socket adapter wrapped with a lock file
/// (R-024).
///
/// The wrapped adapter alone has a start-up race: two copies can probe a dead socket one after the
/// other and each find nothing listening, so each deletes the other's socket and binds its own,
/// ending with two owners of one path. This decorator serializes probe-and-bind across processes
/// with a <see cref="LinuxFileLock"/> and calls the wrapped adapter only while the lock is held,
/// so no copy ever binds while another is deciding. When the lock cannot be had, the copy cannot
/// prove the path is free, so it only probes: an owner that answers means "activate the other
/// one", anything else means running without a guard — which never gives a second owner of the
/// socket path.
///
/// Ownership is released the way it was taken: disposal of an owning instance first waits for any
/// acquire it is still running to finish, then takes the lock file again and closes the listener
/// and deletes the socket file under it. Without the lock the listener is left as it is and a
/// later disposal retries.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxSingleInstance : ISingleInstanceCoordinator
{
    /// <summary>How long a copy waits for the lock file before probing without it.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long disposal waits for the lock file before leaving the listener untouched. Longer
    /// than the most an acquiring copy can hold the lock (its lock wait plus two probes).
    /// </summary>
    public static readonly TimeSpan DefaultDisposeLockTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly LocalSocketSingleInstance _inner;
    private readonly string _socketDirectory;
    private readonly string _lockPath;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan? _lockTimeout;
    private readonly TimeSpan? _disposeLockTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _closed;

    /// <summary>
    /// Wraps the socket adapter for the given directory and scope. Nothing touches the disk here.
    /// </summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A required argument is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A timeout is negative.</exception>
    public LinuxSingleInstance(
        string socketDirectory,
        string scopedAppId,
        TimeSpan? connectTimeout = null,
        TimeSpan? lockTimeout = null,
        TimeSpan? disposeLockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopedAppId);
        ThrowIfNegative(connectTimeout, nameof(connectTimeout));
        ThrowIfNegative(lockTimeout, nameof(lockTimeout));
        ThrowIfNegative(disposeLockTimeout, nameof(disposeLockTimeout));

        _socketDirectory = socketDirectory;
        _lockTimeout = lockTimeout;
        _disposeLockTimeout = disposeLockTimeout;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _inner = new LocalSocketSingleInstance(socketDirectory, scopedAppId, connectTimeout);
        _lockPath = Path.ChangeExtension(_inner.SocketPath, ".lock");
    }

    public event Action<string[]>? Activated
    {
        add => _inner.Activated += value;
        remove => _inner.Activated -= value;
    }

    /// <summary>The directory the socket lives in, as given.</summary>
    public string SocketDirectory => _socketDirectory;

    public string SocketPath => _inner.SocketPath;

    /// <summary>The lock file serializing probe-and-bind on the socket path.</summary>
    public string LockPath => _lockPath;

    public bool HasOwnership => !_closed && _inner.HasOwnership;

    /// <summary>Whether disposal has started; a signal for tests that coordinate with disposal.</summary>
    internal bool IsClosing => _closed;

    public async ValueTask<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_inner.HasOwnership)
            {
                return true;
            }

            if (IsSocketDirectorySymlinkOrFile())
            {
                return await ProbeWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                Directory.CreateDirectory(
                    _socketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(
                    _socketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return await ProbeWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            }

            FileStream? lockStream;
            try
            {
                lockStream = LinuxFileLock.TryAcquire(_lockPath, _lockTimeout ?? DefaultLockTimeout);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return await ProbeWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            }

            if (lockStream is null)
            {
                return await ProbeWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (!File.Exists(_inner.SocketPath))
                {
                    return await _inner.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
                }

                return await ProbeUnderLockAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lockStream.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<bool> NotifyExistingInstanceAsync(
        string[] arguments, CancellationToken cancellationToken = default) =>
        _inner.NotifyExistingInstanceAsync(arguments, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // Synchronous on purpose: ownership reads as false from here on, and a still-running
        // acquire finishes first because the gate serializes the two.
        _closed = true;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_inner.HasOwnership)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
                return;
            }

            FileStream? lockStream;
            try
            {
                lockStream = LinuxFileLock.TryAcquire(
                    _lockPath, _disposeLockTimeout ?? DefaultDisposeLockTimeout);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The listener stays open and the socket file stays put; a later disposal retries.
                return;
            }

            if (lockStream is null)
            {
                return;
            }

            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                lockStream.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ThrowIfNegative(TimeSpan? timeout, string paramName)
    {
        if (timeout is { } value && value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "The timeout must not be negative.");
        }
    }

    private bool IsSocketDirectorySymlinkOrFile()
    {
        // Same trailing-slash trim as the runtime-directory check (R-024): a trailing slash makes
        // DirectoryInfo report a null LinkTarget in .NET 10, so it is stripped before the check.
        var directory = _socketDirectory.TrimEnd('/');
        if (directory.Length == 0)
        {
            directory = "/";
        }

        if (File.Exists(directory))
        {
            return true;
        }

        return Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget is not null;
    }

    private async ValueTask<bool> ProbeWithoutLockAsync(CancellationToken cancellationToken)
    {
        var outcome = await ConnectProbeAsync(cancellationToken).ConfigureAwait(false);
        return outcome != SocketProbe.Connected;
    }

    private async ValueTask<bool> ProbeUnderLockAsync(CancellationToken cancellationToken)
    {
        var outcome = await ConnectProbeAsync(cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            SocketProbe.Connected => false,
            SocketProbe.Inconclusive => true,
            _ => await _inner.TryAcquireAsync(cancellationToken).ConfigureAwait(false),
        };
    }

    private async ValueTask<SocketProbe> ConnectProbeAsync(CancellationToken cancellationToken)
    {
        var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_connectTimeout);
            await client
                .ConnectAsync(new UnixDomainSocketEndPoint(_inner.SocketPath), timeoutCts.Token)
                .ConfigureAwait(false);
            return SocketProbe.Connected;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (exception is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return SocketProbe.NoOwner;
            }

            // The socket file is gone: nobody can own a path that no longer exists.
            if (!File.Exists(_inner.SocketPath))
            {
                return SocketProbe.NoOwner;
            }

            return SocketProbe.Inconclusive;
        }
        finally
        {
            client.Dispose();
        }
    }

    private enum SocketProbe
    {
        Connected,
        NoOwner,
        Inconclusive,
    }
}
