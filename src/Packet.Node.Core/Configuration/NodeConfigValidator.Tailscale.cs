using FluentValidation;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// Validates the embedded-Tailscale (<c>tsnet</c> sidecar) block
/// (<see cref="TailscaleConfig"/>). The shape constraints (a legal hostname, a
/// parseable <c>host:port</c> target, a non-empty state dir) apply <b>only when
/// enabled</b> — a disabled block is inert and unconstrained, matching the
/// default-off discipline of the other opt-in surfaces. The mutually-exclusive
/// auth-key check is enforced <em>always</em>: a block carrying both an inline
/// <c>authKey</c> and an <c>authKeyFile</c> is ambiguous regardless of the enable
/// flag, so it is rejected even when disabled (so toggling it on later can't suddenly
/// be ambiguous). Supplying neither is legal — first join then falls to interactive
/// login. A <c>funnel: true</c> with <c>enabled: false</c> is a deliberate
/// <b>no-op</b> (not an error): funnel is inert until the sidecar runs, so we keep the
/// validation total rather than reject an otherwise-fine disabled block.
/// </summary>
public sealed class TailscaleConfigValidator : AbstractValidator<TailscaleConfig>
{
    public TailscaleConfigValidator()
    {
        // Empty is allowed — it derives <callsign>-pdn (TailscaleHostname.Resolve). Only a
        // non-empty value is constrained to the legal label charset.
        RuleFor(t => t.Hostname)
            .Must(BeEmptyOrLegalTailnetHostname)
            .WithMessage("tailscale.hostname, when set, must match ^[a-z0-9-]+$ (lowercase letters, digits, hyphens) — leave it empty to derive <callsign>-pdn.")
            .When(t => t.Enabled);

        RuleFor(t => t.Target)
            .Must(BeAHostPort)
            .WithMessage(t => $"tailscale.target '{t.Target}' must be a host:port (e.g. 127.0.0.1:8080).")
            .When(t => t.Enabled);

        RuleFor(t => t.StateDir)
            .NotEmpty().WithMessage("tailscale.stateDir is required when tailscale is enabled (it persists the node identity / cert across restarts).")
            .When(t => t.Enabled);

        // Mutually exclusive — enforced ALWAYS (a both-set block is ambiguous whether or
        // not it is currently enabled). Either, or neither (neither → interactive login).
        RuleFor(t => t)
            .Must(t => !(HasValue(t.AuthKey) && HasValue(t.AuthKeyFile)))
            .WithMessage("tailscale.authKey and tailscale.authKeyFile must not both be set — supply one (authKeyFile preferred), or neither for interactive login.");
    }

    private static bool HasValue(string? s) => !string.IsNullOrWhiteSpace(s);

    // A tailnet hostname label: lowercase letters, digits, and hyphens (the safe subset
    // that maps to <hostname>.<tailnet>.ts.net). Empty is legal here — it means "derive
    // <callsign>-pdn" (TailscaleHostname.Resolve), so only a non-empty value is checked.
    private static bool BeEmptyOrLegalTailnetHostname(string? hostname) =>
        string.IsNullOrEmpty(hostname)
        || System.Text.RegularExpressions.Regex.IsMatch(hostname, "^[a-z0-9-]+$");

    // The proxy target is host:port — a non-empty host and a port in 1..65535. We split on
    // the LAST colon so an IPv6 literal target (rare, but legal) still parses its port.
    private static bool BeAHostPort(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }
        var lastColon = target.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == target.Length - 1)
        {
            return false;
        }
        var host = target[..lastColon];
        var portText = target[(lastColon + 1)..];
        return !string.IsNullOrWhiteSpace(host)
            && int.TryParse(portText, out var port)
            && port is >= 1 and <= 65535;
    }
}
