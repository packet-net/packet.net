using FluentValidation;
using Packet.Core;

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

        // Split-station head-end fleet (docs/research/split-station-rf-headend.md). Empty is the
        // default (a purely-local station). Each entry is validated (id + address), ids are unique
        // (the binding key), and every head-end a port references (radio or nino-tnc-tcp) must be
        // declared here — a dangling reference is a config error, not a silent bring-up degrade.
        RuleForEach(c => c.HeadEnds).SetValidator(new HeadEndConfigValidator());

        RuleFor(c => c.HeadEnds)
            .Must(HaveUniqueHeadEndIds)
            .WithMessage("Each head-end must have a unique id (the (instanceId, deviceId) binding key).");

        RuleFor(c => c)
            .Must(c => UnresolvedHeadEndReferences(c).Count == 0)
            .WithMessage(c =>
                "A port references head-end id(s) not declared in headEnds: " +
                $"{string.Join(", ", UnresolvedHeadEndReferences(c).Select(r => $"'{r}'"))}. " +
                "Add a head-end with that id, or fix the binding.");

        // A head-end device is SINGLE-CLIENT: the bridge admits one connection per pipe, so a second
        // binding of the same (headEndId, deviceId) — whether by another port's transport, another
        // port's radio, or a port's own transport+radio naming one device — would silently queue
        // behind the first at bring-up. Reject the collision at config time instead (#586).
        RuleFor(c => c)
            .Must(c => DuplicateHeadEndDeviceBindings(c).Count == 0)
            .WithMessage(c =>
                "The same head-end device is bound more than once: " +
                $"{string.Join(", ", DuplicateHeadEndDeviceBindings(c).Select(d => $"'{d}'"))}. " +
                "A head-end device is single-client — each (headEndId, deviceId) may back only one " +
                "transport or radio across all ports.");

        RuleFor(c => c.Management).NotNull().SetValidator(new ManagementValidator());

        // Security: the MCP OAuth authorization server mints access tokens, but a token
        // is only ENFORCED when management.auth.enabled is on — the scope gate passes
        // through entirely when auth is off. Enabling the OAuth connector with auth off
        // would stand up a working login/consent/token flow whose tokens are never
        // checked, leaving /mcp and the REST API open to anyone who can reach them.
        // Refuse the combination at config-apply rather than ship a false sense of security.
        RuleFor(c => c)
            .Must(c => !(c.Mcp.Oauth.Enabled && !c.Management.Auth.Enabled))
            .WithMessage("mcp.oauth.enabled requires management.auth.enabled — OAuth tokens are only enforced when management auth is on.");

        RuleFor(c => c.NetRom).NotNull().SetValidator(new NetRomValidator());

        RuleFor(c => c.Beacon).NotNull().SetValidator(new BeaconConfigValidator());

        RuleFor(c => c.Rhp).NotNull().SetValidator(new RhpConfigValidator());

        RuleFor(c => c.Traffic).NotNull().SetValidator(new TrafficConfigValidator());

        RuleFor(c => c.Tailscale).NotNull().SetValidator(new TailscaleConfigValidator());

        RuleFor(c => c.Oarc).NotNull().SetValidator(new OarcConfigValidator());

        RuleFor(c => c.Mqtt).NotNull().SetValidator(new MqttConfigValidator());

        // Empty applications is the default (a node with no apps). Each entry is validated,
        // and ids / match-verbs must be unique across the list (the launch + log keys).
        RuleForEach(c => c.Applications).SetValidator(new ApplicationConfigValidator());

        RuleFor(c => c.Applications)
            .Must(HaveUniqueAppIds)
            .WithMessage("Each application must have a unique Id.")
            .Must(HaveUniqueAppMatches)
            .WithMessage("Two applications may not share the same command verb (case-insensitive).");

        // The apps: package-override list (docs/app-packages.md § Owner state). The validator
        // has no filesystem access, so whether an id matches a discovered package is the
        // catalog's concern (an unmatched entry is tolerated — the package may be installed
        // later); here we only require well-formed, unique ids.
        RuleForEach(c => c.Apps).SetValidator(new AppOverrideConfigValidator());

        RuleFor(c => c.Apps)
            .Must(HaveUniqueOverrideIds)
            .WithMessage("Each apps: entry must have a unique id (the package it applies to).");

        // Packet-identity uniqueness (docs/app-packages.md § Application packet identity): a
        // pinned callsign or a NET/ROM alias must be unique across all apps (inline + package
        // overrides) and must not collide with the node's own callsign / alias. Auto-assigned
        // callsigns can't collide by construction (the resolver probes a free SSID), so only
        // EXPLICIT pins are checked here — and the validator has no filesystem, so package
        // pins/aliases participate via their apps[] override, the inline apps via applications[].
        RuleFor(c => c)
            .Must(HaveUniqueAppCallsigns)
            .WithMessage("Two apps may not pin the same callsign, and an app callsign may not equal the node's own (docs/app-packages.md § Application packet identity).")
            .Must(HaveUniqueAppNetromAliases)
            .WithMessage("Two apps may not advertise the same NET/ROM alias, and an app alias may not equal the node's own (docs/app-packages.md § Application packet identity).");

        // appPackageRoots: null means the standard roots; when the list is present every
        // entry must be a non-empty path (an empty string would silently scan nothing).
        RuleForEach(c => c.AppPackageRoots)
            .Must(root => !string.IsNullOrWhiteSpace(root))
            .WithMessage("appPackageRoots entries must be non-empty paths.");
    }

    private static bool HaveUniqueIds(IReadOnlyList<PortConfig> ports)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ports.All(p => p.Id is not null && seen.Add(p.Id));
    }

    private static bool HaveUniqueHeadEndIds(IReadOnlyList<HeadEndConfig> headEnds)
    {
        // Blank ids are reported by HeadEndConfigValidator; don't double-report them here.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return headEnds.All(h => string.IsNullOrWhiteSpace(h.Id) || seen.Add(h.Id));
    }

    /// <summary>Every head-end id a port references — a head-end-bound <c>radio:</c>, a
    /// <c>nino-tnc-tcp</c> transport, or a head-end-bound <c>tait-transparent</c> transport
    /// (#585) — that is NOT declared in <see cref="NodeConfig.HeadEnds"/>.
    /// Empty ⇒ every reference resolves. Drives both the pass/fail verdict and the error message.</summary>
    private static List<string> UnresolvedHeadEndReferences(NodeConfig c)
    {
        var declared = new HashSet<string>(
            c.HeadEnds.Where(h => !string.IsNullOrWhiteSpace(h.Id)).Select(h => h.Id),
            StringComparer.Ordinal);

        var unresolved = new List<string>();
        foreach (var port in c.Ports)
        {
            if (port.Radio is { IsHeadEndBound: true } radio && !declared.Contains(radio.HeadEndId))
            {
                unresolved.Add(radio.HeadEndId);
            }
            var transportHeadEndId = port.Transport switch
            {
                NinoTncTcpTransport { HeadEndId: var id } => id,
                TaitTransparentTransportConfig { HeadEndId: var id } => id,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(transportHeadEndId) && !declared.Contains(transportHeadEndId))
            {
                unresolved.Add(transportHeadEndId);
            }
        }
        return unresolved;
    }

    /// <summary>Every <c>(headEndId, deviceId)</c> bound more than once across all ports' transports
    /// and head-end-bound radios, as <c>headEndId/deviceId</c> keys. Empty ⇒ every device has one
    /// client. Case-insensitive, matching the transport-endpoint uniqueness rule; incomplete bindings
    /// (a blank half) are skipped — their own validators report those.</summary>
    private static List<string> DuplicateHeadEndDeviceBindings(NodeConfig c)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<string>();
        foreach (var port in c.Ports)
        {
            switch (port.Transport)
            {
                case NinoTncTcpTransport t:
                    Check(t.HeadEndId, t.DeviceId);
                    break;
                case TaitTransparentTransportConfig { IsHeadEndBound: true } tt:
                    Check(tt.HeadEndId, tt.DeviceId);   // #585 — same single-client pipe rule
                    break;
            }
            if (port.Radio is { IsHeadEndBound: true } radio)
            {
                Check(radio.HeadEndId, radio.DeviceId);
            }
        }
        return duplicates;

        void Check(string? headEndId, string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(headEndId) || string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }
            var key = $"{headEndId}/{deviceId}";
            if (!seen.Add(key))
            {
                duplicates.Add(key);
            }
        }
    }

    private static bool HaveUniqueAppIds(IReadOnlyList<ApplicationConfig> apps)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return apps.All(a => a.Id is not null && seen.Add(a.Id));
    }

    private static bool HaveUniqueAppMatches(IReadOnlyList<ApplicationConfig> apps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return apps.All(a => string.IsNullOrWhiteSpace(a.Command) || seen.Add(a.Command.Trim()));
    }

    private static bool HaveUniqueOverrideIds(IReadOnlyList<AppOverrideConfig> apps)
    {
        // Blank ids are reported by AppOverrideConfigValidator; don't double-report them here.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return apps.All(a => string.IsNullOrWhiteSpace(a.Id) || seen.Add(a.Id));
    }

    /// <summary>Every explicitly-pinned app callsign (inline + package overrides), normalised
    /// through the node base, must be unique and must not equal the node's own callsign. An
    /// unparsable pin is left to the catalog/UI to flag (the resolver skips it); a blank pin is
    /// the auto-assign path and never collides.</summary>
    private static bool HaveUniqueAppCallsigns(NodeConfig c)
    {
        if (!Callsign.TryParse(c.Identity.Callsign, out var node))
        {
            return true;   // no usable node identity → IdentityValidator reports it; don't pile on.
        }
        var seen = new HashSet<string>(StringComparer.Ordinal) { node.ToString() };
        var pins = c.Applications.Select(a => a.Callsign)
            .Concat(c.Apps.Select(a => a.Callsign));
        foreach (var pin in pins)
        {
            if (string.IsNullOrWhiteSpace(pin))
            {
                continue;   // auto-assign — can't collide by construction.
            }
            if (!Applications.Packages.AppCallsignResolver.TryResolvePin(pin, node.Base, out var call, out _))
            {
                continue;   // unparsable pin — flagged elsewhere; not a uniqueness verdict.
            }
            if (!seen.Add(call.ToString()))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Every NET/ROM alias advertised for an app (inline + package overrides) must be
    /// unique (case-insensitive) and must not equal the node's own NET/ROM alias.</summary>
    private static bool HaveUniqueAppNetromAliases(NodeConfig c)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(c.Identity.Alias))
        {
            seen.Add(c.Identity.Alias!.Trim());
        }
        var aliases = c.Applications.Select(a => a.Netrom?.Alias)
            .Concat(c.Apps.Select(a => a.Netrom?.Alias));
        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }
            if (!seen.Add(alias.Trim()))
            {
                return false;
            }
        }
        return true;
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

        // The node alias is the single node-name concept (the BPQ NODEALIAS). It is also the alias
        // advertised in the NODES broadcast, whose wire field is 6 octets — so it is capped at 6
        // chars. Optional (null/blank = use the callsign for display + the callsign base on the wire).
        RuleFor(i => i.Alias!)
            .Must(a => a.Trim().Length is >= 1 and <= 6)
            .When(i => !string.IsNullOrWhiteSpace(i.Alias))
            .WithMessage("Identity.Alias must be 1–6 characters (the NET/ROM alias wire field is 6 octets); put longer friendly text in services.banner.");
    }
}
