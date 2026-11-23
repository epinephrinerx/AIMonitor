using AIMonitor.Application.Display;

namespace AIMonitor.TestSupport;

/// <summary>
/// Hand-written <see cref="ISerialDisplayPort"/> fake (R-022). Each port has a scripted device: the lines it answers
/// a <c>ping</c> with, or an exception it throws on open. Everything written is recorded per port.
/// </summary>
public sealed class FakeSerialDisplayPort : ISerialDisplayPort
{
    private readonly Dictionary<string, FakeDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Opened { get; } = [];

    public int GetPortNamesCalls { get; private set; }

    /// <summary>A device on <paramref name="portName"/> that answers ping with <paramref name="replies"/>.</summary>
    public FakeDevice Add(string portName, params string[] replies)
    {
        var device = new FakeDevice(portName, replies);
        _devices[portName] = device;
        return device;
    }

    public IReadOnlyList<string> GetPortNames()
    {
        GetPortNamesCalls++;
        return _devices.Keys.ToList();
    }

    public ISerialDisplayConnection Open(string portName)
    {
        Opened.Add(portName);
        if (!_devices.TryGetValue(portName, out var device))
        {
            throw new IOException($"The port '{portName}' does not exist.");
        }

        if (device.OpenFailure is not null)
        {
            throw device.OpenFailure;
        }

        return device.Connect();
    }

    public sealed class FakeDevice(string portName, IReadOnlyList<string> replies)
    {
        private readonly List<FakeConnection> _connections = [];

        public string PortName { get; } = portName;

        public IReadOnlyList<string> Replies { get; set; } = replies;

        /// <summary>Thrown by <see cref="Open"/> while set, e.g. a port held by another program.</summary>
        public Exception? OpenFailure { get; set; }

        /// <summary>Thrown by every write after the handshake while set, e.g. a pulled cable.</summary>
        public Exception? WriteFailure { get; set; }

        /// <summary>Every line written to this port over all connections, handshake included.</summary>
        public List<string> Written { get; } = [];

        /// <summary>Frames only: what was written after a successful handshake.</summary>
        public IEnumerable<string> Frames => Written.Where(static line => line != DisplayFeed.PingLine);

        public int OpenConnections => _connections.Count(static c => !c.Disposed);

        public int Disposals => _connections.Count(static c => c.Disposed);

        internal FakeConnection Connect()
        {
            var connection = new FakeConnection(this);
            _connections.Add(connection);
            return connection;
        }
    }

    internal sealed class FakeConnection(FakeDevice device) : ISerialDisplayConnection
    {
        private readonly Queue<string> _pending = new();

        public bool Disposed { get; private set; }

        public string PortName => device.PortName;

        public void WriteLine(string line)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (line == DisplayFeed.PingLine)
            {
                device.Written.Add(line);
                foreach (var reply in device.Replies)
                {
                    _pending.Enqueue(reply);
                }

                return;
            }

            if (device.WriteFailure is not null)
            {
                throw device.WriteFailure;
            }

            device.Written.Add(line);
        }

        public string? ReadLine(TimeSpan timeout) => _pending.Count > 0 ? _pending.Dequeue() : null;

        public void Dispose() => Disposed = true;
    }
}
