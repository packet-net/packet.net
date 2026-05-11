using System.Net;
using Packet.Ax25;
using Packet.Axudp;
using Packet.Core;

namespace Packet.Axudp.Tests;

public class AxudpSocketTests
{
    [Fact]
    public async Task Two_Loopback_Sockets_Exchange_A_UI_Frame()
    {
        using var receiver = new AxudpSocket(localPort: 0);
        using var sender = new AxudpSocket(localPort: 0);

        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("G7XYZ", 7),
            info: "hello"u8);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var receiveTask = receiver.ReceiveAsync(cts.Token);
        await sender.SendAsync(new IPEndPoint(IPAddress.Loopback, receiver.LocalPort), frame, cts.Token);

        var result = await receiveTask;
        result.DecodedFrame.ShouldNotBeNull();
        result.DecodedFrame!.Source.Callsign.ShouldBe(new Callsign("G7XYZ", 7));
        result.DecodedFrame.Destination.Callsign.ShouldBe(new Callsign("APRS", 0));
        result.DecodedFrame.Info.ToArray().ShouldBe("hello"u8.ToArray());
    }

    [Fact]
    public async Task Raw_Bytes_Sent_Are_Received_Verbatim()
    {
        using var receiver = new AxudpSocket(localPort: 0);
        using var sender = new AxudpSocket(localPort: 0);

        // Junk that isn't a valid AX.25 frame — DecodedFrame should be null
        // but RawFrame should still survive transport.
        var junk = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var receiveTask = receiver.ReceiveAsync(cts.Token);
        await sender.SendRawAsync(new IPEndPoint(IPAddress.Loopback, receiver.LocalPort), junk, cts.Token);

        var result = await receiveTask;
        result.RawFrame.ShouldBe(junk);
        result.DecodedFrame.ShouldBeNull("4 random bytes are too short to parse as an AX.25 frame");
    }

    [Fact]
    public void Local_Port_Is_Selected_When_Zero_Is_Requested()
    {
        using var s = new AxudpSocket(localPort: 0);
        s.LocalPort.ShouldBeGreaterThan(0);
    }
}
