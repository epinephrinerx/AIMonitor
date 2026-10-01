using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AIMonitor.Application.Windows;

namespace AIMonitor.Infrastructure.Windows;

/// <summary>
/// Coordinates single-instance execution per Windows user session using a named Mutex and a
/// current-user-only Named Pipe for inter-process activation, satisfying PAR-023 and ADR-0003.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedMutexSingleInstance : ISingleInstanceCoordinator, IDisposable
{
    public const string DefaultAppId = "AIUsageMonitor";
    private const int MaxPayloadBytes = 64 * 1024; // 64 KB safety bound

    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    private Mutex? _mutex;
    private bool _hasOwnership;
    private bool _wasAbandoned;
    private CancellationTokenSource? _serverCts;
    private Task? _serverLoopTask;
    private bool _disposed;

    public NamedMutexSingleInstance(string appId = DefaultAppId, TimeSpan? connectTimeout = null)
        : this(ResolveNames(appId).MutexName, ResolveNames(appId).PipeName, connectTimeout)
    {
    }

    internal NamedMutexSingleInstance(string mutexName, string pipeName, TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        _mutexName = mutexName;
        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
    }

    public event Action<string[]>? Activated;

    public bool HasOwnership => _hasOwnership;

    public bool WasAbandoned => _wasAbandoned;

    public string MutexName => _mutexName;

    public string PipeName => _pipeName;

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hasOwnership)
        {
            return true;
        }

        try
        {
            _mutex = new Mutex(true, _mutexName, out var createdNew);
            if (createdNew)
            {
                _hasOwnership = true;
                StartServerLoop();
                return true;
            }

            try
            {
                if (_mutex.WaitOne(0, false))
                {
                    _hasOwnership = true;
                    StartServerLoop();
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                _hasOwnership = true;
                _wasAbandoned = true;
                StartServerLoop();
                return true;
            }

            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (AbandonedMutexException)
        {
            _hasOwnership = true;
            _wasAbandoned = true;
            StartServerLoop();
            return true;
        }
    }

    public ValueTask<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(TryAcquire());
    }

    public async ValueTask<bool> NotifyExistingInstanceAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(arguments);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_connectTimeout);

                await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

                var json = JsonSerializer.Serialize(arguments);
                var payload = Encoding.UTF8.GetBytes(json);

                if (payload.Length > MaxPayloadBytes)
                {
                    throw new InvalidOperationException($"Activation payload exceeds {MaxPayloadBytes} bytes limit.");
                }

                var lengthPrefix = BitConverter.GetBytes(payload.Length);
                await client.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
                await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await client.FlushAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    client.WaitForPipeDrain();
                }
                catch
                {
                    // Non-fatal if server finished reading immediately
                }

                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout on this attempt; retry once if attempt == 0
                if (attempt == 0) continue;
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0)
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                return false;
            }
        }

        return false;
    }

    private void StartServerLoop()
    {
        _serverCts = new CancellationTokenSource();
        _serverLoopTask = Task.Run(() => RunServerLoopAsync(_serverCts.Token));
    }

    private async Task RunServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // Enforce CurrentUserOnly to prevent any other Windows user or remote client from connecting.
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // Read 4-byte length prefix
                var lengthBuffer = new byte[4];
                var bytesRead = await server.ReadAsync(lengthBuffer.AsMemory(0, 4), ct).ConfigureAwait(false);
                if (bytesRead == 4)
                {
                    var payloadLength = BitConverter.ToInt32(lengthBuffer, 0);
                    if (payloadLength is > 0 and <= MaxPayloadBytes)
                    {
                        var payload = new byte[payloadLength];
                        var totalRead = 0;
                        while (totalRead < payloadLength)
                        {
                            var read = await server.ReadAsync(payload.AsMemory(totalRead, payloadLength - totalRead), ct).ConfigureAwait(false);
                            if (read == 0) break;
                            totalRead += read;
                        }

                        if (totalRead == payloadLength)
                        {
                            var json = Encoding.UTF8.GetString(payload);
                            var args = JsonSerializer.Deserialize<string[]>(json) ?? [];
                            ThreadPool.QueueUserWorkItem(_ => Activated?.Invoke(args));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Transient I/O error on pipe connection; delay slightly and continue listening.
                if (!ct.IsCancellationRequested)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                if (server is not null)
                {
                    try
                    {
                        if (server.IsConnected) server.Disconnect();
                    }
                    catch
                    {
                        // Ignore disconnect exceptions on disposal.
                    }
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static (string MutexName, string PipeName) ResolveNames(string appId)
    {
        var userSid = GetCurrentUserSid();
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"{appId}_{userSid}"));
        var hashHex = Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();

        return ($@"Local\{appId}_{hashHex}", $"{appId}_{hashHex}");
    }

    private static string GetCurrentUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? "current_user";
        }
        catch
        {
            return Environment.UserName.ToLowerInvariant();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_serverCts is not null)
        {
            _serverCts.Cancel();
            _serverCts.Dispose();
            _serverCts = null;
        }

        if (_mutex is not null)
        {
            if (_hasOwnership)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }
            _mutex.Dispose();
            _mutex = null;
        }

        _hasOwnership = false;
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_serverCts is not null)
        {
            _serverCts.Cancel();
            if (_serverLoopTask is not null)
            {
                try
                {
                    await _serverLoopTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore task wait exceptions on shutdown
                }
            }
            _serverCts.Dispose();
        }

        if (_mutex is not null)
        {
            if (_hasOwnership)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                    // Ignore release errors on process exit
                }
            }
            _mutex.Dispose();
            _mutex = null;
        }

        _hasOwnership = false;
    }
}
