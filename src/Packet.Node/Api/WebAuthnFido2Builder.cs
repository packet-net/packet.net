using Fido2NetLib;
using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Api;

/// <summary>
/// Builds a per-request <see cref="IFido2"/> bound to the configured Relying Party id
/// and the set of origins the verifier will accept for THIS request. This is where the
/// origin-vs-RP-id split (docs/passkeys-lan-trust-pattern.md §4 — "the single most
/// error-prone part") is handled, so the rest of the endpoint code can stay simple.
/// </summary>
/// <remarks>
/// <para>
/// <b>RP id is fixed</b> by config (<see cref="WebAuthnConfig.RelyingPartyId"/>,
/// default <c>localhost</c>) — it is the stable scope a passkey is bound to and must
/// not move, or existing passkeys break.
/// </para>
/// <para>
/// <b>The expected origin is the ACTUAL serving origin</b> the browser used. When
/// <see cref="WebAuthnConfig.AllowedOrigins"/> is empty (the zero-config default) we
/// accept the request's own origin (request scheme + host[:port]) plus the loopback
/// origins, so a node reached on <c>http://localhost:8080</c> just works with no setup.
/// When the operator pins explicit origins (the real-domain / §2a path) we use exactly
/// those — the request origin is no longer trusted to widen the set.
/// </para>
/// <para>
/// A fresh <see cref="Fido2"/> per request is cheap (it is a thin wrapper over an
/// immutable <see cref="Fido2Configuration"/>) and keeps the accepted-origin set exact
/// for the request being verified, rather than baking one global origin at startup that
/// would not match a node reachable under more than one name.
/// </para>
/// </remarks>
public static class WebAuthnFido2Builder
{
    /// <summary>
    /// Construct an <see cref="IFido2"/> for the current request, pinning the RP id from
    /// <paramref name="cfg"/> and the accepted origins (explicit allow-list, else the
    /// request's own serving origin + loopback).
    /// </summary>
    public static IFido2 ForRequest(WebAuthnConfig cfg, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(request);

        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cfg.AllowedOrigins.Count > 0)
        {
            // Operator-pinned origins (real domain) — trust ONLY these.
            foreach (var o in cfg.AllowedOrigins)
            {
                origins.Add(o.TrimEnd('/'));
            }
        }
        else
        {
            // Zero-config default: accept the origin the browser actually used for this
            // request, plus the loopback origins (so localhost works with no setup).
            var serving = ServingOrigin(request);
            if (serving is not null)
            {
                origins.Add(serving);
            }
            // The loopback secure-context exemption — handy when the panel is reached
            // both by name and via localhost on the same box.
            origins.Add($"http://localhost:{request.Host.Port ?? 80}");
            origins.Add("http://localhost");
        }

        var config = new Fido2Configuration
        {
            ServerDomain = cfg.RelyingPartyId,
            ServerName = cfg.RelyingPartyName,
            Origins = origins,
        };
        return new Fido2(config, metadataService: null);
    }

    /// <summary>The serving origin (scheme://host[:port]) the browser used for this
    /// request, or null if it can't be determined.</summary>
    public static string? ServingOrigin(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Host.HasValue)
        {
            return null;
        }
        // request.Host.Value already renders host:port (port omitted when default), and
        // request.Scheme is the wire scheme — together exactly the browser's origin.
        return $"{request.Scheme}://{request.Host.Value}";
    }
}
