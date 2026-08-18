using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Tls;

/// <summary>
/// Unit tests for <see cref="TlsCertificateProvider"/> - the HTTPS cert resolver:
/// load a supplied PKCS#12, or generate + persist + reuse a self-signed cert, all on a
/// <see cref="FakeTimeProvider"/> (no wall-clock, repo rule §2.7). Each test owns a temp
/// dir so the persisted .pfx never leaks between tests.
/// </summary>
public sealed class TlsCertificateProviderTests : IDisposable
{
    private const string SubjectAltNameOid = "2.5.29.17";

    private readonly string dir;
    private readonly string pfxPath;

    public TlsCertificateProviderTests()
    {
        dir = TestPaths.NewPath("pdn-tls");
        Directory.CreateDirectory(dir);
        pfxPath = Path.Combine(dir, "certs", "server.pfx");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly IReadOnlyList<string> Dns = ["localhost", "pdn-test"];
    private static readonly IReadOnlyList<IPAddress> Ips = [IPAddress.Loopback];

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Generates_persists_and_reuses_a_self_signed_cert()
    {
        var cfg = new HttpsConfig { Enabled = true, GenerateSelfSignedOnMissing = true };
        var clock = Clock();

        using var first = TlsCertificateProvider.Resolve(cfg, pfxPath, "M0LTE", Dns, Ips, clock);
        first.Should().NotBeNull();
        first!.HasPrivateKey.Should().BeTrue();
        File.Exists(pfxPath).Should().BeTrue();

        // A second resolve reuses the persisted cert (same thumbprint) rather than minting a new one.
        using var second = TlsCertificateProvider.Resolve(cfg, pfxPath, "M0LTE", Dns, Ips, clock);
        second!.Thumbprint.Should().Be(first.Thumbprint);
    }

    [Fact]
    public void Generated_cert_carries_the_expected_san_and_a_future_expiry()
    {
        var clock = Clock();
        using var cert = TlsCertificateProvider.Resolve(new HttpsConfig { Enabled = true }, pfxPath, "M0LTE", Dns, Ips, clock);
        cert.Should().NotBeNull();
        cert!.NotAfter.ToUniversalTime().Should().BeAfter(clock.GetUtcNow().UtcDateTime);
        var san = cert.Extensions.Single(e => e.Oid?.Value == SubjectAltNameOid);
        san.Format(multiLine: false).Should().Contain("localhost");
    }

    [Fact]
    public void Regenerates_when_the_persisted_cert_has_expired()
    {
        var cfg = new HttpsConfig { Enabled = true };
        using var first = TlsCertificateProvider.Resolve(cfg, pfxPath, "M0LTE", Dns, Ips, Clock());
        // Advance well past the 2-year validity → the next resolve mints a fresh cert.
        var later = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var second = TlsCertificateProvider.Resolve(cfg, pfxPath, "M0LTE", Dns, Ips, later);
        second!.Thumbprint.Should().NotBe(first!.Thumbprint);
    }

    [Fact]
    public void Returns_null_when_a_supplied_cert_path_is_missing()
    {
        var cfg = new HttpsConfig { Enabled = true, CertificatePath = Path.Combine(dir, "nope.pfx") };
        TlsCertificateProvider.Resolve(cfg, pfxPath, "M0LTE", Dns, Ips, Clock()).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_generation_is_disabled_and_no_cert_supplied()
    {
        var cfg = new HttpsConfig { Enabled = true, GenerateSelfSignedOnMissing = false };
        TlsCertificateProvider.Resolve(cfg, pfxPath, "M0LTE", Dns, Ips, Clock()).Should().BeNull();
    }

    [Fact]
    public void Loads_a_supplied_pkcs12_certificate()
    {
        // Mint one via the provider, then point CertificatePath at the resulting .pfx.
        using (TlsCertificateProvider.Resolve(new HttpsConfig { Enabled = true }, pfxPath, "M0LTE", Dns, Ips, Clock())) { }
        var cfg = new HttpsConfig { Enabled = true, CertificatePath = pfxPath };
        using var loaded = TlsCertificateProvider.Resolve(cfg, Path.Combine(dir, "unused.pfx"), "M0LTE", Dns, Ips, Clock());
        loaded.Should().NotBeNull();
        loaded!.HasPrivateKey.Should().BeTrue();
    }
}
