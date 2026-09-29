using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Rhp;
using Packet.Node.Tests.Support;
using Packet.Rhp2;
using Packet.Rhp2.Server;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Extension E1 end to end (<c>docs/rhp2-server.md</c> § Extensions): an RHPv2 <c>open</c>
/// through the node's real gateway and dial, answered by a scripted station on the shared
/// in-memory channel. When the station calls our calling callsign while we dial it, or the link
/// to it is already up when we dial, the <c>openReply</c> carries <c>"crossed":true</c>; when it
/// just answers, the reply has no such key.
/// </summary>
[Trait("Category", "Node")]
public sealed class RhpOpenCrossedCallTests
{
    private static readonly Callsign NodeCall = new("N0BBB", 1);
    private static readonly Callsign AppCall = new("N0BBB", 3);
    private static readonly Callsign StationCall = new("N0AAA", 3);

    // The default port dials SABME after the pre-connect XID probe; `preConnectXid: off` goes
    // straight to the SABME, so the station's call lands while ours awaits its UA instead.
    private static NodeConfig Config(bool probe) => new()
    {
        Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
        Ports =
        [
            new PortConfig
            {
                Id = "p1",
                Enabled = true,
                Transport = new KissTcpTransport { Host = "mem", Port = 1 },
                Link = probe ? null : new PortLinkConfig { PreConnectXid = LinkPreConnectXid.Off },
            },
        ],
    };

    private const string OpenToStation =
        """{"type":"open","id":7,"pfam":"ax25","mode":"stream","port":"p1","local":"N0BBB-3","remote":"N0AAA-3","flags":128}""";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_station_that_calls_us_during_our_dial_gets_crossed_true(bool probe)
    {
        await using var rig = await Rig.StartAsync(Config(probe), callsBack: true);

        await rig.Client.SendRawAsync(OpenToStation);
        var json = await rig.Client.ReadUntilAsync("openReply");

        json.Should().Contain("\"errCode\":0");
        json.Should().EndWith(",\"crossed\":true}");
        rig.Station.CalledBack.Should().BeTrue("the station sent its own SABME to our calling callsign during the dial");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_station_that_only_answers_gets_no_crossed_key(bool probe)
    {
        await using var rig = await Rig.StartAsync(Config(probe), callsBack: false);

        await rig.Client.SendRawAsync(OpenToStation);
        var json = await rig.Client.ReadUntilAsync("openReply");

        json.Should().Contain("\"errCode\":0");
        json.Should().NotContain("crossed");
        rig.Station.CalledBack.Should().BeFalse();
    }

    [Fact]
    public async Task A_dial_on_a_link_that_is_already_up_gets_crossed_true()
    {
        // The station reached our app callsign first (the RHP listener got its accept), then
        // the client dials it from the same callsign: that dial only resets the link, so the
        // station's application is handed nothing new. The station does not call during the
        // dial, so this is the already-up trigger on its own.
        await using var rig = await Rig.StartAsync(Config(probe: true), callsBack: false);

        await rig.Client.SendRawAsync("""{"type":"socket","id":1,"pfam":"ax25","mode":"stream"}""");
        int listener = JsonInt(await rig.Client.ReadUntilAsync("socketReply"), "handle");
        await rig.Client.SendRawAsync($$"""{"type":"bind","id":2,"handle":{{listener}},"local":"N0BBB-3","port":"p1"}""");
        (await rig.Client.ReadUntilAsync("bindReply")).Should().Contain("\"errCode\":0");
        await rig.Client.SendRawAsync($$"""{"type":"listen","id":3,"handle":{{listener}}}""");
        (await rig.Client.ReadUntilAsync("listenReply")).Should().Contain("\"errCode\":0");

        await rig.Station.CallAsync();
        (await rig.Client.ReadUntilAsync("accept")).Should().Contain("\"remote\":\"N0AAA-3\"");

        await rig.Client.SendRawAsync(OpenToStation);
        var json = await rig.Client.ReadUntilAsync("openReply");

        json.Should().Contain("\"errCode\":0");
        json.Should().EndWith(",\"crossed\":true}");
        rig.Station.CalledBack.Should().BeFalse("only the earlier call reached us, none during the dial");
    }

    private static int JsonInt(string json, string key)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(key).GetInt32();
    }

    // The node (one port on the shared channel), an RHPv2 server over its real gateway, a raw
    // RHP client, and the scripted station.
    private sealed class Rig : IAsyncDisposable
    {
        private readonly NodeHostedService host;
        private readonly RhpServer server;

        private Rig(NodeHostedService host, RhpServer server, RawClient client, ScriptedStation station)
        {
            this.host = host;
            this.server = server;
            Client = client;
            Station = station;
        }

        public RawClient Client { get; }

        public ScriptedStation Station { get; }

        public static async Task<Rig> StartAsync(NodeConfig nodeConfig, bool callsBack)
        {
            var bus = new SharedRadioBus();
            var config = new TestConfigProvider(nodeConfig);
            var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", bus.Attach());
            var host = new NodeHostedService(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
            await host.StartAsync(CancellationToken.None);
            await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("p1") == true, "port p1 comes up");

            var station = new ScriptedStation(bus.Attach(), StationCall, AppCall, callsBack);
            var server = new RhpServer(new RhpServerOptions { Bind = IPAddress.Loopback, Port = 0 }, new SupervisorRhpGateway(host, config));
            await server.StartAsync();
            var client = await RawClient.ConnectAsync(server.BoundEndpoint!);
            return new Rig(host, server, client, station);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await server.DisposeAsync();
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
            await Station.DisposeAsync();
        }
    }

    // A station scripted at the frame level, so the moment it calls us is exact. It answers our
    // XID command with the same parameters and our SABM(E) or DISC with UA. When it calls back,
    // its own SABME to our calling callsign goes out BEFORE its answer to our first frame: during
    // the pre-connect XID probe on a probing port, while our SABME awaits its UA on the other.
    private sealed class ScriptedStation : IAsyncDisposable
    {
        private readonly IAx25Transport wire;
        private readonly Callsign me;
        private readonly Callsign caller;
        private readonly bool callsBack;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private int calledBack;

        public ScriptedStation(IAx25Transport wire, Callsign me, Callsign caller, bool callsBack)
        {
            this.wire = wire;
            this.me = me;
            this.caller = caller;
            this.callsBack = callsBack;
            loop = Task.Run(RunAsync);
        }

        public bool CalledBack => Volatile.Read(ref calledBack) != 0;

        /// <summary>Call the node's app callsign outright, outside any dial of ours.</summary>
        public Task CallAsync() => SendAsync(Ax25Frame.Sabme(caller, me));

        private async Task RunAsync()
        {
            try
            {
                await foreach (var inbound in wire.ReceiveAsync(stop.Token))
                {
                    if (!Ax25Frame.TryParse(inbound.Ax25.Span, Ax25ParseOptions.Lenient, out var frame)
                        || !frame.Destination.Callsign.Equals(me)
                        || !frame.Source.Callsign.Equals(caller)
                        || !frame.IsCommand)
                    {
                        continue;
                    }

                    switch (frame.FrameType)
                    {
                        case Ax25FrameType.Xid:
                            await CallBackOnceAsync();
                            await SendAsync(Ax25Frame.Xid(caller, me, frame.Info.Span, isCommand: false, pollFinal: frame.PollFinal));
                            break;
                        case Ax25FrameType.Sabm or Ax25FrameType.Sabme:
                            await CallBackOnceAsync();
                            await SendAsync(Ax25Frame.Ua(caller, me, finalBit: frame.PollFinal));
                            break;
                        case Ax25FrameType.Disc:
                            await SendAsync(Ax25Frame.Ua(caller, me, finalBit: frame.PollFinal));
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // disposed
            }
        }

        private async Task CallBackOnceAsync()
        {
            if (callsBack && Interlocked.Exchange(ref calledBack, 1) == 0)
            {
                await SendAsync(Ax25Frame.Sabme(caller, me));
            }
        }

        private Task SendAsync(Ax25Frame frame) => wire.SendAsync(frame.ToBytes());

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await loop;
            stop.Dispose();
        }
    }

    // A raw RHPv2 client: framed JSON text in and out, so the test sees the reply's exact keys.
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

        // Skips pushes (status, accept, ...) until a message of the wanted type arrives.
        public async Task<string> ReadUntilAsync(string type)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var frame = await RhpFraming.ReadFrameAsync(stream, cts.Token)
                    ?? throw new InvalidOperationException("The server closed the connection.");
                var json = Encoding.UTF8.GetString(frame);
                if (RhpJson.Deserialize(frame).Type == type)
                {
                    return json;
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
