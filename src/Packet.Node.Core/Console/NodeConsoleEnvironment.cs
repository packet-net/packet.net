using Packet.Node.Core.Configuration;
using Packet.Node.Core.NetRom;

namespace Packet.Node.Core.Console;

/// <summary>
/// The read-only view of the node a console session needs to answer commands:
/// the live config (for identity / ports / services text) plus the optional
/// outbound connector for this session. Built fresh per accepted connection
/// from the current <see cref="IConfigProvider.Current"/>, so each session sees
/// the config as it was at accept time; <see cref="Services"/> is read through
/// the provider so banner/prompt edits apply to the next prompt.
/// </summary>
public sealed class NodeConsoleEnvironment
{
    private readonly IConfigProvider config;

    /// <summary>
    /// The connector that <c>Connect</c> uses for this session. Null when
    /// outbound connect is not available on this connection (e.g. a telnet
    /// session with no AX.25 port configured to dial out on).
    /// </summary>
    public IOutboundConnector? OutboundConnector { get; }

    /// <summary>
    /// The read-only view of the node's learned NET/ROM routing table, surfaced by
    /// the <c>Nodes</c> command. Null when NET/ROM awareness isn't wired (older
    /// call sites / tests); the command then shows ports only.
    /// </summary>
    public INetRomRoutingView? NetRom { get; }

    public NodeConsoleEnvironment(IConfigProvider config, IOutboundConnector? outboundConnector, INetRomRoutingView? netRom = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        OutboundConnector = outboundConnector;
        NetRom = netRom;
    }

    /// <summary>The node identity (callsign + alias + grid).</summary>
    public Identity Identity => config.Current.Identity;

    /// <summary>The configured ports, as of the current config.</summary>
    public IReadOnlyList<PortConfig> Ports => config.Current.Ports;

    /// <summary>The current service text (banner/prompt) — read live so a hot
    /// edit lands on the next prompt without rebuilding the session.</summary>
    public ServicesConfig Services => config.Current.Services;

    /// <summary>The node's display name — alias if set, else the callsign.</summary>
    public string NodeName => string.IsNullOrWhiteSpace(Identity.Alias) ? Identity.Callsign : Identity.Alias!;
}
