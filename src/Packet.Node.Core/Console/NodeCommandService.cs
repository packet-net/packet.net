using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
public sealed partial class NodeCommandService
{
    private readonly NodeConsoleEnvironment env;
    private readonly ILogger<NodeCommandService> logger;

    /// <summary>The node software version string, from the assembly's informational
    /// version (falls back to the assembly version).</summary>
    public static string Version { get; } =
        typeof(NodeCommandService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(NodeCommandService).Assembly.GetName().Version?.ToString()
            ?? "dev";

    public NodeCommandService(NodeConsoleEnvironment env, ILogger<NodeCommandService>? logger = null)
    {
        this.env = env ?? throw new ArgumentNullException(nameof(env));
        this.logger = logger ?? NullLogger<NodeCommandService>.Instance;
    }

    /// <summary>
    /// Run the prompt loop until the user disconnects or the connection drops.
    /// Owns nothing it didn't create: the caller disposes
    /// <paramref name="connection"/>.
    /// </summary>
    public async Task RunAsync(INodeConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await WriteLineAsync(connection, Banner(), cancellationToken).ConfigureAwait(false);
        await WritePromptAsync(connection, cancellationToken).ConfigureAwait(false);

        var assembler = new LineAssembler();
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
                    var outcome = await DispatchAsync(connection, command, cancellationToken).ConfigureAwait(false);
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

    private enum DispatchOutcome { Continue, Disconnect }

    private async Task<DispatchOutcome> DispatchAsync(
        INodeConnection connection, NodeCommand command, CancellationToken ct)
    {
        switch (command)
        {
            case EmptyCommand:
                return DispatchOutcome.Continue;

            case HelpCommand:
                await WriteLineAsync(connection, HelpText(), ct).ConfigureAwait(false);
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

            case UnknownCommand unknown:
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
        var connector = env.OutboundConnector;
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
            await ConsoleRelay.PipeAsync(inbound, outbound, ct).ConfigureAwait(false);
            await WriteLineAsync(inbound, $"Disconnected from {connect.Target}.", ct).ConfigureAwait(false);
        }
    }

    // ─── Text builders ──────────────────────────────────────────────────

    private string Banner() => Expand(env.Services.Banner) + $"  [Packet.NET {Version}]";

    private async Task WritePromptAsync(INodeConnection connection, CancellationToken ct)
    {
        var prompt = Expand(env.Services.Prompt);
        await connection.WriteAsync(Encoding.UTF8.GetBytes(prompt), ct).ConfigureAwait(false);
    }

    private string Expand(string template) => template
        .Replace("{node}", env.NodeName, StringComparison.Ordinal)
        .Replace("{call}", env.Identity.Callsign, StringComparison.Ordinal);

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
            return sb.ToString();
        }
        sb.Append("Ports:");
        foreach (var p in ports)
        {
            sb.Append('\n').Append("  ").Append(p.Id)
              .Append(' ').Append(p.Enabled ? "[up]" : "[down]")
              .Append(' ').Append(p.Transport.DescribeEndpoint());
        }
        return sb.ToString();
    }

    private static string HelpText() =>
        "Commands:\n" +
        "  C[onnect] <call>   connect to a station\n" +
        "  N[odes]            list this node and its ports\n" +
        "  I[nfo]             node info and version\n" +
        "  B[ye] / D          disconnect\n" +
        "  H[elp] / ?         this help";

    // ─── IO helpers ─────────────────────────────────────────────────────

    private static async Task WriteLineAsync(INodeConnection connection, string text, CancellationToken ct)
    {
        // Packet/terminal convention: CR-terminated lines. \n in the source text
        // is rendered as CR so multi-line replies break correctly on packet TNCs
        // and telnet alike.
        var rendered = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', '\r') + "\r";
        await connection.WriteAsync(Encoding.UTF8.GetBytes(rendered), ct).ConfigureAwait(false);
    }

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
}
