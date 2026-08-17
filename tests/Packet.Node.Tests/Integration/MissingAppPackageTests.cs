using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// A configured, ENABLED app whose package is not installed (#738 item 2, found by the GB7RDG
/// upgrade rehearsal: CT129's config carried four enabled app entries and the node had no
/// payloads for them).
/// </summary>
/// <remarks>
/// It used to be completely silent. The catalog only warns about packages it FOUND and could
/// not parse; its inventory line is Debug; the API projected discovered packages plus inline
/// entries and nothing else, so <c>GET /apps/packages</c> answered <c>[]</c> and the panel said
/// "No app packages". A lost payload was indistinguishable from a healthy idle node. Now the
/// supervisor logs a Warning per such app at bring-up and on every reconcile, and the inventory
/// carries it as an <c>installed: false</c> row. This boots the REAL composition root - real
/// catalog, real supervisor - over an EMPTY package root, so both halves are exercised together.
/// </remarks>
[Trait("Category", "Node")]
public sealed class MissingAppPackageTests : IDisposable
{
    private readonly string dir;
    private readonly string packagesRoot;
    private readonly string configPath;

    public MissingAppPackageTests()
    {
        dir = TestPaths.NewPath("pdn-missingapp");
        packagesRoot = Path.Combine(dir, "apps");
        Directory.CreateDirectory(packagesRoot);   // exists, and holds nothing

        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, $"""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports: []
            management:
              auth:
                enabled: false
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
            apps:
              - id: ghost
                enabled: true
              - id: dormant
                enabled: false
            appPackageRoots:
              - {packagesRoot}
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    /// <summary>The real node, with every category's output captured (the warning under test is
    /// the supervisor's, raised from the hosted service's bring-up).</summary>
    private sealed class LoggingNodeFactory(CapturingLoggerFactory logs) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureLogging(b => b.AddProvider(logs));
    }

    [Fact]
    public async Task A_configured_app_with_no_package_is_a_visible_row_and_a_logged_warning()
    {
        var logs = new CapturingLoggerFactory();
        await using var factory = new LoggingNodeFactory(logs);
        using var client = factory.CreateClient();

        var inventory = JsonDocument.Parse(await client.GetStringAsync("/api/v1/apps/packages")).RootElement;
        var rows = inventory.EnumerateArray().ToList();

        // Both configured apps are surfaced - the operator's config says they exist, so the
        // inventory says so too, whatever the disk holds.
        var ghost = rows.Single(e => e.GetProperty("id").GetString() == "ghost");
        ghost.GetProperty("installed").GetBoolean().Should().BeFalse("no root holds a package for it");
        ghost.GetProperty("enabled").GetBoolean().Should().BeTrue("the owner's apps: entry enabled it");
        ghost.GetProperty("source").GetString().Should().Be("package");
        ghost.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null, "it is absent, not broken");
        ghost.GetProperty("service").GetString().Should().Be("none");

        rows.Single(e => e.GetProperty("id").GetString() == "dormant")
            .GetProperty("installed").GetBoolean().Should().BeFalse();

        // The Warning, asserted on the RENDERED line: it has to name the app and where we
        // looked, or the operator cannot act on it. Bring-up runs on a background task, so poll.
        await Wait.ForAsync(
            () => logs.Messages.Any(m => m.Level == LogLevel.Warning && m.Text.Contains("'ghost'", StringComparison.Ordinal)),
            "the supervisor warns about an enabled app whose package is not installed");

        var warning = logs.Messages
            .Where(m => m.Level == LogLevel.Warning && m.Text.Contains("'ghost'", StringComparison.Ordinal))
            .Select(m => m.Text)
            .First();
        warning.Should().Contain("no package is installed");
        warning.Should().Contain(packagesRoot, "naming the roots scanned is what makes it actionable");

        // A DISABLED entry is not a problem to shout about - the owner turned it off.
        logs.Messages.Should().NotContain(m =>
            m.Level == LogLevel.Warning && m.Text.Contains("'dormant'", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
