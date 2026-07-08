using System.Net.Sockets;

namespace Packet.Radio.Tait;

/// <summary>
/// A TCP-backed <see cref="ISerialIo"/>: the Tait CCDI driver's byte seam pointed at a remote
/// "head-end" that bridges a serial port as a raw binary TCP pipe (the split-station topology —
/// see <c>docs/research/split-station-rf-headend.md</c>). The socket carries the pure
/// CCDI/PROGRESS byte stream unchanged, so the whole radio-control stack (transactions, DCD
/// carrier-sense edges, SDM, telemetry) runs over the wire exactly as it does over a local
/// <see cref="System.IO.Ports.SerialPort"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-timeout contract.</b> <see cref="TaitCcdiRadio"/>'s read pump relies on the
/// <see cref="System.IO.Ports.SerialPort"/> semantics of <see cref="Read"/>: block up to a short
/// finite timeout and throw <see cref="TimeoutException"/> when the line is idle (the pump
/// swallows it and loops; the transaction engine's own deadline detects a no-response). This
/// class reproduces that with the socket's <see cref="Socket.ReceiveTimeout"/> — a timed-out
/// receive surfaces as <see cref="TimeoutException"/>, never as a 0-length read (which would
/// hot-spin the pump) and never blocked forever.
/// </para>
/// <para>
/// <b>Half-open detection.</b> Beyond the per-read pacing timeout, a longer read-idle budget
/// guards against a silently dropped connection (peer rebooted, cable pulled — no FIN),
/// mirroring <c>Packet.Kiss.KissTcpClient</c> (#464): once the link has produced no byte for
/// that budget a receive timeout is escalated to an <see cref="IOException"/> so the pump faults
/// the radio rather than pacing forever. OS TCP keepalive is enabled as the faster probe.
/// </para>
/// <para>
/// <b>Line rate.</b> The data socket is a pure binary pipe already clocked at the head-end;
/// <see cref="SetBaudRate"/> therefore routes to an injectable out-of-band control callback (the
/// head-end verb a later stage supplies), defaulting to a no-op so a plain raw pipe works today.
/// </para>
/// </remarks>
internal sealed class TcpSerialIo : ISerialIo
{
    /// <summary>Per-read pacing timeout — mirrors the local <see cref="System.IO.Ports.SerialPort.ReadTimeout"/>
    /// of 100 ms the driver opens with, so the pump wakes to check cancellation ~10×/s.</summary>
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Read-idle budget before a quiet link is presumed half-open and dead. Generous
    /// because a healthy packet channel is often quiet for long stretches; OS keepalive is the
    /// faster probe. Mirrors <c>KissTcpClient.DefaultReadIdleTimeout</c>.</summary>
    public static readonly TimeSpan DefaultReadIdleTimeout = TimeSpan.FromMinutes(5);

    private readonly Socket socket;
    private readonly string portName;
    private readonly Func<int, CancellationToken, Task>? setBaud;
    private readonly TimeSpan readIdleTimeout;
    private readonly TimeProvider clock;
    private DateTimeOffset lastReadActivity;

    private TcpSerialIo(
        Socket socket, string portName, Func<int, CancellationToken, Task>? setBaud,
        TimeSpan readTimeout, TimeSpan readIdleTimeout, TimeProvider clock)
    {
        this.socket = socket;
        this.portName = portName;
        this.setBaud = setBaud;
        this.readIdleTimeout = readIdleTimeout;
        this.clock = clock;
        lastReadActivity = clock.GetUtcNow();
        socket.ReceiveTimeout = (int)Math.Clamp(readTimeout.TotalMilliseconds, 1, int.MaxValue);
        EnableTcpKeepAlive(socket);
    }

    public string PortName => portName;

    /// <summary>Dial the head-end at <paramref name="host"/>:<paramref name="port"/> and wrap the
    /// connected socket as an <see cref="ISerialIo"/>.</summary>
    /// <param name="host">Head-end host bridging the serial port.</param>
    /// <param name="port">Head-end TCP port for this radio's raw byte pipe.</param>
    /// <param name="setBaud">Async line-control callback <see cref="SetBaudRate"/> routes to; null
    /// (the default) makes baud a no-op.</param>
    /// <param name="readTimeout">Per-read pacing timeout; null uses <see cref="DefaultReadTimeout"/>.</param>
    /// <param name="readIdleTimeout">Half-open death budget; null uses <see cref="DefaultReadIdleTimeout"/>.</param>
    /// <param name="timeProvider">Clock (test seam); null uses the system clock.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public static async Task<TcpSerialIo> ConnectAsync(
        string host,
        int port,
        Func<int, CancellationToken, Task>? setBaud = null,
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
        return new TcpSerialIo(
            socket, $"{host}:{port}", setBaud,
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
                // breaks and the radio faults, mirroring a closed SerialPort handle.
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

    public void SetBaudRate(int baudRate)
    {
        // The data socket is a pure binary pipe already clocked at the head-end, so a line-rate
        // change travels out-of-band through the injected control callback (the head-end verb a
        // later stage supplies). Default null = no-op, so a plain raw pipe still works today.
        if (setBaud is null)
        {
            return;
        }
        try
        {
            setBaud(baudRate, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Normalise the callback's failure (typically an HTTP error against an unreachable
            // head-end) to the ISerialIo contract's IO failure, so callers' best-effort teardown
            // paths — which catch IOException like any dead serial handle — behave the same here.
            throw new IOException($"line-control (baud) callback failed for {portName}", ex);
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
