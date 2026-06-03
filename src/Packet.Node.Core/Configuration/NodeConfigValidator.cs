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

        RuleFor(c => c.Management).NotNull().SetValidator(new ManagementValidator());
    }

    private static bool HaveUniqueIds(IReadOnlyList<PortConfig> ports)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ports.All(p => p.Id is not null && seen.Add(p.Id));
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

        // Discriminated-union dispatch: validate the concrete transport arm.
        // A `kind:` the deserialiser didn't recognise never reaches here — it
        // fails at parse time — so every value is one of the known subtypes.
        When(p => p.Transport is SerialKissTransport, () =>
            RuleFor(p => (SerialKissTransport)p.Transport).SetValidator(new SerialKissValidator()));
        When(p => p.Transport is NinoTncTransport, () =>
            RuleFor(p => (NinoTncTransport)p.Transport).SetValidator(new NinoTncValidator()));
        When(p => p.Transport is KissTcpTransport, () =>
            RuleFor(p => (KissTcpTransport)p.Transport).SetValidator(new KissTcpValidator()));

        When(p => p.Ax25 is not null, () =>
            RuleFor(p => p.Ax25!).SetValidator(new Ax25ParamsValidator()));
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
    }
}
