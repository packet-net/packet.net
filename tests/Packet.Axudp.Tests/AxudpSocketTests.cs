using System.Net;
using Packet.Ax25;
using Packet.Axudp;
using Packet.Core;

namespace Packet.Axudp.Tests;

public class AxudpSocketTests
{
    [Fact]
    public async Task Loopback_Send_With_Fcs_Appends_Two_Bytes_Before_Body()
    {
        using var receiver = new AxudpSocket(localPort: 0);
        using var sender   = new AxudpSocket(localPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source:      new Callsign("G7XYZ", 0),
            info:        "x"u8);

        var receiveTask = receiver.ReceiveAsync(cts.Token);
        await sender.SendAsync(new IPEndPoint(IPAddress.Loopback, receiver.LocalPort), frame, includeFcs: true, cancellationToken: cts.Token);
        var result = await receiveTask;

        result.RawFrame.Length.Should().Be(frame.RequiredBytesWithFcs);
        result.RawFrame.AsSpan(0, frame.RequiredBytes).SequenceEqual(frame.ToBytes()).Should().BeTrue();
    }

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
        // includeFcs: false here on purpose. AxudpSocket.ReceiveAsync does a best-effort
        // raw decode and deliberately does NOT strip the FCS (that's the session-aware
        // AxudpKissModem's job — see its docstring). With the FCS on, TryParse would slurp
        // the 2 trailing FCS octets into a UI frame's Info, so the bare-decode assertions
        // below need the FCS-less form. (The default-on FCS path is covered end-to-end by
        // AxudpKissModemTests + the integration/interop tests.)
        await sender.SendAsync(new IPEndPoint(IPAddress.Loopback, receiver.LocalPort), frame, includeFcs: false, cancellationToken: cts.Token);

        var result = await receiveTask;
        result.DecodedFrame.Should().NotBeNull();
        result.DecodedFrame!.Source.Callsign.Should().Be(new Callsign("G7XYZ", 7));
        result.DecodedFrame.Destination.Callsign.Should().Be(new Callsign("APRS", 0));
        result.DecodedFrame.Info.ToArray().Should().Equal("hello"u8.ToArray());
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
        result.RawFrame.Should().Equal(junk);
        result.DecodedFrame.Should().BeNull("4 random bytes are too short to parse as an AX.25 frame");
    }

    [Fact]
    public void Local_Port_Is_Selected_When_Zero_Is_Requested()
    {
        using var s = new AxudpSocket(localPort: 0);
        s.LocalPort.Should().BeGreaterThan(0);
    }
}
