using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Node.Core.Applications;
using Packet.Node.Core.Auth;

namespace Packet.Node.Core.Console;

/// <summary>
/// Drives the node console over a single <see cref="INodeConnection"/>: emits the
/// welcome banner, then loops reading lines, parsing them to typed
/// <see cref="NodeCommand"/>s, and acting on them — re-prompting after each — until
/// the user types <c>Bye</c> or the connection drops. Identical behaviour over
/// AX.25 and telnet because it speaks only to <see cref="INodeConnection"/>.
/// </summary>
/// <remarks>
/// The command set (slice 1): <c>C[onnect] &lt;call&gt;</c> (connect-out, relay
/// both ways, then back to prompt), <c>N[odes]</c> / <c>I[nfo]</c> (identity +
/// ports + version), <c>B[ye]</c> / <c>D[isconnect]</c> (teardown), <c>H[elp]</c>
/// / <c>?</c> (command list). An unknown command re-prompts without
/// disconnecting.
/// </remarks>
public sealed partial class NodeCommandService : INodeApplication
{
    private readonly NodeConsoleEnvironment env;
    private readonly ILogger<NodeCommandService> logger;
    private readonly TimeProvider clock;

    /// <summary>The node software version string, from the assembly's informational
    /// version (falls back to the assembly version).</summary>
    public static string Version { get; } =
        typeof(NodeCommandService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(NodeCommandService).Assembly.GetName().Version?.ToString()
            ?? "dev";

    public NodeCommandService(NodeConsoleEnvironment env, ILogger<NodeCommandService>? logger = null, TimeProvider? clock = null)
    {
        this.env = env ?? throw new ArgumentNullException(nameof(env));
        this.logger = logger ?? NullLogger<NodeCommandService>.Instance;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Per-connection over-RF sysop elevation state. Lives for the lifetime of one console
    /// session (a local in <see cref="RunAsync"/>), never shared between connections, so an
    /// elevation on one session can never leak to another. A session starts unelevated;
    /// <c>SYSOP &lt;code&gt;</c> sets <see cref="ElevatedUntil"/> + <see cref="Scope"/>, and
    /// every privileged command re-checks <see cref="IsElevated"/> against the injected
    /// clock so an elevation simply lapses with time (no background timer).
    /// </summary>
    private sealed class SysopSession
    {
        public DateTimeOffset? ElevatedUntil { get; set; }
        public string? Scope { get; set; }

        public bool IsElevated(DateTimeOffset now) =>
            ElevatedUntil is { } until && now < until && !string.IsNullOrEmpty(Scope);
    }

    /// <summary>
    /// Run the prompt loop until the user disconnects or the connection drops.
    /// Owns nothing it didn't create: the caller disposes
    /// <paramref name="connection"/>.
    /// </summary>
    public async Task RunAsync(INodeConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Send the banner AND the first prompt as ONE write (one I-frame), not two
        // back-to-back. On a slow half-duplex channel each extra I-frame is extra
        // air occupancy right when the freshly-connected peer wants to send its
        // first command — two frames is two chances to collide with it (#292). The
        // per-command prompt below is still its own write (it follows our reply, so
        // it does not contend with an inbound frame the way the connect burst does).
        await WriteBannerAndPromptAsync(connection, cancellationToken).ConfigureAwait(false);

        var assembler = new LineAssembler();
        // Per-connection elevation state — created here so it lives exactly as long as this
        // session and is never shared with another connection.
        var sysop = new SysopSession();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadOnlyMemory<byte> chunk;
                try
                {
                    chunk = await ReadOrCompletion(connection, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (chunk.IsEmpty)
                {
                    break;   // EOF / peer gone
                }

                bool disconnect = false;
                foreach (var lineBytes in assembler.Push(chunk))
                {
                    var command = NodeCommandParser.Parse(lineBytes);
                    var outcome = await DispatchAsync(connection, command, sysop, cancellationToken).ConfigureAwait(false);
                    if (outcome == DispatchOutcome.Disconnect)
                    {
                        disconnect = true;
                        break;
                    }
                    await WritePromptAsync(connection, cancellationToken).ConfigureAwait(false);
                }

                if (disconnect)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogConsoleError(ex, connection.PeerId);
        }
    }

    /// <summary>
    /// The console <b>is application #0</b>: it satisfies <see cref="INodeApplication"/> so the
    /// node-prompt session is just "the first app". The launch context is unused — the console
    /// reads identity / ports / services from its <see cref="NodeConsoleEnvironment"/> — so this
    /// delegates to the existing prompt loop unchanged.
    /// </summary>
    Task INodeApplication.RunAsync(INodeConnection session, NodeAppContext context, CancellationToken cancellationToken)
        => RunAsync(session, cancellationToken);

    private enum DispatchOutcome { Continue, Disconnect }

    private async Task<DispatchOutcome> DispatchAsync(
        INodeConnection connection, NodeCommand command, SysopSession sysop, CancellationToken ct)
    {
        switch (command)
        {
            case EmptyCommand:
                return DispatchOutcome.Continue;

            case HelpCommand:
                await WriteLineAsync(connection, HelpText(sysop), ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case InfoCommand:
                await WriteLineAsync(connection, InfoText(), ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case NodesCommand:
                await WriteLineAsync(connection, NodesText(), ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case ByeCommand:
                await WriteLineAsync(connection, "73", ct).ConfigureAwait(false);
                return DispatchOutcome.Disconnect;

            case ConnectCommand connect:
                await HandleConnectAsync(connection, connect, ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case MalformedConnect bad:
                await WriteLineAsync(connection, bad.Reason, ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case SysopCommand sysopCmd:
                await HandleSysopAsync(connection, sysopCmd, sysop, ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case SessionsCommand:
                await HandleSessionsAsync(connection, sysop, ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case KickCommand kick:
                await HandleKickAsync(connection, kick, sysop, ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case MalformedKick badKick:
                await WriteLineAsync(connection, badKick.Reason, ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case PortPowerCommand portCmd:
                await HandlePortAsync(connection, portCmd, sysop, ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case MalformedPort badPort:
                await WriteLineAsync(connection, badPort.Reason, ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case ReloadCommand:
                await HandleReloadAsync(connection, sysop, ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            case UnknownCommand unknown:
                // A verb the console doesn't own may be a registered application — built-in
                // verbs are matched first (above), so an app can never shadow one. If it
                // launches, control returns here when the app exits and we re-prompt.
                if (await TryLaunchAppAsync(connection, unknown, ct).ConfigureAwait(false))
                {
                    return DispatchOutcome.Continue;
                }
                await WriteLineAsync(connection,
                    $"Unknown command: {Sanitise(unknown.Raw)}  (type H for help)", ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;

            default:
                // The command set is closed; an unhandled variant is a bug, but
                // never disconnect the user over it.
                await WriteLineAsync(connection, "Unknown command  (type H for help)", ct).ConfigureAwait(false);
                return DispatchOutcome.Continue;
        }
    }

    private async Task HandleConnectAsync(INodeConnection inbound, ConnectCommand connect, CancellationToken ct)
    {
        // The router (when wired) handles a chosen port and local-app crossconnects, and returns
        // the session's default connector for a plain `C <call>`. Without it (older host / tests)
        // we keep the single-default-connector behaviour and can't honour an explicit port.
        IOutboundConnector? connector;
        if (env.ConnectRouter is { } router)
        {
            var resolution = router.Resolve(connect.Port, connect.Target, inbound);
            if (resolution.Failed)
            {
                await WriteLineAsync(inbound, resolution.Error!, ct).ConfigureAwait(false);
                return;
            }
            connector = resolution.Connector;
        }
        else
        {
            if (connect.Port is not null)
            {
                await WriteLineAsync(inbound,
                    "Port-scoped connect is not available on this connection.", ct).ConfigureAwait(false);
                return;
            }
            connector = env.OutboundConnector;
        }

        if (connector is null)
        {
            await WriteLineAsync(inbound,
                "Connect is not available on this connection (no outbound port configured).", ct).ConfigureAwait(false);
            return;
        }

        await WriteLineAsync(inbound, $"Connecting to {connect.Target} on {connector.PortId}...", ct).ConfigureAwait(false);

        INodeConnection outbound;
        try
        {
            outbound = await connector.ConnectAsync(connect.Target, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await WriteLineAsync(inbound, $"Connect to {connect.Target} timed out.", ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            await WriteLineAsync(inbound, $"Connect to {connect.Target} failed: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        await using (outbound.ConfigureAwait(false))
        {
            await WriteLineAsync(inbound, $"Connected to {connect.Target}.", ct).ConfigureAwait(false);
            // Telnet is char-at-a-time (so the node can echo / line-edit locally); a
            // packet link wants one I-frame per line, not per keystroke. Line-buffer
            // the telnet→AX.25 direction so Connect transmits whole lines, each with a
            // clean single-CR terminator (cf. #51). The AX.25→telnet direction stays a
            // raw pump (newline-normalised at the telnet output boundary).
            var relayInbound = inbound.TransportKind == NodeTransportKind.Telnet
                               && outbound.TransportKind == NodeTransportKind.Ax25
                ? new LineBufferingNodeConnection(inbound)
                : inbound;
            await ConsoleRelay.PipeAsync(relayInbound, outbound, ct).ConfigureAwait(false);
            await WriteLineAsync(inbound, $"Disconnected from {connect.Target}.", ct).ConfigureAwait(false);
        }
    }

    // ─── Application launch (the app platform — console as app #0 launches app N) ─────
    // An unknown verb that matches a registered application launches it over THIS session via
    // the application host (out-of-process; the node shares no code with the app). The app
    // gets the connecting callsign / transport / arrival port / args; control returns here to
    // re-prompt when the app exits. Returns false (→ "unknown command") when no app matches or
    // the platform isn't wired.
    private async Task<bool> TryLaunchAppAsync(INodeConnection connection, UnknownCommand unknown, CancellationToken ct)
    {
        var host = env.Applications;
        if (host is null || string.IsNullOrWhiteSpace(unknown.Raw))
        {
            return false;
        }

        // First whitespace-delimited token is the launch verb; the remainder are args.
        var parts = unknown.Raw.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var app = host.Resolve(parts[0]);
        if (app is null)
        {
            return false;
        }

        var args = parts.Length > 1
            ? parts[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            : [];
        var callsign = Callsign.TryParse(connection.PeerId, out var call) ? call.ToString() : connection.PeerId;
        var context = new NodeAppContext
        {
            Callsign = callsign,
            Transport = connection.TransportKind,
            PortId = env.OutboundConnector?.PortId,   // the working port (== arrival port for same-port AX.25)
            Args = args,
            SysopElevated = false,                    // reserved — apps launch from the unelevated prompt in slice 1
        };

        await host.RunAsync(app, connection, context, ct).ConfigureAwait(false);
        return true;
    }

    // ─── Text builders ──────────────────────────────────────────────────

    private string Banner() => Expand(env.Services.Banner) + $"  [Packet.NET {Version}]";

    private async Task WritePromptAsync(INodeConnection connection, CancellationToken ct)
    {
        var prompt = Expand(env.Services.Prompt);
        await connection.WriteAsync(Encoding.UTF8.GetBytes(prompt), ct).ConfigureAwait(false);
    }

    // Banner line + first prompt in a SINGLE write — one I-frame on the air rather
    // than two back-to-back at the contended just-connected moment (#292).
    private async Task WriteBannerAndPromptAsync(INodeConnection connection, CancellationToken ct)
    {
        var nl = NewLine(connection);
        var banner = Banner().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", nl, StringComparison.Ordinal);
        var prompt = Expand(env.Services.Prompt);
        await connection.WriteAsync(Encoding.UTF8.GetBytes(banner + nl + prompt), ct).ConfigureAwait(false);
    }

    private string Expand(string template)
        => NodeTextTemplate.Expand(template, env.NodeName, env.Identity.Callsign);

    private string InfoText()
    {
        var sb = new StringBuilder();
        sb.Append("Node: ").Append(env.NodeName).Append(" (").Append(env.Identity.Callsign).Append(')');
        if (!string.IsNullOrWhiteSpace(env.Identity.Grid))
        {
            sb.Append("  Grid: ").Append(env.Identity.Grid);
        }
        sb.Append('\n').Append("Software: Packet.NET ").Append(Version);
        return sb.ToString();
    }

    private string NodesText()
    {
        var sb = new StringBuilder();
        sb.Append("Node ").Append(env.NodeName).Append(" (").Append(env.Identity.Callsign).Append(')').Append('\n');
        var ports = env.Ports;
        if (ports.Count == 0)
        {
            sb.Append("Ports: (none configured)");
        }
        else
        {
            sb.Append("Ports:");
            foreach (var p in ports)
            {
                sb.Append('\n').Append("  ").Append(p.Id)
                  .Append(' ').Append(p.Enabled ? "[up]" : "[down]")
                  .Append(' ').Append(p.Transport.DescribeEndpoint());
            }
        }

        AppendNetRom(sb);
        return sb.ToString();
    }

    // Surface the learned NET/ROM routing table (read-only). Shows the directly-
    // heard neighbours and, for each known destination, its best route(s):
    // alias:callsign via best-neighbour at quality (obsolescence). This is the
    // same model a future MCP network_topology tool / web monitor reads.
    private void AppendNetRom(StringBuilder sb)
    {
        var view = env.NetRom;
        if (view is null || !view.Enabled)
        {
            return;
        }

        var snap = view.Snapshot();
        if (snap.NeighbourCount == 0 && snap.DestinationCount == 0)
        {
            sb.Append('\n').Append("NET/ROM: no nodes heard yet");
            return;
        }

        if (snap.NeighbourCount > 0)
        {
            sb.Append('\n').Append("NET/ROM neighbours:");
            foreach (var n in snap.Neighbours)
            {
                sb.Append('\n').Append("  ").Append(Label(n.Alias, n.Neighbour))
                  .Append(" port ").Append(n.PortId)
                  .Append(" qual ").Append(n.PathQuality);
            }
        }

        if (snap.DestinationCount > 0)
        {
            sb.Append('\n').Append("NET/ROM routes:");
            foreach (var d in snap.Destinations)
            {
                sb.Append('\n').Append("  ").Append(Label(d.Alias, d.Destination)).Append(':');
                foreach (var r in d.Routes)
                {
                    sb.Append(" via ").Append(r.Neighbour)
                      .Append('(').Append(r.Quality).Append(',').Append(r.Obsolescence).Append(')');
                    // Surface the INP3 measured-time metric when the overlay has learned one for
                    // this route (a RIF time-route) — the time-space companion to the quality pair.
                    if (r.Inp3 is { } inp3)
                    {
                        sb.Append(" [inp3 ").Append(inp3.TargetTimeMs).Append("ms/")
                          .Append(inp3.HopCount).Append("h]");
                    }
                }
            }
        }
    }

    // "ALIAS:CALL" when an alias is known, else just the callsign.
    private static string Label(string alias, Packet.Core.Callsign call)
        => string.IsNullOrEmpty(alias) ? call.ToString() : $"{alias}:{call}";

    private string HelpText(SysopSession sysop)
    {
        var sb = new StringBuilder();
        sb.Append("Commands:\n")
          .Append("  C[onnect] <call>   connect to a station\n")
          .Append("  N[odes]            list this node and its ports\n")
          .Append("  I[nfo]             node info and version\n")
          .Append("  B[ye] / D          disconnect\n")
          .Append("  H[elp] / ?         this help");

        // Only surface the sysop verbs when elevation is actually available on this node
        // (auth on + the seam wired). When elevated, list the privileged set; otherwise
        // just hint at SYSOP so a licensed sysop knows how to elevate.
        bool available = env.Sysop is not null && env.AuthEnabled;
        if (available && sysop.IsElevated(clock.GetUtcNow()))
        {
            sb.Append("\n\nSysop (elevated):\n")
              .Append("  SESSIONS           list active sessions\n")
              .Append("  KICK <id>          disconnect a session\n")
              .Append("  PORT <id> UP|DOWN  enable/disable a port\n")
              .Append("  RELOAD             re-read the config file");
        }
        else if (available)
        {
            sb.Append("\n  SYSOP <code>       elevate for remote admin");
        }
        return sb.ToString();
    }

    // ─── Over-RF sysop elevation + privileged commands ──────────────────
    // SYSOP verifies a rolling RFC-6238 code (single-use, replay-guarded) and, on success,
    // elevates THIS session for a TTL. The privileged commands gate on a live elevation +
    // a sufficient scope and route through the SAME serialized host seams the web API uses.

    private async Task HandleSysopAsync(INodeConnection connection, SysopCommand cmd, SysopSession sysop, CancellationToken ct)
    {
        var peer = connection.PeerId;
        var ctx = env.Sysop;
        if (ctx is null || !env.AuthEnabled)
        {
            await WriteLineAsync(connection, "Sysop elevation is not available on this node.", ct).ConfigureAwait(false);
            return;
        }

        // Resolve (user, code) per transport. AX.25/NET-ROM: the callsign is implicit from
        // the connection's PeerId and the single argument is the code. Telnet has no
        // callsign, so it is SYSOP <user> <code>.
        UserRecord? user;
        string? code;
        string subject;   // for the audit line (callsign or username) — never the code
        if (connection.TransportKind == NodeTransportKind.Telnet)
        {
            if (string.IsNullOrWhiteSpace(cmd.Token1) || string.IsNullOrWhiteSpace(cmd.Token2))
            {
                await WriteLineAsync(connection, "Usage: SYSOP <user> <code>", ct).ConfigureAwait(false);
                return;
            }
            subject = cmd.Token1;
            code = cmd.Token2;
            user = ctx.Users.FindByUsername(cmd.Token1);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(cmd.Token1))
            {
                await WriteLineAsync(connection, "Usage: SYSOP <code>", ct).ConfigureAwait(false);
                return;
            }
            // Canonicalise the connecting callsign for the lookup (the store match is
            // case-insensitive; canonical form also normalises any SSID rendering).
            subject = Callsign.TryParse(peer, out var call) ? call.ToString() : peer;
            code = cmd.Token1;
            user = ctx.Users.FindByCallsign(subject);
        }

        // No such user, or the user has no TOTP credential enrolled → generic failure with
        // NO oracle on which it was (existence vs no-credential vs wrong code all look the
        // same to the caller).
        if (user is null || string.IsNullOrEmpty(user.TotpSecret))
        {
            LogSysopDenied(peer, "no-credential");
            await WriteLineAsync(connection, "Sysop authentication failed.", ct).ConfigureAwait(false);
            return;
        }

        if (!ctx.Totp.TryVerify(user.TotpSecret, code, user.LastTotpCounter ?? -1, out var counter))
        {
            LogSysopDenied(peer, "bad-code");
            await WriteLineAsync(connection, "Sysop authentication failed.", ct).ConfigureAwait(false);
            return;
        }

        // Burn the accepted counter BEFORE granting: if we cannot persist the new replay
        // high-water mark, refuse to elevate (else the same code could be replayed to
        // re-elevate). This is why UpdateTotpCounter returns false on fault.
        if (!ctx.Users.UpdateTotpCounter(user.Username, counter))
        {
            LogSysopDenied(peer, "counter-not-persisted");
            await WriteLineAsync(connection, "Sysop authentication failed, please try again.", ct).ConfigureAwait(false);
            return;
        }

        var now = clock.GetUtcNow();
        var ttl = env.SysopElevationTtl;
        sysop.ElevatedUntil = now + ttl;
        sysop.Scope = user.Scope;
        LogSysopElevated(subject, user.Scope, peer);
        await WriteLineAsync(connection,
            $"Elevated as {user.Username} ({user.Scope}) for {(int)ttl.TotalMinutes} min. Commands: SESSIONS, KICK, PORT, RELOAD.",
            ct).ConfigureAwait(false);
    }

    private async Task HandleSessionsAsync(INodeConnection connection, SysopSession sysop, CancellationToken ct)
    {
        if (!await RequireElevatedAsync(connection, sysop, AuthScopes.Operate, ct).ConfigureAwait(false))
        {
            return;
        }
        var ops = env.Sysop!.Operations;
        var sessions = await ops.ListSessionsAsync(ct).ConfigureAwait(false);
        LogSysopCommand("SESSIONS", connection.PeerId);
        if (sessions.Count == 0)
        {
            await WriteLineAsync(connection, "No active sessions.", ct).ConfigureAwait(false);
            return;
        }
        var sb = new StringBuilder("Active sessions:");
        foreach (var line in sessions)
        {
            sb.Append('\n').Append("  ").Append(line);
        }
        await WriteLineAsync(connection, sb.ToString(), ct).ConfigureAwait(false);
    }

    private async Task HandleKickAsync(INodeConnection connection, KickCommand kick, SysopSession sysop, CancellationToken ct)
    {
        if (!await RequireElevatedAsync(connection, sysop, AuthScopes.Operate, ct).ConfigureAwait(false))
        {
            return;
        }
        var result = await env.Sysop!.Operations.KickAsync(kick.SessionId, ct).ConfigureAwait(false);
        LogSysopCommand("KICK", connection.PeerId);
        await WriteLineAsync(connection, result.Message, ct).ConfigureAwait(false);
    }

    private async Task HandlePortAsync(INodeConnection connection, PortPowerCommand cmd, SysopSession sysop, CancellationToken ct)
    {
        // Bringing a port up/down is an admin action (it persists a config change).
        if (!await RequireElevatedAsync(connection, sysop, AuthScopes.Admin, ct).ConfigureAwait(false))
        {
            return;
        }
        var result = await env.Sysop!.Operations.SetPortEnabledAsync(cmd.PortId, cmd.Up, ct).ConfigureAwait(false);
        LogSysopCommand(cmd.Up ? "PORT-UP" : "PORT-DOWN", connection.PeerId);
        await WriteLineAsync(connection, result.Message, ct).ConfigureAwait(false);
    }

    private async Task HandleReloadAsync(INodeConnection connection, SysopSession sysop, CancellationToken ct)
    {
        if (!await RequireElevatedAsync(connection, sysop, AuthScopes.Admin, ct).ConfigureAwait(false))
        {
            return;
        }
        var result = await env.Sysop!.Operations.ReloadAsync(ct).ConfigureAwait(false);
        LogSysopCommand("RELOAD", connection.PeerId);
        await WriteLineAsync(connection, result.Message, ct).ConfigureAwait(false);
    }

    // The gate every privileged command passes through: the session must be wired for
    // sysop, currently elevated (TTL not lapsed — checked against the injected clock), and
    // hold a scope that satisfies the required one. Writes the refusal itself and returns
    // false so the caller just returns.
    private async Task<bool> RequireElevatedAsync(INodeConnection connection, SysopSession sysop, string requiredScope, CancellationToken ct)
    {
        if (env.Sysop is null || !env.AuthEnabled)
        {
            await WriteLineAsync(connection, "Sysop commands are not available on this node.", ct).ConfigureAwait(false);
            return false;
        }
        if (!sysop.IsElevated(clock.GetUtcNow()))
        {
            await WriteLineAsync(connection, "Not authorised. Use SYSOP <code> first.", ct).ConfigureAwait(false);
            return false;
        }
        if (!AuthScopes.Satisfies(sysop.Scope, requiredScope))
        {
            await WriteLineAsync(connection, $"Not authorised ({requiredScope} required).", ct).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    // ─── IO helpers ─────────────────────────────────────────────────────

    private static async Task WriteLineAsync(INodeConnection connection, string text, CancellationToken ct)
    {
        // Line endings are per-transport: telnet terminals need CR-LF, while
        // AX.25 terminals (and packet TNCs) use a bare CR. Normalise the source
        // text's newlines to the connection's convention and terminate the line.
        var nl = NewLine(connection);
        var rendered = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", nl, StringComparison.Ordinal) + nl;
        await connection.WriteAsync(Encoding.UTF8.GetBytes(rendered), ct).ConfigureAwait(false);
    }

    /// <summary>The line terminator for a connection: CR-LF for telnet, a bare CR
    /// for AX.25 (the packet-radio convention).</summary>
    private static string NewLine(INodeConnection connection) =>
        connection.TransportKind == NodeTransportKind.Telnet ? "\r\n" : "\r";

    private static async Task<ReadOnlyMemory<byte>> ReadOrCompletion(INodeConnection connection, CancellationToken ct)
    {
        var readTask = connection.ReadAsync(ct).AsTask();
        var completed = await Task.WhenAny(readTask, connection.Completion).ConfigureAwait(false);
        if (completed == connection.Completion && !readTask.IsCompleted)
        {
            return ReadOnlyMemory<byte>.Empty;   // peer gone before the read returned
        }
        return await readTask.ConfigureAwait(false);
    }

    // Strip control chars from an echoed unknown command so a hostile line can't
    // inject terminal escapes / extra newlines into our reply.
    private static string Sanitise(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            sb.Append(char.IsControl(c) ? '.' : c);
        }
        return sb.ToString();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Console session for {PeerId} ended on an error.")]
    private partial void LogConsoleError(Exception ex, string peerId);

    // Sysop audit — subject is the callsign (AX.25) or username (telnet); the code is
    // NEVER logged. Elevation grants are Information; denials are Warning.
    [LoggerMessage(Level = LogLevel.Information, Message = "Sysop elevation granted to {Subject} (scope {Scope}) over {PeerId}.")]
    private partial void LogSysopElevated(string subject, string scope, string peerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sysop elevation denied over {PeerId}: {Reason}.")]
    private partial void LogSysopDenied(string peerId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sysop command {Command} run over {PeerId}.")]
    private partial void LogSysopCommand(string command, string peerId);
}
