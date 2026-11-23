using System.IO.Ports;
using System.Text;
using AIMonitor.Application.Display;

namespace AIMonitor.Infrastructure.Display;

/// <summary>
/// <see cref="ISerialDisplayPort"/> over <see cref="SerialPort"/> (R-022): 115200 8N1, no handshake, DTR and RTS held
/// off because the ESP32 board's auto-reset circuit would otherwise reboot it each time the port opens.
/// </summary>
public sealed class SerialPortDisplayPort : ISerialDisplayPort
{
    public const int BaudRate = 115200;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(1);

    public IReadOnlyList<string> GetPortNames() =>
        SerialPort.GetPortNames()
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PortNumber)
            .ThenBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public ISerialDisplayConnection Open(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            NewLine = "\n",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            WriteTimeout = (int)WriteTimeout.TotalMilliseconds,
        };
        try
        {
            port.Open();
            return new Connection(port);
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    /// <summary>"COM10" sorts after "COM9".</summary>
    private static int PortNumber(string name)
    {
        var digits = new string(name.SkipWhile(static c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : int.MaxValue;
    }

    private sealed class Connection(SerialPort port) : ISerialDisplayConnection
    {
        public string PortName => port.PortName;

        public void WriteLine(string line)
        {
            // The display answers every frame with "ok"; nobody reads those, so drop them before they pile up.
            port.DiscardInBuffer();
            port.Write(line + "\n");
        }

        public string? ReadLine(TimeSpan timeout)
        {
            port.ReadTimeout = Math.Max(1, (int)timeout.TotalMilliseconds);
            try
            {
                return port.ReadLine().TrimEnd('\r');
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        public void Dispose() => port.Dispose();
    }
}
