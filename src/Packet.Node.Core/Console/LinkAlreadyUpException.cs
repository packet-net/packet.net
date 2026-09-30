using Packet.Core;

namespace Packet.Node.Core.Console;

/// <summary>
/// A dial was asked for a peer this callsign already has a link up with, and that link is
/// held by someone else (a console, or an app's accept). Posting the dial's DL-CONNECT
/// request onto it would reset a link its owner is using (figc4.4 <c>t07</c>) and wrap the
/// same session a second time, so the dial is refused instead (packet-net/packet.net#862).
/// The RHPv2 server answers it with error code 18, "Already connected".
/// </summary>
public sealed class LinkAlreadyUpException : InvalidOperationException
{
    public LinkAlreadyUpException(Callsign local, Callsign remote)
        : base($"Already connected to {remote} from {local}.")
    {
        Local = local;
        Remote = remote;
    }

    /// <summary>The callsign the dial would have originated from.</summary>
    public Callsign Local { get; }

    /// <summary>The peer the link is up with.</summary>
    public Callsign Remote { get; }
}
