namespace Packet.LinkBench.Channel;

/// <summary>
/// The DCD-over-KISS forward seam (link-bench plan §8) - a pluggable policy that
/// may defer a transmission while the channel is busy (CSMA-by-DCD). Today the
/// only implementation is <see cref="NoOpTxGatePolicy"/>: transmit immediately,
/// exactly the behaviour a host has without carrier sense. Once Nino's KISS DCD
/// message format is agreed, a real policy waits here on the channel's
/// busy/clear signal (<see cref="InProcChannel.ChannelStateChanged"/>) before
/// keying up - and the half-duplex rung measures what that buys.
/// </summary>
/// <remarks>
/// Keep this distinct from the ackmode TX-complete echo: ackmode is a reply to
/// MY send ("your frame finished transmitting"); DCD is an unsolicited
/// modem→host state signal about ANYONE's transmission. The gate consumes the
/// latter, never the former.
/// </remarks>
internal interface ITxGatePolicy
{
    /// <summary>Called by an endpoint's TX pump immediately before it keys up.
    /// A CSMA-by-DCD policy defers here while the channel is busy.</summary>
    ValueTask WaitForClearAsync(InProcChannel channel, CancellationToken ct);
}

/// <summary>No carrier sense: transmit immediately. The status quo.</summary>
internal sealed class NoOpTxGatePolicy : ITxGatePolicy
{
    public static readonly NoOpTxGatePolicy Instance = new();
    private NoOpTxGatePolicy() { }
    public ValueTask WaitForClearAsync(InProcChannel channel, CancellationToken ct) => default;
}
