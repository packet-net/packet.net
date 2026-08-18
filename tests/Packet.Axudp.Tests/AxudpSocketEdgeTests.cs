using System.Net;
using System.Net.Sockets;
using Packet.Ax25;
using Packet.Axudp;
using Packet.Core;

namespace Packet.Axudp.Tests;

/// <summary>
/// Edge paths of <see cref="AxudpSocket"/> the main suite didn't reach: the
/// send-side null guard, the too-short-datagram drop (distinct from the bad-FCS
/// drop), and a datagram whose FCS validates but whose body isn't a parseable
/// AX.25 frame (RawFrame returned, DecodedFrame null).
/// </summary>
public class AxudpSocketEdgeTests
{
    [Fact]
    public async Task SendAsync_rejects_a_null_frame()
    {
        using var sender = new AxudpSocket(localPort: 0);
        var act = async () => await sender.SendAsync(new IPEndPoint(IPAddress.Loopback, 9999), null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Receive_drops_a_datagram_too_short_to_carry_an_FCS()
    {
        // Shorter than the 17-byte minimum (2 addresses + control + 2-octet FCS):
        // dropped before any CRC check, exactly like a bad-FCS datagram. Proving the
        // drop, not a hang - the next valid datagram is delivered.
        using var receiver = new AxudpSocket(localPort: 0);
        using var sender = new AxudpSocket(localPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var to = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);

        var good = Ax25Frame.Ui(new Callsign("APRS", 0), new Callsign("G7XYZ", 7), "ok"u8);

        var receiveTask = receiver.ReceiveAsync(cts.Token);
        await sender.SendRawAsync(to, new byte[] { 0x01, 0x02, 0x03 }, cts.Token);   // 3 bytes → dropped
        await sender.SendAsync(to, good, cts.Token);                                 // delivered

        var result = await receiveTask;
        result.RawFrame.Should().Equal(good.ToBytes());
        result.DecodedFrame!.Info.ToArray().Should().Equal("ok"u8.ToArray());
    }

    [Fact]
    public async Task A_valid_FCS_over_an_unparseable_body_yields_a_null_decode()
    {
        using var receiver = new AxudpSocket(localPort: 0);
        using var sender = new AxudpSocket(localPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var to = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);

        // 15 octets with no address-field terminator (every octet has bit0 = 0, so the
        // parser keeps expecting more addresses and runs out): long enough to pass the
        // length+FCS gate, but not a valid AX.25 frame.
        var body = new byte[15];
        Array.Fill(body, (byte)0x40);
        var datagram = new byte[body.Length + 2];
        body.CopyTo(datagram, 0);
        ushort fcs = Crc16Ccitt.Compute(body);
        datagram[^2] = (byte)(fcs & 0xFF);
        datagram[^1] = (byte)((fcs >> 8) & 0xFF);

        var receiveTask = receiver.ReceiveAsync(cts.Token);
        await sender.SendRawAsync(to, datagram, cts.Token);

        var result = await receiveTask;
        result.RawFrame.Should().Equal(body, "the FCS validated, so the bare body is delivered");
        result.DecodedFrame.Should().BeNull("the body is not a parseable AX.25 frame");
    }
}
