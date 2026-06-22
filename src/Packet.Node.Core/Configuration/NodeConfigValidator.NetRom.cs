using FluentValidation;
using Packet.NetRom.Wire;

namespace Packet.Node.Core.Configuration;

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
        // A routing role that opens interlinks (endpoint/transit — resolved from the new
        // routing knob or the legacy connect/forward keys) requires the service enabled.
        RuleFor(c => c)
            .Must(c => !(c.EffectiveRouting != NetRomRouting.None && !c.Enabled))
            .WithMessage("netrom.routing (endpoint/transit) requires netrom.enabled.");

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

        // inp3.enabled requires an interlink-opening routing role — INP3 rides on the
        // connected-mode interlink machinery (L3RTT / RIF are 0xCF I-frames on the same
        // sessions L4 uses), so the host constructs the overlay only when interlinks are
        // enabled (routing endpoint/transit, resolved from the new knob or the legacy
        // connect/forward keys). Without this rule, inp3.enabled + routing:none would pass
        // validation and then silently no-op (the overlay never built) — reject it
        // explicitly rather than accept-then-ignore (the named-flag discipline).
        RuleFor(c => c)
            .Must(c => !(c.Inp3.Enabled && c.EffectiveRouting == NetRomRouting.None))
            .WithMessage("netrom.inp3.enabled requires netrom.routing: endpoint or transit.");
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
