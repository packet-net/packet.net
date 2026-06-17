using Packet.Core;

namespace Packet.Node.Core.Console;

/// <summary>
/// A parsed node-console command — a closed, typed set. The parser
/// (<see cref="NodeCommandParser"/>) maps a raw input line to exactly one of
/// these and <b>never throws</b>; a line it cannot make sense of becomes
/// <see cref="UnknownCommand"/>, and a recognised verb with a bad argument
/// becomes a typed error variant (e.g. <see cref="MalformedConnect"/>) — never
/// an exception. This is the fuzz target's contract.
/// </summary>
public abstract record NodeCommand
{
    private protected NodeCommand() { }
}

/// <summary>
/// <c>C[onnect] [port] &lt;call&gt;</c> — connect to the given station and relay until either
/// side drops. <see cref="Port"/> is an optional 1-indexed port (config order, XRouter
/// convention: <c>C 1 G0ABC-2</c> dials out the first port); null means "no port specified" —
/// the router then bridges to a locally-registered app of that callsign if one exists, else
/// dials the default port.
/// </summary>
public sealed record ConnectCommand(Callsign Target, int? Port = null) : NodeCommand;

/// <summary><c>N[odes]</c> — list the node identity + the NET/ROM table.</summary>
public sealed record NodesCommand : NodeCommand;

/// <summary><c>P[orts]</c> — list the node's radio ports (split out of NODES).</summary>
public sealed record PortsCommand : NodeCommand;

/// <summary>
/// <c>MH</c> (node-wide) / <c>MH &lt;port&gt;</c> (per-port) — list recently heard stations from
/// the persisted heard log (#454). <see cref="PortId"/> is null for the node-wide view (each
/// callsign merged across the ports it was heard on) or the requested port id for the per-port
/// view (one row per callsign heard on that port). Read-only; no elevation.
/// </summary>
public sealed record MhCommand(string? PortId) : NodeCommand;

/// <summary><c>I[nfo]</c> — node identity + version banner.</summary>
public sealed record InfoCommand : NodeCommand;

/// <summary><c>B[ye]</c> / <c>D[isconnect]</c> — tear the console connection down.</summary>
public sealed record ByeCommand : NodeCommand;

/// <summary><c>H[elp]</c> / <c>?</c> — the command list.</summary>
public sealed record HelpCommand : NodeCommand;

/// <summary>
/// An empty line — the user pressed enter with nothing typed. The console
/// re-prompts without comment (it is not an error).
/// </summary>
public sealed record EmptyCommand : NodeCommand;

/// <summary>
/// A recognised verb the parser could not complete — e.g. <c>CONNECT</c> with a
/// missing or unparseable callsign. Carries the offending input and a reason so
/// the console can reply helpfully. NOT an exception.
/// </summary>
public sealed record MalformedConnect(string Raw, string Reason) : NodeCommand;

/// <summary>An input line that matched no known verb.</summary>
public sealed record UnknownCommand(string Raw) : NodeCommand;

// ─── Over-RF sysop elevation + privileged commands (auth part 4) ──────────────
// These verbs are always PARSED (the parser is transport-agnostic); the command
// service is what GATES them — SYSOP verifies a rolling code and elevates the
// session for a TTL, and the privileged commands below are refused unless the
// session is currently elevated with a sufficient scope.

/// <summary>
/// <c>SYSOP &lt;code&gt;</c> (over AX.25 — the callsign is implicit from the
/// connection's <c>PeerId</c>) or <c>SYSOP &lt;user&gt; &lt;code&gt;</c> (over telnet,
/// which has no callsign). Carries the raw tokens; the command service interprets them
/// per transport (it knows the <see cref="INodeConnection.PeerId"/> and
/// <see cref="INodeConnection.TransportKind"/>), resolves the user, verifies the
/// rolling code, and on success elevates the session. <see cref="Token1"/> is the first
/// argument and <see cref="Token2"/> the optional second; both null/empty when
/// <c>SYSOP</c> was typed with no argument (the service then shows usage). The code is
/// never logged.
/// </summary>
public sealed record SysopCommand(string? Token1, string? Token2) : NodeCommand;

/// <summary><c>SESSIONS</c> — list the node's active sessions. Privileged (operate).</summary>
public sealed record SessionsCommand : NodeCommand;

/// <summary><c>KICK &lt;id&gt;</c> — disconnect a session by its <c>portId:peer</c> id.
/// Privileged (operate).</summary>
public sealed record KickCommand(string SessionId) : NodeCommand;

/// <summary><c>KICK</c> with a missing id — carries a reason for a helpful reply.</summary>
public sealed record MalformedKick(string Reason) : NodeCommand;

/// <summary><c>PORT &lt;id&gt; UP|DOWN</c> — enable/disable a configured port.
/// Privileged (admin).</summary>
public sealed record PortPowerCommand(string PortId, bool Up) : NodeCommand;

/// <summary><c>PORT</c> with a missing / not-UP/DOWN argument — carries a reason.</summary>
public sealed record MalformedPort(string Reason) : NodeCommand;

/// <summary><c>RELOAD</c> — re-read the on-disk conffile. Privileged (admin).</summary>
public sealed record ReloadCommand : NodeCommand;

/// <summary><c>CAP</c> / <c>CAPS</c> (bare) — list the per-peer AX.25 capability cache.
/// Read-only; no elevation.</summary>
public sealed record CapabilitiesCommand : NodeCommand;

/// <summary><c>CAP CLEAR &lt;port:peer&gt;</c> — forget one cached (port, peer) capability
/// record. Privileged (operate). <see cref="Target"/> is the raw <c>port:peer</c> id; the
/// command service splits it (the cache key is the pair).</summary>
public sealed record ClearCapabilityCommand(string Target) : NodeCommand;

/// <summary><c>CAP</c> with an argument the parser couldn't make sense of (anything that
/// isn't <c>CLEAR &lt;port:peer&gt;</c>) — carries a reason for a helpful usage reply.</summary>
public sealed record MalformedCapability(string Reason) : NodeCommand;
