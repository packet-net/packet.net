using System.Net;
using Packet.Ax25;
using Packet.Axudp;
using Packet.Core;
using Packet.Kiss;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Transports;

/// <summary>
/// The <see cref="AxudpKissModem"/> adapter, exercised over real loopback UDP: it
/// presents an <see cref="AxudpSocket"/> as an <see cref="IKissModem"/> so an
/// Ax25Listener can run over AXUDP. Send puts the AX.25 frame body straight into a
/// datagram (verbatim, or with FCS); receive surfaces each datagram as a KISS Data
/// frame.
/// </summary>
public sealed class AxudpKissModemTests
{
    private static Ax25Frame Ui(string info) => Ax25Frame.Ui(
        destination: new Callsign("APRS", 0),
        source: new Callsign("G7XYZ", 7),
        info: System.Text.Encoding.ASCII.GetBytes(info));

    [Fact]
    public async Task Send_puts_the_frame_body_verbatim_into_a_datagram_a_peer_socket_receives()
    {
        using var peer = new AxudpSocket(localPort: 0);
        var frame = Ui("hello");

        await using var modem = new AxudpKissModem(
            new IPEndPoint(IPAddress.Loopback, peer.LocalPort), localPort: 0, includeFcs: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receiveTask = peer.ReceiveAsync(cts.Token);
        await modem.SendFrameAsync(frame.ToBytes(), cts.Token);
        var result = await receiveTask;

        // The datagram payload is exactly the AX.25 frame body (no KISS framing,
        // no FCS) — what the listener handed us, verbatim.
        result.RawFrame.Should().Equal(frame.ToBytes());
        result.DecodedFrame.Should().NotBeNull();
        result.DecodedFrame!.Source.Callsign.Should().Be(new Callsign("G7XYZ", 7));
    }

    [Fact]
    public async Task Default_includeFcs_is_true_so_an_out_of_the_box_modem_appends_the_fcs()
    {
        // Pin the de-facto interoperable default: an AXUDP modem constructed WITHOUT
        // specifying includeFcs must put the 2-octet FCS on the wire, so it talks to
        // LinBPQ BPQAXIP / XRouter / ax25ipd out of the box. (Survey verdict: every
        // real AXIP/AXUDP peer REQUIRES the FCS; FCS-less is pdn-only — see
        // docs/strict-vs-pragmatic-audit.md.)
        using var peer = new AxudpSocket(localPort: 0);
        var frame = Ui("x");
        var body = frame.ToBytes();
        ushort fcs = Crc16Ccitt.Compute(body);

        await using var modem = new AxudpKissModem(
            new IPEndPoint(IPAddress.Loopback, peer.LocalPort), localPort: 0);   // <- no includeFcs arg

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receiveTask = peer.ReceiveAsync(cts.Token);
        await modem.SendFrameAsync(body, cts.Token);
        var result = await receiveTask;

        result.RawFrame.Length.Should().Be(body.Length + 2, "the default carries the 2-octet FCS");
        result.RawFrame.AsSpan(0, body.Length).SequenceEqual(body).Should().BeTrue();
        result.RawFrame[body.Length].Should().Be((byte)(fcs & 0xFF), "FCS low byte first");
        result.RawFrame[body.Length + 1].Should().Be((byte)((fcs >> 8) & 0xFF));
    }

    [Fact]
    public async Task Send_with_includeFcs_appends_the_two_octet_fcs_low_byte_first()
    {
        using var peer = new AxudpSocket(localPort: 0);
        var frame = Ui("x");
        var body = frame.ToBytes();
        ushort fcs = Crc16Ccitt.Compute(body);

        await using var modem = new AxudpKissModem(
            new IPEndPoint(IPAddress.Loopback, peer.LocalPort), localPort: 0, includeFcs: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receiveTask = peer.ReceiveAsync(cts.Token);
        await modem.SendFrameAsync(body, cts.Token);
        var result = await receiveTask;

        result.RawFrame.Length.Should().Be(body.Length + 2);
        result.RawFrame.AsSpan(0, body.Length).SequenceEqual(body).Should().BeTrue();
        result.RawFrame[body.Length].Should().Be((byte)(fcs & 0xFF), "FCS low byte first");
        result.RawFrame[body.Length + 1].Should().Be((byte)((fcs >> 8) & 0xFF));
    }

    [Fact]
    public async Task ReadFrames_surfaces_each_inbound_datagram_as_a_kiss_data_frame()
    {
        // FCS-less link (includeFcs: false) — the raw sender below transmits a bare
        // frame body with no FCS, so the modem must be on the matching FCS-less setting
        // (includeFcs is a symmetric link parameter). The FCS-on receive path is covered
        // by the strip/corrupt-drop tests below.
        await using var modem = new AxudpKissModem(
            new IPEndPoint(IPAddress.Loopback, 1), localPort: 0, includeFcs: false);    // remote irrelevant for RX
        using var sender = new AxudpSocket(localPort: 0);

        var frame = Ui("inbound");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start reading, then send one datagram to the modem's bound port.
        var readTask = FirstFrameAsync(modem, cts.Token);
        await sender.SendRawAsync(new IPEndPoint(IPAddress.Loopback, modem.LocalPort), frame.ToBytes(), cts.Token);

        var kiss = await readTask;
        kiss.Command.Should().Be(KissCommand.Data, "AXUDP datagrams surface as KISS Data frames");
        kiss.Payload.Should().Equal(frame.ToBytes());
        Ax25Frame.TryParse(kiss.Payload, out var parsed).Should().BeTrue();
        parsed!.Source.Callsign.Should().Be(new Callsign("G7XYZ", 7));
    }

    [Fact]
    public async Task With_includeFcs_the_receiver_strips_the_fcs_so_the_parsed_payload_is_the_bare_frame()
    {
        // An FCS-using receiver bound first; a sender (also FCS) points at it and
        // sends with FCS. The receiver strips it and surfaces the bare AX.25 body
        // (so the listener's parser — which rejects trailing bytes on S-frames —
        // sees a clean frame).
        await using var receiver = new AxudpKissModem(new IPEndPoint(IPAddress.Loopback, 1), localPort: 0, includeFcs: true);
        await using var sender = new AxudpKissModem(new IPEndPoint(IPAddress.Loopback, receiver.LocalPort), localPort: 0, includeFcs: true);

        var frame = Ui("acked");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var readTask = FirstFrameAsync(receiver, cts.Token);
        await sender.SendFrameAsync(frame.ToBytes(), cts.Token);
        var kiss = await readTask;

        kiss.Command.Should().Be(KissCommand.Data);
        kiss.Payload.Should().Equal(frame.ToBytes(), "the 2-octet FCS is stripped on receive, leaving the bare frame");
    }

    [Fact]
    public async Task With_includeFcs_a_corrupt_fcs_datagram_is_dropped_not_surfaced()
    {
        await using var modem = new AxudpKissModem(
            new IPEndPoint(IPAddress.Loopback, 1), localPort: 0, includeFcs: true);
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
        var readTask = FirstFrameAsync(modem, cts.Token);
        // Send the corrupt one first, then a good one: the reader must skip the
        // corrupt datagram and return the good frame (proving the drop, not a hang).
        await sender.SendRawAsync(new IPEndPoint(IPAddress.Loopback, modem.LocalPort), corrupt, cts.Token);
        await sender.SendRawAsync(new IPEndPoint(IPAddress.Loopback, modem.LocalPort), goodWithFcs, cts.Token);

        var kiss = await readTask;
        kiss.Payload.Should().Equal(good, "the corrupt-FCS datagram is dropped; the next valid one is delivered, FCS stripped");
    }

    [Fact]
    public async Task Csma_setters_are_no_ops_and_AckMode_is_unsupported()
    {
        await using var modem = new AxudpKissModem(new IPEndPoint(IPAddress.Loopback, 1));

        // CSMA knobs are inert on a UDP tunnel — they must accept + return without throwing.
        await modem.SetTxDelayAsync(30);
        await modem.SetPersistenceAsync(63);
        await modem.SetSlotTimeAsync(10);
        await modem.SetTxTailAsync(0);

        var ack = async () => await modem.SendFrameWithAckAsync(new byte[] { 0x01 });
        await ack.Should().ThrowAsync<NotSupportedException>();
    }

    private static async Task<KissFrame> FirstFrameAsync(AxudpKissModem modem, CancellationToken ct)
    {
        await foreach (var f in modem.ReadFramesAsync(ct).ConfigureAwait(false))
        {
            return f;
        }
        throw new InvalidOperationException("no frame arrived");
    }
}
