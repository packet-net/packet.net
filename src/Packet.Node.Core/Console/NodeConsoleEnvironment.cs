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

    /// <summary>
    /// The over-RF sysop dependencies (user store + TOTP verifier + privileged
    /// operations). Null when sysop elevation isn't wired — older call sites, tests, or a
    /// node where the host couldn't supply the seam — in which case the <c>SYSOP</c>
    /// command reports "not available" and the privileged commands are inert. Even when
    /// present, elevation is only honoured while <see cref="AuthEnabled"/> is true.
    /// </summary>
    public SysopContext? Sysop { get; }

    public NodeConsoleEnvironment(
        IConfigProvider config,
        IOutboundConnector? outboundConnector,
        INetRomRoutingView? netRom = null,
        SysopContext? sysop = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        OutboundConnector = outboundConnector;
        NetRom = netRom;
        Sysop = sysop;
    }

    /// <summary>The default over-RF elevation lifetime when the config leaves it unset.</summary>
    public const int DefaultSysopElevationMinutes = 15;

    /// <summary>The node identity (callsign + alias + grid).</summary>
    public Identity Identity => config.Current.Identity;

    /// <summary>The configured ports, as of the current config.</summary>
    public IReadOnlyList<PortConfig> Ports => config.Current.Ports;

    /// <summary>The current service text (banner/prompt) — read live so a hot
    /// edit lands on the next prompt without rebuilding the session.</summary>
    public ServicesConfig Services => config.Current.Services;

    /// <summary>The node's display name — alias if set, else the callsign.</summary>
    public string NodeName => string.IsNullOrWhiteSpace(Identity.Alias) ? Identity.Callsign : Identity.Alias!;

    /// <summary>Whether web/management auth is enabled — read live. Over-RF <c>SYSOP</c>
    /// elevation is only honoured when this is true (the TOTP secrets + scopes it relies on
    /// only exist in an auth-enabled node); with auth off the command reports "not
    /// available", matching the default-off contract.</summary>
    public bool AuthEnabled => config.Current.Management.Auth.Enabled;

    /// <summary>How long an over-RF elevation lasts — read live from
    /// <c>management.auth.sysopElevationMinutes</c> (default
    /// <see cref="DefaultSysopElevationMinutes"/>).</summary>
    public TimeSpan SysopElevationTtl =>
        TimeSpan.FromMinutes(config.Current.Management.Auth.SysopElevationMinutes ?? DefaultSysopElevationMinutes);
}
