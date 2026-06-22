using System.IO.Ports;

namespace Packet.Kiss.Serial;

/// <summary>
/// The narrow byte-level seam <see cref="KissSerialModem"/> drives the wire over:
/// a blocking, finite-timeout read, a blocking write, and a name. Production wraps
/// a <see cref="SerialPort"/> (<see cref="SystemSerialPortIo"/>); tests substitute a
/// scripted fake so the read pump, KISS encoding, and dispose ordering can be
/// exercised without real hardware.
/// </summary>
internal interface ISerialPortIo : IDisposable
{
    /// <summary>The underlying port name (e.g. <c>/dev/ttyACM0</c>).</summary>
    string PortName { get; }

    /// <summary>
    /// Read available bytes into <paramref name="buffer"/>. Mirrors
    /// <see cref="SerialPort.Read(byte[], int, int)"/>: blocks up to the port's read
    /// timeout, throws <see cref="TimeoutException"/> when none arrive, and throws once
    /// the handle is closed (which is how disposing the port unblocks the pump).
    /// </summary>
    int Read(byte[] buffer, int offset, int count);

    /// <summary>Write <paramref name="count"/> bytes. Mirrors
    /// <see cref="SerialPort.Write(byte[], int, int)"/>.</summary>
    void Write(byte[] buffer, int offset, int count);
}

/// <summary>The production <see cref="ISerialPortIo"/>: a thin pass-through to a
/// <see cref="SerialPort"/>.</summary>
internal sealed class SystemSerialPortIo(SerialPort serial) : ISerialPortIo
{
    public string PortName => serial.PortName;

    public int Read(byte[] buffer, int offset, int count) => serial.Read(buffer, offset, count);

    public void Write(byte[] buffer, int offset, int count) => serial.Write(buffer, offset, count);

    public void Dispose() => serial.Dispose();
}
