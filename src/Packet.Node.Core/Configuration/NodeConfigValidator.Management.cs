using FluentValidation;

namespace Packet.Node.Core.Configuration;

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

        // HTTPS (only validated when enabled).
        RuleFor(m => m.Https.Port).InclusiveBetween(1, 65535)
            .When(m => m.Https.Enabled)
            .WithMessage("Https port must be in 1..65535.");
        RuleFor(m => m.Https.Bind).NotEmpty()
            .When(m => m.Https.Enabled)
            .WithMessage("Https bind address is required.");
        RuleFor(m => m)
            .Must(m => !(m.Https.Bind == m.Http.Bind && m.Https.Port == m.Http.Port))
            .When(m => m.Https.Enabled)
            .WithMessage("Http and Https cannot bind the same address:port.");
        RuleFor(m => m)
            .Must(m => !(m.Telnet.Enabled && m.Telnet.Bind == m.Https.Bind && m.Telnet.Port == m.Https.Port))
            .When(m => m.Https.Enabled)
            .WithMessage("Telnet and Https cannot bind the same address:port.");
        // If self-signed generation is off, an explicit cert path is required.
        RuleFor(m => m.Https.CertificatePath).NotEmpty()
            .When(m => m.Https.Enabled && !m.Https.GenerateSelfSignedOnMissing)
            .WithMessage("management.https.certificatePath is required when generateSelfSignedOnMissing is false.");

        RuleFor(m => m.Auth.AccessTokenMinutes!.Value).GreaterThan(0)
            .When(m => m.Auth.AccessTokenMinutes.HasValue)
            .WithMessage("management.auth.accessTokenMinutes must be positive.");
        RuleFor(m => m.Auth.RefreshTokenMinutes!.Value).GreaterThan(0)
            .When(m => m.Auth.RefreshTokenMinutes.HasValue)
            .WithMessage("management.auth.refreshTokenMinutes must be positive.");
        RuleFor(m => m.Auth.SysopElevationMinutes!.Value).GreaterThan(0)
            .When(m => m.Auth.SysopElevationMinutes.HasValue)
            .WithMessage("management.auth.sysopElevationMinutes must be positive.");
        // A refresh token must outlive the access token it renews — otherwise the
        // silent-renew has nothing to renew with (the refresh token would expire
        // first, forcing a re-login the moment the access token did). Only checked
        // when BOTH are explicitly set; either-default is fine (60 < 10080).
        RuleFor(m => m.Auth)
            .Must(a => !(a.RefreshTokenMinutes.HasValue && a.AccessTokenMinutes.HasValue
                && a.RefreshTokenMinutes.Value <= a.AccessTokenMinutes.Value))
            .WithMessage("management.auth.refreshTokenMinutes must be greater than accessTokenMinutes.");

        // WebAuthn / passkey relying-party config. Validated always (it is inert when
        // no passkey is enrolled, but a bad RP id / origin should still be rejected at
        // config-apply rather than only blowing up the first ceremony).
        RuleFor(m => m.Auth.WebAuthn).NotNull().SetValidator(new WebAuthnConfigValidator());

        // mDNS / DNS-SD advertisement. A set InstanceName becomes a single DNS-SD service
        // instance label AND an avahi-publish positional, so it must fit RFC 6763's 63-octet
        // limit and never lead with '-' (which avahi-publish would read as an option) or carry
        // control characters. Checked always — inert when disabled, but a bad name should be
        // rejected at config-apply, not silently break the advert later.
        RuleFor(m => m.Mdns.InstanceName!)
            .Must(n => System.Text.Encoding.UTF8.GetByteCount(n) <= 63)
            .When(m => !string.IsNullOrWhiteSpace(m.Mdns.InstanceName))
            .WithMessage("management.mdns.instanceName must be at most 63 bytes.");
        RuleFor(m => m.Mdns.InstanceName!)
            .Must(n => !n.StartsWith('-') && !n.Any(char.IsControl))
            .When(m => !string.IsNullOrWhiteSpace(m.Mdns.InstanceName))
            .WithMessage("management.mdns.instanceName must not start with '-' or contain control characters.");
    }
}

/// <summary>
/// Validates the WebAuthn relying-party config. The RP id must be non-empty and a
/// plausible domain label (never an IP literal — an IP is not a legal RP id, see
/// docs/passkeys-lan-trust-pattern.md §1). Each allowed origin, when given, must be an
/// absolute http/https URL (the exact origin the verifier pins).
/// </summary>
public sealed class WebAuthnConfigValidator : AbstractValidator<WebAuthnConfig>
{
    public WebAuthnConfigValidator()
    {
        RuleFor(w => w.RelyingPartyId)
            .NotEmpty().WithMessage("management.auth.webAuthn.relyingPartyId is required.")
            .Must(BeARegistrableDomainNotAnIp)
            .WithMessage("management.auth.webAuthn.relyingPartyId must be a domain name (e.g. localhost or pdn.lab.example), never an IP address.");

        RuleFor(w => w.RelyingPartyName)
            .NotEmpty().WithMessage("management.auth.webAuthn.relyingPartyName is required.");

        RuleForEach(w => w.AllowedOrigins)
            .Must(BeAnAbsoluteHttpOrigin)
            .WithMessage("each management.auth.webAuthn.allowedOrigins entry must be an absolute http(s) origin (e.g. https://pdn.lab.example:8443).");
    }

    // An RP id must be a registrable domain string — never an IP literal. We reject a
    // value that parses as an IPv4/IPv6 address; everything else (a bare label like
    // "localhost" or a dotted name like "pdn.lab.example") is accepted as a domain.
    private static bool BeARegistrableDomainNotAnIp(string? rpId) =>
        !string.IsNullOrWhiteSpace(rpId) && !System.Net.IPAddress.TryParse(rpId, out _);

    private static bool BeAnAbsoluteHttpOrigin(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
