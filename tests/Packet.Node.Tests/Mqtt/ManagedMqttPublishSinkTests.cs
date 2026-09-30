using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using MQTTnet;
using MQTTnet.Server;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Mqtt;

namespace Packet.Node.Tests.Mqtt;

/// <summary>
/// The publish sink on MQTTnet 5 (#882), which carries the queue and reconnect loop the v4
/// ManagedClient used to: a publish never blocks, messages reach the broker in order, the
/// queue is bounded with drop-oldest overflow (#582), and a broker that goes away is retried
/// with the queue intact. Proven against an in-process MQTTnet broker, not a fake client.
/// </summary>
[Trait("Category", "Node")]
public sealed class ManagedMqttPublishSinkTests
{
    private static MqttConfig Cfg(int port = 1883) => new()
    {
        Enabled = true,
        BrokerHost = "127.0.0.1",
        BrokerPort = port,
        NodeName = "gb7rdg-node",
        Username = "op",
        Password = "secret",
    };

    [Fact]
    public void Client_id_endpoint_and_credentials_pass_through()
    {
        var options = ManagedMqttPublishSink.BuildOptions(Cfg(1884), "gb7rdg-node_pdn_1a2b3c4d");
        options.ClientId.Should().Be("gb7rdg-node_pdn_1a2b3c4d");
        var tcp = options.ChannelOptions.Should().BeOfType<MqttClientTcpOptions>().Subject;
        tcp.RemoteEndpoint.Should().BeOfType<DnsEndPoint>().Which.Port.Should().Be(1884);
        options.Credentials.GetUserName(options).Should().Be("op");
        Encoding.UTF8.GetString(options.Credentials.GetPassword(options)).Should().Be("secret");
        ManagedMqttPublishSink.MaxPendingMessages.Should().Be(10_000, "the bound keeps broker-down memory in the tens of MB");
    }

    [Fact]
    public async Task Messages_reach_the_broker_in_order_and_survive_the_broker_going_away()
    {
        int port = FreePort();
        var received = new List<string>();
        var gotOne = new SemaphoreSlim(0);
        using var server = new MqttServerFactory().CreateMqttServer(
            new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointBoundIPV6Address(IPAddress.None).WithDefaultEndpointPort(port).Build());
        server.InterceptingPublishAsync += e =>
        {
            lock (received) { received.Add(Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray())); }
            gotOne.Release();
            return Task.CompletedTask;
        };
        await server.StartAsync();

        var clock = new FakeTimeProvider();
        await using var sink = new ManagedMqttPublishSink(
            new MqttClientFactory().CreateMqttClient(), ManagedMqttPublishSink.BuildOptions(Cfg(port) with { Username = null }, "pdn-test"), clock);

        await sink.PublishAsync("t", "one"u8.ToArray(), 0, false, CancellationToken.None);
        await sink.PublishAsync("t", "two"u8.ToArray(), 0, false, CancellationToken.None);
        await sink.PublishAsync("t", "three"u8.ToArray(), 0, false, CancellationToken.None);
        for (int i = 0; i < 3; i++)
        {
            (await gotOne.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeTrue("the broker receives what the sink queued");
        }
        lock (received) { received.Should().Equal("one", "two", "three"); }

        // The broker goes away: the sink keeps what it is given and retries after its delay
        // once the broker is back, with nothing lost and the order kept.
        await server.StopAsync();
        await sink.PublishAsync("t", "four"u8.ToArray(), 0, false, CancellationToken.None);
        await sink.PublishAsync("t", "five"u8.ToArray(), 0, false, CancellationToken.None);
        await Task.Delay(300);
        sink.PendingMessageCount.Should().BeGreaterThanOrEqualTo(1, "nothing was delivered while the broker was down");

        await server.StartAsync();
        for (int attempt = 0; attempt < 40 && received.Count < 5; attempt++)
        {
            clock.Advance(ManagedMqttPublishSink.ReconnectDelay);
            await Task.Delay(250);
        }
        lock (received) { received.Should().Equal("one", "two", "three", "four", "five"); }
        sink.PendingMessageCount.Should().Be(0);
    }

    [Fact]
    public async Task The_queue_drops_the_oldest_past_its_bound()
    {
        int port = FreePort();
        var received = new List<string>();
        using var server = new MqttServerFactory().CreateMqttServer(
            new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointBoundIPV6Address(IPAddress.None).WithDefaultEndpointPort(port).Build());
        server.InterceptingPublishAsync += e =>
        {
            lock (received) { received.Add(Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray())); }
            return Task.CompletedTask;
        };

        // Not started yet: everything queues. Bound 3, six offered: the queue keeps only the
        // newest three, plus the first if the pump had already taken it in hand when the
        // flood came (a race the test does not fix, since either way is the contract).
        var clock = new FakeTimeProvider();
        await using var sink = new ManagedMqttPublishSink(
            new MqttClientFactory().CreateMqttClient(), ManagedMqttPublishSink.BuildOptions(Cfg(port) with { Username = null }, "pdn-test"), clock, maxPending: 3);
        for (int i = 0; i < 6; i++)
        {
            await sink.PublishAsync("t", Encoding.UTF8.GetBytes($"m{i}"), 0, false, CancellationToken.None);
        }
        await Task.Delay(300);
        sink.PendingMessageCount.Should().BeLessThanOrEqualTo(4);

        await server.StartAsync();
        for (int attempt = 0; attempt < 40 && received.Count < 3; attempt++)
        {
            clock.Advance(ManagedMqttPublishSink.ReconnectDelay);
            await Task.Delay(250);
        }
        await Task.Delay(500);   // anything still coming would be a dropped message reappearing
        lock (received)
        {
            received.Should().NotContain("m1").And.NotContain("m2", "the oldest past the bound are dropped");
            received.TakeLast(3).Should().Equal(["m3", "m4", "m5"], "the newest three are kept, in order");
            received.Should().HaveCountLessThanOrEqualTo(4);
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
