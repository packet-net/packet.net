using System.Net;
using System.Net.Sockets;
using Packet.Ax25;

namespace Packet.Axudp;

/// <summary>
/// A bidirectional AXUDP endpoint. AXUDP is UDP encapsulation of raw AX.25
/// frames — the UDP payload is the AX.25 frame body (no opening / closing
/// HDLC flag, no FCS). Each peer maintains its own UDP socket and addresses
/// remotes by <see cref="IPEndPoint"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the simplest AXUDP variant in common use (matches LinBPQ's AXIP
/// with UDP=1 and no checksum-mode prefix). Future work may add header-prefix
/// variants if real-world interop requires them.
/// </para>
/// <para>
/// FCS handling: since the UDP transport is reliable in the practical sense
/// (frames are not corrupted in transit), AXUDP traditionally omits the FCS
/// — the receiver doesn't need to verify it. <see cref="SendAsync"/> writes
/// the AX.25 body only; <see cref="ReceiveAsync"/> expects the same.
/// </para>
/// </remarks>
public sealed class AxudpSocket : IDisposable
{
    private readonly UdpClient udp;

    /// <summary>The local UDP port we're bound to.</summary>
    public int LocalPort { get; }

    /// <summary>
    /// Open an AXUDP endpoint bound to <paramref name="localPort"/> (0 picks
    /// any free ephemeral port). The endpoint listens on all interfaces.
    /// </summary>
    public AxudpSocket(int localPort = 0)
    {
        udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        LocalPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Send a serialised AX.25 frame to <paramref name="remote"/>.
    /// </summary>
    public async Task<int> SendAsync(IPEndPoint remote, Ax25Frame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var bytes = frame.ToBytes();
        return await udp.SendAsync(bytes, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Send raw bytes to <paramref name="remote"/>. Caller is responsible for
    /// constructing a well-formed AX.25 frame body — useful for replaying
    /// captures or forwarding unparsed frames.
    /// </summary>
    public async Task<int> SendRawAsync(IPEndPoint remote, ReadOnlyMemory<byte> rawFrame, CancellationToken cancellationToken = default)
    {
        return await udp.SendAsync(rawFrame, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for the next datagram. Returns the sender endpoint, the raw bytes,
    /// and an attempted decode (null if the bytes weren't a parseable AX.25
    /// frame).
    /// </summary>
    public async Task<AxudpReceiveResult> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        var raw = result.Buffer;
        Ax25Frame? decoded = Ax25Frame.TryParse(raw, out var frame) ? frame : null;
        return new AxudpReceiveResult(result.RemoteEndPoint, raw, decoded);
    }

    /// <inheritdoc/>
    public void Dispose() => udp.Dispose();
}

/// <summary>
/// One received AXUDP datagram.
/// </summary>
/// <param name="From">The remote endpoint that sent the datagram.</param>
/// <param name="RawFrame">The raw UDP payload — the AX.25 frame bytes.</param>
/// <param name="DecodedFrame">
/// The parsed frame, or <c>null</c> if the bytes did not decode as a valid
/// AX.25 frame (callers may still want to inspect <see cref="RawFrame"/>).
/// </param>
public readonly record struct AxudpReceiveResult(IPEndPoint From, byte[] RawFrame, Ax25Frame? DecodedFrame);
