using System.Runtime.CompilerServices;

namespace Packet.Ax25.Transport;

/// <summary>
/// The seam between the AX.25 data-link layer and whatever moves AX.25 frames on/off a
/// channel — a KISS TNC, an AXUDP socket, a future AGW or native-socket transport. The
/// currency is the <b>AX.25 frame body</b> (no FCS, no link-framing); the transport owns
/// whatever wire framing its medium needs. KISS is one implementation behind this contract,
/// never a property of it.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately the minimal surface the <c>Ax25Listener</c> actually needs: send a
/// frame, and read the stream of inbound frames. Two further, OPTIONAL capabilities live on
/// separate interfaces a transport may also implement — <see cref="ITxCompletionTransport"/>
/// (confirm a frame left the wire, for T1 re-arm / pacing / latency) and
/// <see cref="ICsmaChannelParams"/> (the half-duplex-radio channel-access knobs). Consumers
/// feature-detect with <c>is</c> and degrade gracefully when a capability is absent, so a
/// transport that has neither (e.g. AXUDP) implements only this interface and fakes nothing.
/// </para>
/// <para>
/// <b>Frame-level seam only.</b> A transport owns framing/FCS but NOT connected-mode L2.
/// Mediums that own the AX.25 session themselves — AGW connected-mode, a SOCK_SEQPACKET
/// kernel socket, VARA's ARQ — are a different (session) layer and do NOT implement this
/// interface; do not force them under it.
/// </para>
/// <para>
/// <b>Scope of the contract:</b> mode-switching (KISS SETHW and the like) is intentionally
/// not here — it varies per modem and is selected from config at construction, living in
/// modem-specific packages (e.g. NinoTNC's <c>SetModeAsync</c>).
/// </para>
/// </remarks>
public interface IAx25Transport : IAsyncDisposable
{
    /// <summary>
    /// Send one AX.25 frame body (no FCS, no link-framing), fire-and-forget at this layer.
    /// The transport applies whatever framing its medium needs.
    /// </summary>
    Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default);

    /// <summary>
    /// Long-running async stream of inbound AX.25 frames, until disposal or
    /// <paramref name="cancellationToken"/> fires. The transport pre-filters to genuine AX.25
    /// frames (a KISS transport drops non-Data KISS commands itself), so the consumer never
    /// sees a non-AX.25 frame and never has to know the wire protocol.
    /// </summary>
    /// <remarks>
    /// The default implementation returns an empty stream — write-only adapters and test
    /// stubs that expose no inbound path leave it as-is, and consumers that need RX simply
    /// see nothing. <c>Ax25Listener</c>-shape consumers expect a long-running enumerable that
    /// yields frames as they arrive.
    /// </remarks>
    IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default)
#pragma warning disable CS1998 // async body, but the empty path doesn't need awaits
        => EmptyAsync(cancellationToken);
#pragma warning restore CS1998

    private static async IAsyncEnumerable<Ax25InboundFrame> EmptyAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Yield zero frames. The await keeps the iterator state-machine honest under
        // cancellation — without it the compiler warns the async body has no awaits.
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
}
