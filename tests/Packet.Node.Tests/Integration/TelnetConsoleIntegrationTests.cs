using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The local dial-in path (exit criterion iii): a plain TCP client reaches the
/// prompt and the same commands work — no callsign, no KISS, no AX.25. Drives a
/// real <see cref="TelnetConsoleListener"/> bound to an ephemeral loopback port.
/// </summary>
[Trait("Category", "Node")]
public sealed class TelnetConsoleIntegrationTests
{
    private static NodeConfig NodeConfig() => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1", Alias = "TELNETNODE" },
        Ports = [],
    };

    private static TelnetConsoleListener BuildListener(out int chosenPortPlaceholder)
    {
        chosenPortPlaceholder = 0;
        var config = new TestConfigProvider(NodeConfig());
        // No outbound connector for telnet in this idle-node test → Connect reports
        // "not available", which is the correct slice-1 behaviour with no ports.
        return new TelnetConsoleListener(
            new TelnetConfig { Enabled = true, Bind = "127.0.0.1", Port = 0 },
            _ => new NodeCommandService(new NodeConsoleEnvironment(config, outboundConnector: null), NullLogger<NodeCommandService>.Instance),
            NullLogger<TelnetConsoleListener>.Instance);
    }

    [Fact]
    public async Task Telnet_client_reaches_the_prompt_and_Info_Help_Bye_work()
    {
        await using var listener = BuildListener(out _);
        await listener.StartAsync();
        var port = listener.BoundEndpoint!.Port;

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();

        var received = new StringBuilder();
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                while (!readCts.Token.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, readCts.Token);
                    if (n == 0) break;
                    lock (received) received.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        });

        async Task SendAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }

        bool Saw(string needle) { lock (received) return received.ToString().Contains(needle, StringComparison.Ordinal); }

        // Banner on connect.
        await Wait.ForAsync(() => Saw("TELNETNODE"), "telnet banner should arrive");

        await SendAsync("I");
        await Wait.ForAsync(() => Saw("Software: Packet.NET"), "Info works over telnet");

        await SendAsync("?");
        await Wait.ForAsync(() => Saw("Commands:"), "Help works over telnet");

        await SendAsync("N");
        await Wait.ForAsync(() => Saw("Ports:"), "Nodes works over telnet");

        await SendAsync("B");
        await Wait.ForAsync(() => Saw("73"), "Bye is acknowledged over telnet");

        // The server closes the socket after Bye.
        await Wait.ForAsync(() => !client.Connected || reader.IsCompleted, "the socket closes after Bye", 4000);
        await readCts.CancelAsync();
    }

    [Fact]
    public async Task Unknown_command_over_telnet_re_prompts_without_dropping()
    {
        await using var listener = BuildListener(out _);
        await listener.StartAsync();
        var port = listener.BoundEndpoint!.Port;

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();
        var received = new StringBuilder();
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                while (!readCts.Token.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, readCts.Token);
                    if (n == 0) break;
                    lock (received) received.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        });
        bool Saw(string needle) { lock (received) return received.ToString().Contains(needle, StringComparison.Ordinal); }

        await Wait.ForAsync(() => Saw("TELNETNODE"), "banner");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("WIBBLE\r\n"));
        await stream.FlushAsync();
        await Wait.ForAsync(() => Saw("Unknown command"), "unknown reported");
        client.Connected.Should().BeTrue("an unknown command must not drop the telnet session");
        await readCts.CancelAsync();
    }
}
