using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.Capabilities;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the per-peer AX.25
/// capability-cache control surface: the read projection (<c>GET /api/v1/capabilities</c>,
/// from <c>PdnReadApi</c>) and the operate-gated forget action (<c>DELETE
/// /api/v1/capabilities/{id}</c>, from <c>PdnCapabilitiesApi</c>). Mirrors
/// <see cref="ReadApiTests"/> / <see cref="SessionsApiTests"/> — a temp YAML config with no
/// ports and telnet disabled (so the WAF host binds no fixed TCP port), the routing/cap store
/// in the same temp dir. The cache is seeded by reaching the registered
/// <see cref="PeerCapabilityCache"/> singleton through <c>factory.Services</c> and recording
/// dial outcomes, so the projection has deterministic rows to assert.
/// </summary>
[Trait("Category", "Node")]
public sealed class CapabilitiesApiTests : IDisposable
{
    private const string Callsign = "M0LTE-1";
    private readonly string configPath;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public CapabilitiesApiTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-capsapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, $"""
            schemaVersion: 1
            identity:
              callsign: {Callsign}
              alias: LONDON
            ports: []
            management:
              auth:
                enabled: false
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    private sealed class NodeAppFactory : WebApplicationFactory<Program>;

    // Seed the registered cache singleton with two deterministic records:
    //   vhf:GB7RDG-1 — a v2.2 peer that answered SREJ via XID (both positive, no refusal).
    //   vhf:G8PZT-2  — offered SABME, came back mod-8 (extended=false, stamps LastRefused).
    private static void Seed(WebApplicationFactory<Program> factory)
    {
        var cache = factory.Services.GetRequiredService<PeerCapabilityCache>();
        cache.RecordOutcome("vhf", "GB7RDG-1",
            dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: true, observedSrejEnabled: true);
        cache.RecordOutcome("vhf", "G8PZT-2",
            dialedExtended: true, observedIsExtended: false,
            dialedPreConnectXid: false, observedSrejEnabled: false);
    }

    [Fact]
    public async Task Get_capabilities_projects_seeded_rows()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();
        Seed(factory);

        var resp = await client.GetAsync("/api/v1/capabilities");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = JsonSerializer.Deserialize<JsonElement[]>(
            await resp.Content.ReadAsStringAsync(), Web);
        rows.Should().NotBeNull();
        rows!.Should().HaveCount(2);

        var gb7rdg = rows.Single(r => r.GetProperty("peer").GetString() == "GB7RDG-1");
        gb7rdg.GetProperty("portId").GetString().Should().Be("vhf");
        gb7rdg.GetProperty("supportsExtended").GetBoolean().Should().BeTrue();
        gb7rdg.GetProperty("supportsSrejViaXid").GetBoolean().Should().BeTrue();
        // LastProbed renders as a relative-ago "h:mm:ss" string (NetRom row style), never null.
        gb7rdg.GetProperty("lastProbed").GetString().Should().MatchRegex(@"^\d+:\d{2}:\d{2}$");
        // This peer never refused → LastRefused is null.
        gb7rdg.GetProperty("lastRefused").ValueKind.Should().Be(JsonValueKind.Null);

        var g8pzt = rows.Single(r => r.GetProperty("peer").GetString() == "G8PZT-2");
        g8pzt.GetProperty("supportsExtended").GetBoolean().Should().BeFalse();
        // It degraded an extended dial → LastRefused is stamped (relative-ago string).
        g8pzt.GetProperty("lastRefused").GetString().Should().MatchRegex(@"^\d+:\d{2}:\d{2}$");
    }

    [Fact]
    public async Task Get_capabilities_is_an_empty_array_when_nothing_is_cached()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/capabilities");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = JsonSerializer.Deserialize<JsonElement[]>(
            await resp.Content.ReadAsStringAsync(), Web);
        rows.Should().NotBeNull();
        rows!.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_a_cached_capability_forgets_it_and_returns_204()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();
        Seed(factory);

        var resp = await client.DeleteAsync("/api/v1/capabilities/vhf:GB7RDG-1");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The cache no longer holds it; the projection now only has the other peer.
        var cache = factory.Services.GetRequiredService<PeerCapabilityCache>();
        cache.All().Should().ContainSingle().Which.Peer.Should().Be("G8PZT-2");

        var after = JsonSerializer.Deserialize<JsonElement[]>(
            await client.GetStringAsync("/api/v1/capabilities"), Web);
        after!.Should().ContainSingle().Which.GetProperty("peer").GetString().Should().Be("G8PZT-2");
    }

    [Fact]
    public async Task Delete_an_unknown_capability_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();
        Seed(factory);

        // A well-formed id, but no record for this (port, peer) → 404 (nothing forgotten).
        var resp = await client.DeleteAsync("/api/v1/capabilities/vhf:GHOST-9");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The two seeded records are untouched.
        factory.Services.GetRequiredService<PeerCapabilityCache>().All().Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_a_malformed_capability_id_returns_404()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // No ':' → not a valid port:peer id → no split → 404.
        var resp = await client.DeleteAsync("/api/v1/capabilities/garbage");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_records_an_audit_entry()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();
        Seed(factory);

        (await client.DeleteAsync("/api/v1/capabilities/vhf:GB7RDG-1"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        factory.Services.GetRequiredService<Packet.Node.Core.Audit.IAuditLog>().Recent(50)
            .Should().Contain(e => e.Action == "clear_capability" && e.Target == "vhf:GB7RDG-1");
    }

    [Fact]
    public async Task Delete_requires_operate_when_auth_is_on()
    {
        // Rewrite the config with auth ON for this boot: a tokenless forget hits the operate gate.
        File.WriteAllText(configPath, $"""
            schemaVersion: 1
            identity:
              callsign: {Callsign}
              alias: LONDON
            ports: []
            management:
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
              auth:
                enabled: true
            """);
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();
        Seed(factory);

        var resp = await client.DeleteAsync("/api/v1/capabilities/vhf:GB7RDG-1");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The record survives — the gate rejected the action before it reached the cache.
        factory.Services.GetRequiredService<PeerCapabilityCache>().All().Should().HaveCount(2);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (dir is not null)
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best effort */ }
    }
}
