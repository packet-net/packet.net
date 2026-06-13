using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Packet.Mcp.Tools;

/// <summary>
/// The write-side MCP tools. Gated <c>operate</c> by the host and audit-logged
/// there (actor/transport/scope/payload hash) — the caller identity comes from
/// <see cref="IMcpCallerAccessor"/>. Each delegates to <see cref="INodeMcpBackend"/>,
/// whose live implementation runs the action through the node's existing
/// exclusive-gated action paths (so an MCP write never races a reconcile).
/// Outbound construction stays strict: <c>send_ui_frame</c> builds through the
/// spec-strict factories.
/// </summary>
[McpServerToolType]
public sealed class WriteTools(INodeMcpBackend backend, IMcpCallerAccessor caller)
{
    private readonly INodeMcpBackend backend = backend;
    private readonly IMcpCallerAccessor caller = caller;

    [McpServerTool(Name = "send_ui_frame")]
    [Description("Transmit a connectionless UI frame on a port. The frame is built strictly (spec-valid); the payload is sent as UTF-8.")]
    public Task<SendResult> SendUiFrame(
        [Description("Port to transmit on.")] string port,
        [Description("Destination callsign (with optional -SSID).")] string dest,
        [Description("Payload as a UTF-8 string.")] string payload,
        [Description("Optional digipeater path, in order.")] IReadOnlyList<string>? path = null,
        [Description("PID byte; defaults to 0xF0 (no layer 3).")] byte pid = 0xF0,
        CancellationToken ct = default)
        => backend.SendUiFrameAsync(new SendUiRequest(port, dest, payload, path, pid), caller.Current, ct);

    [McpServerTool(Name = "reset_port")]
    [Description("Restart a radio port (tear down and bring back up). Disrupts any sessions on the port.")]
    public Task<PortActionResult> ResetPort(
        [Description("Port id to restart.")] string port,
        CancellationToken ct = default)
        => backend.ResetPortAsync(port, caller.Current, ct);

    [McpServerTool(Name = "disconnect_session")]
    [Description("Disconnect a live AX.25 session by its port:peer id.")]
    public Task<SessionResult> DisconnectSession(
        [Description("Session id, formatted port:peer.")] string id,
        CancellationToken ct = default)
        => backend.DisconnectSessionAsync(id, caller.Current, ct);

    [McpServerTool(Name = "set_kiss_param")]
    [Description("Set a KISS parameter on a port (txdelay, persist, slottime, txtail). The result says whether it took live or needs a port restart.")]
    public Task<KissParamResult> SetKissParam(
        [Description("Port whose KISS parameter to set.")] string port,
        [Description("Parameter name: txdelay, persist, slottime, txtail.")] string param,
        [Description("Parameter value (0..255).")] int value,
        CancellationToken ct = default)
        => backend.SetKissParamAsync(new SetKissParamRequest(port, param, value), caller.Current, ct);
}
