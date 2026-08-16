namespace Packet.Node.Api;

/// <summary>
/// Endpoint metadata marking a route as one that may take its bearer token from the
/// <c>?access_token=</c> query string instead of the <c>Authorization</c> header.
/// </summary>
/// <remarks>
/// <para>
/// This exists for exactly one reason: a browser <c>EventSource</c> has no header API,
/// so an SSE feed the control panel opens cannot present a bearer token any other way.
/// The JWT-bearer <c>OnMessageReceived</c> handler in <c>Program.cs</c> reads the query
/// parameter only for endpoints carrying this marker; every other route keeps
/// header-only auth, so tokens-in-URLs (which leak into proxy logs and referrers) stay
/// confined to the six SSE routes that need them.
/// </para>
/// <para>
/// <b>Why metadata and not a path list.</b> The predicate used to be a hand-maintained
/// list of paths in <c>Program.cs</c>, which silently fell out of step when
/// <c>/ports/{id}/tuning/events</c> and <c>/ports/{id}/spectrum/events</c> were added:
/// both 401'd under the default auth-on posture, and the Link Tuner / Waterfall screens
/// reported the dead stream as "ended"/"unavailable" (review item C001,
/// <see href="https://github.com/packet-net/packet.net/issues/689">#689</see>). Marking
/// the route where it is mapped keeps the permission next to the endpoint, so a new SSE
/// feed carries it or it does not exist.
/// </para>
/// </remarks>
public sealed class AcceptsQueryAccessToken
{
    /// <summary>The single shared marker instance - it carries no state.</summary>
    public static readonly AcceptsQueryAccessToken Instance = new();

    private AcceptsQueryAccessToken()
    {
    }
}
