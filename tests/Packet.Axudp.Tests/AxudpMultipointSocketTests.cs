using System.Net;
using System.Net.Sockets;
using Packet.Ax25;
using Packet.Axudp;
using Packet.Core;

namespace Packet.Axudp.Tests;

/// <summary>
/// The <see cref="AxudpMultipointSocket"/> - one UDP socket, many partners (the BPQ
/// <c>BPQAXIP</c> pump). It is the multi-partner counterpart to <see cref="AxudpSocket"/>:
/// the caller picks the destination endpoint per datagram; inbound datagrams are accepted
/// from any sender. The FCS handling is identical (append on send, strip + validate on
/// receive, drop a bad-FCS datagram).
/// </summary>
public sealed class AxudpMultipointSocketTests
{
    private static byte[] UiBody(string dest, string source, string info) =>
        Ax25Frame.Ui(new Callsign(dest, 0), new Callsign(source, 0), System.Text.Encoding.ASCII.GetBytes(info)).ToBytes();

    [Fact]
    public async Task Send_appends_the_two_octet_fcs_low_byte_first()
    {
        using var rawReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var receivePort = ((IPEndPoint)rawReceiver.Client.LocalEndPoint!).Port;
        using var sender = new AxudpMultipointSocket(localPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var body = UiBody("APRS", "G7XYZ", "x");
        var receiveTask = rawReceiver.ReceiveAsync(cts.Token);
        await sender.SendAsync(new IPEndPoint(IPAddress.Loopback, receivePort), body, cts.Token);
        var datagram = (await receiveTask).Buffer;

        ushort fcs = Crc16Ccitt.Compute(body);
        datagram.Length.Should().Be(body.Length + 2, "the multipoint socket always appends the 2-octet FCS");
        datagram.AsSpan(0, body.Length).SequenceEqual(body).Should().BeTrue();
        datagram[body.Length].Should().Be((byte)(fcs & 0xFF), "FCS low byte first");
        datagram[body.Length + 1].Should().Be((byte)((fcs >> 8) & 0xFF));
    }

    [Fact]
    public async Task Round_trips_a_frame_between_two_sockets_fcs_stripped_on_receive()
    {
        using var receiver = new AxudpMultipointSocket(localPort: 0);
        using var sender = new AxudpMultipointSocket(localPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var body = UiBody("APRS", "G7XYZ", "hello");
        var receiveTask = receiver.ReceiveAsync(cts.Token);
        await sender.SendAsync(new IPEndPoint(IPAddress.Loopback, receiver.LocalPort), body, cts.Token);

        var result = await receiveTask;
        result.RawFrame.Should().Equal(body, "the FCS is stripped, leaving the bare frame body");
    }

    [Fact]
    public async Task Receive_drops_a_bad_fcs_datagram_then_delivers_the_next_valid_one()
    {
        using var receiver = new AxudpMultipointSocket(localPort: 0);
        using var rawSender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var to = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);

        var goodBody = UiBody("APRS", "G7XYZ", "good");
        var corrupt = new byte[goodBody.Length + 2];   // valid body, wrong FCS
        goodBody.CopyTo(corrupt, 0);
        corrupt[^2] = 0xFF;
        corrupt[^1] = 0xFF;

        var receiveTask = receiver.ReceiveAsync(cts.Token);
        await rawSender.SendAsync(corrupt, to, cts.Token);              // dropped (bad FCS)
        using (var validSender = new AxudpMultipointSocket(localPort: 0))
        {
            await validSender.SendAsync(to, goodBody, cts.Token);       // delivered (FCS appended)
            var result = await receiveTask;
            result.RawFrame.Should().Equal(goodBody, "the bad-FCS datagram is dropped; the next valid one is delivered, FCS stripped");
        }
    }

    [Fact]
    public async Task Accepts_datagrams_from_many_different_senders_on_one_socket()
    {
        using var receiver = new AxudpMultipointSocket(localPort: 0);
        using var senderA = new AxudpMultipointSocket(localPort: 0);
        using var senderB = new AxudpMultipointSocket(localPort: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var to = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);

        var first = await receiver.ReceiveAsync(cts.Token).ContinueWithSend(senderA, to, UiBody("NODE1", "PEERA", "a"), cts.Token);
        first.From.Port.Should().Be(senderA.LocalPort, "the learned reply endpoint is the actual sender");
        first.RawFrame.Should().Equal(UiBody("NODE1", "PEERA", "a"));

        var second = await receiver.ReceiveAsync(cts.Token).ContinueWithSend(senderB, to, UiBody("NODE1", "PEERB", "b"), cts.Token);
        second.From.Port.Should().Be(senderB.LocalPort, "a different sender's endpoint is reported");
        second.RawFrame.Should().Equal(UiBody("NODE1", "PEERB", "b"));
    }

    [Fact]
    public void Local_port_is_selected_when_zero_requested()
    {
        using var s = new AxudpMultipointSocket(localPort: 0);
        s.LocalPort.Should().BeGreaterThan(0);
    }
}

// Small helper so the "receive then make the matching send fire" ordering reads inline
// (the receive Task is awaited after the send, but the send must happen first - kick the
// send, then return the still-pending receive Task's awaiter).
internal static class MultipointTestExtensions
{
    public static async Task<AxudpMultipointReceiveResult> ContinueWithSend(
        this Task<AxudpMultipointReceiveResult> receiveTask,
        AxudpMultipointSocket sender,
        IPEndPoint to,
        byte[] body,
        CancellationToken ct)
    {
        await sender.SendAsync(to, body, ct).ConfigureAwait(false);
        return await receiveTask.ConfigureAwait(false);
    }
}
