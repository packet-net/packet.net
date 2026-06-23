using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Axudp;
using Packet.Core;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Transports;

/// <summary>
/// The <see cref="AxudpMultipointFrameTransport"/> (the BPQ <c>BPQAXIP</c> analog), exercised
/// over real loopback UDP: ONE socket, MANY callsign-mapped peers. Outbound frames route by
/// the AX.25 destination callsign to the matching peer; NODES/ID/BEACON broadcasts fan out to
/// every <c>broadcast=true</c> peer; inbound datagrams are accepted from any sender and
/// surfaced as neutral <see cref="Ax25InboundFrame"/>s. A learned source endpoint is the reply
/// fallback for an unmapped station.
/// </summary>
public sealed class AxudpMultipointFrameTransportTests
{
    private static byte[] Ui(string dest, string source, string info) =>
        Ax25Frame.Ui(new Callsign(dest, 0), new Callsign(source, 0), System.Text.Encoding.ASCII.GetBytes(info)).ToBytes();

    // A raw UDP peer that strips + validates the AXUDP FCS, so a test reads the bare body.
    private static byte[] StripFcs(byte[] datagram)
    {
        var body = datagram.AsSpan(0, datagram.Length - 2);
        ushort expected = Crc16Ccitt.Compute(body);
        ushort actual = (ushort)(datagram[^2] | (datagram[^1] << 8));
        expected.Should().Be(actual, "the multipoint transport always appends a valid FCS");
        return body.ToArray();
    }

    [Fact]
    public async Task Routes_an_outbound_frame_to_the_peer_matching_the_destination_callsign()
    {
        // Two MAPped peers on two raw sockets. A frame to GB7AAA must reach peerA's endpoint
        // ONLY; a frame to GB7BBB must reach peerB's endpoint ONLY.
        using var peerA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var peerB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var portA = ((IPEndPoint)peerA.Client.LocalEndPoint!).Port;
        var portB = ((IPEndPoint)peerB.Client.LocalEndPoint!).Port;

        await using var transport = new AxudpMultipointFrameTransport(
            [
                new(new Callsign("GB7AAA"), new IPEndPoint(IPAddress.Loopback, portA), Broadcast: false),
                new(new Callsign("GB7BBB"), new IPEndPoint(IPAddress.Loopback, portB), Broadcast: false),
            ],
            localPort: 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var toA = Ui("GB7AAA", "M0LTE", "for-a");
        var toB = Ui("GB7BBB", "M0LTE", "for-b");

        var recvA = peerA.ReceiveAsync(cts.Token);
        await transport.SendAsync(toA, cts.Token);
        StripFcs((await recvA).Buffer).Should().Equal(toA, "the frame to GB7AAA routed to peer A");

        var recvB = peerB.ReceiveAsync(cts.Token);
        await transport.SendAsync(toB, cts.Token);
        StripFcs((await recvB).Buffer).Should().Equal(toB, "the frame to GB7BBB routed to peer B");
    }

    [Fact]
    public async Task Fans_a_NODES_broadcast_out_to_every_broadcast_peer_only()
    {
        // peerA + peerB are broadcast=true; peerC is broadcast=false. A NODES UI frame must
        // reach A and B but NOT C.
        using var peerA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var peerB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var peerC = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var portA = ((IPEndPoint)peerA.Client.LocalEndPoint!).Port;
        var portB = ((IPEndPoint)peerB.Client.LocalEndPoint!).Port;
        var portC = ((IPEndPoint)peerC.Client.LocalEndPoint!).Port;

        await using var transport = new AxudpMultipointFrameTransport(
            [
                new(new Callsign("GB7AAA"), new IPEndPoint(IPAddress.Loopback, portA), Broadcast: true),
                new(new Callsign("GB7BBB"), new IPEndPoint(IPAddress.Loopback, portB), Broadcast: true),
                new(new Callsign("GB7CCC"), new IPEndPoint(IPAddress.Loopback, portC), Broadcast: false),
            ],
            localPort: 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var nodes = Ui("NODES", "M0LTE", "broadcast");

        var recvA = peerA.ReceiveAsync(cts.Token);
        var recvB = peerB.ReceiveAsync(cts.Token);
        await transport.SendAsync(nodes, cts.Token);

        StripFcs((await recvA).Buffer).Should().Equal(nodes, "the NODES broadcast fanned to peer A");
        StripFcs((await recvB).Buffer).Should().Equal(nodes, "the NODES broadcast fanned to peer B");

        // peerC (broadcast=false) must NOT have received it: a follow-up directed frame to it
        // arrives, proving the broadcast did not leak to C (the directed frame is the only one).
        var directToC = Ui("GB7CCC", "M0LTE", "directed");
        var recvC = peerC.ReceiveAsync(cts.Token);
        await transport.SendAsync(directToC, cts.Token);
        StripFcs((await recvC).Buffer).Should().Equal(directToC,
            "peer C's first datagram is the directed frame — the broadcast never reached it");
    }

    [Fact]
    public async Task Accepts_inbound_from_many_senders_and_surfaces_each_as_a_neutral_frame()
    {
        // No peers configured for inbound — the listener routes by callsign; the transport just
        // accepts from anyone on the one socket and yields the bare body.
        await using var transport = new AxudpMultipointFrameTransport([], localPort: 0);
        using var senderA = new AxudpMultipointSocket(localPort: 0);
        using var senderB = new AxudpMultipointSocket(localPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var to = new IPEndPoint(IPAddress.Loopback, transport.LocalPort);

        var frames = new List<Ax25InboundFrame>();
        var reader = Task.Run(async () =>
        {
            await foreach (var f in transport.ReceiveAsync(cts.Token))
            {
                frames.Add(f);
                if (frames.Count == 2)
                {
                    break;
                }
            }
        }, cts.Token);

        await senderA.SendAsync(to, Ui("M0LTE", "PEERA", "a"), cts.Token);
        await senderB.SendAsync(to, Ui("M0LTE", "PEERB", "b"), cts.Token);
        await reader;

        frames.Should().HaveCount(2);
        frames.Select(f => f.Ax25.ToArray()).Should().Contain(x => x.SequenceEqual(Ui("M0LTE", "PEERA", "a")));
        frames.Select(f => f.Ax25.ToArray()).Should().Contain(x => x.SequenceEqual(Ui("M0LTE", "PEERB", "b")));
        frames.Should().OnlyContain(f => f.PortId == 0, "AXUDP-multipoint is one shared channel");
    }

    [Fact]
    public async Task Two_transports_round_trip_a_frame_over_their_callsign_map()
    {
        // A real two-socket round-trip: node 1 (M0LTE) MAPs node 2 (GB7RDG) and dials it; node 2
        // MAPs node 1 and replies. Each routes the reply by destination callsign back to the peer.
        await using var node2 = new AxudpMultipointFrameTransport([], localPort: 0);
        await using var node1 = new AxudpMultipointFrameTransport(
            [new(new Callsign("GB7RDG"), new IPEndPoint(IPAddress.Loopback, node2.LocalPort), Broadcast: false)],
            localPort: 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // node1 → node2
        var toNode2 = Ui("GB7RDG", "M0LTE", "ping");
        var node2Read = FirstFrameAsync(node2, cts.Token);
        await node1.SendAsync(toNode2, cts.Token);
        var atNode2 = await node2Read;
        atNode2.Ax25.ToArray().Should().Equal(toNode2);

        // node2 learned M0LTE's endpoint from that datagram → it can reply by callsign with no
        // explicit MAP (the learned-fallback path).
        var toNode1 = Ui("M0LTE", "GB7RDG", "pong");
        var node1Read = FirstFrameAsync(node1, cts.Token);
        await node2.SendAsync(toNode1, cts.Token);
        var atNode1 = await node1Read;
        atNode1.Ax25.ToArray().Should().Equal(toNode1, "node 2 replied via the learned source endpoint");
    }

    [Fact]
    public async Task A_directed_frame_to_an_unmapped_unheard_station_is_dropped_not_fanned_out()
    {
        // A broadcast peer is configured, but a DIRECTED frame to a station we have no MAP for
        // and have never heard from must NOT leak to the broadcast peer (only NODES/ID/BEACON
        // pseudo-destinations fan out). Prove it by sending the directed frame then a real
        // NODES broadcast: the peer's FIRST datagram must be the NODES one.
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;

        await using var transport = new AxudpMultipointFrameTransport(
            [new(new Callsign("GB7AAA"), new IPEndPoint(IPAddress.Loopback, port), Broadcast: true)],
            localPort: 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await transport.SendAsync(Ui("ZZ9XYZ", "M0LTE", "directed-nowhere"), cts.Token);   // dropped
        var nodes = Ui("NODES", "M0LTE", "fan");
        var recv = peer.ReceiveAsync(cts.Token);
        await transport.SendAsync(nodes, cts.Token);

        StripFcs((await recv).Buffer).Should().Equal(nodes,
            "the directed frame to the unmapped station was dropped; the broadcast peer's first datagram is the NODES one");
    }

    [Fact]
    public void Offers_no_csma_no_txcompletion_capabilities()
    {
        IAx25Transport transport = new AxudpMultipointFrameTransport([], localPort: 0);
        (transport is ICsmaChannelParams).Should().BeFalse("a UDP mesh has no CSMA channel-access");
        (transport is ITxCompletionTransport).Should().BeFalse("there is no TNC to echo a TX-completion");
    }

    [Fact]
    public async Task Logs_a_first_contact_outbound_transition_per_peer_not_per_frame()
    {
        // Cutover observability: the FIRST send to a peer renders a Debug "sent to" line; a
        // SECOND send to the same peer (no silence) does NOT — it is a transition log, so a
        // busy port stays quiet after first contact.
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;
        var log = new CapturingLogger<AxudpMultipointFrameTransport>();

        await using var transport = new AxudpMultipointFrameTransport(
            [new(new Callsign("GB7NDH"), new IPEndPoint(IPAddress.Loopback, port), Broadcast: false)],
            localPort: 0, logger: log);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = Ui("GB7NDH", "M0LTE", "x");

        var recv = peer.ReceiveAsync(cts.Token);
        await transport.SendAsync(frame, cts.Token);
        await recv;
        await transport.SendAsync(frame, cts.Token);   // second send — must NOT re-log

        log.Render(LogLevel.Debug).Should().ContainSingle(
            m => m.Contains("sent to GB7NDH", StringComparison.Ordinal) && m.Contains("broadcast=False", StringComparison.Ordinal),
            "first contact logs once at Debug; the busy follow-up send does not re-log");
    }

    [Fact]
    public async Task Logs_inbound_heard_at_trace_and_a_learned_endpoint_at_debug()
    {
        // Cutover observability: every inbound datagram renders a Trace "heard {Source}" line;
        // the FIRST datagram from a new source endpoint also renders a Debug "learned peer
        // endpoint" line (a transition — a second datagram from the same endpoint does not).
        var log = new CapturingLogger<AxudpMultipointFrameTransport>();
        await using var transport = new AxudpMultipointFrameTransport([], localPort: 0, logger: log);
        using var sender = new AxudpMultipointSocket(localPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var to = new IPEndPoint(IPAddress.Loopback, transport.LocalPort);

        var seen = 0;
        var reader = Task.Run(async () =>
        {
            await foreach (var _ in transport.ReceiveAsync(cts.Token))
            {
                if (Interlocked.Increment(ref seen) == 2)
                {
                    break;
                }
            }
        }, cts.Token);

        await sender.SendAsync(to, Ui("M0LTE", "GB7BDH", "a"), cts.Token);
        await sender.SendAsync(to, Ui("M0LTE", "GB7BDH", "b"), cts.Token);
        await reader;

        log.Render(LogLevel.Trace).Should().Contain(
            m => m.Contains("heard GB7BDH", StringComparison.Ordinal),
            "each inbound datagram renders a Trace heard line");
        log.Render(LogLevel.Debug).Should().ContainSingle(
            m => m.Contains("learned peer endpoint GB7BDH", StringComparison.Ordinal),
            "the source endpoint is learned-and-logged once, on first contact");
    }

    private static async Task<Ax25InboundFrame> FirstFrameAsync(AxudpMultipointFrameTransport transport, CancellationToken ct)
    {
        await foreach (var f in transport.ReceiveAsync(ct).ConfigureAwait(false))
        {
            return f;
        }
        throw new InvalidOperationException("no frame arrived");
    }

    // Captures rendered log STRINGS per level so a test asserts the message a human reads — not
    // just that something was logged (catches a placeholder/arg swap the source-gen would hide).
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly object gate = new();
        private readonly List<(LogLevel Level, string Text)> messages = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            lock (gate)
            {
                messages.Add((level, fmt(state, ex)));
            }
        }
        public List<string> Render(LogLevel level)
        {
            lock (gate)
            {
                return messages.Where(m => m.Level == level).Select(m => m.Text).ToList();
            }
        }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
