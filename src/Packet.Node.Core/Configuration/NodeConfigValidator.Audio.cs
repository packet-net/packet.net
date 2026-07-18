using FluentValidation;
using Packet.SoundModem.FlexRadio;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// Validates the POCSAG paging service block. Shape constraints (port range, baud, capture rate)
/// are checked always so a disabled-but-edited block can't hold junk that detonates on enable; the
/// need-a-real-value rules (bind, device) apply only when the service is enabled.
/// </summary>
public sealed class PagingConfigValidator : AbstractValidator<PagingConfig>
{
    private static readonly int[] Bauds = [512, 1200, 2400];

    public PagingConfigValidator()
    {
        RuleFor(p => p.Port)
            .InclusiveBetween(1, 65535)
            .WithMessage("paging.port must be in 1..65535.");

        RuleFor(p => p.Baud)
            .Must(b => Bauds.Contains(b))
            .WithMessage("paging.baud must be 512, 1200 or 2400.");

        // captureRate applies to ALSA devices only; a flex: device supplies its own DAX clock.
        RuleFor(p => p.CaptureRate)
            .Must(r => r > 0 && r % 12000 == 0)
            .When(p => !FlexDevice.IsFlex(p.Device))
            .WithMessage("paging.captureRate must be a positive multiple of 12000 (the paging DSP rate).");

        RuleFor(p => p.Ptt)
            .Must(string.IsNullOrEmpty)
            .When(p => FlexDevice.IsFlex(p.Device))
            .WithMessage("paging.ptt must be empty for a flex: device — the radio keys itself.");

        RuleFor(p => p.Device)
            .NotEmpty()
            .When(p => p.Enabled)
            .WithMessage("paging.device is required when paging is enabled.");

        RuleFor(p => p.Bind)
            .Must(b => System.Net.IPAddress.TryParse(b, out _))
            .When(p => p.Enabled)
            .WithMessage(p => $"paging.bind '{p.Bind}' must be an IP address when paging is enabled.");
    }
}
