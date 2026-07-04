using Packet.Ax25.Transport;

namespace Packet.Node.Core.Telemetry;

/// <summary>
/// A node-layer read of the per-frame radio metadata (<see cref="RadioMetadata"/>) the
/// <c>Packet.Radio.RssiTaggingTransport</c> attaches to each inbound frame. The node consumes it
/// to stamp RSSI/SNR onto the monitor / heard / traffic surfaces — a <b>node-telemetry</b> concern,
/// deliberately kept OFF the AX.25-protocol contract (<c>Ax25FrameEventArgs</c> stays
/// <c>Frame</c>/<c>Direction</c>/<c>Timestamp</c>, and the parity-tracked <c>Ax25Listener</c>
/// surface is untouched).
/// </summary>
/// <remarks>
/// Read <see cref="LatestInboundRadio"/> only from inside an <c>RX</c>
/// <c>Ax25Listener.FrameTraced</c> handler — see <see cref="InboundRadioTap"/> for why that read is
/// race-free.
/// </remarks>
public interface IInboundRadioSource
{
    /// <summary>
    /// The <see cref="RadioMetadata"/> of the most recently delivered inbound frame, or <c>null</c>
    /// when the last frame carried none (no radio attributed it) — or when no frame has arrived yet.
    /// </summary>
    RadioMetadata? LatestInboundRadio { get; }
}

/// <summary>
/// A thin node-owned <see cref="IAx25Transport"/> decorator that records each inbound frame's
/// <see cref="RadioMetadata"/> as it passes from the radio-tagging transport up to the
/// <c>Ax25Listener</c>. It is the node's consumption point for the radio metadata the AX.25 layer
/// carries but never surfaces on its event contract, so per-frame RSSI/SNR can reach
/// <c>NodeTelemetry</c> without widening the parity-tracked <c>Ax25Listener</c> / <c>Ax25FrameEventArgs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the correlation is race-free.</b> The listener's inbound pump is a single task that, for
/// each frame, pulls one <see cref="Ax25InboundFrame"/> from this transport, parses it, and — for a
/// frame that parses — synchronously raises <c>FrameTraced(Received)</c> before it loops back to pull
/// the next frame. This decorator writes <see cref="LatestInboundRadio"/> immediately before it
/// yields each frame, so between that write and the pump's <c>FrameTraced</c> for the same frame no
/// other inbound frame is processed (one pump thread, no intervening <c>await</c>). A
/// <c>NodeTelemetry</c> tap reading <see cref="LatestInboundRadio"/> from that RX handler therefore
/// sees exactly this frame's metadata. Frames the listener drops (parse-rejected) simply overwrite
/// the field before the next trace — their metadata is discarded, which is correct: no monitor event
/// is produced for them either. TX traces run on other threads and must NOT read this field (there is
/// no inbound radio for an outbound frame); the telemetry tap gates the read on RX for that reason.
/// </para>
/// <para>
/// <b>Ownership.</b> Unlike the RSSI-tagging transport (which does not own its inner), this
/// decorator OWNS the transport it wraps: disposing it disposes the tagging wrapper (stopping its
/// sampler) — the outermost teardown step the port's <c>RunningPort</c> performs, before the modem
/// chain and the radio.
/// </para>
/// </remarks>
public sealed class InboundRadioTap : IAx25Transport, IInboundRadioSource, IAsyncDisposable
{
    private readonly IAx25Transport inner;
    private readonly object gate = new();
    private RadioMetadata? latest;

    /// <summary>Wrap <paramref name="inner"/> (the radio-tagging transport). Ownership IS taken —
    /// <see cref="DisposeAsync"/> disposes it.</summary>
    public InboundRadioTap(IAx25Transport inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
    }

    /// <inheritdoc/>
    // A short lock (not Volatile — RadioMetadata? is a value type) guards the field. In practice
    // both the write below and this read run on the single inbound-pump thread, so it is uncontended.
    public RadioMetadata? LatestInboundRadio
    {
        get { lock (gate) { return latest; } }
    }

    /// <inheritdoc/>
    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default) =>
        inner.SendAsync(ax25, cancellationToken);

    /// <inheritdoc/>
    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            // Publish before yielding: the listener pump reads it during the synchronous
            // FrameTraced(Received) that follows the yield (see the type remarks).
            lock (gate)
            {
                latest = frame.Radio;
            }
            yield return frame;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
