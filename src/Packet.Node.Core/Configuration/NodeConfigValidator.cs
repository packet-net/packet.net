using FluentValidation;
using Packet.Core;
using Packet.NetRom.Wire;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// FluentValidation rules for a candidate <see cref="NodeConfig"/>. Run by the
/// config provider <b>before</b> a candidate is allowed to become
/// <see cref="IConfigProvider.Current"/>; a failing candidate is rejected whole
/// (atomic apply — see <see cref="FileConfigProvider"/>).
/// </summary>
/// <remarks>
/// The callsign is validated by round-tripping through
/// <c>Packet.Core.Callsign.TryParse</c> — which is exactly where the
/// 1–6-char base + SSID 0–15 rules live — rather than re-encoding them here.
/// Empty <see cref="NodeConfig.Ports"/> is legal (an idle node).
/// </remarks>
public sealed class NodeConfigValidator : AbstractValidator<NodeConfig>
{
    public NodeConfigValidator()
    {
        RuleFor(c => c.SchemaVersion)
            .GreaterThan(0)
            .WithMessage("SchemaVersion must be a positive integer.");

        RuleFor(c => c.Identity)
            .NotNull()
            .WithMessage("Identity is required.")
            .SetValidator(new IdentityValidator());

        // Empty ports is legal — an idle node still answers telnet + /healthz.
        RuleForEach(c => c.Ports).SetValidator(new PortConfigValidator());

        RuleFor(c => c.Ports)
            .Must(HaveUniqueIds)
            .WithMessage("Each port must have a unique Id (the reconcile key).")
            .Must(HaveUniqueEndpoints)
            .WithMessage("Two ports may not share the same transport device / endpoint.");

        RuleFor(c => c.Management).NotNull().SetValidator(new ManagementValidator());

        RuleFor(c => c.NetRom).NotNull().SetValidator(new NetRomValidator());

        RuleFor(c => c.Beacon).NotNull().SetValidator(new BeaconConfigValidator());

        RuleFor(c => c.Rhp).NotNull().SetValidator(new RhpConfigValidator());

        // Empty applications is the default (a node with no apps). Each entry is validated,
        // and ids / match-verbs must be unique across the list (the launch + log keys).
        RuleForEach(c => c.Applications).SetValidator(new ApplicationConfigValidator());

        RuleFor(c => c.Applications)
            .Must(HaveUniqueAppIds)
            .WithMessage("Each application must have a unique Id.")
            .Must(HaveUniqueAppMatches)
            .WithMessage("Two applications may not share the same Match verb (case-insensitive).");
    }

    private static bool HaveUniqueIds(IReadOnlyList<PortConfig> ports)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ports.All(p => p.Id is not null && seen.Add(p.Id));
    }

    private static bool HaveUniqueAppIds(IReadOnlyList<ApplicationConfig> apps)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return apps.All(a => a.Id is not null && seen.Add(a.Id));
    }

    private static bool HaveUniqueAppMatches(IReadOnlyList<ApplicationConfig> apps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return apps.All(a => string.IsNullOrWhiteSpace(a.Match) || seen.Add(a.Match.Trim()));
    }

    private static bool HaveUniqueEndpoints(IReadOnlyList<PortConfig> ports)
    {
        // Two ports pointing at the same physical device / TCP endpoint can't
        // both own it — flag the collision regardless of Enabled, so toggling a
        // disabled twin on later doesn't suddenly conflict.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ports.All(p => p.Transport is null || seen.Add(p.Transport.DescribeEndpoint()));
    }
}

/// <summary>Validates <see cref="Identity"/> — the callsign must parse.</summary>
public sealed class IdentityValidator : AbstractValidator<Identity>
{
    public IdentityValidator()
    {
        RuleFor(i => i.Callsign)
            .NotEmpty().WithMessage("Identity.Callsign is required.")
            .Must(c => Callsign.TryParse(c, out _))
            .WithMessage(i =>
                $"Identity.Callsign '{i.Callsign}' is not a valid AX.25 callsign " +
                "(1–6 uppercase alphanumerics, optional -SSID in 0–15).");
    }
}

/// <summary>Validates one <see cref="PortConfig"/> + its transport union arm.</summary>
public sealed class PortConfigValidator : AbstractValidator<PortConfig>
{
    public PortConfigValidator()
    {
        RuleFor(p => p.Id).NotEmpty().WithMessage("Port.Id is required (it is the reconcile key).");

        RuleFor(p => p.Transport).NotNull().WithMessage("Port.Transport is required.");

        // A profile is optional, but a typo'd one is a config error (it would
        // silently do nothing otherwise). Null/empty = no profile = spec defaults.
        RuleFor(p => p.Profile)
            .Must(ChannelProfiles.IsKnown)
            .WithMessage(p =>
                $"Port.Profile '{p.Profile}' is not a known channel profile " +
                $"(expected one of: {string.Join(", ", ChannelProfiles.Names)} — or omit it for spec defaults).");

        // Discriminated-union dispatch: validate the concrete transport arm.
        // A `kind:` the deserialiser didn't recognise never reaches here — it
        // fails at parse time — so every value is one of the known subtypes.
        When(p => p.Transport is SerialKissTransport, () =>
            RuleFor(p => (SerialKissTransport)p.Transport).SetValidator(new SerialKissValidator()));
        When(p => p.Transport is NinoTncTransport, () =>
            RuleFor(p => (NinoTncTransport)p.Transport).SetValidator(new NinoTncValidator()));
        When(p => p.Transport is KissTcpTransport, () =>
            RuleFor(p => (KissTcpTransport)p.Transport).SetValidator(new KissTcpValidator()));
        When(p => p.Transport is AxudpTransport, () =>
            RuleFor(p => (AxudpTransport)p.Transport).SetValidator(new AxudpValidator()));

        When(p => p.Ax25 is not null, () =>
            RuleFor(p => p.Ax25!).SetValidator(new Ax25ParamsValidator()));

        When(p => p.Beacon is not null, () =>
            RuleFor(p => p.Beacon!).SetValidator(new PortBeaconValidator()));

        When(p => p.Compat is not null, () =>
            RuleFor(p => p.Compat!).SetValidator(new PortCompatValidator()));
    }
}

/// <summary>
/// Validates a per-port AX.25 compatibility profile (<see cref="PortCompatConfig"/>).
/// A typo'd preset or quirks name is a config error — it would otherwise silently
/// resolve as the default, the accept-then-ignore failure the named-flag discipline
/// exists to prevent. Name knowledge lives in <see cref="Ax25CompatPresets"/> (the
/// same authority the supervisor resolves with), not here.
/// </summary>
public sealed class PortCompatValidator : AbstractValidator<PortCompatConfig>
{
    public PortCompatValidator()
    {
        RuleFor(c => c.Preset)
            .Must(Ax25CompatPresets.IsKnownPreset)
            .WithMessage(c =>
                $"compat.preset '{c.Preset}' is not a known AX.25 parse preset " +
                $"(expected one of: {string.Join(", ", Ax25CompatPresets.PresetNames)} — or omit it for lenient).");

        RuleFor(c => c.Quirks)
            .Must(Ax25CompatPresets.IsKnownQuirks)
            .WithMessage(c =>
                $"compat.quirks '{c.Quirks}' is not a known session-quirks selector " +
                $"(expected one of: {string.Join(", ", Ax25CompatPresets.QuirksNames)} — or omit it for default).");
    }
}

/// <summary>
/// Validates the system-default beacon (<see cref="BeaconConfig"/>). The interval
/// must be at least one minute, and an <em>enabled</em> beacon must carry non-empty
/// text (a blank ID frame is meaningless). A disabled beacon is unconstrained — the
/// text/interval are inert until the operator turns it on.
/// </summary>
public sealed class BeaconConfigValidator : AbstractValidator<BeaconConfig>
{
    public BeaconConfigValidator()
    {
        RuleFor(b => b.IntervalMinutes).GreaterThanOrEqualTo(1)
            .WithMessage("beacon.intervalMinutes must be at least 1.");
        RuleFor(b => b.Text).NotEmpty()
            .When(b => b.Enabled)
            .WithMessage("beacon.text must be non-empty when the beacon is enabled.");
    }
}

/// <summary>
/// Validates a per-port beacon override (<see cref="PortBeaconConfig"/>). Same shape
/// as the system default, but the interval/text are nullable (null = inherit), so the
/// range/non-empty checks apply only when the field is set. An <em>enabled</em> port
/// override that <em>sets</em> the text may not set it to empty.
/// </summary>
public sealed class PortBeaconValidator : AbstractValidator<PortBeaconConfig>
{
    public PortBeaconValidator()
    {
        RuleFor(b => b.IntervalMinutes!.Value).GreaterThanOrEqualTo(1)
            .When(b => b.IntervalMinutes.HasValue)
            .WithMessage("port beacon.intervalMinutes must be at least 1.");
        RuleFor(b => b.Text!).NotEmpty()
            .When(b => b.Enabled && b.Text is not null)
            .WithMessage("port beacon.text must be non-empty when set on an enabled beacon.");
    }
}

/// <summary>Validates the RHPv2 server block: a sane port always (so a disabled-but-edited
/// block can't hold junk), a parseable bind when enabled.</summary>
public sealed class RhpConfigValidator : AbstractValidator<RhpConfig>
{
    public RhpConfigValidator()
    {
        RuleFor(r => r.Port)
            .InclusiveBetween(1, 65535)
            .WithMessage("rhp.port must be in 1..65535.");

        RuleFor(r => r.Bind)
            .Must(b => System.Net.IPAddress.TryParse(b, out _))
            .When(r => r.Enabled)
            .WithMessage(r => $"rhp.bind '{r.Bind}' must be an IP address when rhp is enabled.");
    }
}

/// <summary>
/// Validates one <see cref="ApplicationConfig"/>: a stable id, a launch verb that does not
/// collide with a built-in console verb, and the fields its <see cref="ApplicationKind"/>
/// requires (a process app needs a command). The built-in-verb collision is checked by
/// running the verb through <see cref="NodeCommandParser"/> — the single source of truth for
/// what the console already understands — and rejecting anything it classifies as a real
/// command (so a registered app can never be dead config, shadowed by a built-in).
/// </summary>
public sealed class ApplicationConfigValidator : AbstractValidator<ApplicationConfig>
{
    public ApplicationConfigValidator()
    {
        RuleFor(a => a.Id)
            .NotEmpty().WithMessage("application.id is required.");

        RuleFor(a => a.Match)
            .NotEmpty().WithMessage("application.match (the launch verb) is required.")
            .Must(NotABuiltInVerb)
            .WithMessage(a => $"application.match '{a.Match}' collides with a built-in console verb " +
                "(CONNECT/BYE/NODES/INFO/HELP/SYSOP/SESSIONS/KICK/PORT/RELOAD or an abbreviation) — pick another.");

        RuleFor(a => a.Command)
            .NotEmpty().WithMessage("application.command is required for a process application.")
            .When(a => a.Kind == ApplicationKind.Process);

        RuleFor(a => a.SocketPath)
            .NotEmpty().WithMessage("application.socketPath is required for a socket application.")
            .When(a => a.Kind == ApplicationKind.Socket);

        // When a ui block is present, its upstream must be an absolute http(s) URL — pdn
        // reverse-proxies to it, so anything else is unusable config.
        When(a => a.Ui is not null, () =>
            RuleFor(a => a.Ui!.Upstream)
                .Must(BeAnAbsoluteHttpUrl)
                .WithMessage(a => $"application.ui.upstream '{a.Ui!.Upstream}' must be an absolute http(s) URL (e.g. http://127.0.0.1:9090)."));
    }

    private static bool BeAnAbsoluteHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    // The verb is safe iff the parser does NOT recognise it as a command — i.e. it falls
    // through to Unknown (or is empty). Anything that parses to a real verb (or a malformed
    // form of one, e.g. a bare "C") would be intercepted by the console before the app could
    // ever launch, so reject it at config time rather than ship dead config.
    private static bool NotABuiltInVerb(string? match)
    {
        if (string.IsNullOrWhiteSpace(match))
        {
            return true;   // emptiness is reported by the NotEmpty rule above.
        }
        var parsed = NodeCommandParser.Parse(match.Trim());
        return parsed is UnknownCommand or EmptyCommand;
    }
}

/// <summary>Validates a <see cref="SerialKissTransport"/>.</summary>
public sealed class SerialKissValidator : AbstractValidator<SerialKissTransport>
{
    public SerialKissValidator()
    {
        RuleFor(t => t.Device).NotEmpty().WithMessage("serial-kiss transport requires a device.");
        RuleFor(t => t.Baud).GreaterThan(0).WithMessage("serial-kiss baud must be positive.");
    }
}

/// <summary>Validates a <see cref="NinoTncTransport"/> incl. the 0–15 mode range.</summary>
public sealed class NinoTncValidator : AbstractValidator<NinoTncTransport>
{
    public NinoTncValidator()
    {
        RuleFor(t => t.Device).NotEmpty().WithMessage("nino-tnc transport requires a device.");
        RuleFor(t => t.Baud).GreaterThan(0).WithMessage("nino-tnc baud must be positive.");
        RuleFor(t => t.Mode).InclusiveBetween(0, 15).WithMessage("nino-tnc mode must be in 0..15.");
    }
}

/// <summary>Validates a <see cref="KissTcpTransport"/>.</summary>
public sealed class KissTcpValidator : AbstractValidator<KissTcpTransport>
{
    public KissTcpValidator()
    {
        RuleFor(t => t.Host).NotEmpty().WithMessage("kiss-tcp transport requires a host.");
        RuleFor(t => t.Port).InclusiveBetween(1, 65535).WithMessage("kiss-tcp port must be in 1..65535.");
    }
}

/// <summary>Validates an <see cref="AxudpTransport"/> (remote host/port + the
/// local bind port; localPort 0 = ephemeral, so it is allowed).</summary>
public sealed class AxudpValidator : AbstractValidator<AxudpTransport>
{
    public AxudpValidator()
    {
        RuleFor(t => t.Host).NotEmpty().WithMessage("axudp transport requires a host (the remote peer).");
        RuleFor(t => t.Port).InclusiveBetween(1, 65535).WithMessage("axudp port must be in 1..65535.");
        // localPort 0 means "pick an ephemeral port" (send-only / monitor); a
        // bound port for receiving must be in range.
        RuleFor(t => t.LocalPort).InclusiveBetween(0, 65535).WithMessage("axudp localPort must be in 0..65535 (0 = ephemeral).");
    }
}

/// <summary>Validates the optional per-port AX.25 tuning.</summary>
public sealed class Ax25ParamsValidator : AbstractValidator<Ax25PortParams>
{
    public Ax25ParamsValidator()
    {
        RuleFor(p => p.T1Ms!.Value).GreaterThan(0).When(p => p.T1Ms.HasValue).WithMessage("T1Ms must be positive.");
        RuleFor(p => p.T2Ms!.Value).GreaterThan(0).When(p => p.T2Ms.HasValue).WithMessage("T2Ms must be positive.");
        RuleFor(p => p.T3Ms!.Value).GreaterThan(0).When(p => p.T3Ms.HasValue).WithMessage("T3Ms must be positive.");
        RuleFor(p => p.N2!.Value).GreaterThan(0).When(p => p.N2.HasValue).WithMessage("N2 must be positive.");
        RuleFor(p => p.WindowSize!.Value).InclusiveBetween(1, 127).When(p => p.WindowSize.HasValue)
            .WithMessage("WindowSize (k) must be in 1..127.");
        RuleFor(p => p.MaxCachedPeers!.Value).GreaterThan(0).When(p => p.MaxCachedPeers.HasValue)
            .WithMessage("MaxCachedPeers must be positive.");
    }
}

/// <summary>
/// Validates the optional NET/ROM knobs. Qualities are 0..255 (the NET/ROM quality
/// range); OBSINIT/OBSMIN, the sweep interval, the L4 window/timeout/retries, and
/// the TTL are positive (TTL ≤ 255 — a 1-octet header field). Broadcast/connect
/// require the service enabled. A typo'd value is rejected here rather than
/// silently clamped, matching the per-port tuning discipline. The nested INP3
/// overlay's ranges are delegated to <see cref="NetRomInp3Options.Validate"/>
/// (one validation authority), and <c>inp3.enabled</c> requires both the service
/// enabled and <c>connect</c> on (INP3 rides the connected-mode interlink, so it is
/// only constructed under Connect).
/// </summary>
public sealed class NetRomValidator : AbstractValidator<NetRomConfig>
{
    public NetRomValidator()
    {
        RuleFor(c => c.DefaultNeighbourQuality!.Value).InclusiveBetween(0, 255)
            .When(c => c.DefaultNeighbourQuality.HasValue)
            .WithMessage("netrom.defaultNeighbourQuality must be in 0..255.");
        RuleFor(c => c.MinQuality!.Value).InclusiveBetween(0, 255)
            .When(c => c.MinQuality.HasValue)
            .WithMessage("netrom.minQuality must be in 0..255.");
        RuleFor(c => c.ObsoleteInitial!.Value).GreaterThan(0)
            .When(c => c.ObsoleteInitial.HasValue)
            .WithMessage("netrom.obsoleteInitial must be positive.");
        RuleFor(c => c.ObsoleteMinimum!.Value).GreaterThanOrEqualTo(0)
            .When(c => c.ObsoleteMinimum.HasValue)
            .WithMessage("netrom.obsoleteMinimum must be zero or positive.");
        RuleFor(c => c.SweepIntervalSeconds!.Value).GreaterThan(0)
            .When(c => c.SweepIntervalSeconds.HasValue)
            .WithMessage("netrom.sweepIntervalSeconds must be positive.");
        RuleFor(c => c.Window!.Value).InclusiveBetween(1, 127)
            .When(c => c.Window.HasValue)
            .WithMessage("netrom.window must be in 1..127 (the 8-bit sequence space).");
        RuleFor(c => c.TransportTimeoutSeconds!.Value).GreaterThan(0)
            .When(c => c.TransportTimeoutSeconds.HasValue)
            .WithMessage("netrom.transportTimeoutSeconds must be positive.");
        RuleFor(c => c.TransportRetries!.Value).GreaterThan(0)
            .When(c => c.TransportRetries.HasValue)
            .WithMessage("netrom.transportRetries must be positive.");
        RuleFor(c => c.TimeToLive!.Value).InclusiveBetween(1, 255)
            .When(c => c.TimeToLive.HasValue)
            .WithMessage("netrom.timeToLive must be in 1..255.");
        RuleFor(c => c)
            .Must(c => !(c.Broadcast && !c.Enabled))
            .WithMessage("netrom.broadcast requires netrom.enabled.");
        RuleFor(c => c)
            .Must(c => !(c.Connect && !c.Enabled))
            .WithMessage("netrom.connect requires netrom.enabled.");

        // INP3 overlay: delegate the range + cross-field checks to the record's own
        // Validate() (one source of truth for the knob ranges — the same "one
        // validation authority" discipline the callsign rule uses by round-tripping
        // through Callsign.TryParse), surfaced as a FluentValidation failure so a bad
        // nested inp3: block rejects the whole candidate config atomically.
        RuleFor(c => c.Inp3)
            .NotNull()
            .Must(BeValidInp3Options)
            .WithMessage(c => $"netrom.inp3 is invalid: {DescribeInp3Fault(c.Inp3)}");

        // inp3.enabled requires netrom.enabled — a routing overlay on a deaf node is
        // meaningless (mirrors the broadcast/connect-require-enabled guards above).
        RuleFor(c => c)
            .Must(c => !(c.Inp3.Enabled && !c.Enabled))
            .WithMessage("netrom.inp3.enabled requires netrom.enabled.");

        // inp3.enabled requires netrom.connect — INP3 rides on the connected-mode interlink
        // machinery (L3RTT / RIF are 0xCF I-frames on the same sessions L4 uses), so the host
        // constructs the overlay only under Connect. Without this rule, inp3.enabled + connect:false
        // would pass validation and then silently no-op (the overlay never built) — reject it
        // explicitly rather than accept-then-ignore (the named-flag discipline).
        RuleFor(c => c)
            .Must(c => !(c.Inp3.Enabled && !c.Connect))
            .WithMessage("netrom.inp3.enabled requires netrom.connect.");
    }

    private static bool BeValidInp3Options(NetRomInp3Options o)
    {
        try
        {
            o.Validate();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string DescribeInp3Fault(NetRomInp3Options o)
    {
        try
        {
            o.Validate();
            return "ok";
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>Validates the management surfaces (telnet + http binds/ports).</summary>
public sealed class ManagementValidator : AbstractValidator<ManagementConfig>
{
    public ManagementValidator()
    {
        RuleFor(m => m.Telnet.Port).InclusiveBetween(1, 65535).WithMessage("Telnet port must be in 1..65535.");
        RuleFor(m => m.Telnet.Bind).NotEmpty().WithMessage("Telnet bind address is required.");
        RuleFor(m => m.Http.Port).InclusiveBetween(1, 65535).WithMessage("Http port must be in 1..65535.");
        RuleFor(m => m.Http.Bind).NotEmpty().WithMessage("Http bind address is required.");
        RuleFor(m => m)
            .Must(m => !(m.Telnet.Enabled && m.Telnet.Bind == m.Http.Bind && m.Telnet.Port == m.Http.Port))
            .WithMessage("Telnet and Http cannot bind the same address:port.");

        // HTTPS (only validated when enabled).
        RuleFor(m => m.Https.Port).InclusiveBetween(1, 65535)
            .When(m => m.Https.Enabled)
            .WithMessage("Https port must be in 1..65535.");
        RuleFor(m => m.Https.Bind).NotEmpty()
            .When(m => m.Https.Enabled)
            .WithMessage("Https bind address is required.");
        RuleFor(m => m)
            .Must(m => !(m.Https.Bind == m.Http.Bind && m.Https.Port == m.Http.Port))
            .When(m => m.Https.Enabled)
            .WithMessage("Http and Https cannot bind the same address:port.");
        RuleFor(m => m)
            .Must(m => !(m.Telnet.Enabled && m.Telnet.Bind == m.Https.Bind && m.Telnet.Port == m.Https.Port))
            .When(m => m.Https.Enabled)
            .WithMessage("Telnet and Https cannot bind the same address:port.");
        // If self-signed generation is off, an explicit cert path is required.
        RuleFor(m => m.Https.CertificatePath).NotEmpty()
            .When(m => m.Https.Enabled && !m.Https.GenerateSelfSignedOnMissing)
            .WithMessage("management.https.certificatePath is required when generateSelfSignedOnMissing is false.");

        RuleFor(m => m.Auth.AccessTokenMinutes!.Value).GreaterThan(0)
            .When(m => m.Auth.AccessTokenMinutes.HasValue)
            .WithMessage("management.auth.accessTokenMinutes must be positive.");
        RuleFor(m => m.Auth.RefreshTokenMinutes!.Value).GreaterThan(0)
            .When(m => m.Auth.RefreshTokenMinutes.HasValue)
            .WithMessage("management.auth.refreshTokenMinutes must be positive.");
        RuleFor(m => m.Auth.SysopElevationMinutes!.Value).GreaterThan(0)
            .When(m => m.Auth.SysopElevationMinutes.HasValue)
            .WithMessage("management.auth.sysopElevationMinutes must be positive.");
        // A refresh token must outlive the access token it renews — otherwise the
        // silent-renew has nothing to renew with (the refresh token would expire
        // first, forcing a re-login the moment the access token did). Only checked
        // when BOTH are explicitly set; either-default is fine (60 < 10080).
        RuleFor(m => m.Auth)
            .Must(a => !(a.RefreshTokenMinutes.HasValue && a.AccessTokenMinutes.HasValue
                && a.RefreshTokenMinutes.Value <= a.AccessTokenMinutes.Value))
            .WithMessage("management.auth.refreshTokenMinutes must be greater than accessTokenMinutes.");

        // WebAuthn / passkey relying-party config. Validated always (it is inert when
        // no passkey is enrolled, but a bad RP id / origin should still be rejected at
        // config-apply rather than only blowing up the first ceremony).
        RuleFor(m => m.Auth.WebAuthn).NotNull().SetValidator(new WebAuthnConfigValidator());
    }
}

/// <summary>
/// Validates the WebAuthn relying-party config. The RP id must be non-empty and a
/// plausible domain label (never an IP literal — an IP is not a legal RP id, see
/// docs/passkeys-lan-trust-pattern.md §1). Each allowed origin, when given, must be an
/// absolute http/https URL (the exact origin the verifier pins).
/// </summary>
public sealed class WebAuthnConfigValidator : AbstractValidator<WebAuthnConfig>
{
    public WebAuthnConfigValidator()
    {
        RuleFor(w => w.RelyingPartyId)
            .NotEmpty().WithMessage("management.auth.webAuthn.relyingPartyId is required.")
            .Must(BeARegistrableDomainNotAnIp)
            .WithMessage("management.auth.webAuthn.relyingPartyId must be a domain name (e.g. localhost or pdn.lab.example), never an IP address.");

        RuleFor(w => w.RelyingPartyName)
            .NotEmpty().WithMessage("management.auth.webAuthn.relyingPartyName is required.");

        RuleForEach(w => w.AllowedOrigins)
            .Must(BeAnAbsoluteHttpOrigin)
            .WithMessage("each management.auth.webAuthn.allowedOrigins entry must be an absolute http(s) origin (e.g. https://pdn.lab.example:8443).");
    }

    // An RP id must be a registrable domain string — never an IP literal. We reject a
    // value that parses as an IPv4/IPv6 address; everything else (a bare label like
    // "localhost" or a dotted name like "pdn.lab.example") is accepted as a domain.
    private static bool BeARegistrableDomainNotAnIp(string? rpId) =>
        !string.IsNullOrWhiteSpace(rpId) && !System.Net.IPAddress.TryParse(rpId, out _);

    private static bool BeAnAbsoluteHttpOrigin(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
