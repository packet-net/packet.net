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
/// (an absolute http(s) base URL, positive intervals) are checked <b>always</b> - even when
/// disabled - so a disabled-but-edited block can't hold junk that would 500 the day it is enabled.
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

/// <summary>
/// Validates one port's UK packet map details (<see cref="PortM0lteMapConfig"/>): a link type
/// from the collector's set, a positive frequency, non-negative rates, and short text fields.
/// Checked whether or not map reporting is on, for the same reason as the node block.
/// </summary>
public sealed class PortM0lteMapValidator : AbstractValidator<PortM0lteMapConfig>
{
    /// <summary>The longest modulation / usage text accepted.</summary>
    public const int MaxShortTextLength = 32;

    /// <summary>The longest port comment accepted (LinBPQ's port <c>ID=</c> is 30 characters;
    /// this leaves room without letting a paragraph through).</summary>
    public const int MaxCommentLength = 80;

    public PortM0lteMapValidator()
    {
        RuleFor(m => m.LinkType!.Value).IsInEnum()
            .When(m => m.LinkType.HasValue)
            .WithMessage(m =>
                $"m0lteMap.linkType '{(int)m.LinkType!.Value}' is not a known link type " +
                $"(expected one of: {string.Join(", ", Enum.GetNames<M0lteMapLinkType>())} - or omit it to derive it from the transport).");

        // Above 0 and below 300 GHz: catches a value typed in Hz or kHz by mistake.
        RuleFor(m => m.FrequencyMhz!.Value)
            .Must(mhz => mhz is > 0m and < 300_000m)
            .When(m => m.FrequencyMhz.HasValue)
            .WithMessage("m0lteMap.frequencyMhz must be a frequency in MHz, e.g. 144.9375 (omit it if the port has none).");

        RuleFor(m => m.Baud!.Value).GreaterThanOrEqualTo(0)
            .When(m => m.Baud.HasValue)
            .WithMessage("m0lteMap.baud must not be negative.");

        RuleFor(m => m.Bitrate!.Value).GreaterThanOrEqualTo(0)
            .When(m => m.Bitrate.HasValue)
            .WithMessage("m0lteMap.bitrate must not be negative.");

        RuleFor(m => m.Modulation!).MaximumLength(MaxShortTextLength)
            .When(m => m.Modulation is not null)
            .WithMessage($"m0lteMap.modulation must be at most {MaxShortTextLength} characters.");

        RuleFor(m => m.Usage!).MaximumLength(MaxShortTextLength)
            .When(m => m.Usage is not null)
            .WithMessage($"m0lteMap.usage must be at most {MaxShortTextLength} characters.");

        RuleFor(m => m.Comment!).MaximumLength(MaxCommentLength)
            .When(m => m.Comment is not null)
            .WithMessage($"m0lteMap.comment must be at most {MaxCommentLength} characters.");
    }
}

/// <summary>
/// Validates the kissproxy MQTT emission block (<see cref="MqttConfig"/>). The shape constraints
/// (a valid broker port, a QoS in 0..2) are checked <b>always</b> - even when disabled - so a
/// disabled-but-edited block can't hold junk that would fail the day it is enabled. The one
/// enabled-only rule is the broker host: an enabled emitter with no host has nowhere to publish.
/// (<see cref="MqttConfig.Password"/> is a secret carried here but never validated for content;
/// it belongs in the git-ignored <c>appsettings.Local.json</c>.)
/// </summary>
public sealed class MqttConfigValidator : AbstractValidator<MqttConfig>
{
    public MqttConfigValidator()
    {
        RuleFor(m => m.BrokerPort)
            .InclusiveBetween(1, 65535)
            .WithMessage("mqtt.brokerPort must be in 1..65535.");

        RuleFor(m => m.Qos)
            .InclusiveBetween(0, 2)
            .WithMessage("mqtt.qos must be 0 (at-most-once), 1 (at-least-once), or 2 (exactly-once).");

        RuleFor(m => m.BrokerHost)
            .NotEmpty()
            .When(m => m.Enabled)
            .WithMessage("mqtt.brokerHost is required when mqtt is enabled (the broker to publish to).");
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
