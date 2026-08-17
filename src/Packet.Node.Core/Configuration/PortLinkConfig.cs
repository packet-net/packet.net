using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// Which AX.25 version a port's <b>outbound</b> dials offer first.
/// </summary>
/// <remarks>
/// Inbound is never affected: the answerer adopts whatever version the caller's
/// SABM/SABME asked for (figc4.1), so this knob only ever chooses what WE send.
/// </remarks>
public enum LinkDialPreference
{
    /// <summary>
    /// Offer AX.25 v2.2 (SABME / mod-128) first and <b>learn per (port, peer)</b> - today's
    /// behaviour, plus the negative half. A peer that answers is remembered as extended-capable;
    /// a peer that FRMRs or DMs degrades to v2.0 in the engine (Ax25Spec45 / Ax25Spec48); and a
    /// peer that answers a SABME with nothing at all is recorded as silent-to-SABME, so the next
    /// dial to it on this port goes v2.0.
    /// </summary>
    Auto,

    /// <summary>Always offer v2.2 (SABME). No fallback, and no negative is learned from a
    /// silent peer - for a port whose stations are all known v2.2-capable and where a timeout
    /// means "the station is off air", not "the station ignores SABME".</summary>
    V22,

    /// <summary>Always dial plain AX.25 v2.0 (SABM / mod-8). The setting for a port facing
    /// BPQ / LinBPQ or an older TNC: many of them ignore a SABME outright rather than rejecting
    /// it, so a v2.2-first dial burns the whole (N2+1) x T1V budget and fails. The tell in a
    /// <c>bpq32.cfg</c> is <c>MAXFRAME &lt;= 7</c> on that port.</summary>
    V20,
}

/// <summary>
/// Whether a mod-8 dial on this port leads with an XID exchange to negotiate Selective Reject
/// before the SABM (the LinBPQ SREJ accommodation - BPQ only honours an XID that PRECEDES the
/// SABM). Moot on a v2.2 dial, which negotiates XID after the link is up.
/// </summary>
public enum LinkPreConnectXid
{
    /// <summary>Send the pre-connect XID unless the per-peer capability cache has freshly
    /// learned that this peer does not answer one - today's behaviour.</summary>
    Auto,

    /// <summary>Always send the pre-connect XID on a mod-8 dial, cache or no cache.</summary>
    On,

    /// <summary>Never send it: go straight to the SABM (go-back-N link).</summary>
    Off,
}

/// <summary>
/// Per-port <b>link policy</b>: what this port's outbound connects offer, as a declaration by
/// the operator rather than something the node has to discover one stalled dial at a time.
/// Absent (<c>null</c>) - or both members left at <see cref="LinkDialPreference.Auto"/> /
/// <see cref="LinkPreConnectXid.Auto"/> - is byte-for-byte the node's historical behaviour.
/// </summary>
/// <remarks>
/// <para>
/// This is the port-level channel knowledge the operator already has and previously had nowhere
/// to put: "the stations on this HF port are BPQ, they are v2.0". The per-peer
/// <see cref="Packet.Node.Core.Capabilities.PeerCapabilityCache"/> stays the adaptive layer, but
/// it now optimises <em>within</em> a declared policy instead of being the only source of dial
/// knowledge. A declared <see cref="LinkDialPreference.V20"/> / <see cref="LinkDialPreference.V22"/>
/// wins over anything learned; <see cref="LinkDialPreference.Auto"/> leaves the cache in charge.
/// </para>
/// <para>
/// Like the rest of <see cref="PortConfig"/> this is a value record, so the reconcile planner
/// diffs it with <c>Equals</c> and classifies a link-only edit as a <b>live</b> (no-restart)
/// change: it gates future dials only, and rides the same
/// <c>Ax25Listener.UpdateSessionParameters</c> reseed the timer/compat knobs use.
/// </para>
/// </remarks>
public sealed record PortLinkConfig
{
    /// <summary>Which AX.25 version outbound dials on this port offer first.
    /// Default <see cref="LinkDialPreference.Auto"/>.</summary>
    public LinkDialPreference Dial { get; init; } = LinkDialPreference.Auto;

    /// <summary>Whether a mod-8 dial leads with an SREJ-negotiating XID.
    /// Default <see cref="LinkPreConnectXid.Auto"/>.</summary>
    public LinkPreConnectXid PreConnectXid { get; init; } = LinkPreConnectXid.Auto;

    /// <summary>The all-auto policy - what a port with no <c>link:</c> block runs.</summary>
    [YamlIgnore]
    [JsonIgnore]
    public static PortLinkConfig Default { get; } = new();

    /// <summary>True when this policy declares nothing: both members are <c>auto</c>, so the
    /// port behaves exactly as it did before the block existed.</summary>
    /// <remarks>
    /// Derived, not stored - <see cref="YamlIgnoreAttribute"/> / <see cref="JsonIgnoreAttribute"/>
    /// keep it (and the two projections below) off the wire, so the persisted block and the
    /// control panel's <c>PortLinkConfig</c> carry exactly the two keys an operator writes.
    /// </remarks>
    [YamlIgnore]
    [JsonIgnore]
    public bool IsDefault => Dial == LinkDialPreference.Auto && PreConnectXid == LinkPreConnectXid.Auto;

    /// <summary>
    /// The <c>Ax25ListenerOptions.PreferExtendedConnect</c> seed for this policy: false only
    /// when the port declares <see cref="LinkDialPreference.V20"/>. This governs dials that do
    /// NOT go through <c>Ax25OutboundConnector</c> (a bare <c>listener.ConnectAsync(remote)</c>),
    /// so a <c>v20</c> port is mod-8 on every path, not just the connector's.
    /// </summary>
    [YamlIgnore]
    [JsonIgnore]
    public bool PrefersExtendedConnect => Dial != LinkDialPreference.V20;

    /// <summary>The <c>Ax25ListenerOptions.PreConnectXidNegotiatesSrej</c> seed for this policy:
    /// false only when the port declares <see cref="LinkPreConnectXid.Off"/>.</summary>
    [YamlIgnore]
    [JsonIgnore]
    public bool PreConnectXidNegotiatesSrej => PreConnectXid != LinkPreConnectXid.Off;

    /// <summary>Resolve a (possibly null) per-port block to an effective policy.</summary>
    public static PortLinkConfig Resolve(PortLinkConfig? link) => link ?? Default;
}
