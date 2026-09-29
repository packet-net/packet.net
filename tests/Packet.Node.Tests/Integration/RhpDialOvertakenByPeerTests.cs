using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Ax25.Xid;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Rhp;
using Packet.Node.Tests.Support;
using Packet.Rhp2;
using Packet.Rhp2.Server;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// packet.net#867 end to end, as DAPPS saw it: station A's XID probe reaches node B just as B's
/// RHPv2 client opens a stream to A, then A's SABME and A's answer to B's probe follow. The
/// pump's cache write used to overwrite the session B's dial had just created, so the dial never
/// saw A's frames: it went on to XID and SABME for 40 s and more, and failed with errCode 15,
/// while the link A's call set up acknowledged A's data with no handle attached. The dial must
/// complete on the link A's call set up (crossed:true) and B's client must get A's data.
/// </summary>
/// <remarks>
/// A logger on B's listener holds the pump inside the pre-session XID responder, after its cache
/// lookup missed and before it caches a session, until B's dial has created its own: the race
/// DAPPS hit about one run in five, made to happen every time.
/// </remarks>
[Trait("Category", "Node")]
public sealed class RhpDialOvertakenByPeerTests
{
    private static readonly Callsign NodeB = new("N0BBB", 1);
    private static readonly Callsign AppB = new("N0BBB", 3);
    private static readonly Callsign AppA = new("N0AAA", 3);

    [Fact]
    public async Task A_dial_that_starts_as_the_peer_s_XID_arrives_completes_on_the_peer_s_link_and_delivers_its_data()
    {
        var bus = new SharedRadioBus();
        var hold = new HoldingLoggerFactory(eventId: 5222);
        var config = new TestConfigProvider(new NodeConfig
        {
            Identity = new Identity { Callsign = NodeB.ToString(), Alias = "NODEB" },
            Ports = [new PortConfig { Id = "p1", Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 1 } }],
        });
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", bus.Attach());
        using var host = new NodeHostedService(config, factory, TimeProvider.System, hold);
        await host.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("p1") == true, "port p1 comes up");

        await using var server = new RhpServer(new RhpServerOptions { Bind = IPAddress.Loopback, Port = 0 }, new SupervisorRhpGateway(host, config));
        await server.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.BoundEndpoint!);
        var stream = tcp.GetStream();

        // DAPPS on B listens on its app callsign, as it does in the crossed-call scenario.
        await SendAsync(stream, """{"type":"socket","id":1,"pfam":"ax25","mode":"stream"}""");
        var listenerHandle = System.Text.Json.JsonDocument.Parse(await ReadUntilAsync(stream, "socketReply")).RootElement.GetProperty("handle").GetInt32();
        await SendAsync(stream, $$"""{"type":"bind","id":2,"handle":{{listenerHandle}},"local":"N0BBB-3","port":"p1"}""");
        (await ReadUntilAsync(stream, "bindReply")).Should().Contain("\"errCode\":0");
        await SendAsync(stream, $$"""{"type":"listen","id":3,"handle":{{listenerHandle}}}""");
        (await ReadUntilAsync(stream, "listenReply")).Should().Contain("\"errCode\":0");

        var a = bus.Attach();
        var heardByA = new Heard(a);

        // A's probe arrives; B's pump is held in the XID responder...
        await a.SendAsync(Ax25Frame.Xid(AppB, AppA, XidInfoField.Encode(Ax25ManagementDataLink.DefaultOfferFor(
            new Ax25SessionContext { Local = AppA, Remote = AppB, IsExtended = true, SrejEnabled = true })), isCommand: true, pollFinal: true).ToBytes());
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // ...while B's client opens to A, and B's dial creates its session and sends its probe.
        await SendAsync(stream, """{"type":"open","id":7,"pfam":"ax25","mode":"stream","port":"p1","local":"N0BBB-3","remote":"N0AAA-3","flags":128}""");
        var probe = await heardByA.NextAsync(f => f.FrameType == Ax25FrameType.Xid && f.IsCommand, "B's dial probes A");
        hold.Release.Set();

        // A's call, then A's answer to B's probe; once B answers the call A speaks at once.
        await a.SendAsync(Ax25Frame.Sabme(AppB, AppA).ToBytes());
        await a.SendAsync(Ax25Frame.Xid(AppB, AppA, probe.Info.Span, isCommand: false, pollFinal: true).ToBytes());
        await heardByA.NextAsync(f => f.FrameType == Ax25FrameType.Ua, "B answers A's call");
        await a.SendAsync(Ax25Frame.I(AppB, AppA, nr: 0, ns: 0, "exchange\r"u8, extended: true).ToBytes());

        var reply = await ReadUntilAsync(stream, "openReply");
        reply.Should().Contain("\"errCode\":0").And.EndWith(",\"crossed\":true}");
        var recv = await ReadUntilAsync(stream, "recv");
        recv.Should().Contain("exchange", "A's data reaches the handle B's client opened, not nobody");
        heardByA.Any(IsEstablish).Should().BeFalse("B's dial took the link A's call set up, and did not re-dial it");

        await host.StopAsync(CancellationToken.None);
    }

    private static bool IsEstablish(Ax25Frame f) => f.FrameType is Ax25FrameType.Sabm or Ax25FrameType.Sabme;

    private static Task SendAsync(NetworkStream stream, string json) => RhpFraming.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(json));

    private static async Task<string> ReadUntilAsync(NetworkStream stream, string type)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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

    // Everything B sends that station A hears.
    private sealed class Heard
    {
        private readonly List<Ax25Frame> frames = [];

        public Heard(IAx25Transport wire)
        {
            _ = Task.Run(async () =>
            {
                await foreach (var inbound in wire.ReceiveAsync())
                {
                    if (Ax25Frame.TryParse(inbound.Ax25.Span, Ax25ParseOptions.Lenient, out var f)
                        && f.Destination.Callsign.Equals(AppA) && f.Source.Callsign.Equals(AppB))
                    {
                        lock (frames) { frames.Add(f); }
                    }
                }
            });
        }

        public bool Any(Func<Ax25Frame, bool> match)
        {
            lock (frames) { return frames.Any(match); }
        }

        public async Task<Ax25Frame> NextAsync(Func<Ax25Frame, bool> match, string because)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (frames)
                {
                    if (frames.FirstOrDefault(match) is { } f)
                    {
                        return f;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"A did not hear: {because}");
        }
    }

    // Holds the pump on the first listener log event with the given id until released.
    private sealed class HoldingLoggerFactory(int eventId) : ILoggerFactory
    {
        private int held;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new();

        public ILogger CreateLogger(string categoryName)
            => categoryName.EndsWith(".Ax25Listener", StringComparison.Ordinal) ? new Holding(this) : NullLogger.Instance;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Holding(HoldingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (id.Id == owner.eventIdToHold && Interlocked.Exchange(ref owner.held, 1) == 0)
                {
                    owner.Entered.TrySetResult();
                    owner.Release.Wait(TimeSpan.FromSeconds(15));
                }
            }
        }

        private readonly int eventIdToHold = eventId;
    }
}
