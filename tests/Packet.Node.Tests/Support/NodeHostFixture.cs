using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A booted <c>Packet.Node</c> composition root over a throwaway config and database.
/// </summary>
/// <remarks>
/// <para>
/// The node reads its config path from <c>PACKETNET_CONFIG</c> and its database from
/// <c>PACKETNET_DB</c>, so an integration test has to write a YAML file into a temp directory,
/// set both environment variables, boot a <see cref="WebApplicationFactory{TEntryPoint}"/>, and
/// then put the environment back. Twenty five test classes had each written their own copy of
/// that (#700 C115), including twenty five identical <c>NodeAppFactory</c> declarations.
/// </para>
/// <para>
/// The environment variables are process-global, which is safe here only because
/// <c>Support/TestCollections.cs</c> disables assembly parallelisation. The temp directory comes
/// from <see cref="TestPaths"/>, so it is unique per user and per run (#628).
/// </para>
/// </remarks>
public sealed class NodeHostFixture : IDisposable
{
    private readonly string previousConfig;
    private readonly string previousDb;
    private readonly NodeAppFactory factory;
    private readonly List<HttpClient> clients = [];

    /// <summary>Boot a node over <paramref name="yaml"/>.</summary>
    /// <param name="yaml">The whole config document (see <see cref="NodeYaml"/> for the usual shape).</param>
    /// <param name="label">Short hint used in the temp directory name.</param>
    public NodeHostFixture(string yaml, string label = "node")
    {
        Directory = TestPaths.NewDirectory(label);
        ConfigPath = Path.Combine(Directory, "node.yaml");
        DbPath = Path.Combine(Directory, "pdn.db");
        File.WriteAllText(ConfigPath, yaml);

        previousConfig = Environment.GetEnvironmentVariable("PACKETNET_CONFIG") ?? "";
        previousDb = Environment.GetEnvironmentVariable("PACKETNET_DB") ?? "";
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", ConfigPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", DbPath);

        factory = new NodeAppFactory();
    }

    /// <summary>The temp directory holding the config and the database.</summary>
    public string Directory { get; }

    /// <summary>The config file the node booted from.</summary>
    public string ConfigPath { get; }

    /// <summary>The SQLite database path the node booted with.</summary>
    public string DbPath { get; }

    /// <summary>An HTTP client against the booted host. Disposed with the fixture.</summary>
    public HttpClient CreateClient()
    {
        var client = factory.CreateClient();
        clients.Add(client);
        return client;
    }

    /// <summary>Resolve a service out of the booted host's container.</summary>
    public T Service<T>() where T : notnull => factory.Services.GetRequiredService<T>();

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }

        factory.Dispose();
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", previousConfig.Length == 0 ? null : previousConfig);
        Environment.SetEnvironmentVariable("PACKETNET_DB", previousDb.Length == 0 ? null : previousDb);

        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // The node's SQLite handles may not be closed yet; TestPaths reaps the run root.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: never fail a test on cleanup.
        }
    }

    private sealed class NodeAppFactory : WebApplicationFactory<Program>;
}

/// <summary>
/// Builds the node YAML the integration tests keep re-typing: an identity, an optional set of
/// port blocks, and the management block with auth and telnet off (nothing binds a fixed TCP
/// port under the in-memory test host).
/// </summary>
public static class NodeYaml
{
    /// <summary>A port block for a configured but disabled KISS-TCP port.</summary>
    /// <param name="id">The port id.</param>
    /// <param name="port">The TCP port in the transport block (never dialled while disabled).</param>
    public static string DisabledKissTcpPort(string id = "vhf", int port = 8101) =>
        $"""
          - id: {id}
            enabled: false
            transport:
              kind: kiss-tcp
              host: 127.0.0.1
              port: {port}
        """;

    /// <summary>
    /// The standard test document: identity, zero or more port blocks, management with auth and
    /// telnet disabled.
    /// </summary>
    /// <param name="callsign">The node callsign.</param>
    /// <param name="alias">The node alias, or null to omit it.</param>
    /// <param name="ports">Port blocks, e.g. from <see cref="DisabledKissTcpPort"/>.</param>
    /// <param name="authEnabled">Whether management auth is on (off by default: most tests are not about auth).</param>
    /// <param name="httpPort">The management HTTP port recorded in the config.</param>
    public static string Build(
        string callsign = "M0LTE-1",
        string? alias = "LONDON",
        IEnumerable<string>? ports = null,
        bool authEnabled = false,
        int httpPort = 8080)
    {
        var portBlocks = (ports ?? []).ToList();
        var identity = alias is null
            ? $"""
              identity:
                callsign: {callsign}
              """
            : $"""
              identity:
                callsign: {callsign}
                alias: {alias}
              """;
        var portsSection = portBlocks.Count == 0 ? "ports: []" : "ports:\n" + string.Join("\n", portBlocks);

        return $"""
            schemaVersion: 1
            {identity}
            {portsSection}
            management:
              auth:
                enabled: {(authEnabled ? "true" : "false")}
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: {httpPort}
            """;
    }
}
