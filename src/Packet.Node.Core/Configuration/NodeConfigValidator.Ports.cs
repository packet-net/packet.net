using FluentValidation;

namespace Packet.Node.Core.Configuration;

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

        // Per-port NET/ROM route quality (BPQ per-port QUALITY): 0..255 — the NET/ROM
        // quality range. A typo'd value is rejected rather than silently clamped,
        // matching the per-port tuning discipline. Null = inherit the global default.
        RuleFor(p => p.NetRomQuality!.Value).InclusiveBetween(0, 255)
            .When(p => p.NetRomQuality.HasValue)
            .WithMessage("Port.netRomQuality must be in 0..255 (the NET/ROM quality range).");

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
        // T2Ms = 0 is meaningful: it disables the §6.7.1.2 acknowledge delay,
        // restoring ack-per-frame (one RR per received I-frame).
        RuleFor(p => p.T2Ms!.Value).GreaterThanOrEqualTo(0).When(p => p.T2Ms.HasValue)
            .WithMessage("T2Ms must be >= 0 (0 disables the T2 acknowledge delay - ack per frame).");
        RuleFor(p => p.T3Ms!.Value).GreaterThan(0).When(p => p.T3Ms.HasValue).WithMessage("T3Ms must be positive.");
        RuleFor(p => p.N2!.Value).GreaterThan(0).When(p => p.N2.HasValue).WithMessage("N2 must be positive.");
        RuleFor(p => p.WindowSize!.Value).InclusiveBetween(1, 127).When(p => p.WindowSize.HasValue)
            .WithMessage("WindowSize (k) must be in 1..127.");
        // N1 / PACLEN: a sensible floor (>= 16 — below that an I-frame is almost all
        // header, and the segmenter needs room for a payload) up to the AX.25 v2.2
        // ceiling (256 octets — the XID-default N1 and the largest the spec negotiates).
        // An HF port wanting ~80 sits comfortably in range. Out-of-range is rejected,
        // not clamped, matching the per-port tuning discipline.
        RuleFor(p => p.N1!.Value).InclusiveBetween(16, 256).When(p => p.N1.HasValue)
            .WithMessage("N1 (PACLEN) must be in 16..256 octets (the AX.25 v2.2 max info-field length).");
        RuleFor(p => p.MaxCachedPeers!.Value).GreaterThan(0).When(p => p.MaxCachedPeers.HasValue)
            .WithMessage("MaxCachedPeers must be positive.");
    }
}
