using Packet.Core;

namespace Packet.Node.Core.Transports;

/// <summary>
/// Optional capability: a transport that routes frames by destination callsign and so can say,
/// before anything is sent, that it has nowhere to send a frame for a given station (multipoint
/// AXUDP with no peers entry). The outbound connector uses it to fail a connect at once with a
/// clear error instead of retrying SABMs into the void until N2 x T1 runs out.
/// </summary>
public interface IRouteAwareTransport
{
    /// <summary>
    /// True when a frame addressed to <paramref name="destination"/> would be sent somewhere
    /// (a configured peer, or an endpoint learned from that station).
    /// </summary>
    bool HasRouteTo(Callsign destination);
}
