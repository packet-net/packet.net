using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Applications.Packages;

/// <summary>
/// An app package's <c>pdn-app.yaml</c> - the manifest <b>authored by the app</b>, not the node
/// owner (the owner's surface is the <c>apps:</c> override list, <see cref="AppOverrideConfig"/>).
/// The full contract is <c>docs/app-packages.md</c>; this record tree is its C# shape. At least
/// one of <see cref="Session"/> / <see cref="Service"/> / <see cref="Ui"/> must be present
/// (validated by the catalog).
/// </summary>
public sealed record AppPackageManifest
{
    /// <summary>Manifest schema version. Only <c>1</c> is understood; anything else is a
    /// validation error (forward-incompatible by design).</summary>
    public int Manifest { get; init; }

    /// <summary>Stable app identity - lowercase <c>[a-z0-9-]</c>, MUST equal the package
    /// directory name. The reconcile / log / override key.</summary>
    public required string Id { get; init; }

    /// <summary>Human label (UI tiles, the management list). Default: <see cref="Id"/>.</summary>
    public string? Name { get; init; }

    /// <summary>Informational version string, shown in the UI. Not parsed.</summary>
    public string? Version { get; init; }

    /// <summary>One-line human description, shown in the management list.</summary>
    public string? Description { get; init; }

    /// <summary>Optional lucide icon name (cosmetic).</summary>
    public string? Icon { get; init; }

    /// <summary>Declared capabilities - shown to the owner at enable time (the trust prompt),
    /// not enforced in v1. Conventional values: <c>session</c>, <c>network</c>, <c>web</c>.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Packet-plane identity (<c>docs/app-packages.md</c> § Application packet identity) -
    /// optional. Carries the app-authored node-prompt verb (<see cref="AppPacketSpec.Command"/>);
    /// the callsign / NET/ROM alias are the <b>node's</b>, set in the owner's
    /// <c>apps[]</c>/<c>applications[]</c> entry, never the manifest.</summary>
    public AppPacketSpec? Packet { get; init; }

    /// <summary>Packet-plane console attachment (the <c>pdn-app/1</c> wire) - optional.
    /// The console verb that attaches a session is <see cref="AppPacketSpec.Command"/> on
    /// <see cref="Packet"/>, not a field here.</summary>
    public AppSessionSpec? Session { get; init; }

    /// <summary>A long-running daemon pdn supervises (or observes, when
    /// <see cref="AppServiceSpec.Managed"/> is <see cref="AppServiceManaged.External"/>) - optional.</summary>
    public AppServiceSpec? Service { get; init; }

    /// <summary>Human-plane web UI (the existing app-gateway contract) - optional.</summary>
    public AppUiConfig? Ui { get; init; }

    /// <summary>Declared tailnet port forwards (<c>docs/network-access.md</c> § App-declared
    /// port forwarding) - the app asks pdn's embedded Tailscale node to expose a port on the
    /// tailnet and proxy it to the app's loopback listener. Each forward is a capability the
    /// owner sees at enable time. Default empty; tailnet-only (applied only when
    /// <c>tailscale.enabled</c>).</summary>
    public IReadOnlyList<AppForwardSpec> Forward { get; init; } = [];
}

/// <summary>How the sidecar terminates TLS for a forwarded port.</summary>
public enum ForwardTls
{
    /// <summary>The sidecar terminates TLS with the node's tailnet cert and proxies plaintext
    /// to the app's loopback <see cref="AppForwardSpec.Target"/> (the default - pdn owns the
    /// TLS edge; the app stays plaintext-on-loopback). The everyday IMAPS/SMTPS shape.</summary>
    Terminate,

    /// <summary>The sidecar passes the TCP stream through unterminated, relying on WireGuard for
    /// transport encryption - for an app that speaks its own TLS (or a plaintext tailnet
    /// protocol).</summary>
    Raw,
}

/// <summary>
/// One entry in the manifest's <c>forward:</c> block - a tailnet-facing port the embedded
/// Tailscale node exposes and reverse-proxies to one of the app's loopback listeners. The Go
/// sidecar consumes these (the supervisor writes them to <c>--forwards-file</c> as a JSON
/// array). Validated by <see cref="AppPackageCatalog"/>: <see cref="Listen"/> in 1..65535 and
/// not 443 (reserved for the web reverse-proxy), <see cref="Target"/> a loopback host:port,
/// and a <see cref="Listen"/> port may be claimed by only one discovered package.
/// </summary>
public sealed record AppForwardSpec
{
    /// <summary>The tailnet-facing port the node's tsnet node listens on (e.g. <c>993</c> for
    /// IMAPS). 1..65535, never 443 (the web reverse-proxy owns that).</summary>
    public int Listen { get; init; }

    /// <summary>The app's plaintext loopback listener as <c>host:port</c> - host ∈
    /// {127.0.0.1, ::1, localhost}, port 1..65535. pdn refuses to proxy the tailnet to a
    /// non-loopback host.</summary>
    public required string Target { get; init; }

    /// <summary>TLS handling. Default <see cref="ForwardTls.Terminate"/> (the sidecar adds TLS
    /// with the node cert).</summary>
    public ForwardTls Tls { get; init; } = ForwardTls.Terminate;
}

/// <summary>
/// The manifest's <c>packet:</c> block - the app's packet-plane identity
/// (<c>docs/app-packages.md</c> § Application packet identity). Only the
/// <see cref="Command"/> verb is app-authored: it <i>is</i> the app's identity (a generic
/// "bbs" app legitimately calls itself <c>BBS</c>). The callsign and NET/ROM alias are the
/// node's - they live in the owner's <c>apps[]</c>/<c>applications[]</c> entry, never here.
/// </summary>
public sealed record AppPacketSpec
{
    /// <summary>The node-prompt verb / application name (e.g. <c>"BBS"</c>) - a sensible
    /// app-authored default the owner may override via <see cref="AppOverrideConfig.Command"/>.
    /// Registers a bare node-prompt verb; for a <c>service</c> app it loopback-connects to the
    /// app's resolved callsign, for a <c>session</c> app it attaches the per-connection session.
    /// Optional: omit it and the app is reachable only by callsign / NET/ROM alias. This
    /// <b>replaces</b> the old <c>session.match</c> (no back-compat parsing). Note it is distinct
    /// from <see cref="AppSessionSpec.Command"/> (the executable for <c>kind: process</c>).</summary>
    public string? Command { get; init; }
}

/// <summary>The manifest's <c>session:</c> block - how a console verb attaches a user session
/// to the app over the <c>pdn-app/1</c> wire. Mirrors the inline
/// <see cref="ApplicationConfig"/> fields, minus owner-only concerns. The verb that triggers
/// the attachment is <see cref="AppPacketSpec.Command"/> (on <see cref="AppPackageManifest.Packet"/>),
/// not a field on this record.</summary>
public sealed record AppSessionSpec
{
    /// <summary>Transport of the wire: spawn-per-connect <see cref="ApplicationKind.Process"/>
    /// or connect-per-session <see cref="ApplicationKind.Socket"/>.</summary>
    public ApplicationKind Kind { get; init; } = ApplicationKind.Process;

    /// <summary>Executable for <see cref="ApplicationKind.Process"/> - absolute, or relative to
    /// the package directory.</summary>
    public string? Command { get; init; }

    /// <summary>Arguments; an element naming an existing file relative to the package dir
    /// resolves to its absolute path (see the contract's path-resolution rule).</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>The Unix-domain socket for <see cref="ApplicationKind.Socket"/>.</summary>
    public string? SocketPath { get; init; }
}

/// <summary>Restart policy for a supervised service.</summary>
public enum AppServiceRestart
{
    /// <summary>Restart (with backoff) only when the process exits non-zero or crashes.</summary>
    OnFailure,

    /// <summary>Restart (with backoff) on any exit.</summary>
    Always,

    /// <summary>Never restart automatically; any exit leaves the service stopped.</summary>
    Never,
}

/// <summary>Who owns the daemon's lifecycle.</summary>
public enum AppServiceManaged
{
    /// <summary>pdn starts, supervises, restarts, and stops the daemon (the default).</summary>
    Pdn,

    /// <summary>The owner runs the daemon (systemd etc.); pdn never starts or stops it and
    /// reports its state as <see cref="AppServiceState.External"/>.</summary>
    External,
}

/// <summary>The manifest's <c>service:</c> block - a long-running daemon.</summary>
public sealed record AppServiceSpec
{
    /// <summary>Executable - absolute, or relative to the package directory.</summary>
    public required string Command { get; init; }

    /// <summary>Arguments, package-dir-relative resolution as for the session block.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Manifest-declared environment, merged UNDER the owner's
    /// <see cref="AppOverrideConfig.Environment"/> (owner wins), both over the
    /// <c>PDN_APP_*</c> variables the supervisor injects.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Working directory; null = the app's state dir
    /// (<c>/var/lib/packetnet/apps/&lt;id&gt;</c>).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Restart policy. Default <see cref="AppServiceRestart.OnFailure"/>.</summary>
    public AppServiceRestart Restart { get; init; } = AppServiceRestart.OnFailure;

    /// <summary>Lifecycle ownership. Default <see cref="AppServiceManaged.Pdn"/>.</summary>
    public AppServiceManaged Managed { get; init; } = AppServiceManaged.Pdn;
}
