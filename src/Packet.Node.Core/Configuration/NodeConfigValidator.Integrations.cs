using FluentValidation;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// Validates the traffic-log block (<see cref="TrafficConfig"/>). The retention +
/// size bounds must be sane <em>always</em> (not just when enabled) so a
/// disabled-but-edited block can't hold junk that detonates on re-enable; a
/// <c>path:</c> that is set must be a non-blank path (null = the default beside
/// <c>pdn.db</c>).
/// </summary>
public sealed class TrafficConfigValidator : AbstractValidator<TrafficConfig>
{
    public TrafficConfigValidator()
    {
        RuleFor(t => t.RetentionDays).GreaterThanOrEqualTo(1)
            .WithMessage("traffic.retentionDays must be at least 1.");
        RuleFor(t => t.MaxMb).GreaterThanOrEqualTo(1)
            .WithMessage("traffic.maxMb must be at least 1.");
        RuleFor(t => t.Path!)
            .Must(p => !string.IsNullOrWhiteSpace(p))
            .When(t => t.Path is not null)
            .WithMessage("traffic.path must be a non-empty path when set (omit it for the default beside pdn.db).");
    }
}

/// <summary>
/// Validates the OARC network-map reporting block (<see cref="OarcConfig"/>). The shape constraints
/// (an absolute http(s) base URL, positive intervals) are checked <b>always</b> — even when
/// disabled — so a disabled-but-edited block can't hold junk that would 500 the day it is enabled.
/// The locator precondition (a node can't report without a valid Maidenhead grid) is a runtime
/// concern surfaced by the reporter + UI, not a hard config error: a node may legitimately enable
/// reporting before its grid is set.
/// </summary>
public sealed class OarcConfigValidator : AbstractValidator<OarcConfig>
{
    public OarcConfigValidator()
    {
        RuleFor(o => o.BaseUrl)
            .NotEmpty().WithMessage("oarc.baseUrl is required (the collector URL).")
            .Must(BeAbsoluteHttpUrl)
            .WithMessage("oarc.baseUrl must be an absolute http(s) URL, e.g. https://node-api.packet.oarc.uk/.");

        RuleFor(o => o.StatusIntervalSecs).GreaterThan(0)
            .WithMessage("oarc.statusIntervalSecs must be greater than 0.");

        RuleFor(o => o.SessionStatusIntervalSecs).GreaterThan(0)
            .WithMessage("oarc.sessionStatusIntervalSecs must be greater than 0.");
    }

    private static bool BeAbsoluteHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
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

        RuleFor(r => r.MaxConnections)
            .GreaterThan(0)
            .WithMessage("rhp.maxConnections must be greater than 0.");

        RuleFor(r => r.MaxHandlesPerClient)
            .GreaterThan(0)
            .WithMessage("rhp.maxHandlesPerClient must be greater than 0.");

        RuleFor(r => r.InFrameTimeoutSeconds)
            .GreaterThanOrEqualTo(0)
            .WithMessage("rhp.inFrameTimeoutSeconds must be 0 (disabled) or greater.");
    }
}
