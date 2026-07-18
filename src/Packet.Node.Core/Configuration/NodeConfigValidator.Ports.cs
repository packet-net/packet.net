using FluentValidation;
using Packet.Core;
using Packet.Radio.Tait;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.Modems;

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
        When(p => p.Transport is NinoTncTcpTransport, () =>
            RuleFor(p => (NinoTncTcpTransport)p.Transport).SetValidator(new NinoTncTcpValidator()));
        When(p => p.Transport is KissTcpTransport, () =>
            RuleFor(p => (KissTcpTransport)p.Transport).SetValidator(new KissTcpValidator()));
        When(p => p.Transport is AxudpTransport, () =>
            RuleFor(p => (AxudpTransport)p.Transport).SetValidator(new AxudpValidator()));
        When(p => p.Transport is AxudpMultipointTransport, () =>
            RuleFor(p => (AxudpMultipointTransport)p.Transport).SetValidator(new AxudpMultipointValidator()));
        When(p => p.Transport is TaitTransparentTransportConfig, () =>
            RuleFor(p => (TaitTransparentTransportConfig)p.Transport).SetValidator(new TaitTransparentValidator()));
        When(p => p.Transport is SoundModemTransportConfig, () =>
            RuleFor(p => (SoundModemTransportConfig)p.Transport).SetValidator(new SoundModemValidator()));

        When(p => p.Ax25 is not null, () =>
            RuleFor(p => p.Ax25!).SetValidator(new Ax25ParamsValidator()));

        // Per-port NET/ROM route quality (BPQ per-port QUALITY): 0..255 — the NET/ROM
        // quality range. A typo'd value is rejected rather than silently clamped,
        // matching the per-port tuning discipline. Null = inherit the global default.
        RuleFor(p => p.NetRomQuality!.Value).InclusiveBetween(0, 255)
            .When(p => p.NetRomQuality.HasValue)
            .WithMessage("Port.netRomQuality must be in 0..255 (the NET/ROM quality range).");

        // Per-port NET/ROM MINQUAL (BPQ per-port MINQUAL): 0..255 — the NET/ROM quality
        // range, same discipline as netRomQuality above. Null = inherit the global default.
        RuleFor(p => p.NetRomMinQuality!.Value).InclusiveBetween(0, 255)
            .When(p => p.NetRomMinQuality.HasValue)
            .WithMessage("Port.netRomMinQuality must be in 0..255 (the NET/ROM quality range).");

        // Per-port NODESPACLEN: the NODES-broadcast UI-frame octet cap. A floor of one whole
        // entry past the header (7 + 21 = 28) — below that no entry fits — up to the AX.25 UI
        // ceiling (256). Out-of-range is rejected, not clamped (the per-port tuning discipline).
        // Null = no cap (today's behaviour).
        RuleFor(p => p.NodesPaclen!.Value).InclusiveBetween(28, 256)
            .When(p => p.NodesPaclen.HasValue)
            .WithMessage("Port.nodesPaclen must be in 28..256 octets (28 = the 7-octet header + one 21-octet entry; 256 = the AX.25 UI-frame ceiling).");

        When(p => p.Beacon is not null, () =>
            RuleFor(p => p.Beacon!).SetValidator(new PortBeaconValidator()));

        When(p => p.Compat is not null, () =>
            RuleFor(p => p.Compat!).SetValidator(new PortCompatValidator()));

        // Optional radio-control attachment. Its own fields are validated by PortRadioValidator;
        // additionally the block must pair with a transport that has a co-located radio:
        //  - a LOCAL radio (port/serial) needs a local serial-modem transport (serial-kiss /
        //    nino-tnc) — its control channel is a second serial cable beside the modem's, which a
        //    kiss-tcp / AXUDP port doesn't physically have.
        //  - a HEAD-END-bound radio needs the co-located full-control NinoTNC on the same head-end
        //    (nino-tnc-tcp) — the modem+radio pair is always on one instance. This lifts the
        //    serial-only rule specifically for head-end ports; a kiss-tcp (LinBPQ) port still can't
        //    carry a cabled radio.
        //  - a RIG-backed radio (kind rig) has no cable at all — it dials the port's rig: daemon —
        //    so it pairs with ANY transport but requires the rig: block (rules inside the arm).
        // A rig (CAT/station-control) attachment has no transport pairing to enforce — rigctld and
        // flrig are always TCP daemons beside any kind of port (an HF kiss-tcp port with an IC-7300
        // behind it is the motivating case), so the block validates standalone.
        When(p => p.Rig is not null, () =>
            RuleFor(p => p.Rig!).SetValidator(new PortRigValidator()));

        When(p => p.Radio is not null, () =>
        {
            RuleFor(p => p.Radio!).SetValidator(new PortRadioValidator());

            // radio: kind rig — the port's rig: (CAT) daemon re-presented as the packet-medium
            // seam over a dedicated second connection. It has no control cable of its own, so
            // NONE of the transport-pairing rules below apply: a rig-backed radio works with any
            // transport (the headline case is a kiss-tcp soundmodem beside rigctld). What it DOES
            // need is the rig: block that says which daemon to dial.
            When(p => RadioKinds.Is(p.Radio!.Kind, RadioKinds.Rig), () =>
                RuleFor(p => p.Rig)
                    .NotNull()
                    .WithMessage(
                        "radio: kind rig requires a rig: block on the same port — it is the " +
                        "rig's DCD/strength/PTT re-presented as the port's radio."));

            When(p => !RadioKinds.Is(p.Radio!.Kind, RadioKinds.Rig), () =>
            {
                // The tait-ccdi (and future cabled-control) pairing rules, exactly as before —
                // the rig arm above must not weaken any of them.
                When(p => !p.Radio!.IsHeadEndBound, () =>
                    RuleFor(p => p.Transport)
                        .Must(t => t is SerialKissTransport or NinoTncTransport)
                        .WithMessage(p =>
                            $"a local radio (port/serial) is only valid on a serial-modem transport " +
                            $"({TransportKinds.SerialKiss}, {TransportKinds.NinoTnc}) — a '{p.Transport?.Kind}' port has no " +
                            "locally-cabled radio control channel."));

                When(p => p.Radio!.IsHeadEndBound, () =>
                {
                    RuleFor(p => p.Transport)
                        .Must(t => t is NinoTncTcpTransport)
                        .WithMessage(p =>
                            $"a head-end-bound radio pairs with a '{TransportKinds.NinoTncTcp}' transport (the co-located " +
                            $"full-control NinoTNC on the same head-end) — not a '{p.Transport?.Kind}' port.");

                    // "Same head-end" means SAME head-end: the transport-TYPE rule above admits a
                    // nino-tnc-tcp transport on instance A beside a radio on instance B, which is a
                    // split pair that can never be co-located hardware. When both halves carry an id,
                    // they must be equal (blank halves are reported by their own completeness rules).
                    RuleFor(p => p)
                        .Must(p => p.Transport is not NinoTncTcpTransport t
                            || string.IsNullOrWhiteSpace(t.HeadEndId)
                            || string.IsNullOrWhiteSpace(p.Radio!.HeadEndId)
                            || string.Equals(t.HeadEndId, p.Radio!.HeadEndId, StringComparison.Ordinal))
                        .WithMessage(p =>
                            $"a head-end-bound radio must live on the SAME head-end as its port's transport " +
                            $"(radio headEndId '{p.Radio!.HeadEndId}' != transport headEndId " +
                            $"'{(p.Transport as NinoTncTcpTransport)?.HeadEndId}') — the modem+radio pair is " +
                            "co-located on one instance.");
                });
            });
        });
    }
}

/// <summary>
/// Validates a per-port radio-control attachment (<see cref="PortRadioConfig"/>):
/// a known <c>kind</c> (a typo'd one would otherwise silently fail at bring-up),
/// the binding-mode discipline for a cabled (non-<c>rig</c>) kind — <b>exactly one</b> of
/// <c>port</c> (the local control-channel device), <c>serial</c> (the local CCDI serial — the
/// stable identity that survives device renumbering), or <c>headEndId</c>+<c>deviceId</c> (a Tait
/// CCDI device on a split-station head-end, both halves required) — or, for kind <c>rig</c>,
/// that NO binding-mode field is set (the rig-backed radio dials the port's <c>rig:</c> daemon
/// instead) — plus a positive baud and a positive health interval when set. Kind knowledge lives
/// in <see cref="RadioKinds"/> (the same authority the radio factory resolves with), not here.
/// </summary>
public sealed class PortRadioValidator : AbstractValidator<PortRadioConfig>
{
    public PortRadioValidator()
    {
        RuleFor(r => r.Kind)
            .Must(RadioKinds.IsKnown)
            .WithMessage(r =>
                $"radio.kind '{r.Kind}' is not a known radio-control kind " +
                $"(expected one of: {string.Join(", ", RadioKinds.Names)}).");

        // Kind rig has no control channel of its own — it dials a SECOND, dedicated connection to
        // the port's rig: daemon — so the binding-mode fields (which describe a Tait CCDI link)
        // are meaningless and must stay unset. (That the rig: block exists is a cross-field rule
        // at the PortConfig level, where the sibling block is visible.)
        When(r => RadioKinds.Is(r.Kind, RadioKinds.Rig), () =>
            RuleFor(r => r)
                .Must(r => !HasNonEmptyPort(r) && !HasNonEmptySerial(r)
                    && !HasNonEmptyHeadEndId(r) && !HasNonEmptyDeviceId(r))
                .WithMessage(
                    "radio: kind rig has no control channel of its own (it dials the port's rig: " +
                    "daemon over a dedicated connection), so `port`, `serial`, `headEndId` and " +
                    "`deviceId` must not be set — they describe a tait-ccdi link."));

        When(r => !RadioKinds.Is(r.Kind, RadioKinds.Rig), () =>
        {
            // Pin the radio by EXACTLY ONE binding mode: the local device path (`port`), the local CCDI
            // serial (`serial`), or a split-station head-end device (`headEndId`+`deviceId`). Several is
            // ambiguous (which wins?); none leaves bring-up nothing to open.
            RuleFor(r => r)
                .Must(ExactlyOneBindingMode)
                .WithMessage(
                    "radio requires exactly one binding mode: `port` (the control-channel device, e.g. " +
                    "/dev/ttyUSB0), `serial` (the CCDI serial — the stable identity that survives device " +
                    "renumbering), or `headEndId`+`deviceId` (a Tait CCDI device on a split-station head-end); " +
                    "set one mode, not several, not none.");

            // A head-end binding needs BOTH halves — the instance id AND the device id on it.
            RuleFor(r => r)
                .Must(r => HasNonEmptyHeadEndId(r) && HasNonEmptyDeviceId(r))
                .When(r => HasNonEmptyHeadEndId(r) || HasNonEmptyDeviceId(r))
                .WithMessage(
                    "a head-end-bound radio requires BOTH `headEndId` (the head-end instance id) and " +
                    "`deviceId` (the device id on it) — set both, not one.");
        });

        RuleFor(r => r.Baud).GreaterThan(0).WithMessage("radio baud must be positive.");

        RuleFor(r => r.HealthIntervalSeconds!.Value).GreaterThan(0)
            .When(r => r.HealthIntervalSeconds.HasValue)
            .WithMessage("radio healthIntervalSeconds must be positive (omit it for the 10 s default).");

        // A resident hail responder must know whom to answer (v1 is point-to-point).
        RuleFor(r => r.HailResponderPeer)
            .Must(peer => peer.Length == TaitSdmSideChannel.IdentityLength)
            .When(r => r.HailResponder)
            .WithMessage(
                $"radio.hailResponderPeer must be exactly {TaitSdmSideChannel.IdentityLength} characters " +
                "(the neighbour's SDM data identity) when hailResponder is enabled.");
    }

    private static bool HasNonEmptyPort(PortRadioConfig r) => !string.IsNullOrWhiteSpace(r.Port);

    private static bool HasNonEmptySerial(PortRadioConfig r) => !string.IsNullOrWhiteSpace(r.Serial);

    private static bool HasNonEmptyHeadEndId(PortRadioConfig r) => !string.IsNullOrWhiteSpace(r.HeadEndId);

    private static bool HasNonEmptyDeviceId(PortRadioConfig r) => !string.IsNullOrWhiteSpace(r.DeviceId);

    // Exactly one of the three binding modes. A partial head-end binding (only one of
    // headEndId/deviceId) still counts as "attempting head-end mode" for exclusivity — the
    // completeness rule above reports the missing half separately, so a lone headEndId gives one
    // clear error, not a confusing "no binding mode".
    private static bool ExactlyOneBindingMode(PortRadioConfig r)
    {
        var modes =
            (HasNonEmptyPort(r) ? 1 : 0) +
            (HasNonEmptySerial(r) ? 1 : 0) +
            (HasNonEmptyHeadEndId(r) || HasNonEmptyDeviceId(r) ? 1 : 0);
        return modes == 1;
    }
}

/// <summary>
/// Validates a port's <c>rig:</c> attachment (<see cref="PortRigConfig"/>): a known kind, the
/// binding-shape discipline — the BYO-daemon shape (<c>host</c>/<c>port</c>, either rig kind) or
/// the node-managed shape (<c>device</c>+<c>model</c>, hamlib only, no <c>port</c>, no remote
/// <c>host</c>) — plus a non-empty host, a sane TCP port when set, and positive poll cadences
/// when set. Kind and shape knowledge live in <see cref="RigKinds"/> /
/// <see cref="PortRigConfig.IsNodeManaged"/> (the same authorities the rig factory and the port
/// supervisor resolve with), not here.
/// </summary>
public sealed class PortRigValidator : AbstractValidator<PortRigConfig>
{
    public PortRigValidator()
    {
        RuleFor(r => r.Kind)
            .Must(RigKinds.IsKnown)
            .WithMessage(r =>
                $"rig.kind '{r.Kind}' is not a known rig-control kind " +
                $"(expected one of: {string.Join(", ", RigKinds.Names)}).");

        RuleFor(r => r.Host)
            .Must(h => !string.IsNullOrWhiteSpace(h))
            .WithMessage("rig.host must not be empty (omit it for the 127.0.0.1 default).");

        RuleFor(r => r.Port!.Value).InclusiveBetween(1, 65535)
            .When(r => r.Port.HasValue)
            .WithMessage(r =>
                $"rig.port must be in 1..65535 (omit it for the kind default, " +
                $"{RigKinds.Hamlib} → {RigKinds.DefaultPort(RigKinds.Hamlib)}, " +
                $"{RigKinds.Flrig} → {RigKinds.DefaultPort(RigKinds.Flrig)}).");

        // The node-managed shape (`device` set → the node spawns and supervises rigctld):
        // `model` is what rigctld -m needs to speak the rig's CAT dialect, so it is required;
        // flrig is a GUI app the node can't sensibly spawn, so the shape is hamlib-only;
        // `port`/a remote `host` describe a daemon somebody ELSE runs, which contradicts a
        // locally-spawned one. (Mirrors PortRadioValidator's binding-mode discipline.)
        When(r => r.IsNodeManaged, () =>
        {
            RuleFor(r => r.Model)
                .NotNull()
                .WithMessage(
                    "a node-managed rig (`device` set) requires `model` — the hamlib rig model " +
                    "number rigctld is launched with (see `rigctl -l` for the list).");

            RuleFor(r => r)
                .Must(r => RigKinds.Is(r.Kind, RigKinds.Hamlib))
                .WithMessage(
                    "a node-managed rig (`device` set) must be kind `hamlib` — the node spawns " +
                    "rigctld itself. flrig is a GUI application the node cannot spawn; run it " +
                    "yourself and bind by `host`/`port` instead.");

            RuleFor(r => r.Port)
                .Null()
                .WithMessage(
                    "a node-managed rig (`device` set) must not set `port` — the node spawns " +
                    "rigctld on a loopback port it allocates itself. Set `host`/`port` (without " +
                    "`device`) to point at a daemon you run yourself.");

            RuleFor(r => r.Host)
                .Must(h => string.IsNullOrWhiteSpace(h) || h == "127.0.0.1")
                .WithMessage(
                    "a node-managed rig (`device` set) must not set a remote `host` — the node " +
                    "spawns rigctld locally on 127.0.0.1. Set `host`/`port` (without `device`) " +
                    "to point at a daemon running elsewhere.");
        });

        RuleFor(r => r.Model!.Value).GreaterThan(0)
            .When(r => r.Model.HasValue)
            .WithMessage("rig.model must be positive (a hamlib rig model number — see `rigctl -l`).");

        RuleFor(r => r.SerialSpeed!.Value).GreaterThan(0)
            .When(r => r.SerialSpeed.HasValue)
            .WithMessage("rig.serialSpeed must be positive (baud; omit it for hamlib's per-model default).");

        // `model`/`serialSpeed` describe the daemon the node spawns; without `device` there is
        // no spawned daemon for them to describe — a BYO daemon already knows its own rig.
        When(r => !r.IsNodeManaged, () =>
        {
            RuleFor(r => r.Model)
                .Null()
                .WithMessage(
                    "rig.model only applies to a node-managed rig — set `device` (the rig's " +
                    "serial device path) with it, or drop it for the `host`/`port` shape (your " +
                    "own daemon already knows its rig).");

            RuleFor(r => r.SerialSpeed)
                .Null()
                .WithMessage(
                    "rig.serialSpeed only applies to a node-managed rig — set `device` (the " +
                    "rig's serial device path) with it, or drop it for the `host`/`port` shape.");
        });

        RuleFor(r => r.PollIntervalSeconds!.Value).GreaterThan(0)
            .When(r => r.PollIntervalSeconds.HasValue)
            .WithMessage("rig.pollIntervalSeconds must be positive (omit it for the 5 s default).");

        RuleFor(r => r.MeterIntervalSeconds!.Value).GreaterThan(0)
            .When(r => r.MeterIntervalSeconds.HasValue)
            .WithMessage("rig.meterIntervalSeconds must be positive (omit it for the 1 s default).");
    }
}

/// <summary>
/// Validates a <see cref="NinoTncTcpTransport"/> — the full-control NinoTNC over a split-station
/// head-end: a non-empty head-end id + device id (the <c>(headEndId, deviceId)</c> binding resolved
/// at bring-up) and the 0..15 mode range (mirroring the local <see cref="NinoTncValidator"/>). That
/// the referenced head-end id actually exists is a whole-config check in
/// <see cref="NodeConfigValidator"/> (it needs the head-ends list).
/// </summary>
public sealed class NinoTncTcpValidator : AbstractValidator<NinoTncTcpTransport>
{
    public NinoTncTcpValidator()
    {
        RuleFor(t => t.HeadEndId).NotEmpty()
            .WithMessage("nino-tnc-tcp transport requires a headEndId (the head-end instance id).");
        RuleFor(t => t.DeviceId).NotEmpty()
            .WithMessage("nino-tnc-tcp transport requires a deviceId (the NinoTNC device id on the head-end).");
        RuleFor(t => t.Mode).InclusiveBetween(0, 15)
            .WithMessage("nino-tnc-tcp mode must be in 0..15.");
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

/// <summary>
/// Validates a <see cref="TaitTransparentTransportConfig"/>: exactly one binding mode —
/// <c>device</c> (local path), <c>serial</c> (local CCDI serial), or <c>headEndId</c>+<c>deviceId</c>
/// (a split-station head-end device, both halves required, #585) — the two bauds and the FFSK
/// baud are positive, and the lead-in is non-negative. Mirrors <see cref="PortRadioValidator"/>'s
/// binding-mode discipline. That a referenced head-end id is actually declared is a whole-config
/// check in <see cref="NodeConfigValidator"/> (it needs the head-ends list).
/// </summary>
public sealed class TaitTransparentValidator : AbstractValidator<TaitTransparentTransportConfig>
{
    public TaitTransparentValidator()
    {
        // Pin the radio by EXACTLY ONE binding mode: the local device path (`device`), the local
        // CCDI serial (`serial`), or a split-station head-end device (`headEndId`+`deviceId`).
        // Several is ambiguous (which wins?); none leaves bring-up nothing to open.
        RuleFor(t => t)
            .Must(ExactlyOneBindingMode)
            .WithMessage(
                "tait-transparent transport requires exactly one binding mode: `device` (the radio's " +
                "serial device path), `serial` (the CCDI serial — the stable identity that survives " +
                "device renumbering), or `headEndId`+`deviceId` (the radio's serial port on a " +
                "split-station head-end); set one mode, not several, not none.");

        // A head-end binding needs BOTH halves — the instance id AND the device id on it.
        RuleFor(t => t)
            .Must(t => HasNonEmptyHeadEndId(t) && HasNonEmptyDeviceId(t))
            .When(t => HasNonEmptyHeadEndId(t) || HasNonEmptyDeviceId(t))
            .WithMessage(
                "a head-end-bound tait-transparent transport requires BOTH `headEndId` (the head-end " +
                "instance id) and `deviceId` (the device id on it) — set both, not one.");

        RuleFor(t => t.Baud).GreaterThan(0).WithMessage("tait-transparent baud must be positive.");
        RuleFor(t => t.TransparentBaud).GreaterThan(0).WithMessage("tait-transparent transparentBaud must be positive.");
        RuleFor(t => t.FfskBaud).GreaterThan(0).WithMessage("tait-transparent ffskBaud must be positive.");
        RuleFor(t => t.LeadInMs).GreaterThanOrEqualTo(0).WithMessage("tait-transparent leadInMs must be non-negative.");
    }

    private static bool HasNonEmptyDevice(TaitTransparentTransportConfig t) => !string.IsNullOrWhiteSpace(t.Device);

    private static bool HasNonEmptySerial(TaitTransparentTransportConfig t) => !string.IsNullOrWhiteSpace(t.Serial);

    private static bool HasNonEmptyHeadEndId(TaitTransparentTransportConfig t) => !string.IsNullOrWhiteSpace(t.HeadEndId);

    private static bool HasNonEmptyDeviceId(TaitTransparentTransportConfig t) => !string.IsNullOrWhiteSpace(t.DeviceId);

    // Exactly one of the three binding modes. A partial head-end binding (only one of
    // headEndId/deviceId) still counts as "attempting head-end mode" for exclusivity — the
    // completeness rule above reports the missing half separately, so a lone headEndId gives one
    // clear error, not a confusing "no binding mode". Mirrors PortRadioValidator.
    private static bool ExactlyOneBindingMode(TaitTransparentTransportConfig t)
    {
        var modes =
            (HasNonEmptyDevice(t) ? 1 : 0) +
            (HasNonEmptySerial(t) ? 1 : 0) +
            (HasNonEmptyHeadEndId(t) || HasNonEmptyDeviceId(t) ? 1 : 0);
        return modes == 1;
    }
}

/// <summary>
/// Validates an <see cref="AxudpMultipointTransport"/> (the BPQ <c>BPQAXIP</c> analog):
/// the local bind port, each peer's callsign / host / port, and that no two peers share
/// a callsign (the routing key must be unique — a duplicate would make routing ambiguous).
/// </summary>
public sealed class AxudpMultipointValidator : AbstractValidator<AxudpMultipointTransport>
{
    public AxudpMultipointValidator()
    {
        // The one shared socket must bind a real port (no ephemeral 0: partners MAP back
        // to a fixed port, so a non-deterministic bind would be unreachable).
        RuleFor(t => t.LocalPort).InclusiveBetween(1, 65535)
            .WithMessage("axudp-multipoint localPort must be in 1..65535 (the fixed port partners MAP back to).");

        RuleForEach(t => t.Peers).SetValidator(new AxudpPeerValidator());

        // No two peers may share a callsign — the callsign is the outbound routing key, so a
        // duplicate is an ambiguous MAP. Compared on the parsed Callsign (so "G7XYZ-0" and
        // "G7XYZ" collide), skipping peers whose callsign doesn't parse (the per-peer rule
        // already reports those).
        RuleFor(t => t.Peers)
            .Must(NoDuplicateCallsigns)
            .WithMessage("axudp-multipoint peers must have unique callsigns (the callsign is the routing key).");
    }

    private static bool NoDuplicateCallsigns(IReadOnlyList<AxudpPeerConfig> peers)
    {
        var seen = new HashSet<Callsign>();
        foreach (var peer in peers)
        {
            if (Callsign.TryParse(peer.Call, out var call) && !seen.Add(call))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>Validates one <see cref="AxudpPeerConfig"/> (a BPQ <c>MAP</c> line).</summary>
public sealed class AxudpPeerValidator : AbstractValidator<AxudpPeerConfig>
{
    public AxudpPeerValidator()
    {
        RuleFor(p => p.Call)
            .Must(c => Callsign.TryParse(c, out _))
            .WithMessage(p => $"axudp-multipoint peer call '{p.Call}' is not a valid callsign.");
        RuleFor(p => p.Host).NotEmpty().WithMessage("axudp-multipoint peer requires a host.");
        RuleFor(p => p.Port).InclusiveBetween(1, 65535).WithMessage("axudp-multipoint peer port must be in 1..65535.");
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

/// <summary>
/// Validates a <see cref="SoundModemTransportConfig"/>: a known modem mode, a capture
/// rate the modem's decimator can divide down to the mode's DSP rate, an in-passband
/// centre frequency where one applies, and a parseable PTT spec.
/// </summary>
public sealed class SoundModemValidator : AbstractValidator<SoundModemTransportConfig>
{
    // The exposed mode set is the shared catalogue's, minus bpsk1200-multi: the 1200-baud
    // diversity bank is not exposed (no over-the-air evidence yet — bpsk1200 stays the legacy
    // single-carrier modem). Sourcing this from ModemCatalog keeps it from drifting again.
    private static readonly string[] KnownModes =
        ModemCatalog.KnownModes.Where(m => m != "bpsk1200-multi").ToArray();

    public SoundModemValidator()
    {
        RuleFor(t => t.Device)
            .NotEmpty()
            .WithMessage("soundmodem transport requires `device` (an ALSA device such as `default` or `plughw:1,0`, or a `flex:<radio>[:slice][@station]` FlexRadio device).");

        RuleFor(t => t.Mode)
            .Must(mode => KnownModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            .WithMessage(t =>
                $"soundmodem `mode` '{t.Mode}' is not one of: {string.Join(", ", KnownModes)}.");

        // The RX pipeline captures at CaptureRate and decimates by an integer factor to the
        // mode's DSP rate: 48000 for the 9600 baseband modes, 12000 for everything else.
        // A flex: device supplies its own DAX sample clock, so captureRate does not apply.
        RuleFor(t => t)
            .Must(t => t.CaptureRate > 0 && t.CaptureRate % DspRate(t.Mode) == 0)
            .When(t => KnownModes.Contains(t.Mode, StringComparer.OrdinalIgnoreCase)
                && !FlexDevice.IsFlex(t.Device))
            .WithMessage(t =>
                $"soundmodem `captureRate` {t.CaptureRate} must be a positive multiple of {DspRate(t.Mode)} " +
                $"(the DSP rate for mode '{t.Mode}'); 48000 works for every mode.");

        // A centre frequency is settable only on the variable-centre families (afsk/bpsk/qpsk);
        // there it must leave room for the mode's occupied bandwidth inside the audio passband.
        RuleFor(t => t.Frequency)
            .Must(f => f == 0 || (f >= 300 && f <= 3300))
            .When(t => ModemCatalog.AcceptsCentreFrequency(t.Mode.ToLowerInvariant()))
            .WithMessage("soundmodem `frequency` must be 0 (mode default) or 300–3300 Hz (the audio passband).");

        // The baseband fsk*/c4fsk* and the fixed-centre freedv-*/ms110d- modes have no settable
        // carrier — reject a non-zero frequency rather than silently ignoring it (matches the
        // daemon and ModemCatalog.Create, which throws).
        RuleFor(t => t.Frequency)
            .Must(f => f == 0)
            .When(t => KnownModes.Contains(t.Mode, StringComparer.OrdinalIgnoreCase)
                && !ModemCatalog.AcceptsCentreFrequency(t.Mode.ToLowerInvariant()))
            .WithMessage(t =>
                $"soundmodem `frequency` is not settable for mode '{t.Mode}' (a fixed-centre baseband/OFDM mode) — use 0.");

        // Diversity-bank knobs (bpsk300): range-checked here; ignored by non-bank modes.
        RuleFor(t => t.OffsetPairs)
            .Must(p => p is null or >= 0)
            .WithMessage("soundmodem `offsetPairs` must be >= 0 (0 = a single modem; null = the mode default).");

        RuleFor(t => t.OffsetStepHz)
            .Must(s => s is null or > 0)
            .WithMessage("soundmodem `offsetStepHz` must be > 0 Hz (null = the mode default).");

        RuleFor(t => t.PskDetector)
            .Must(d => d is null
                || d.Equals("coherent", StringComparison.OrdinalIgnoreCase)
                || d.Equals("differential", StringComparison.OrdinalIgnoreCase))
            .WithMessage("soundmodem `pskDetector` must be `coherent` or `differential` (null = the per-family default).");

        RuleFor(t => t.Ptt)
            .Must(ValidPttSpec)
            .WithMessage(
                "soundmodem `ptt` must be empty (VOX), `serial:<device>[:rts|:dtr]`, or `cm108:<hidraw>[:gpio]`.");

        // A flex: device keys itself over CAT — a configured PTT would be ignored, so reject it.
        RuleFor(t => t.Ptt)
            .Must(string.IsNullOrEmpty)
            .When(t => FlexDevice.IsFlex(t.Device))
            .WithMessage("soundmodem `ptt` must be empty for a `flex:` device — the radio keys itself.");
    }

    private static int DspRate(string mode) => ModemCatalog.DspRateFor(mode.ToLowerInvariant());

    private static bool ValidPttSpec(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return true;
        }

        string[] parts = spec.Split(':');
        return parts switch
        {
            ["serial", var device] => !string.IsNullOrWhiteSpace(device),
            ["serial", var device, "rts" or "dtr"] => !string.IsNullOrWhiteSpace(device),
            ["cm108", var device] => !string.IsNullOrWhiteSpace(device),
            ["cm108", var device, var gpio] =>
                !string.IsNullOrWhiteSpace(device) && int.TryParse(gpio, out int pin) && pin is >= 1 and <= 8,
            _ => false,
        };
    }
}
