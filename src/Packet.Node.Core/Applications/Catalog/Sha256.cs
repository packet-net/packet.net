using System.Security.Cryptography;

namespace Packet.Node.Core.Applications.Catalog;

/// <summary>SHA-256 helpers for the catalog installer's hash-verification step.</summary>
public static class Sha256
{
    /// <summary>Compute the SHA-256 of a file and return it as a 64-char lowercase hex string —
    /// the spelling the catalog pins (<see cref="ArtifactRef.Sha256"/>). Streams the file so a
    /// ~100 MB binary never lands in memory whole.</summary>
    public static async Task<string> OfFileHexAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Synchronous convenience over <see cref="OfFileHexAsync"/> for small files.</summary>
    public static string OfFileHex(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
