using FluentValidation;

namespace Packet.Node.Core.Configuration;

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
