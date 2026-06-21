using System.Net;
using System.Net.Sockets;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Axudp;
using Packet.Core;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Transports;

/// <summary>
/// The <see cref="AxudpFrameTransport"/>, exercised over real loopback UDP: it presents an
/// <see cref="AxudpSocket"/> as a native <see cref="IAx25Transport"/>. Send puts the AX.25
/// frame body + the 2-octet FCS into a datagram; receive (via <see cref="AxudpSocket"/>) strips
/// + validates the FCS and surfaces each datagram directly as an <see cref="Ax25InboundFrame"/>
/// — no KISS object is ever constructed. AXUDP always carries the FCS — there is no FCS-less form.
/// </summary>
public sealed class AxudpFrameTransportTests
{
    private static Ax25Frame Ui(string info) => Ax25Frame.Ui(
        destination: new Callsign("APRS", 0),
        source: new Callsign("G7XYZ", 7),
        info: System.Text.Encoding.ASCII.GetBytes(info));

    [Fact]
    public async Task Send_always_appends_the_two_octet_fcs_low_byte_first()
    {
        // AXUDP unconditionally carries the FCS, so the transport must put the 2-octet
        // FCS on the wire — that is how it talks to LinBPQ BPQAXIP / XRouter / ax25ipd out
        // of the box. Observe the raw wire with a plain UdpClient (AxudpSocket would strip
        // the FCS on receive).
        using var rawPeer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerPort = ((IPEndPoint)rawPeer.Client.LocalEndPoint!).Port;
        var frame = Ui("x");
        var body = frame.ToBytes();
        ushort fcs = Crc16Ccitt.Compute(body);

        await using var transport = new AxudpFrameTransport(
            new IPEndPoint(IPAddress.Loopback, peerPort), localPort: 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receiveTask = rawPeer.ReceiveAsync(cts.Token);
        await transport.SendAsync(body, cts.Token);
        var datagram = (await receiveTask).Buffer;

        datagram.Length.Should().Be(body.Length + 2, "AXUDP always carries the 2-octet FCS");
        datagram.AsSpan(0, body.Length).SequenceEqual(body).Should().BeTrue();
        datagram[body.Length].Should().Be((byte)(fcs & 0xFF), "FCS low byte first");
        datagram[body.Length + 1].Should().Be((byte)((fcs >> 8) & 0xFF));
    }

    [Fact]
    public async Task Receive_strips_the_fcs_and_surfaces_each_datagram_as_a_neutral_frame()
    {
        // A sender (FCS appended) → a receiver: the receive path strips + validates the FCS
        // and surfaces the bare AX.25 body as a neutral Ax25InboundFrame, so the listener's
        // parser (which rejects trailing bytes on S-frames) sees a clean frame.
        await using var receiver = new AxudpFrameTransport(new IPEndPoint(IPAddress.Loopback, 1), localPort: 0);
        await using var sender = new AxudpFrameTransport(new IPEndPoint(IPAddress.Loopback, receiver.LocalPort), localPort: 0);

        var frame = Ui("inbound");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var readTask = FirstFrameAsync(receiver, cts.Token);
        await sender.SendAsync(frame.ToBytes(), cts.Token);
        var inbound = await readTask;

        inbound.Ax25.ToArray().Should().Equal(frame.ToBytes(), "the 2-octet FCS is stripped on receive, leaving the bare frame");
        inbound.PortId.Should().Be((byte)0, "AXUDP is a single point-to-point tunnel");
        Ax25Frame.TryParse(inbound.Ax25.Span, out var parsed).Should().BeTrue();
        parsed!.Source.Callsign.Should().Be(new Callsign("G7XYZ", 7));
    }

    [Fact]
    public async Task A_corrupt_fcs_datagram_is_dropped_not_surfaced()
    {
        await using var transport = new AxudpFrameTransport(new IPEndPoint(IPAddress.Loopback, 1), localPort: 0);
        using var sender = new AxudpSocket(localPort: 0);

        var body = Ui("x").ToBytes();
        var corrupt = new byte[body.Length + 2];
        body.CopyTo(corrupt);
        corrupt[^2] = 0xFF;   // wrong FCS
        corrupt[^1] = 0xFF;

        var good = Ui("good").ToBytes();
        var goodWithFcs = new byte[good.Length + 2];
        good.CopyTo(goodWithFcs);
        ushort fcs = Crc16Ccitt.Compute(good);
        goodWithFcs[^2] = (byte)(fcs & 0xFF);
        goodWithFcs[^1] = (byte)((fcs >> 8) & 0xFF);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var readTask = FirstFrameAsync(transport, cts.Token);
        // Send the corrupt one first, then a good one: the reader must skip the
        // corrupt datagram and return the good frame (proving the drop, not a hang).
        await sender.SendRawAsync(new IPEndPoint(IPAddress.Loopback, transport.LocalPort), corrupt, cts.Token);
        await sender.SendRawAsync(new IPEndPoint(IPAddress.Loopback, transport.LocalPort), goodWithFcs, cts.Token);

        var inbound = await readTask;
        inbound.Ax25.ToArray().Should().Equal(good, "the corrupt-FCS datagram is dropped; the next valid one is delivered, FCS stripped");
    }

    [Fact]
    public void Offers_no_capabilities_no_csma_no_txcompletion()
    {
        // The point of the refactor: AXUDP is a plain frame transport with no radio control
        // channel, so it implements NEITHER capability and fakes nothing (no no-op CSMA
        // setters, no throwing ACKMODE). A consumer feature-detects their absence and degrades.
        // Probe through the IAx25Transport reference, exactly as a consumer feature-detects.
        IAx25Transport transport = new AxudpFrameTransport(new IPEndPoint(IPAddress.Loopback, 1));
        (transport is ICsmaChannelParams).Should().BeFalse("a UDP tunnel has no CSMA channel-access");
        (transport is ITxCompletionTransport).Should().BeFalse("there is no TNC to echo a TX-completion");
    }

    private static async Task<Ax25InboundFrame> FirstFrameAsync(AxudpFrameTransport transport, CancellationToken ct)
    {
        await foreach (var f in transport.ReceiveAsync(ct).ConfigureAwait(false))
        {
            return f;
        }
        throw new InvalidOperationException("no frame arrived");
    }
}
