using System.IO.Ports;

namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// The narrow byte-level seam <see cref="BootloaderNinoTncFirmwareFlasher"/>
/// drives the wire over. The bootloader protocol is raw bytes (not KISS), so
/// this is deliberately simpler than <c>Packet.Kiss.Serial</c>'s pump-oriented
/// seam: single-byte finite-timeout reads, writes, and an input-discard.
/// Production wraps a <see cref="SerialPort"/>
/// (<see cref="SystemBootloaderSerialPort"/>); tests substitute a scripted
/// fake so every protocol path can be exercised without hardware.
/// </summary>
internal interface INinoTncBootloaderSerialPort : IDisposable
{
    /// <summary>The underlying port name (e.g. <c>/dev/ttyACM1</c>).</summary>
    string PortName { get; }

    /// <summary>
    /// Read one byte, blocking up to the port's read timeout. Returns the
    /// byte (0–255), or <c>-1</c> when the timeout elapsed with nothing
    /// received — a full read-timeout of line silence.
    /// </summary>
    int ReadByte();

    /// <summary>Write the bytes to the port.</summary>
    void Write(ReadOnlySpan<byte> bytes);

    /// <summary>Discard everything currently in the receive buffer.</summary>
    void DiscardInBuffer();
}

/// <summary>
/// The production <see cref="INinoTncBootloaderSerialPort"/>: a
/// <see cref="SerialPort"/> opened the way the verified flash protocol
/// requires — 57 600 8N1, no flow control, finite read timeout. DTR/RTS are
/// asserted to match pyserial's defaults (upstream <c>flashtnc.py</c> is the
/// hardware-validated reference) and our own <c>KissSerialModem</c>.
/// </summary>
internal sealed class SystemBootloaderSerialPort : INinoTncBootloaderSerialPort
{
    private readonly SerialPort serial;

    private SystemBootloaderSerialPort(SerialPort serial) => this.serial = serial;

    /// <summary>Open <paramref name="portName"/> for a flash session.</summary>
    public static SystemBootloaderSerialPort Open(string portName, TimeSpan readTimeout)
    {
        var serial = new SerialPort(portName, NinoTncSerialPort.DefaultBaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = (int)readTimeout.TotalMilliseconds,
            WriteTimeout = 5000,
            DtrEnable = true,
            RtsEnable = true,
        };
        serial.Open();
        return new SystemBootloaderSerialPort(serial);
    }

    public string PortName => serial.PortName;

    public int ReadByte()
    {
        try
        {
            return serial.ReadByte();
        }
        catch (TimeoutException)
        {
            return -1;
        }
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        var buffer = bytes.ToArray();
        serial.Write(buffer, 0, buffer.Length);
    }

    public void DiscardInBuffer() => serial.DiscardInBuffer();

    public void Dispose() => serial.Dispose();
}
