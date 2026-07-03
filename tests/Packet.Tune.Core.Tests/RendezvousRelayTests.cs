using System.Net.WebSockets;
using System.Text;

namespace Packet.Tune.Core.Tests;

/// <summary>
/// The PIN-rendezvous relay over real loopback sockets (port 0): pairing,
/// verbatim forwarding both ways, PIN single-use, and session death when
/// either socket goes.
/// </summary>
public class RendezvousRelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Two_clients_with_the_same_pin_pair_and_frames_forward_verbatim_both_ways()
    {
        await using var relay = RendezvousRelay.Start(0);
        using var tuned = await ConnectAsync(relay.Port, "424242", "tuned");
        using var meter = await ConnectAsync(relay.Port, "424242", "meter");

        await SendTextAsync(tuned, "hello from tuned");
        (await ReceiveTextAsync(meter)).Should().Be("hello from tuned");

        await SendTextAsync(meter, "hello from meter");
        (await ReceiveTextAsync(tuned)).Should().Be("hello from meter");
    }

    [Fact]
    public async Task Different_pins_do_not_pair()
    {
        await using var relay = RendezvousRelay.Start(0);
        using var a = await ConnectAsync(relay.Port, "111111", "tuned");
        using var b = await ConnectAsync(relay.Port, "222222", "meter");

        await SendTextAsync(a, "anyone there?");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var buffer = new byte[256];
        var act = async () => await b.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>("client b is parked in a different session");
    }

    [Fact]
    public async Task A_pin_is_single_use()
    {
        await using var relay = RendezvousRelay.Start(0);
        using var tuned = await ConnectAsync(relay.Port, "313131", "tuned");
        using var meter = await ConnectAsync(relay.Port, "313131", "meter");

        var act = async () => await ConnectAsync(relay.Port, "313131", "meter");
        await act.Should().ThrowAsync<WebSocketException>("the PIN was consumed by the first pairing");
    }

    [Fact]
    public async Task A_malformed_pin_is_rejected()
    {
        await using var relay = RendezvousRelay.Start(0);
        var act = async () => await ConnectAsync(relay.Port, "not-a-pin", "tuned");
        await act.Should().ThrowAsync<WebSocketException>();
    }

    [Fact]
    public async Task The_session_dies_when_either_socket_closes()
    {
        await using var relay = RendezvousRelay.Start(0);
        using var tuned = await ConnectAsync(relay.Port, "515151", "tuned");
        using var meter = await ConnectAsync(relay.Port, "515151", "meter");
        await SendTextAsync(tuned, "ping");
        (await ReceiveTextAsync(meter)).Should().Be("ping");

        tuned.Abort();

        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[256];
        bool sessionOver = false;
        try
        {
            var result = await meter.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            sessionOver = result.MessageType == WebSocketMessageType.Close;
        }
        catch (WebSocketException)
        {
            sessionOver = true;
        }
        sessionOver.Should().BeTrue("the surviving client must see the session end");
    }

    [Fact]
    public async Task WebSocketTuningLinks_exchange_telegrams_through_the_relay()
    {
        await using var relay = RendezvousRelay.Start(0);
        var endpoint = new Uri($"ws://127.0.0.1:{relay.Port}");
        await using var tuned = await WebSocketTuningLink.ConnectAsync(endpoint, "616161", "tuned");
        await using var meter = await WebSocketTuningLink.ConnectAsync(endpoint, "616161", "meter");

        var report = new MeterReport(4, 5, 12, 0, -90.4);
        await tuned.SendAsync(new TuningTelegram(1, TuningVerb.Hello, "tuned")).WaitAsync(Timeout);
        await meter.SendAsync(new TuningTelegram(1, TuningVerb.Measurement, report.ToArgs())).WaitAsync(Timeout);

        var atMeter = new List<TuningTelegram>();
        await foreach (var telegram in meter.ReceiveAsync().WithTimeout(Timeout))
        {
            atMeter.Add(telegram);
            break;
        }
        atMeter.Should().ContainSingle().Which.Should().Be(new TuningTelegram(1, TuningVerb.Hello, "tuned"));

        var atTuned = new List<TuningTelegram>();
        await foreach (var telegram in tuned.ReceiveAsync().WithTimeout(Timeout))
        {
            atTuned.Add(telegram);
            break;
        }
        atTuned.Should().ContainSingle();
        MeterReport.TryParse(atTuned[0].Args, out var received).Should().BeTrue();
        received.Should().Be(report);
    }

    [Fact]
    public void Generated_pins_are_six_digits()
    {
        for (int i = 0; i < 20; i++)
        {
            string pin = RendezvousRelay.GeneratePin();
            pin.Should().HaveLength(6).And.MatchRegex("^[0-9]{6}$");
        }
    }

    private static async Task<ClientWebSocket> ConnectAsync(int port, string pin, string role)
    {
        var socket = new ClientWebSocket();
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?pin={pin}&role={role}"), cts.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return socket;
    }

    private static async Task SendTextAsync(WebSocket socket, string text)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cts.Token);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        result.MessageType.Should().Be(WebSocketMessageType.Text);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
