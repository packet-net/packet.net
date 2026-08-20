using System.IO.Ports;

namespace Packet.Tait.Codeplug;

/// <summary>
/// The narrow byte-level seam the programmer drives the wire over. Same shape as
/// <c>Packet.Radio.Tait.ISerialIo</c>: blocking finite-timeout reads, blocking writes, a name,
/// and a live baud change (programming opens at 9600 then re-clocks to 19200). Production wraps a
/// <see cref="SerialPort"/>; tests substitute a scripted mock radio.
/// </summary>
public interface ISerialLine : IDisposable
{
    /// <summary>The underlying port name (e.g. <c>/dev/ttyUSB0</c>).</summary>
    string PortName { get; }

    /// <summary>Read available bytes; blocks up to the port's read timeout and throws
    /// <see cref="TimeoutException"/> when none arrive.</summary>
    int Read(byte[] buffer, int offset, int count);

    /// <summary>Write <paramref name="count"/> bytes.</summary>
    void Write(byte[] buffer, int offset, int count);

    /// <summary>Change the line rate on the already-open port.</summary>
    void SetBaudRate(int baudRate);
}

/// <summary>The production <see cref="ISerialLine"/>: a thin pass-through to a
/// <see cref="SerialPort"/> configured 8N1 with a read timeout.</summary>
public sealed class SerialPortLine : ISerialLine
{
    private readonly SerialPort _port;

    /// <summary>Open <paramref name="portName"/> at <paramref name="baudRate"/> 8N1.</summary>
    public SerialPortLine(string portName, int baudRate, int readTimeoutMs = 2000)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = readTimeoutMs,
            WriteTimeout = readTimeoutMs,
            Handshake = Handshake.None,
        };
        _port.Open();
    }

    /// <inheritdoc/>
    public string PortName => _port.PortName;

    /// <inheritdoc/>
    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);

    /// <inheritdoc/>
    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);

    /// <inheritdoc/>
    public void SetBaudRate(int baudRate) => _port.BaudRate = baudRate;

    /// <inheritdoc/>
    public void Dispose() => _port.Dispose();
}
