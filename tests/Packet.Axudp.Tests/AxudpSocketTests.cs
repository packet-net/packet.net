using System.Net;
using System.Net.Sockets;
using Packet.Ax25;
using Packet.Axudp;
using Packet.Core;

namespace Packet.Axudp.Tests;

public class AxudpSocketTests
{
    [Fact]
    public async Task Send_Always_Appends_The_Two_Octet_Fcs_On_The_Wire()
    {
        // AXUDP unconditionally carries the FCS. Observe the raw datagram with a
        // plain UdpClient (not AxudpSocket, which strips the FCS on receive) to
        // confirm SendAsync put body + 2-octet FCS, low byte first, on the wire.
        using var rawReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var receivePort = ((IPEndPoint)rawReceiver.Client.LocalEndPoint!).Port;
        using var sender = new AxudpSocket(localPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source:      new Callsign("G7XYZ", 0),
            info:        "x"u8);

        var receiveTask = rawReceiver.ReceiveAsync(cts.Token);
        await sender.SendAsync(new IPEndPoint(IPAddress.Loopback, receivePort), frame, cts.Token);
        var datagram = (await receiveTask).Buffer;

        var body = frame.ToBytes();
        ushort fcs = Crc16Ccitt.Compute(body);
        datagram.Length.Should().Be(body.Length + 2, "AXUDP always appends the 2-octet FCS");
        datagram.AsSpan(0, body.Length).SequenceEqual(body).Should().BeTrue();
        datagram[body.Length].Should().Be((byte)(fcs & 0xFF), "FCS low byte first");
        datagram[body.Length + 1].Should().Be((byte)((fcs >> 8) & 0xFF));
    }

    [Fact]
    public async Task Two_Loopback_Sockets_Exchange_A_UI_Frame_Fcs_Stripped_On_Receive()
    {
        using var receiver = new AxudpSocket(localPort: 0);
        using var sender = new AxudpSocket(localPort: 0);

        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 7),
            info: "hello"u8);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var receiveTask = receiver.ReceiveAsync(cts.Token);
        // SendAsync always appends the FCS; ReceiveAsync strips + validates it, so the
        // decoded frame is clean — the 2 trailing FCS octets are NOT slurped into Info.
        await sender.SendAsync(new IPEndPoint(IPAddress.Loopback, receiver.LocalPort), frame, cts.Token);

        var result = await receiveTask;
        result.RawFrame.Should().Equal(frame.ToBytes(), "the FCS is stripped, leaving the bare frame body");
        result.DecodedFrame.Should().NotBeNull();
        result.DecodedFrame!.Source.Callsign.Should().Be(new Callsign("G7XYZ", 7));
        result.DecodedFrame.Destination.Callsign.Should().Be(new Callsign("APRS", 0));
        result.DecodedFrame.Info.ToArray().Should().Equal("hello"u8.ToArray());
    }

    [Fact]
    public async Task Receive_Drops_A_Datagram_With_A_Bad_Fcs()
    {
        // A datagram whose trailing FCS doesn't validate is dropped (as every real
        // peer drops a bad-CRC datagram); the next valid one is delivered — proving
        // the drop, not a hang.
        using var receiver = new AxudpSocket(localPort: 0);
        using var sender = new AxudpSocket(localPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var to = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);

        var good = Ax25Frame.Ui(destination: new Callsign("APRS", 0), source: new Callsign("G7XYZ", 7), info: "good"u8);
        var goodBody = good.ToBytes();
        var corrupt = new byte[goodBody.Length + 2];   // valid body, wrong FCS
        goodBody.CopyTo(corrupt, 0);
        corrupt[^2] = 0xFF;
        corrupt[^1] = 0xFF;

        var receiveTask = receiver.ReceiveAsync(cts.Token);
        await sender.SendRawAsync(to, corrupt, cts.Token);     // dropped (bad FCS)
        await sender.SendAsync(to, good, cts.Token);           // delivered

        var result = await receiveTask;
        result.RawFrame.Should().Equal(goodBody, "the bad-FCS datagram is dropped; the next valid one is delivered, FCS stripped");
        result.DecodedFrame!.Info.ToArray().Should().Equal("good"u8.ToArray());
    }

    [Fact]
    public void Local_Port_Is_Selected_When_Zero_Is_Requested()
    {
        using var s = new AxudpSocket(localPort: 0);
        s.LocalPort.Should().BeGreaterThan(0);
    }
}
