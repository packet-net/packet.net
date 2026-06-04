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
/// 2-octet AX.25 FCS (the RFC-1226 AXIP/AXUDP wire form — always present). The
/// adapter therefore:
/// </para>
/// <list type="bullet">
/// <item><b>Send</b> (<see cref="SendFrameAsync"/>): the bytes the listener hands
/// us already are the AX.25 frame body, so they go straight into a datagram to the
/// configured remote with the CRC-16-CCITT FCS (low byte first) appended.</item>
/// <item><b>Receive</b> (<see cref="ReadFramesAsync"/>): <see cref="AxudpSocket"/>
/// strips + validates the trailing FCS and surfaces the bare AX.25 frame body,
/// which is passed on as a <see cref="KissFrame"/> with
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
/// <b>AXUDP unconditionally carries the 2-octet AX.25 FCS — this is the de-facto
/// wire format.</b> A citation survey of every real AXIP/AXUDP implementation
/// (RFC 1226 + rfc1226-bis, ax25ipd, LinBPQ's BPQAXIP, XRouter — see
/// <c>docs/strict-vs-pragmatic-audit.md</c>) found the FCS is mandatory in all of
/// them and FCS-less is accepted by none, so an AXUDP port talks to
/// LinBPQ/XRouter/ax25ipd/JNOS out of the box. Stripping + validating the FCS on
/// receive (done in <see cref="AxudpSocket.ReceiveAsync"/>) is mandatory, not
/// cosmetic: <c>Ax25Frame.TryParse</c> rejects an S-frame (RR/RNR/REJ ack) that
/// carries trailing bytes, so an unstripped FCS tail would silently drop every
/// supervisory frame and break connected mode.
/// </para>
/// </remarks>
public sealed class AxudpKissModem : IKissModem, IAsyncDisposable
{
    private readonly AxudpSocket socket;
    private readonly IPEndPoint remote;
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
    public AxudpKissModem(IPEndPoint remote, int localPort = 0)
    {
        this.remote = remote ?? throw new ArgumentNullException(nameof(remote));
        socket = new AxudpSocket(localPort);
    }

    /// <inheritdoc/>
    public async Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
    {
        // The listener hands us the AX.25 frame body (KISS form, no FCS) — that is
        // the AXUDP datagram payload. Append the 2-octet FCS (low byte first,
        // matching Ax25Frame.WriteToWithFcs); AXUDP always carries it.
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

            // AxudpSocket.ReceiveAsync has already stripped + validated the FCS and
            // dropped any bad-FCS datagram, so result.RawFrame is the bare AX.25
            // frame body. Surface it as a KISS Data frame — the listener's pump
            // parses it as AX.25 next.
            yield return new KissFrame((byte)0, KissCommand.Data, result.RawFrame);
        }
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
