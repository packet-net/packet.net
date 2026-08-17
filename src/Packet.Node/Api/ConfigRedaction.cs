using Packet.Node.Core.Configuration;

namespace Packet.Node.Api;

/// <summary>
/// Redacts the config's secret-bearing fields on the way OUT of the read endpoints, and
/// restores them on the way back IN - so <c>GET /config</c> → edit → <c>PUT /config</c>
/// round-trips without either leaking a secret or wiping one.
/// </summary>
/// <remarks>
/// <para>
/// <c>GET /api/v1/config</c> and <c>GET /api/v1/config/raw</c> are <b>read</b>-scoped, and an
/// admin can hand out read-scope accounts; both returned <c>tailscale.authKey</c> (a reusable
/// tailnet join key), <c>mqtt.password</c> (broker credentials) and
/// <c>management.https.certificatePassword</c> verbatim (review item C010). Each is replaced
/// by <see cref="Placeholder"/> when it has a value, and left null when it does not - so a
/// reader learns only WHETHER a secret is set, which is what the editor needs to render.
/// </para>
/// <para>
/// The write side treats <see cref="Placeholder"/> as <b>keep what is stored</b>. That is what
/// makes the panel's read-modify-write safe: the editor PUTs back the config it was served,
/// placeholders included, and the stored secrets survive untouched. Typing a new value
/// replaces it; clearing the field to empty/null still clears the secret, so a secret can be
/// removed. Only the exact placeholder string is special - a real secret of <c>***</c> would
/// be preserved rather than set, which is a better failure than leaking it.
/// </para>
/// </remarks>
internal static class ConfigRedaction
{
    /// <summary>What a set secret reads as on a redacted projection, and what the write side
    /// reads as "keep the stored value".</summary>
    internal const string Placeholder = "***";

    /// <summary>The read projection: every secret-bearing field replaced by
    /// <see cref="Placeholder"/> when set, left null when not.</summary>
    internal static NodeConfig Redact(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config with
        {
            Tailscale = config.Tailscale with { AuthKey = Mask(config.Tailscale.AuthKey) },
            Mqtt = config.Mqtt with { Password = Mask(config.Mqtt.Password) },
            Management = config.Management with
            {
                Https = config.Management.Https with
                {
                    CertificatePassword = Mask(config.Management.Https.CertificatePassword),
                },
            },
        };
    }

    /// <summary>The write projection: any secret field the candidate left at
    /// <see cref="Placeholder"/> is restored from <paramref name="current"/> (the live config),
    /// so a round-tripped edit keeps the stored secret instead of persisting "***".</summary>
    internal static NodeConfig Unredact(NodeConfig candidate, NodeConfig current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(current);

        return candidate with
        {
            Tailscale = candidate.Tailscale with
            {
                AuthKey = Restore(candidate.Tailscale.AuthKey, current.Tailscale.AuthKey),
            },
            Mqtt = candidate.Mqtt with
            {
                Password = Restore(candidate.Mqtt.Password, current.Mqtt.Password),
            },
            Management = candidate.Management with
            {
                Https = candidate.Management.Https with
                {
                    CertificatePassword = Restore(
                        candidate.Management.Https.CertificatePassword,
                        current.Management.Https.CertificatePassword),
                },
            },
        };
    }

    private static string? Mask(string? value) =>
        string.IsNullOrEmpty(value) ? value : Placeholder;

    private static string? Restore(string? candidate, string? current) =>
        candidate == Placeholder ? current : candidate;
}
