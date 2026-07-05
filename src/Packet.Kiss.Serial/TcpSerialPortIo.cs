using System.Net.Sockets;

namespace Packet.Kiss.Serial;

/// <summary>
/// A TCP-backed <see cref="ISerialPortIo"/>: the KISS serial modem's byte seam pointed at a
/// remote "head-end" that bridges a serial port as a raw binary TCP pipe (the split-station
/// topology — see <c>docs/research/split-station-rf-headend.md</c>). KISS framing rides the
/// socket unchanged, so a modem — and, layered on top, a NinoTNC's full control surface
/// (GETVER / mode agility / GETRSSI) — works remotely, distinct from the generic control-less
/// <c>kiss-tcp</c> transport.
/// </summary>
/// <remarks>
/// Reproduces the <see cref="System.IO.Ports.SerialPort"/> read contract
/// <see cref="KissSerialModem"/>'s pump depends on: a finite per-read timeout that throws
/// <see cref="TimeoutException"/> when idle (swallowed and looped) via the socket's
/// <see cref="Socket.ReceiveTimeout"/>, plus a longer read-idle budget that escalates to an
/// <see cref="IOException"/> to surface a half-open link (peer gone, no FIN) — mirroring
/// <c>Packet.Kiss.KissTcpClient</c> (#464). OS TCP keepalive is enabled as the faster probe.
/// NinoTNC baud is fictional over USB-CDC and irrelevant over TCP, so — unlike the Tait
/// <c>TcpSerialIo</c> — this seam carries no line-rate control.
/// </remarks>
internal sealed class TcpSerialPortIo : ISerialPortIo
{
    /// <summary>Per-read pacing timeout — mirrors the local <see cref="System.IO.Ports.SerialPort.ReadTimeout"/>
    /// of 100 ms the modem opens with, so the pump wakes to check cancellation ~10×/s.</summary>
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Read-idle budget before a quiet link is presumed half-open and dead. Generous
    /// because a healthy packet channel is often quiet for long stretches; OS keepalive is the
    /// faster probe. Mirrors <c>KissTcpClient.DefaultReadIdleTimeout</c>.</summary>
    public static readonly TimeSpan DefaultReadIdleTimeout = TimeSpan.FromMinutes(5);

    private readonly Socket socket;
    private readonly string portName;
    private readonly TimeSpan readIdleTimeout;
    private readonly TimeProvider clock;
    private DateTimeOffset lastReadActivity;

    private TcpSerialPortIo(
        Socket socket, string portName, TimeSpan readTimeout, TimeSpan readIdleTimeout, TimeProvider clock)
    {
        this.socket = socket;
        this.portName = portName;
        this.readIdleTimeout = readIdleTimeout;
        this.clock = clock;
        lastReadActivity = clock.GetUtcNow();
        socket.ReceiveTimeout = (int)Math.Clamp(readTimeout.TotalMilliseconds, 1, int.MaxValue);
        EnableTcpKeepAlive(socket);
    }

    public string PortName => portName;

    /// <summary>Dial the head-end at <paramref name="host"/>:<paramref name="port"/> and wrap the
    /// connected socket as an <see cref="ISerialPortIo"/>.</summary>
    /// <param name="host">Head-end host bridging the serial port.</param>
    /// <param name="port">Head-end TCP port for this modem's raw byte pipe.</param>
    /// <param name="readTimeout">Per-read pacing timeout; null uses <see cref="DefaultReadTimeout"/>.</param>
    /// <param name="readIdleTimeout">Half-open death budget; null uses <see cref="DefaultReadIdleTimeout"/>.</param>
    /// <param name="timeProvider">Clock (test seam); null uses the system clock.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public static async Task<TcpSerialPortIo> ConnectAsync(
        string host,
        int port,
        TimeSpan? readTimeout = null,
        TimeSpan? readIdleTimeout = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new TcpSerialPortIo(
            socket, $"{host}:{port}",
            readTimeout ?? DefaultReadTimeout,
            readIdleTimeout ?? DefaultReadIdleTimeout,
            timeProvider ?? TimeProvider.System);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            int read = socket.Receive(buffer, offset, count, SocketFlags.None);
            if (read == 0)
            {
                // Orderly close from the head-end (FIN): treat as a hard link failure so the pump
                // faults its inbound stream, mirroring a closed SerialPort handle.
                throw new IOException($"the head-end at {portName} closed the connection");
            }
            lastReadActivity = clock.GetUtcNow();
            return read;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
        {
            // No byte within the per-read window. Normally this is the SerialPort.ReadTimeout
            // contract the pump swallows and loops on — UNLESS the link has been entirely silent
            // past the idle budget, in which case presume it half-open (dead) and fault the pump.
            if (readIdleTimeout > TimeSpan.Zero && readIdleTimeout != Timeout.InfiniteTimeSpan
                && clock.GetUtcNow() - lastReadActivity >= readIdleTimeout)
            {
                throw new IOException(
                    $"no data from the head-end at {portName} for {readIdleTimeout.TotalSeconds:0}s; presuming it is gone");
            }
            throw new TimeoutException();
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        int sent = 0;
        while (sent < count)
        {
            sent += socket.Send(buffer, offset + sent, count - sent, SocketFlags.None);
        }
    }

    public void Dispose() => socket.Dispose();

    // Ask the OS to probe a quiet peer so a half-open connection surfaces as a read error in
    // bounded time. Best-effort: keepalive knobs are platform-dependent and a failure to set them
    // is non-fatal — the read-idle timeout is the portable backstop. Mirrors KissTcpClient.
    private static void EnableTcpKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            TrySetSocketOption(socket, SocketOptionName.TcpKeepAliveTime, 30);
            TrySetSocketOption(socket, SocketOptionName.TcpKeepAliveInterval, 10);
            TrySetSocketOption(socket, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch
        {
            // best-effort; the read-idle timeout still detects a dead link
        }
    }

    private static void TrySetSocketOption(Socket socket, SocketOptionName name, int value)
    {
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, name, value); }
        catch { /* not supported on this platform/stack */ }
    }
}
