using Packet.Core;

namespace Packet.Node.Core.Applications;

/// <summary>
/// A read-only view of the app callsigns the node is <b>currently</b> registered for on behalf of
/// external programs - what each app actually <c>bind</c>ed over RHP, not what the node resolved it
/// <i>should</i> bind. The console's bare-verb → service-app resolution
/// (<see cref="IApplicationHost.ResolveServiceCommandCallsign"/>) consults this so a self-deriving
/// app - one that bound a different SSID than its node-resolved <c>PDN_APP_CALLSIGN</c> - is still
/// reachable by its command verb (packet.net#476). The RHP <c>bind</c> wire carries only a callsign
/// (XRouter-compatible - no app id), so the registry is keyed purely by callsign; this seam exposes
/// the live key set so the resolver can bridge the node-resolved identity to the actually-bound one.
/// </summary>
public interface ILocalAppRegistry
{
    /// <summary>True when <paramref name="callsign"/> is registered as a local app right now.</summary>
    bool IsRegistered(Callsign callsign);

    /// <summary>A snapshot of every app callsign currently registered (in no particular order).
    /// A fresh list each call so the caller can hold it without racing a bind/unbind.</summary>
    IReadOnlyCollection<Callsign> RegisteredCallsigns();
}
