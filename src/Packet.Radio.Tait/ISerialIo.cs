using System.IO.Ports;

namespace Packet.Radio.Tait;

/// <summary>
/// The narrow byte-level seam <see cref="TaitCcdiRadio"/> drives the wire over — same pattern
/// as <c>Packet.Kiss.Serial</c>'s <c>ISerialPortIo</c>: blocking finite-timeout reads, blocking
/// writes, and a name. Production wraps a <see cref="SerialPort"/>; tests substitute a scripted
/// fake so transactions, unsolicited demux, and dispose ordering run without hardware.
/// </summary>
internal interface ISerialIo : IDisposable
{
    /// <summary>The underlying port name (e.g. <c>/dev/ttyUSB0</c>).</summary>
    string PortName { get; }

    /// <summary>Read available bytes; blocks up to the port's read timeout and throws
    /// <see cref="TimeoutException"/> when none arrive.</summary>
    int Read(byte[] buffer, int offset, int count);

    /// <summary>Write <paramref name="count"/> bytes.</summary>
    void Write(byte[] buffer, int offset, int count);
}

/// <summary>The production <see cref="ISerialIo"/>: a thin pass-through to a
/// <see cref="SerialPort"/>.</summary>
internal sealed class SystemSerialIo(SerialPort serial) : ISerialIo
{
    public string PortName => serial.PortName;

    public int Read(byte[] buffer, int offset, int count) => serial.Read(buffer, offset, count);

    public void Write(byte[] buffer, int offset, int count) => serial.Write(buffer, offset, count);

    public void Dispose() => serial.Dispose();
}
