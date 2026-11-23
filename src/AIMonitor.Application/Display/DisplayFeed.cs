using AIMonitor.Application.Time;

namespace AIMonitor.Application.Display;

/// <summary>Where the feed stands, for diagnostics and tests.</summary>
public enum DisplayFeedState
{
    /// <summary>Switched off in Settings: no port is open or probed.</summary>
    Off,

    /// <summary>Switched on but no display answered yet; the feed retries every <see cref="DisplayFeed.RetryInterval"/>.</summary>
    Searching,

    /// <summary>A display answered the handshake and receives frames.</summary>
    Connected,
}

/// <summary>
/// Keeps an ESP32 desk display fed with the latest quota frame over USB serial (R-022).
/// <list type="bullet">
/// <item>Only talks to a port whose device answers <c>ping</c> with <c>pong aimonitor-cyd 1</c>, so frames never go to
/// some other serial device.</item>
/// <item>Sends a new frame as soon as one is published, and resends the latest one every
/// <see cref="HeartbeatInterval"/> so the display can tell a quiet PC from a gone one. Each send rebuilds the frame
/// for the current instant, so the display's clock never steps back.</item>
/// <item>A pulled cable or busy port closes the connection quietly and retries later; nothing is thrown to callers.</item>
/// </list>
/// All port I/O happens on one background loop (<see cref="Start"/>); <see cref="Configure"/> and
/// <see cref="Publish"/> only record state and wake it.
/// </summary>
public sealed class DisplayFeed : IDisposable
{
    public const string AutoPort = "auto";
    public const string PingLine = "ping";
    public const string ExpectedPong = "pong aimonitor-cyd 1";

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>Lines read while waiting for the pong; the board may print its boot banner or an earlier "ok" first.</summary>
    private const int MaxProbeLines = 6;

    private static readonly TimeSpan LoopTick = TimeSpan.FromSeconds(1);

    private readonly ISerialDisplayPort _port;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();

    // Shared with callers; guarded by _gate.
    private bool _enabled;
    private string _portSetting = AutoPort;
    private Func<DateTimeOffset, string>? _frame;
    private bool _frameDirty;
    private DisplayFeedState _state = DisplayFeedState.Off;
    private string? _connectedPort;

    // Owned by the loop (Step) only.
    private ISerialDisplayConnection? _connection;
    private string? _connectionSetting;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastSent;

    private Task? _loop;
    private bool _disposed;

    public DisplayFeed(ISerialDisplayPort port, IClock clock)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public DisplayFeedState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The port the display answered on, while <see cref="State"/> is <see cref="DisplayFeedState.Connected"/>.</summary>
    public string? ConnectedPort
    {
        get
        {
            lock (_gate)
            {
                return _connectedPort;
            }
        }
    }

    /// <summary>
    /// Applies the Settings choice. <paramref name="portName"/> is a port such as "COM5" or <see cref="AutoPort"/>
    /// to try every port. A change of port drops the current connection and searches again straight away.
    /// </summary>
    public void Configure(bool enabled, string? portName)
    {
        var port = string.IsNullOrWhiteSpace(portName) ? AutoPort : portName.Trim();
        lock (_gate)
        {
            if (_enabled == enabled && string.Equals(_portSetting, port, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _enabled = enabled;
            _portSetting = port;
            _nextAttempt = DateTimeOffset.MinValue;
        }

        Wake();
    }

    /// <summary>
    /// Replaces the frame to send. <paramref name="frame"/> builds the JSON line for a given instant; it is called on
    /// every send, including heartbeats.
    /// </summary>
    public void Publish(Func<DateTimeOffset, string> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            _frame = frame;
            _frameDirty = true;
        }

        Wake();
    }

    /// <summary>Starts the background loop. Calling it again does nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= Task.Run(() => RunAsync(_stop.Token));
        }
    }

    /// <summary>
    /// One pass of the loop: connect when needed, then send a frame if one is new or a heartbeat is due. Public so
    /// tests can drive the feed deterministically; production code calls <see cref="Start"/> instead.
    /// </summary>
    public void Step()
    {
        bool enabled;
        string portSetting;
        Func<DateTimeOffset, string>? frame;
        bool dirty;
        lock (_gate)
        {
            enabled = _enabled;
            portSetting = _portSetting;
            frame = _frame;
            dirty = _frameDirty;
        }

        var now = _clock.UtcNow;
        if (!enabled)
        {
            CloseConnection(DisplayFeedState.Off);
            return;
        }

        if (_connection is not null
            && !string.Equals(_connectionSetting, portSetting, StringComparison.OrdinalIgnoreCase))
        {
            CloseConnection(DisplayFeedState.Searching);
        }

        if (_connection is null)
        {
            DateTimeOffset nextAttempt;
            lock (_gate)
            {
                nextAttempt = _nextAttempt;
                _state = DisplayFeedState.Searching;
            }

            if (now < nextAttempt)
            {
                return;
            }

            var connection = TryConnect(portSetting);
            if (connection is null)
            {
                lock (_gate)
                {
                    _nextAttempt = now + RetryInterval;
                }

                return;
            }

            _connection = connection;
            _connectionSetting = portSetting;
            _lastSent = null;
            lock (_gate)
            {
                _state = DisplayFeedState.Connected;
                _connectedPort = connection.PortName;
            }
        }

        if (frame is null)
        {
            return;
        }

        if (!dirty && _lastSent is DateTimeOffset last && now - last < HeartbeatInterval)
        {
            return;
        }

        string line;
        try
        {
            line = frame(now);
        }
        catch (Exception)
        {
            // A frame that cannot be built is skipped; the connection itself is fine.
            return;
        }

        try
        {
            _connection.WriteLine(line);
            _lastSent = now;
            lock (_gate)
            {
                if (ReferenceEquals(_frame, frame))
                {
                    _frameDirty = false;
                }
            }
        }
        catch (Exception)
        {
            // Cable pulled, port taken or driver gone: drop it and look again later.
            CloseConnection(DisplayFeedState.Searching);
            lock (_gate)
            {
                _nextAttempt = now + RetryInterval;
            }
        }
    }

    private ISerialDisplayConnection? TryConnect(string portSetting)
    {
        IReadOnlyList<string> candidates;
        if (string.Equals(portSetting, AutoPort, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                candidates = _port.GetPortNames();
            }
            catch (Exception)
            {
                return null;
            }
        }
        else
        {
            candidates = [portSetting];
        }

        foreach (var name in candidates)
        {
            ISerialDisplayConnection? connection = null;
            try
            {
                connection = _port.Open(name);
                connection.WriteLine(PingLine);
                for (var i = 0; i < MaxProbeLines; i++)
                {
                    var reply = connection.ReadLine(ProbeTimeout);
                    if (reply is null)
                    {
                        break;
                    }

                    if (string.Equals(reply.Trim(), ExpectedPong, StringComparison.Ordinal))
                    {
                        return connection;
                    }
                }
            }
            catch (Exception)
            {
                // Missing, busy or unrelated device: try the next one.
            }

            SafeDispose(connection);
        }

        return null;
    }

    private void CloseConnection(DisplayFeedState state)
    {
        SafeDispose(_connection);
        _connection = null;
        _connectionSetting = null;
        lock (_gate)
        {
            _state = state;
            _connectedPort = null;
        }
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception)
        {
            // Closing a port whose device vanished can throw; nothing else to do.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Step();
            }
            catch (Exception)
            {
                // The loop must outlive any single failure.
            }

            try
            {
                await _wake.WaitAsync(LoopTick, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed while a caller was still publishing.
        }
    }

    public void Dispose()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
        }

        _stop.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Shutting down regardless.
        }

        CloseConnection(DisplayFeedState.Off);
        _stop.Dispose();
        _wake.Dispose();
    }
}
