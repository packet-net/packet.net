using System.Net;
using System.Net.Sockets;
using Packet.Core;

namespace Packet.Axudp;

/// <summary>
/// A multipoint AXUDP endpoint - the BPQ <c>BPQAXIP</c> analog. ONE UDP socket
/// reaches MANY partners, each addressed by an <see cref="IPEndPoint"/> the caller
/// resolves from a callsign→ip:port map; inbound datagrams are accepted from any
/// sender on the bound port.
/// </summary>
/// <remarks>
/// <para>
/// This is the multi-partner counterpart to the point-to-point <see cref="AxudpSocket"/>.
/// The wire format is identical - the UDP payload is the AX.25 frame body followed by
/// the mandatory 2-octet AX.25 FCS (CRC-16-CCITT / X.25, low byte first), the RFC-1226
/// AXIP/AXUDP form every real peer (LinBPQ's BPQAXIP, XRouter, ax25ipd, JNOS) requires.
/// The only difference is the addressing model: where <see cref="AxudpSocket"/> binds
/// one socket per peer, this binds <em>one socket for all peers</em> and the caller
/// chooses the destination endpoint per datagram (BPQ's <c>MAP &lt;call&gt; &lt;ip&gt; UDP
/// &lt;port&gt;</c>). Routing the AX.25 frame to the right peer's endpoint - by destination
/// callsign, with a broadcast fan-out - is the responsibility of the
/// <c>Packet.Node.Core</c> adapter that drives this socket; this type is the pure UDP
/// pump (send to an endpoint, receive from anyone).
/// </para>
/// <para>
/// <b>FCS handling is identical to <see cref="AxudpSocket"/>:</b> <see cref="SendAsync"/>
/// always appends the 2-octet FCS, and <see cref="ReceiveAsync"/> always strips +
/// validates it, dropping any datagram whose FCS doesn't check - see the
/// <see cref="AxudpSocket"/> remarks for the citation survey establishing the FCS as
/// the de-facto, mandatory wire form.
/// </para>
/// </remarks>
public sealed class AxudpMultipointSocket : IDisposable
{
    private readonly UdpClient udp;

    /// <summary>The local UDP port we're bound to (the one socket all peers share).</summary>
    public int LocalPort { get; }

    /// <summary>
    /// Open the one multipoint AXUDP socket bound to <paramref name="localPort"/>
    /// (0 picks any free ephemeral port). The socket listens on all interfaces and
    /// accepts datagrams from any sender.
    /// </summary>
    public AxudpMultipointSocket(int localPort = 0)
    {
        udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        LocalPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Send a serialised AX.25 frame body to <paramref name="remote"/>, with the
    /// 2-octet AX.25 FCS appended (the unconditional AXIP/AXUDP wire form). The
    /// caller has already chosen <paramref name="remote"/> for this frame (by
    /// destination-callsign routing or broadcast fan-out).
    /// </summary>
    public async Task<int> SendAsync(IPEndPoint remote, ReadOnlyMemory<byte> ax25Body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var body = ax25Body.Span;
        var withFcs = new byte[body.Length + 2];
        body.CopyTo(withFcs);
        ushort fcs = Crc16Ccitt.Compute(body);
        withFcs[body.Length] = (byte)(fcs & 0xFF);
        withFcs[body.Length + 1] = (byte)((fcs >> 8) & 0xFF);
        return await udp.SendAsync(withFcs, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for the next valid datagram from any sender. The trailing 2-octet AX.25
    /// FCS is stripped + validated; a datagram too short to carry an FCS, or whose
    /// FCS doesn't check, is dropped (and the wait continues) - exactly as every real
    /// AXIP/AXUDP peer drops a bad-CRC datagram. Returns the sender endpoint and the
    /// bare AX.25 frame body (FCS removed). The caller routes the frame up by its
    /// AX.25 address; the sender endpoint is a learned-reply fallback.
    /// </summary>
    public async Task<AxudpMultipointReceiveResult> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (!TryStripFcs(result.Buffer, out var body))
            {
                continue;   // too short, or FCS mismatch - drop, as real peers do
            }
            return new AxudpMultipointReceiveResult(result.RemoteEndPoint, body);
        }
    }

    // Validate + strip the trailing 2-octet FCS (low byte first). Returns false on a
    // too-short datagram or an FCS mismatch (a corrupt datagram to drop). Mirrors
    // AxudpSocket.TryStripFcs exactly.
    private static bool TryStripFcs(byte[] datagram, out byte[] body)
    {
        body = datagram;
        if (datagram.Length < (2 * 7) + 1 + 2)   // min AX.25 frame (2 addresses + control) + 2-octet FCS
        {
            return false;
        }
        var bodySpan = datagram.AsSpan(0, datagram.Length - 2);
        ushort expected = Crc16Ccitt.Compute(bodySpan);
        ushort actual = (ushort)(datagram[^2] | (datagram[^1] << 8));
        if (expected != actual)
        {
            return false;
        }
        body = bodySpan.ToArray();
        return true;
    }

    /// <inheritdoc/>
    public void Dispose() => udp.Dispose();
}

/// <summary>
/// One received multipoint-AXUDP datagram, after the trailing FCS has been stripped +
/// validated.
/// </summary>
/// <param name="From">The remote endpoint that sent the datagram (a learned-reply
/// fallback; the adapter prefers a configured peer endpoint when one matches).</param>
/// <param name="RawFrame">The AX.25 frame body - the datagram payload with the 2-octet
/// FCS removed (what the AX.25 parser / listener consumes).</param>
public readonly record struct AxudpMultipointReceiveResult(IPEndPoint From, byte[] RawFrame);
