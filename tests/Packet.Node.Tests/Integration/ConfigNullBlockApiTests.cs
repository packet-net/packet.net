using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// A config write carrying an explicit <c>null</c> block is a clean 422, never a 500 (#727 item 4).
/// </summary>
/// <remarks>
/// <para>
/// <c>NodeConfig</c>'s sub-blocks are declared non-nullable, but neither System.Text.Json (web
/// defaults, no <c>RespectNullableAnnotations</c>) nor YamlDotNet honours that: an explicit
/// <c>"mqtt": null</c>, or an emptied <c>tailscale:</c> key in the advanced YAML editor, lands a
/// real null on the property. <c>ConfigRedaction.Unredact</c> and the <c>AuthBlockChanged</c>
/// guard both ran ahead of <c>Validate</c> and dereferenced those blocks with no guard, so the
/// handler threw before the validator's <c>NotNull</c> rules could produce the 422 the panel
/// knows how to render.
/// </para>
/// <para>
/// The <c>allowedOrigins</c> case was worse than a bad request: <c>RuleForEach</c> SKIPS a null
/// collection, so a null passed validation, got persisted, and from then on the LIVE side of the
/// comparison was null too - every subsequent config write on that node 500'd permanently, with
/// no error text to explain it and no way to save config from the panel again.
/// </para>
/// </remarks>
[Trait("Category", "Node")]
public sealed class ConfigNullBlockApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = NodeConfigJson.CreateOptions();

    private readonly string dir;

    public ConfigNullBlockApiTests()
    {
        dir = TestPaths.NewPath("packetnet-cfgnullblock");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
              alias: LONDON
            ports:
              - id: vhf
                enabled: false
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8101
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

    private sealed class NodeAppFactory : WebApplicationFactory<Program>
    {
        // Boots Program.Main's host; Kestrel is replaced by the in-memory TestServer.
    }

    [Theory]
    [InlineData("mqtt")]
    [InlineData("tailscale")]
    public async Task An_explicitly_null_block_in_a_json_body_is_a_422(string block)
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // Start from the live config so the body is otherwise complete, then null one block -
        // exactly what a client that serialises an absent object as null produces.
        var body = JsonNode.Parse(await client.GetStringAsync("/api/v1/config"))!.AsObject();
        body[block] = null;

        var resp = await client.PutAsync("/api/v1/config",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
            "the validator owns this verdict; redaction must not throw before it runs");
        var problem = await resp.Content.ReadFromJsonAsync<ValidationProblem>(Web);
        problem!.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_emptied_block_in_the_advanced_yaml_editor_is_a_422()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // The operator blanks a block by leaving the key with nothing under it. YamlDotNet
        // parses that as a null value on a non-nullable property.
        var yaml = await client.GetStringAsync("/api/v1/config/raw") + "\nmqtt:\n";

        var resp = await client.PutAsync("/api/v1/config/raw",
            new StringContent(yaml, Encoding.UTF8, "text/plain"));

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_persisted_null_allowed_origins_list_does_not_poison_every_later_write()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        // 1. Persist a config whose webAuthn.allowedOrigins is null. FluentValidation's
        //    RuleForEach skips a null collection, so this is accepted - which is precisely how
        //    a node ends up in this state without anyone doing anything wrong.
        var body = JsonNode.Parse(await client.GetStringAsync("/api/v1/config"))!.AsObject();
        body["management"]!["auth"]!["webAuthn"]!["allowedOrigins"] = null;

        var persisted = await client.PutAsync("/api/v1/config",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        persisted.StatusCode.Should().Be(HttpStatusCode.OK, "a null list is not a validation error today");

        // 2. Now the LIVE config carries the null, and every later write compares against it.
        //    The hand-rolled field compare dereferenced both sides, so this used to 500 forever.
        var after = JsonNode.Parse(await client.GetStringAsync("/api/v1/config"))!.AsObject();
        after["identity"]!["grid"] = "JO01aa";

        var benign = await client.PutAsync("/api/v1/config",
            new StringContent(after.ToJsonString(), Encoding.UTF8, "application/json"));

        benign.StatusCode.Should().Be(HttpStatusCode.OK,
            "a node that once persisted a null list must still be configurable");
        var result = await benign.Content.ReadFromJsonAsync<ReconcileResult>(Web);
        result!.Applied.Should().BeTrue();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
