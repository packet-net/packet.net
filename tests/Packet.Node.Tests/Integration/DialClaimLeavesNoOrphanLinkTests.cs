using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;
using Packet.Node.Rhp;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// #867, the node's half: while a dial is in progress the supervisor drops the dialled peer's
/// SessionAccepted, so that the dial's own link does not start a console against the station it
/// dials. A link that comes up under that claim must still end up with an owner: the dial, a
/// console or app accept, or nobody at all, but never up with nobody attached, acknowledging the
/// peer's I frames and delivering them nowhere.
/// </summary>
[Trait("Category", "Node")]
public sealed class DialClaimLeavesNoOrphanLinkTests
{
    private static readonly Callsign NodeCall = new("N0BBB", 1);
    private static readonly Callsign AppCall = new("N0BBB", 3);
    private static readonly Callsign Station = new("N0AAA", 3);

    [Fact]
    public async Task A_call_to_the_node_s_own_callsign_during_an_app_s_dial_still_gets_its_console()
    {
        // An app dials the station from its own callsign; the station calls the NODE's callsign
        // in the middle of it. The claim used to cover the station on the whole port, so the
        // node answered UA and then left that link with no console.
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(new NodeConfig
        {
            Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
            Ports = [new PortConfig { Id = "p1", Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 1 } }],
        });
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", bus.Attach());
        using var host = new NodeHostedService(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("p1") == true, "port p1 comes up");

        await using var station = new ScriptedStation(bus.Attach());
        var gateway = new SupervisorRhpGateway(host, config);

        await using var conn = await gateway.OpenAx25StreamAsync("p1", AppCall.ToString(), Station.ToString());

        station.CalledTheNode.Should().BeTrue("the station called the node's callsign while the app's dial was in its probe");
        await Wait.ForAsync(() => station.HeardFromTheNode.Contains("Welcome", StringComparison.Ordinal),
            "the node's console greets the station on the link its call set up", TimeSpan.FromSeconds(15));
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_dial_that_fails_after_the_peer_s_call_set_up_the_link_ends_that_link()
    {
        // The peer's call brings the dial's own link up during the XID probe, then the dial is
        // cancelled before it returns (the RHPv2 client went away, say). The supervisor dropped
        // the link's accept under the dial's claim, so it would be left up with nobody holding it.
        var bus = new SharedRadioBus();
        var config = new TestConfigProvider(new NodeConfig
        {
            Identity = new Identity { Callsign = NodeCall.ToString(), Alias = "TESTNODE" },
            Ports = [new PortConfig { Id = "p1", Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 1 } }],
        });
        var factory = new FakeTransportFactory().Provide("kiss-tcp:mem:1", bus.Attach());
        using var host = new NodeHostedService(config, factory, TimeProvider.System, NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        await Wait.ForAsync(() => host.Supervisor?.RunningPortIds.Contains("p1") == true, "port p1 comes up");
        var connector = host.Supervisor!.ResolveConnector("p1", AppCall)!;

        var theirs = bus.Attach();
        var heard = Channel.CreateUnbounded<Ax25Frame>();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var f in theirs.ReceiveAsync(stop.Token))
                {
                    if (Ax25Frame.TryParse(f.Ax25.Span, Ax25ParseOptions.Lenient, out var frame)
                        && frame.Source.Callsign.Equals(AppCall) && frame.Destination.Callsign.Equals(Station))
                    {
                        heard.Writer.TryWrite(frame);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        using var dialCancel = new CancellationTokenSource();
        var dial = connector.ConnectAsync(Station, dialCancel.Token);

        // The station calls instead of answering the probe, so the dial is still probing.
        await NextAsync(heard, f => f.FrameType == Ax25FrameType.Xid && f.IsCommand, "our dial's XID probe");
        await theirs.SendAsync(Ax25Frame.Sabme(AppCall, Station).ToBytes());
        await NextAsync(heard, f => f.FrameType == Ax25FrameType.Ua, "the station's call is answered, so the link is up");

        await dialCancel.CancelAsync();
        var cancelled = async () => await dial;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        await NextAsync(heard, f => f.FrameType == Ax25FrameType.Disc, "the link nobody holds is ended, not left acknowledging data");
        await stop.CancelAsync();
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_dial_without_a_host_claim_leaves_the_peer_s_link_to_whoever_accepted_it()
    {
        // With no claim, nothing dropped the link's accept (SessionAccepted went to the
        // listener's own subscribers), so a failed dial is not the link's last chance of an
        // owner and must not end it.
        var (ours, theirs) = InMemoryRadio.CreatePair();
        await using var listener = new Ax25Listener(ours, new Ax25ListenerOptions { MyCall = NodeCall }, TimeProvider.System);
        await listener.StartAsync();
        var connector = new Ax25OutboundConnector("p1", listener, claim: null, localOverride: AppCall);

        var heard = Channel.CreateUnbounded<Ax25Frame>();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var f in theirs.ReceiveAsync(stop.Token))
                {
                    if (Ax25Frame.TryParse(f.Ax25.Span, Ax25ParseOptions.Lenient, out var frame))
                    {
                        heard.Writer.TryWrite(frame);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        using var dialCancel = new CancellationTokenSource();
        var dial = connector.ConnectAsync(Station, dialCancel.Token);
        await NextAsync(heard, f => f.FrameType == Ax25FrameType.Xid && f.IsCommand, "our dial's XID probe");
        await theirs.SendAsync(Ax25Frame.Sabme(AppCall, Station).ToBytes());
        await NextAsync(heard, f => f.FrameType == Ax25FrameType.Ua, "the station's call is answered");

        await dialCancel.CancelAsync();
        var cancelled = async () => await dial;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        await Task.Delay(500);
        listener.ActiveSessions.Single(s => s.Context.Local.Equals(AppCall)).CurrentState.Should().Be("Connected");
        await stop.CancelAsync();
    }

    private static async Task NextAsync(Channel<Ax25Frame> heard, Func<Ax25Frame, bool> match, string because)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            Ax25Frame frame;
            try
            {
                frame = await heard.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"did not hear: {because}");
            }

            if (match(frame))
            {
                return;
            }
        }
    }

    // The station: answers the app's XID probe after first calling the NODE's callsign, answers
    // the app's SABME, and records what the node's console sends it.
    private sealed class ScriptedStation : IAsyncDisposable
    {
        private readonly IAx25Transport wire;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private readonly System.Text.StringBuilder fromNode = new();
        private int calledNode;

        public ScriptedStation(IAx25Transport wire)
        {
            this.wire = wire;
            loop = Task.Run(RunAsync);
        }

        public bool CalledTheNode => Volatile.Read(ref calledNode) != 0;

        public string HeardFromTheNode
        {
            get { lock (fromNode) { return fromNode.ToString(); } }
        }

        private async Task RunAsync()
        {
            try
            {
                await foreach (var inbound in wire.ReceiveAsync(stop.Token))
                {
                    if (!Ax25Frame.TryParse(inbound.Ax25.Span, Ax25ParseOptions.Lenient, out var frame)
                        || !frame.Destination.Callsign.Equals(Station))
                    {
                        continue;
                    }

                    if (frame.Source.Callsign.Equals(NodeCall) && frame.FrameType == Ax25FrameType.I)
                    {
                        lock (fromNode) { fromNode.Append(System.Text.Encoding.ASCII.GetString(frame.Info.Span)); }
                        continue;
                    }

                    if (!frame.Source.Callsign.Equals(AppCall) || !frame.IsCommand)
                    {
                        continue;
                    }

                    switch (frame.FrameType)
                    {
                        case Ax25FrameType.Xid:
                            if (Interlocked.Exchange(ref calledNode, 1) == 0)
                            {
                                await SendAsync(Ax25Frame.Sabme(NodeCall, Station));
                            }

                            await SendAsync(Ax25Frame.Xid(AppCall, Station, frame.Info.Span, isCommand: false, pollFinal: frame.PollFinal));
                            break;
                        case Ax25FrameType.Sabm or Ax25FrameType.Sabme or Ax25FrameType.Disc:
                            await SendAsync(Ax25Frame.Ua(AppCall, Station, finalBit: frame.PollFinal));
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // disposed
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
}
