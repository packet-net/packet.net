using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Packet.Node.Core.Applications.Catalog;

/// <summary>
/// The .deb data-extraction seam: unpack a <c>.deb</c>'s data tree into a directory. The
/// catalog installs <c>deb</c>-kind apps by EXTRACTING (not dpkg-installing) the package and
/// taking its <c>usr/share/packetnet/apps/&lt;id&gt;/</c> subtree as the payload.
/// </summary>
public interface IDebExtractor
{
    /// <summary>Extract the data portion of <paramref name="debPath"/> into
    /// <paramref name="destDir"/> (created if missing), preserving the deb's internal tree
    /// (<c>usr/share/...</c> etc.). Throws on failure.</summary>
    Task ExtractDataAsync(string debPath, string destDir, CancellationToken cancellationToken);
}

/// <summary>The production <see cref="IDebExtractor"/>: shells <c>dpkg-deb -x &lt;deb&gt;
/// &lt;dir&gt;</c>, the standard data-extract-without-install command.</summary>
public sealed partial class DpkgDebExtractor : IDebExtractor
{
    private readonly ILogger<DpkgDebExtractor> log;

    /// <summary>Create a <c>dpkg-deb</c>-backed extractor.</summary>
    public DpkgDebExtractor(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        log = loggerFactory.CreateLogger<DpkgDebExtractor>();
    }

    /// <inheritdoc/>
    public async Task ExtractDataAsync(string debPath, string destDir, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(debPath);
        ArgumentException.ThrowIfNullOrEmpty(destDir);
        Directory.CreateDirectory(destDir);

        var psi = new ProcessStartInfo("dpkg-deb")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-x");
        psi.ArgumentList.Add(debPath);
        psi.ArgumentList.Add(destDir);

        LogExtracting(debPath, destDir);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dpkg-deb -x failed for '{debPath}' (exit {process.ExitCode}): {stderr.Trim()}");
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Extracting {DebPath} into {DestDir} via dpkg-deb -x.")]
    private partial void LogExtracting(string debPath, string destDir);
}
