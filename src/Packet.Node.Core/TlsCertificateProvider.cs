using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core;

/// <summary>
/// Resolves the X.509 certificate for the optional HTTPS listener (see
/// <see cref="HttpsConfig"/>): loads an operator-supplied PKCS#12, or - when none is
/// configured and <see cref="HttpsConfig.GenerateSelfSignedOnMissing"/> is set -
/// generates a self-signed cert on first start and persists it so it is stable across
/// restarts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Graceful degrade.</b> Every path that can't produce a usable cert returns
/// <c>null</c> (logged), so the host simply does not bind the HTTPS endpoint and the
/// plain HTTP listener keeps serving - a TLS misconfiguration never takes the node
/// down. (HTTPS is opt-in, so failing loud-but-soft is the right posture.)
/// </para>
/// <para>
/// <b>Self-signed limits.</b> A generated cert encrypts the channel but is not trusted
/// by browsers; for a trusted secure context (e.g. so WebAuthn/passkeys work over a LAN
/// IP) the operator points <see cref="HttpsConfig.CertificatePath"/> at a trusted cert
/// or reaches the node via <c>localhost</c>.
/// </para>
/// <para>
/// <b>No wall-clock</b> (repo rule §2.7): validity windows + the expiry check ride the
/// injected <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
public static partial class TlsCertificateProvider
{
    // serverAuth EKU OID - browsers + Kestrel expect a TLS server cert to assert it.
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>
    /// Resolve the HTTPS certificate, or <c>null</c> if none can be produced (the caller
    /// then skips the HTTPS listener). <paramref name="selfSignedPath"/> is where a
    /// generated cert is persisted (a PKCS#12); <paramref name="commonName"/> + the SAN
    /// entries name the node.
    /// </summary>
    public static X509Certificate2? Resolve(
        HttpsConfig config,
        string selfSignedPath,
        string commonName,
        IReadOnlyList<string> sanDnsNames,
        IReadOnlyList<IPAddress> sanIpAddresses,
        TimeProvider clock,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        logger ??= NullLogger.Instance;

        try
        {
            // 1) Operator-supplied cert wins.
            if (!string.IsNullOrWhiteSpace(config.CertificatePath))
            {
                if (!File.Exists(config.CertificatePath))
                {
                    LogSuppliedCertMissing(logger, config.CertificatePath);
                    return null;
                }
                return X509CertificateLoader.LoadPkcs12FromFile(config.CertificatePath, config.CertificatePassword);
            }

            // 2) No supplied cert and generation disabled → can't serve HTTPS.
            if (!config.GenerateSelfSignedOnMissing)
            {
                LogNoCertConfigured(logger);
                return null;
            }

            // 3) Reuse a persisted self-signed cert while it is still valid; else (re)generate.
            if (File.Exists(selfSignedPath))
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(selfSignedPath, null);
                // Keep a margin so we rotate before, not at, expiry.
                if (existing.NotAfter.ToUniversalTime() > clock.GetUtcNow().UtcDateTime.AddDays(1))
                {
                    return existing;
                }
                existing.Dispose();
                LogSelfSignedExpired(logger, selfSignedPath);
            }

            return GenerateAndPersist(selfSignedPath, commonName, sanDnsNames, sanIpAddresses, clock, logger);
        }
        catch (Exception ex)
        {
            LogResolveFault(logger, ex);
            return null;
        }
    }

    private static X509Certificate2 GenerateAndPersist(
        string path,
        string commonName,
        IReadOnlyList<string> sanDnsNames,
        IReadOnlyList<IPAddress> sanIpAddresses,
        TimeProvider clock,
        ILogger logger)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], critical: false));

        var san = new SubjectAlternativeNameBuilder();
        bool anySan = false;
        foreach (var dns in sanDnsNames.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            san.AddDnsName(dns);
            anySan = true;
        }
        foreach (var ip in sanIpAddresses.Distinct())
        {
            san.AddIpAddress(ip);
            anySan = true;
        }
        if (anySan)
        {
            request.CertificateExtensions.Add(san.Build());
        }

        var now = clock.GetUtcNow();
        using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(2));

        // Persist as a PKCS#12 (cert + private key) and reload from those bytes so the
        // returned cert's key is backed identically to a fresh-start load (avoids
        // ephemeral-key quirks when the same cert is reused on the next run).
        var pfx = cert.Export(X509ContentType.Pkcs12);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, pfx);
        TrySetOwnerOnlyPermissions(path);

        var notAfter = cert.NotAfter.ToUniversalTime();
        LogGenerated(logger, path, notAfter);
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    // The PKCS#12 holds the private key - keep it owner-only (best-effort; no-op off Unix).
    private static void TrySetOwnerOnlyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // Best-effort; the StateDirectory is already 0750 packetnet-owned.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "HTTPS: configured certificatePath '{Path}' does not exist; HTTPS listener not started.")]
    private static partial void LogSuppliedCertMissing(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "HTTPS: no certificatePath and generateSelfSignedOnMissing is false; HTTPS listener not started.")]
    private static partial void LogNoCertConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTPS: persisted self-signed cert at '{Path}' expired; regenerating.")]
    private static partial void LogSelfSignedExpired(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTPS: generated a self-signed certificate at '{Path}' (valid until {NotAfter:u}). Browsers will warn until it is trusted; supply a trusted cert for a trusted secure context.")]
    private static partial void LogGenerated(ILogger logger, string path, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Error, Message = "HTTPS: failed to resolve a certificate; HTTPS listener not started.")]
    private static partial void LogResolveFault(ILogger logger, Exception ex);
}
