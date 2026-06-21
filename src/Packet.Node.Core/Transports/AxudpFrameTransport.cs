using System.Net;
using System.Runtime.CompilerServices;
using Packet.Ax25.Transport;
using Packet.Axudp;
using Packet.Core;

namespace Packet.Node.Core.Transports;

/// <summary>
/// Presents an <see cref="AxudpSocket"/> (AX.25 frames over UDP) as an
/// <see cref="IAx25Transport"/>, so an <c>Ax25Listener</c> runs over an AXUDP tunnel
/// through the exact same seam the KISS transports use.
/// </summary>
/// <remarks>
/// <para>
/// AXUDP is <b>not KISS</b> — there is no SLIP framing, no command byte, no CSMA. A
/// datagram's payload <em>is</em> the AX.25 frame body, followed by the 2-octet AX.25 FCS
/// (the RFC-1226 AXIP/AXUDP wire form — always present). So this transport implements ONLY
/// the neutral <see cref="IAx25Transport"/>: no <see cref="ICsmaChannelParams"/> (a UDP link
/// has no carrier to sense or slot timing) and no <see cref="ITxCompletionTransport"/> (there
/// is no TNC to echo a TX-completion). A consumer that wants either capability feature-detects
/// its absence and degrades — it is never offered a no-op. This is the transport whose mere
/// existence proves KISS is one implementation behind the seam, not a property of it: it
/// constructs no KISS object at all.
/// </para>
/// <list type="bullet">
/// <item><b>Send</b>: the bytes the listener hands us already are the AX.25 frame body, so they
/// go straight into a datagram to the configured remote with the CRC-16-CCITT FCS (low byte
/// first) appended.</item>
/// <item><b>Receive</b>: <see cref="AxudpSocket"/> strips + validates the trailing FCS and
/// surfaces the bare AX.25 frame body, yielded directly as an <see cref="Ax25InboundFrame"/> —
/// no KISS envelope to wrap or unwrap.</item>
/// </list>
/// <para>
/// Unlike a shared RF channel (or the broadcast in-memory bus), AXUDP is a point-to-point
/// tunnel: every outbound frame goes to the one configured <see cref="remote"/>. A frame
/// addressed to a third station is still sent to the configured peer (which the peer's AX.25
/// layer then ignores by address) — same as pointing a serial KISS link at one modem.
/// </para>
/// <para>
/// <b>AXUDP unconditionally carries the 2-octet AX.25 FCS — the de-facto wire format.</b> A
/// citation survey of every real AXIP/AXUDP implementation (RFC 1226 + rfc1226-bis, ax25ipd,
/// LinBPQ's BPQAXIP, XRouter — see <c>docs/strict-vs-pragmatic-audit.md</c>) found the FCS
/// mandatory in all of them and FCS-less accepted by none, so an AXUDP port talks to
/// LinBPQ/XRouter/ax25ipd/JNOS out of the box. Stripping + validating the FCS on receive (in
/// <see cref="AxudpSocket.ReceiveAsync"/>) is mandatory, not cosmetic:
/// <c>Ax25Frame.TryParse</c> rejects an S-frame (RR/RNR/REJ ack) carrying trailing bytes, so
/// an unstripped FCS tail would silently drop every supervisory frame and break connected mode.
/// </para>
/// </remarks>
public sealed class AxudpFrameTransport : IAx25Transport
{
    private readonly AxudpSocket socket;
    private readonly IPEndPoint remote;
    private readonly TimeProvider clock;
    private int disposed;

    /// <summary>The local UDP port this transport is bound to (0 in config resolves
    /// to a real ephemeral port, surfaced here).</summary>
    public int LocalPort => socket.LocalPort;

    /// <summary>
    /// Open an AXUDP transport: bind <paramref name="localPort"/> for receive and send
    /// every frame to <paramref name="remote"/>.
    /// </summary>
    /// <param name="remote">The remote AXUDP peer every frame is sent to.</param>
    /// <param name="localPort">Local UDP port to bind for receive (0 = ephemeral).</param>
    /// <param name="timeProvider">Clock for stamping inbound-frame capture time (default system).</param>
    public AxudpFrameTransport(IPEndPoint remote, int localPort = 0, TimeProvider? timeProvider = null)
    {
        this.remote = remote ?? throw new ArgumentNullException(nameof(remote));
        clock = timeProvider ?? TimeProvider.System;
        socket = new AxudpSocket(localPort);
    }

    /// <inheritdoc/>
    public async Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
    {
        // The listener hands us the AX.25 frame body (no FCS) — that is the AXUDP datagram
        // payload. Append the 2-octet FCS (low byte first, matching Ax25Frame.WriteToWithFcs);
        // AXUDP always carries it.
        var body = ax25.Span;
        var withFcs = new byte[body.Length + 2];
        body.CopyTo(withFcs);
        ushort fcs = Crc16Ccitt.Compute(body);
        withFcs[body.Length] = (byte)(fcs & 0xFF);
        withFcs[body.Length + 1] = (byte)((fcs >> 8) & 0xFF);
        await socket.SendRawAsync(remote, withFcs, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AxudpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (ObjectDisposedException)
            {
                // Socket disposed out from under us (shutdown) — end the stream.
                yield break;
            }

            // AxudpSocket.ReceiveAsync has already stripped + validated the FCS and dropped any
            // bad-FCS datagram, so result.RawFrame is the bare AX.25 frame body — yield it
            // directly. No KISS object is ever constructed.
            yield return new Ax25InboundFrame(result.RawFrame, PortId: 0, ReceivedAt: clock.GetUtcNow());
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            socket.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
