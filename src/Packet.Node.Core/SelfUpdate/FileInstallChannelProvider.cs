namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// Resolves the install channel from the build-stamped marker file the package ships
/// (<c>/usr/lib/packetnet/install-channel</c>, a single token: <c>apt</c> or
/// <c>selfcontained</c>), with an environment override (<c>PDN_INSTALL_CHANNEL</c>)
/// for dev/test. Absent or unrecognised → <see cref="InstallChannel.Unknown"/>, which
/// makes the node decline to self-update rather than guess what owns it.
/// </summary>
/// <remarks>
/// The marker is the primary signal because the node's own <c>.deb</c> ships it (and a
/// self-contained installer writes the <c>selfcontained</c> one), so it's authoritative
/// without probing. A <c>dpkg-query</c> sniff of the running binary is the documented
/// belt-and-braces fallback (<c>docs/node-self-update-design.md</c>) for a stripped
/// marker; it is deliberately left out of this slice — the marker is sufficient and a
/// process probe is environment-fragile. Resolution is eager + cached in the ctor.
/// </remarks>
public sealed class FileInstallChannelProvider : IInstallChannelProvider
{
    /// <summary>The default marker path the package ships.</summary>
    public const string DefaultMarkerPath = "/usr/lib/packetnet/install-channel";

    /// <summary>Environment variable that overrides the marker (dev/test).</summary>
    public const string EnvOverride = "PDN_INSTALL_CHANNEL";

    /// <inheritdoc/>
    public InstallChannel Channel { get; }

    /// <summary>Resolve from <paramref name="markerPath"/> (default the shipped path),
    /// honouring the <c>PDN_INSTALL_CHANNEL</c> override first.</summary>
    public FileInstallChannelProvider(string? markerPath = null)
    {
        Channel = Resolve(markerPath ?? DefaultMarkerPath);
    }

    private static InstallChannel Resolve(string markerPath)
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvOverride);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Parse(fromEnv);
        }

        try
        {
            if (File.Exists(markerPath))
            {
                return Parse(File.ReadAllText(markerPath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable marker → Unknown (fail safe: decline to self-update).
        }

        return InstallChannel.Unknown;
    }

    /// <summary>Parse a channel token (case/whitespace-insensitive). Unrecognised → Unknown.</summary>
    public static InstallChannel Parse(string token) => token.Trim().ToLowerInvariant() switch
    {
        "apt" => InstallChannel.Apt,
        "selfcontained" or "self-contained" => InstallChannel.SelfContained,
        _ => InstallChannel.Unknown,
    };
}
