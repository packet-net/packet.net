using Packet.Node.Mcp;

namespace Packet.Node.Tests.Mcp;

/// <summary>
/// Token resolution + client configuration for the <c>pdn mcp</c> stdio bridge (review item
/// C061, #694): with the node's auth on by default, a bridge that sends no bearer token 401s on
/// every tool call. The lookups are injected, so these assert the resolution ORDER without
/// touching process environment (and without racing a parallel test class that does).
/// </summary>
[Trait("Category", "Node")]
public sealed class McpStdioEntryTests
{
    private static string? NoEnv(string _) => null;
    private static string? NoFile(string _) => null;

    [Fact]
    public void The_token_flag_wins_over_the_environment()
    {
        var token = McpStdioEntry.ResolveToken(
            ["mcp", "--node-url", "http://127.0.0.1:8080", "--token", "flag-token"],
            name => name == McpStdioEntry.TokenEnvVar ? "env-token" : null,
            NoFile);

        token.Should().Be("flag-token");
    }

    [Fact]
    public void The_environment_variable_is_used_when_there_is_no_flag()
    {
        var token = McpStdioEntry.ResolveToken(
            ["mcp"],
            name => name == McpStdioEntry.TokenEnvVar ? "env-token" : null,
            NoFile);

        token.Should().Be("env-token");
    }

    [Fact]
    public void A_token_file_is_read_when_neither_flag_nor_variable_is_set()
    {
        var token = McpStdioEntry.ResolveToken(
            ["mcp"],
            name => name == McpStdioEntry.TokenFileEnvVar ? "/run/pdn/mcp.token" : null,
            path => path == "/run/pdn/mcp.token" ? "  file-token\n" : null);

        token.Should().Be("file-token", "the file's contents are the token, trimmed of the trailing newline");
    }

    [Fact]
    public void No_token_anywhere_is_null_rather_than_a_failure()
    {
        // An auth-off node needs no token, and a stdio server must not die at startup: the
        // first tool call reports the auth requirement instead.
        McpStdioEntry.ResolveToken(["mcp"], NoEnv, NoFile).Should().BeNull();
    }

    [Fact]
    public void An_unreadable_token_file_degrades_to_no_token()
    {
        var token = McpStdioEntry.ResolveToken(
            ["mcp"],
            name => name == McpStdioEntry.TokenFileEnvVar ? "/nope/missing.token" : null,
            _ => null);

        token.Should().BeNull();
    }

    [Fact]
    public void The_resolved_token_becomes_the_clients_default_bearer_header()
    {
        using var client = new HttpClient();

        McpStdioEntry.ConfigureClient(client, McpStdioEntry.DefaultNodeUrl, "abc.def.ghi");

        client.BaseAddress.Should().Be(new Uri("http://127.0.0.1:8080"));
        client.DefaultRequestHeaders.Authorization.Should().NotBeNull();
        client.DefaultRequestHeaders.Authorization!.Scheme.Should().Be("Bearer");
        client.DefaultRequestHeaders.Authorization.Parameter.Should().Be("abc.def.ghi");
    }

    [Fact]
    public void With_no_token_the_client_sends_no_authorization_header()
    {
        using var client = new HttpClient();

        McpStdioEntry.ConfigureClient(client, McpStdioEntry.DefaultNodeUrl, token: null);

        client.DefaultRequestHeaders.Authorization.Should().BeNull("an auth-off node needs none");
    }
}
