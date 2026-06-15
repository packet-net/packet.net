using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// Builds the validated github-channel Apply request the privileged helper applies: resolve the
/// latest <c>node-v*</c> release, find the per-arch <c>packetnet_&lt;ver&gt;_&lt;arch&gt;.deb</c>
/// asset, and look up its expected SHA-256 from the release's <c>SHA256SUMS</c> asset — all over
/// HTTPS (the github channel's trust root). The node passes this to the helper, which
/// <em>re-verifies</em> the sha256 against the download rather than trusting it.
/// </summary>
public sealed partial class GithubUpdateRequestBuilder
{
    private readonly IGitHubReleaseClient github;
    private readonly ILogger<GithubUpdateRequestBuilder> log;

    public GithubUpdateRequestBuilder(IGitHubReleaseClient github, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.github = github;
        log = loggerFactory.CreateLogger<GithubUpdateRequestBuilder>();
    }

    /// <summary>The Debian arch token for the running process, or <c>null</c> on an unsupported
    /// arch (then no github update can be built — the API declines).</summary>
    public static string? CurrentDebArch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "armhf",
        _ => null,
    };

    /// <summary>
    /// Resolve the github Apply request for the latest release, or <c>null</c> if it can't be built
    /// (offline / no release / no matching asset / no checksum / unsupported arch). Never throws.
    /// </summary>
    public async Task<GithubUpdateRequest?> BuildAsync(CancellationToken cancellationToken = default)
    {
        var arch = CurrentDebArch;
        if (arch is null)
        {
            LogUnsupportedArch(RuntimeInformation.ProcessArchitecture.ToString());
            return null;
        }

        GitHubRelease? release;
        try
        {
            release = await github.GetLatestNodeReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFault(ex);
            return null;
        }

        if (release is null || !NodeVersion.TryParse(release.TagName, out var version))
        {
            return null;
        }
        var ver = version.ToString();

        var debName = $"packetnet_{ver}_{arch}.deb";
        if (!release.Assets.TryGetValue(debName, out var debUrl))
        {
            LogNoAsset(debName);
            return null;
        }
        if (!string.Equals(debUrl.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return null; // the github channel only trusts https downloads.
        }

        // The expected sha256 comes from the release's SHA256SUMS asset (HTTPS). The helper
        // re-verifies; this is the value we tell it to expect.
        if (!release.Assets.TryGetValue("SHA256SUMS", out var sumsUrl))
        {
            LogNoAsset("SHA256SUMS");
            return null;
        }

        string? sums;
        try
        {
            sums = await github.GetTextAssetAsync(sumsUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFault(ex);
            return null;
        }

        var sha = sums is null ? null : ExtractSha(sums, debName);
        if (sha is null)
        {
            LogNoChecksum(debName);
            return null;
        }

        return new GithubUpdateRequest(ver, arch, debUrl.ToString(), sha);
    }

    /// <summary>Pull the sha256 of <paramref name="fileName"/> from a <c>sha256sum</c>-format body
    /// (<c>&lt;hex&gt;␠␠&lt;name&gt;</c> per line; the leading <c>*</c> binary marker tolerated).
    /// Returns the lowercase hex, or <c>null</c> if absent / malformed.</summary>
    internal static string? ExtractSha(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            int sp = line.IndexOf(' ', StringComparison.Ordinal);
            if (sp <= 0)
            {
                continue;
            }
            var hex = line[..sp];
            // The name is whatever follows the hash + separator; tolerate a leading '*' (binary) or
            // a path prefix (we match on the basename).
            var name = line[(sp + 1)..].TrimStart(' ', '*', '\t');
            name = name.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name[(slash + 1)..];
            }
            if (string.Equals(name, fileName, StringComparison.Ordinal) && IsHex(hex))
            {
                return hex.ToLowerInvariant();
            }
        }
        return null;
    }

    private static bool IsHex(string s)
    {
        if (s.Length != 64)
        {
            return false; // a SHA-256 is 64 hex chars.
        }
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "github update: unsupported process arch {Arch}; can't build a release download.")]
    private partial void LogUnsupportedArch(string arch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "github update: release is missing the {Asset} asset; declining.")]
    private partial void LogNoAsset(string asset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "github update: no checksum for {File} in SHA256SUMS; declining.")]
    private partial void LogNoChecksum(string file);

    [LoggerMessage(Level = LogLevel.Debug, Message = "github update request build failed; declining.")]
    private partial void LogFault(Exception ex);
}
