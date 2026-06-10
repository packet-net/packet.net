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
    /// configured port), or null for the first port.</param>
    /// <param name="local">The client-requested local (originating) callsign, or null for the
    /// node's own. R-2 requires this to be the node callsign (see the named limitation).</param>
    /// <param name="remote">The destination callsign text (validated by the gateway).</param>
    /// <exception cref="RhpGatewayException">On any open failure — carries the RHPv2
    /// <c>errCode</c>/<c>errText</c> the server should put on the <c>openReply</c>.</exception>
    Task<INodeConnection> OpenAx25StreamAsync(
        string? portLabel, string? local, string remote, CancellationToken cancellationToken = default);
}

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
