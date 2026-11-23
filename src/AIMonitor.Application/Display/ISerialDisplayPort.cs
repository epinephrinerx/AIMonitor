namespace AIMonitor.Application.Display;

/// <summary>
/// The machine's serial ports, as far as the ESP32 desk display needs them (R-022). Implemented in
/// Infrastructure over <c>System.IO.Ports</c>; tests use a hand-written fake.
/// </summary>
public interface ISerialDisplayPort
{
    /// <summary>Names of the serial ports present right now (e.g. "COM5"). Empty when there are none.</summary>
    IReadOnlyList<string> GetPortNames();

    /// <summary>
    /// Opens <paramref name="portName"/> at 115200 8N1 with DTR and RTS held off, so opening the port does not
    /// reset the board. Throws whatever the platform throws when the port is missing or in use.
    /// </summary>
    ISerialDisplayConnection Open(string portName);
}

/// <summary>One open serial port. Every member may throw an I/O exception once the cable is pulled.</summary>
public interface ISerialDisplayConnection : IDisposable
{
    string PortName { get; }

    /// <summary>Writes <paramref name="line"/> followed by a single <c>\n</c>.</summary>
    void WriteLine(string line);

    /// <summary>The next line the device sent within <paramref name="timeout"/>, or <see langword="null"/> on timeout.</summary>
    string? ReadLine(TimeSpan timeout);
}
