namespace Packet.Node.Core.Console;

/// <summary>
/// What a dial's claim on a peer (the <c>claim</c> an <see cref="Ax25OutboundConnector"/> is
/// given) can report back: while the claim is held the host drops the peer's SessionAccepted, so
/// the dial's own link does not start a console against the station it dials. If the peer's own
/// call set that link up meanwhile and the dial then fails, nobody holds the link, and it must not
/// be left up acknowledging the peer's frames to no one (packet.net#867).
/// </summary>
public interface IOutboundClaim : IDisposable
{
    /// <summary>True when an accept for the claimed link was dropped under this claim, no dial on
    /// the same link has handed it over, and no other dial on it is still running.</summary>
    bool LeavesALinkNobodyHolds { get; }

    /// <summary>A dial on the claimed link returned it to its caller, who now holds it.</summary>
    void MarkDelivered();
}
