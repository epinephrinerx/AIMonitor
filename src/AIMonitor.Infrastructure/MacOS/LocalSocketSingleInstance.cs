using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIMonitor.Application.Windows;

namespace AIMonitor.Infrastructure.MacOS;

/// <summary>
/// The macOS counterpart of <c>NamedMutexSingleInstance</c>: one listening Unix domain socket is both
/// the lock and the activation channel, because whoever manages to listen owns the app and anyone who
/// can connect knows a copy is already running (PAR-023).
///
/// macOS has no equivalent of the Windows named-mutex namespace or of a pipe ACL tied to a user SID,
/// and the socket needs no cleanup after a crash beyond its own stale file, so the two Windows
/// mechanisms collapse into this one — the same guard the 1.x macOS port shipped.
///
/// The wire format is the Windows one: a four-byte little-endian length followed by the arguments as
/// UTF-8 JSON, so the activation payload means the same thing on both platforms.
/// </summary>
internal sealed class LocalSocketSingleInstance : ISingleInstanceCoordinator
{
    public const string DefaultAppId = PlatformAdapters.AppId;
    private const int MaxPayloadBytes = 64 * 1024;

    /// <summary>
    /// <c>sockaddr_un.sun_path</c> holds 104 bytes on macOS, including the terminator, and a bind
    /// past that fails with a message naming nothing useful. Refusing early says which path was too
    /// long and by how much.
    /// </summary>
    private const int MaxSocketPathBytes = 103;

    private readonly string _socketPath;
    private readonly TimeSpan _connectTimeout;
    private Socket? _listener;
    private CancellationTokenSource? _serverCts;
    private Task? _serverLoopTask;
    private bool _hasOwnership;
    private bool _disposed;

    public LocalSocketSingleInstance(string? directory = null, string appId = DefaultAppId, TimeSpan? connectTimeout = null)
        : this(ResolveSocketPath(directory ?? DefaultDirectory(), appId), connectTimeout)
    {
    }

    /// <summary>For tests and for a caller that has already chosen the exact socket path.</summary>
    internal static LocalSocketSingleInstance ForSocketPath(string socketPath, TimeSpan? connectTimeout = null) =>
        new(socketPath, connectTimeout);

    private LocalSocketSingleInstance(string socketPath, TimeSpan? connectTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        var byteLength = Encoding.UTF8.GetByteCount(socketPath);
        if (byteLength > MaxSocketPathBytes)
        {
            throw new ArgumentException(
                $"The socket path is {byteLength} bytes, {byteLength - MaxSocketPathBytes} more than a "
                + $"Unix domain socket allows: '{socketPath}'.",
                nameof(socketPath));
        }

        _socketPath = socketPath;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
    }

    public event Action<string[]>? Activated;

    public bool HasOwnership => _hasOwnership;

    public string SocketPath => _socketPath;

    /// <summary>A name unique to the app, the user and this machine, hashed to stay inside the path limit.</summary>
    internal static string ResolveSocketPath(string directory, string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        var identity = $"{appId}_{Environment.UserName}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Path.Combine(directory, $"si-{Convert.ToHexString(digest)[..12].ToLowerInvariant()}.sock");
    }

    private static string DefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DefaultAppId);

    public async ValueTask<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasOwnership)
        {
            return true;
        }

        if (await IsAnotherInstanceListeningAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (TryListen())
        {
            _hasOwnership = true;
            return true;
        }

        // Lost a start-up race: between the failed connect and the failed bind another copy claimed
        // the path. Hand over to it.
        if (await IsAnotherInstanceListeningAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Neither connectable nor bindable, for a reason this process cannot fix (a read-only
        // directory, exhausted descriptors). Refusing to start over a broken guard would be worse
        // than the duplicate it prevents, so run on without one.
        return true;
    }

    public async ValueTask<bool> NotifyExistingInstanceAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(arguments);

        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(arguments));
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidOperationException($"Activation payload exceeds {MaxPayloadBytes} bytes limit.");
        }

        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_connectTimeout);

            await client.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), timeoutCts.Token).ConfigureAwait(false);
            await client.SendAsync(BitConverter.GetBytes(payload.Length), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            await client.SendAsync(payload, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            client.Shutdown(SocketShutdown.Both);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    private async ValueTask<bool> IsAnotherInstanceListeningAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_socketPath))
        {
            return false;
        }

        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_connectTimeout);
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Nothing answered, so the file is a leftover from a copy that died.
            return false;
        }
    }

    private bool TryListen()
    {
        try
        {
            var directory = Path.GetDirectoryName(_socketPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows())
                {
                    // Only this user may reach the socket, which is what the Windows side gets from
                    // a pipe ACL bound to the current user's SID.
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            // Nothing answered the connect above, so a file still sitting here is a leftover; bind
            // would fail with "address already in use" until it is gone.
            File.Delete(_socketPath);

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            listener.Listen(backlog: 8);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            _listener = listener;
            _serverCts = new CancellationTokenSource();
            _serverLoopTask = Task.Run(() => RunServerLoopAsync(listener, _serverCts.Token));
            return true;
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task RunServerLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                var arguments = await ReadArgumentsAsync(connection, cancellationToken).ConfigureAwait(false);
                if (arguments is not null)
                {
                    var handler = Activated;
                    if (handler is not null)
                    {
                        ThreadPool.QueueUserWorkItem(_ => handler(arguments));
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // One failed connection must not end the loop; the next launch still has to be heard.
            }
        }
    }

    private static async Task<string[]?> ReadArgumentsAsync(Socket connection, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        if (!await ReadExactlyAsync(connection, lengthBuffer, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BitConverter.ToInt32(lengthBuffer);
        if (length is <= 0 or > MaxPayloadBytes)
        {
            return null;
        }

        var payload = new byte[length];
        if (!await ReadExactlyAsync(connection, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(payload)) ?? [];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> ReadExactlyAsync(Socket connection, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await connection
                .ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_serverCts is not null)
        {
            await _serverCts.CancelAsync().ConfigureAwait(false);
        }

        _listener?.Dispose();
        _listener = null;

        if (_serverLoopTask is not null)
        {
            try
            {
                await _serverLoopTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                // Shutting down: a loop that will not wind up in two seconds is not worth waiting on.
            }
        }

        _serverCts?.Dispose();
        _serverCts = null;

        if (_hasOwnership)
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover file is handled by the next launch, which finds nothing listening.
            }
        }

        _hasOwnership = false;
    }
}
