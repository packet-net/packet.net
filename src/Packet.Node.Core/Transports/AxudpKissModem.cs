using System.Net;
using System.Runtime.CompilerServices;
using Packet.Axudp;
using Packet.Core;
using Packet.Kiss;

namespace Packet.Node.Core.Transports;

/// <summary>
/// Presents an <see cref="AxudpSocket"/> (AX.25 frames over UDP) as an
/// <see cref="IKissModem"/>, so an <c>Ax25Listener</c> can run over an AXUDP
/// tunnel through the exact same seam the KISS transports use.
/// </summary>
/// <remarks>
/// <para>
/// AXUDP is not KISS — there is no SLIP framing, no command byte, no CSMA. A
/// datagram's payload <em>is</em> the AX.25 frame body (the same KISS-form octets
/// <c>Ax25Frame.ToBytes()</c> / <c>Ax25Listener</c> produce), followed by the
/// 2-octet AX.25 FCS (the RFC-1226 AXIP/AXUDP wire form — included by default).
/// The adapter therefore:
/// </para>
/// <list type="bullet">
/// <item><b>Send</b> (<see cref="SendFrameAsync"/>): the bytes the listener hands
/// us already are the AX.25 frame body, so they go straight into a datagram to the
/// configured remote — verbatim, or with the CRC-16-CCITT FCS (low byte first)
/// appended when <c>includeFcs</c> is set.</item>
/// <item><b>Receive</b> (<see cref="ReadFramesAsync"/>): each inbound datagram's
/// payload is surfaced as a <see cref="KissFrame"/> with
/// <see cref="KissCommand.Data"/> — exactly what the listener's inbound pump
/// filters for and parses.</item>
/// <item><b>KISS parameter setters</b> (TXDELAY/PERSIST/SLOTTIME/TXTAIL): no-ops.
/// They are CSMA knobs for a half-duplex radio modem; a UDP link has no carrier to
/// sense and no slot timing. They are accepted (so the reconcile path is uniform)
/// and ignored.</item>
/// <item><b>ACKMODE</b> (<see cref="SendFrameWithAckAsync"/>): not supported — there
/// is no TNC to echo a TX-completion. Throws, like the KISS-TCP modem.</item>
/// </list>
/// <para>
/// Unlike a shared RF channel (or the broadcast in-memory bus), AXUDP is a
/// point-to-point tunnel: every outbound frame goes to the one configured
/// <see cref="remote"/>. A frame addressed to a third station is still sent to the
/// configured peer (which the peer's AX.25 layer then ignores by address) — same
/// as pointing a serial KISS link at one modem.
/// </para>
/// <para>
/// <b>FCS / <c>includeFcs</c> — included by default; this is the de-facto wire
/// format.</b> A citation survey of every real AXIP/AXUDP implementation
/// (RFC 1226 + rfc1226-bis, ax25ipd, LinBPQ's BPQAXIP, XRouter — see
/// <c>docs/strict-vs-pragmatic-audit.md</c>) found the 2-octet FCS is mandatory
/// in all of them and FCS-less is accepted by none. So an out-of-the-box AXUDP
/// port talks to LinBPQ/XRouter/ax25ipd/JNOS by default. <c>includeFcs: false</c>
/// is the non-standard FCS-less raw-body form, kept only for a symmetric pdn↔pdn
/// tunnel that opts out on both ends. Stripping the FCS on receive is mandatory,
/// not cosmetic, when the link uses one — see <see cref="ReadFramesAsync"/>.
/// </para>
/// </remarks>
public sealed class AxudpKissModem : IKissModem, IAsyncDisposable
{
    private readonly AxudpSocket socket;
    private readonly IPEndPoint remote;
    private readonly bool includeFcs;
    private int disposed;

    /// <summary>The local UDP port this modem is bound to (0 in config resolves
    /// to a real ephemeral port, surfaced here).</summary>
    public int LocalPort => socket.LocalPort;

    /// <summary>
    /// Open an AXUDP modem: bind <paramref name="localPort"/> for receive and send
    /// every frame to <paramref name="remote"/>.
    /// </summary>
    /// <param name="remote">The remote AXUDP peer every frame is sent to.</param>
    /// <param name="localPort">Local UDP port to bind for receive (0 = ephemeral).</param>
    /// <param name="includeFcs">Append (and, on receive, strip+validate) the
    /// 2-octet CRC-16/X.25 FCS. <c>true</c> (the default) is the standard RFC-1226
    /// AXIP/AXUDP wire form that every real peer expects — LinBPQ's BPQAXIP over UDP
    /// (source-verified), XRouter's AXUDP, ax25ipd, JNOS. Set <c>false</c> only for
    /// the non-standard FCS-less raw-body form (a symmetric pdn↔pdn tunnel that opts
    /// out on both ends); no surveyed real implementation accepts it.</param>
    public AxudpKissModem(IPEndPoint remote, int localPort = 0, bool includeFcs = true)
    {
        this.remote = remote ?? throw new ArgumentNullException(nameof(remote));
        this.includeFcs = includeFcs;
        socket = new AxudpSocket(localPort);
    }

    /// <inheritdoc/>
    public async Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
    {
        // The listener hands us the AX.25 frame body (KISS form, no FCS) — that is
        // exactly the AXUDP datagram payload. Send verbatim, or with the FCS
        // (low byte first, matching Ax25Frame.WriteToWithFcs) appended.
        if (!includeFcs)
        {
            await socket.SendRawAsync(remote, ax25Bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        var body = ax25Bytes.Span;
        var withFcs = new byte[body.Length + 2];
        body.CopyTo(withFcs);
        ushort fcs = Crc16Ccitt.Compute(body);
        withFcs[body.Length] = (byte)(fcs & 0xFF);
        withFcs[body.Length + 1] = (byte)((fcs >> 8) & 0xFF);
        await socket.SendRawAsync(remote, withFcs, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<KissFrame> ReadFramesAsync(
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

            // Surface the datagram payload as a KISS Data frame — the listener's
            // pump parses it as AX.25 next. When the link uses an FCS (a symmetric
            // link parameter, so the peer appends one too), strip + validate the
            // trailing 2 octets here, exactly as a TNC strips the FCS on RX. This
            // is mandatory, not cosmetic: Ax25Frame.TryParse rejects an S-frame
            // (RR/RNR/REJ ack) that carries trailing bytes, so an unstripped FCS
            // tail would silently drop every supervisory frame and break connected
            // mode. A bad FCS is treated as a corrupt datagram and dropped.
            var body = result.RawFrame;
            if (includeFcs)
            {
                if (!TryStripFcs(body, out body))
                {
                    continue;   // too short, or FCS mismatch — drop
                }
            }
            yield return new KissFrame((byte)0, KissCommand.Data, body);
        }
    }

    // Validate + strip the trailing 2-octet FCS (low byte first, matching
    // Ax25Frame.WriteToWithFcs). Returns false on a too-short datagram or an FCS
    // mismatch (a corrupt datagram to drop).
    private static bool TryStripFcs(byte[] datagram, out byte[] body)
    {
        body = datagram;
        if (datagram.Length < (2 * 7) + 1 + 2)   // min AX.25 frame + 2-octet FCS
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
    public Task<AckModeReceipt> SendFrameWithAckAsync(
        ReadOnlyMemory<byte> ax25Bytes, TimeSpan? timeout = null, ushort? sequenceTag = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("AXUDP has no TNC TX-completion echo; ACKMODE is not supported.");

    // CSMA knobs are inert on a UDP tunnel — accept + ignore so the reconcile
    // path is uniform across transports.
    /// <inheritdoc/>
    public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <inheritdoc/>
    public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <inheritdoc/>
    public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <inheritdoc/>
    public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;

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
