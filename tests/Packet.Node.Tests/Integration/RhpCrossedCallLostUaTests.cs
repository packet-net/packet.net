using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Rhp;
using Packet.Node.Tests.Support;
using Packet.Rhp2;
using Packet.Rhp2.Server;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// packet.net#856, the shape DAPPS found it in: two pdn nodes on one channel, each with an
/// RHPv2 client that opens a stream to the other's app callsign at the same moment. One UA of
/// the crossing is lost on the air, so node A connects while node B is still waiting, and A's
/// client, told <c>"crossed":true</c>, sends at once. B's retried SABM(E) then reached a
/// connected A, which reset the link (figc4.4) and threw that data away. With
/// <c>Ax25Spec50RepeatedConnectSabmReacknowledged</c> A answers the retry with UA again and the data gets
/// through.
/// </summary>
[Trait("Category", "Node")]
public sealed class RhpCrossedCallLostUaTests
{
    private static readonly Callsign NodeA = new("N0AAA", 1);
    private static readonly Callsign AppA = new("N0AAA", 3);
    private static readonly Callsign NodeB = new("N0BBB", 1);
    private static readonly Callsign AppB = new("N0BBB", 3);

    private static NodeConfig Config(Callsign node) => new()
    {
        Identity = new Identity { Callsign = node.ToString(), Alias = node.Base },
        Ports =
        [
            new PortConfig
            {
                Id = "p1",
                Enabled = true,
                Transport = new KissTcpTransport { Host = node.Base, Port = 1 },
                Ax25 = new Ax25PortParams { T1Ms = 600 },
            },
        ],
    };

    [Fact]
    public async Task Data_sent_on_crossed_true_arrives_when_the_other_node_retries_its_SABM()
    {
        var bus = new SharedRadioBus();

        // Lose the first UA node A sends: its answer to node B's SABM(E).
        var lostOne = 0;
        var wireA = bus.Attach(frame =>
            Ax25Frame.TryParse(frame.Span, Ax25ParseOptions.Lenient, out var f)
            && f.FrameType == Ax25FrameType.Ua
            && Interlocked.Exchange(ref lostOne, 1) == 0);

        await using var a = await Node.StartAsync(NodeA, wireA);
        await using var b = await Node.StartAsync(NodeB, bus.Attach());

        // Both clients open at once, as the two DAPPS daemons do.
        await Task.WhenAll(
            a.Client.SendRawAsync("""{"type":"open","id":7,"pfam":"ax25","mode":"stream","port":"p1","local":"N0AAA-3","remote":"N0BBB-3","flags":128}"""),
            b.Client.SendRawAsync("""{"type":"open","id":7,"pfam":"ax25","mode":"stream","port":"p1","local":"N0BBB-3","remote":"N0AAA-3","flags":128}"""));

        var replyA = await a.Client.ReadUntilAsync("openReply");
        replyA.Should().Contain("\"errCode\":0");
        Volatile.Read(ref lostOne).Should().Be(1, "node A's UA to node B was lost, so B is still dialling");

        // What DAPPS does on crossed:true: speak at once.
        int handleA = JsonInt(replyA, "handle");
        await a.Client.SendRawAsync($$"""{"type":"send","id":8,"handle":{{handleA}},"data":"exchange\r"}""");

        var replyB = await b.Client.ReadUntilAsync("openReply");
        replyB.Should().Contain("\"errCode\":0");

        var recv = await b.Client.ReadUntilAsync("recv");
        recv.Should().Contain("exchange", "the data A sent before B connected reaches B's client");
    }

    private static int JsonInt(string json, string key)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(key).GetInt32();
    }

    // A node on the shared channel with an RHPv2 server over its real gateway and a raw client.
    private sealed class Node : IAsyncDisposable
    {
        private readonly NodeHostedService host;
        private readonly RhpServer server;

        private Node(NodeHostedService host, RhpServer server, RawClient client)
        {
            this.host = host;
            this.server = server;
            Client = client;
        }

        public RawClient Client { get; }

        public static async Task<Node> StartAsync(Callsign call, Packet.Ax25.Transport.IAx25Transport wire)
        {
            var config = new TestConfigProvider(Config(call));
            var factory = new FakeTransportFactory().Provide($"kiss-tcp:{call.Base}:1", wire);
            var host = new NodeHostedService(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
            await host.StartAsync(CancellationToken.None);
            await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("p1") == true, $"{call} port p1 comes up");

            var server = new RhpServer(new RhpServerOptions { Bind = IPAddress.Loopback, Port = 0 }, new SupervisorRhpGateway(host, config));
            await server.StartAsync();
            var client = await RawClient.ConnectAsync(server.BoundEndpoint!);
            return new Node(host, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await server.DisposeAsync();
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    private sealed class RawClient : IAsyncDisposable
    {
        private readonly TcpClient tcp;
        private readonly NetworkStream stream;

        private RawClient(TcpClient tcp)
        {
            this.tcp = tcp;
            stream = tcp.GetStream();
        }

        public static async Task<RawClient> ConnectAsync(IPEndPoint endpoint)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(endpoint);
            return new RawClient(tcp);
        }

        public Task SendRawAsync(string json) => RhpFraming.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(json));

        // Skips other messages until one of the wanted type arrives.
        public async Task<string> ReadUntilAsync(string type)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var frame = await RhpFraming.ReadFrameAsync(stream, cts.Token)
                    ?? throw new InvalidOperationException("The server closed the connection.");
                if (RhpJson.Deserialize(frame).Type == type)
                {
                    return Encoding.UTF8.GetString(frame);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
