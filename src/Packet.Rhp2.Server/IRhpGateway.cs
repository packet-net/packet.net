using Packet.Node.Core.Console;

namespace Packet.Rhp2.Server;

/// <summary>
/// The seam between the RHPv2 wire server and the node's packet engine: open an outbound
/// AX.25 connected-mode session on a named port and hand it back as the node's standard
/// transport-agnostic byte stream. The host (Packet.Node) implements this over the running
/// <c>PortSupervisor</c>; the server itself never touches AX.25 — it is a JSON-wire ↔
/// <see cref="INodeConnection"/> translator (see <c>docs/rhp2-server.md</c>).
/// </summary>
public interface IRhpGateway
{
    /// <summary>
    /// Open an outbound AX.25 stream (the wire's <c>open</c> with the Active flag).
    /// </summary>
    /// <param name="portLabel">The client-supplied 1-indexed port label (<c>"1"</c> = the first
    /// configured port). Null resolves a locally-registered app (loopback) or errors — it does
    /// NOT silently default to the first port for an RF dial.</param>
    /// <param name="local">The client-requested local (originating) callsign, or null for the
    /// node's own. R-2 requires this to be the node callsign (see the named limitation).</param>
    /// <param name="remote">The destination callsign text (validated by the gateway).</param>
    /// <exception cref="RhpGatewayException">On any open failure — carries the RHPv2
    /// <c>errCode</c>/<c>errText</c> the server should put on the <c>openReply</c>.</exception>
    Task<INodeConnection> OpenAx25StreamAsync(
        string? portLabel, string? local, string remote, CancellationToken cancellationToken = default);

    /// <summary>
    /// Register <paramref name="local"/> as a callsign the node answers for (the wire's
    /// <c>bind</c>+<c>listen</c>): every inbound AX.25 connection addressed to it is handed to
    /// <paramref name="onAccepted"/> as an <see cref="INodeConnection"/> together with the
    /// 1-indexed label of the port it arrived on (the <c>accept.port</c> string). Dispose the
    /// returned registration to stop listening (live sessions are unaffected).
    /// </summary>
    /// <param name="portLabel">Restrict to one port (1-indexed label), or null for all ports —
    /// the wire's null bind port.</param>
    /// <exception cref="RhpGatewayException">6 — not a valid callsign; 9 — already
    /// listening / the node's own callsign; 10 — no such port.</exception>
    IDisposable RegisterListener(
        string? portLabel, string local, Func<INodeConnection, string, Task> onAccepted);

    /// <summary>
    /// Send a connectionless AX.25 <b>UI datagram</b> (the wire's <c>sendto</c> in DGRAM mode):
    /// a single UI frame with an explicit source (<paramref name="local"/>) and destination
    /// (<paramref name="remote"/>), bypassing connected-mode entirely. This is the IP-over-AX.25
    /// (pid <c>0xCC</c>) / native beacon / APRS (pid <c>0xF0</c>) path.
    /// </summary>
    /// <param name="portLabel">The client-supplied 1-indexed port label (<c>"1"</c> = the first
    /// configured port), or null for the first port.</param>
    /// <param name="local">The UI frame's source (originating) callsign.</param>
    /// <param name="remote">The UI frame's destination callsign.</param>
    /// <param name="info">The UI frame's information field.</param>
    /// <param name="pid">The Layer-3 PID (e.g. <c>0xF0</c> no-L3, <c>0xCC</c> IP).</param>
    /// <exception cref="RhpGatewayException">On a bad port (10) or an invalid local (6) /
    /// remote (7) callsign — the same vocabulary as <see cref="OpenAx25StreamAsync"/>.</exception>
    Task SendUiAsync(
        string? portLabel, string local, string remote, ReadOnlyMemory<byte> info, byte pid,
        CancellationToken ct = default);

    /// <summary>
    /// Register for inbound AX.25 <b>UI datagrams</b> (the wire's DGRAM <c>recv</c> path):
    /// every received UI frame on the scoped port(s) is handed to <paramref name="onReceived"/>
    /// as a <see cref="UiDatagram"/> with the frame's true source / destination / PID / info and
    /// the 1-indexed arrival port label. This tap is <b>promiscuous</b> — it hears broadcast UI
    /// (APRS, IP-over-AX.25) regardless of the frame's destination, exactly how the NET/ROM
    /// service taps NODES — so a bound RHP dgram socket sees all UI on its port and the client
    /// filters by <c>recv.local</c>. Dispose the returned registration to stop hearing.
    /// </summary>
    /// <param name="portLabel">Restrict to one port (1-indexed label), or null for all ports —
    /// the wire's null bind port.</param>
    /// <exception cref="RhpGatewayException">10 — no such port.</exception>
    IDisposable RegisterUiListener(string? portLabel, Func<UiDatagram, Task> onReceived);
}

/// <summary>
/// One inbound AX.25 UI datagram surfaced to the RHP dgram <c>recv</c> path: the frame's true
/// source / destination (surfaced verbatim — the tap is promiscuous), its Layer-3 PID, its
/// information field, and the 1-indexed label of the port it arrived on.
/// </summary>
/// <param name="Source">The frame's source callsign (→ <c>recv.remote</c>).</param>
/// <param name="Dest">The frame's destination callsign (→ <c>recv.local</c>).</param>
/// <param name="Pid">The frame's Layer-3 PID (→ <c>recv.pid</c>).</param>
/// <param name="Info">The frame's information field (→ <c>recv.data</c>).</param>
/// <param name="PortLabel">The 1-indexed arrival port label (→ <c>recv.port</c>).</param>
public sealed record UiDatagram(
    string Source, string Dest, byte Pid, ReadOnlyMemory<byte> Info, string PortLabel);

/// <summary>
/// An open failure expressed in the wire's own terms: the RHPv2 <c>errCode</c> (see PWP-0222)
/// plus the human <c>errText</c>. The server copies both onto the failed reply verbatim, so
/// the gateway — the part that knows *why* (no such port, bad callsign, no route) — owns the
/// error vocabulary and the wire loop stays mechanical.
/// </summary>
public sealed class RhpGatewayException : Exception
{
    public RhpGatewayException(int errCode, string errText) : base(errText) => ErrCode = errCode;

    /// <summary>The RHPv2 error code for the reply (e.g. 10 NoSuchPort, 15 NoRoute).</summary>
    public int ErrCode { get; }
}
