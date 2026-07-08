using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Api;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Mqtt;
using Packet.Node.Core.Telemetry;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Metrics;

/// <summary>
/// The MQTT frame-emission bucket of <c>/metrics</c> (#582): <c>pdn_mqtt_published_total</c> /
/// <c>pdn_mqtt_publish_failures_total</c> / <c>pdn_mqtt_pending_messages</c>, read straight off the
/// live <see cref="MqttFrameEmitter"/> counters (no second counter store) and driven here through a
/// fake publish sink — no broker, no Kestrel.
/// </summary>
[Trait("Category", "Node")]
public sealed class MqttMetricsExporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeSink : IMqttPublishSink
    {
        public int Published;
        public bool Fail;
        public long PendingMessageCount => 3;

        public ValueTask PublishAsync(string topic, byte[] payload, int qos, bool retain, CancellationToken ct)
        {
            if (Fail)
            {
                throw new InvalidOperationException("broker unhappy");
            }
            Published++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (MqttFrameEmitter Emitter, FakeSink Sink, MqttConfig Cfg) Build()
    {
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Ports = [new PortConfig { Id = "vhf", Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8001 } }],
            Mqtt = new MqttConfig { Enabled = true, BrokerHost = "broker.example", NodeName = "n" },
        };
        var sink = new FakeSink();
        var emitter = new MqttFrameEmitter(
            new NodeTelemetry(), new TestConfigProvider(config), NullLogger<MqttFrameEmitter>.Instance,
            (_, _, _) => ValueTask.FromResult<IMqttPublishSink>(sink), machineSuffix: "1a2b3c4d");
        return (emitter, sink, config.Mqtt);
    }

    private static MonitorEvent Frame() => MonitorEventFactory.From(
        1, "vhf", new Ax25FrameEventArgs
        {
            Frame = Ax25Frame.Ui(Callsign.Parse("APRS"), Callsign.Parse("M0LTE-1"), "hi"u8.ToArray()),
            Direction = FrameDirection.Received,
            Timestamp = T0,
        });

    private static string Render(MqttFrameEmitter? emitter)
    {
        var w = new PrometheusTextWriter();
        PdnMetricsApi.WriteMqttStats(w, emitter);
        return w.ToString();
    }

    [Fact]
    public async Task Counters_track_publishes_and_failures_through_the_fake_sink()
    {
        var (emitter, sink, cfg) = Build();

        await emitter.EmitAsync(sink, cfg, Frame(), default);          // 2 published
        sink.Fail = true;
        var act = () => emitter.EmitAsync(sink, cfg, Frame(), default).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>();    // 1 failure

        var body = Render(emitter);
        body.Should().Contain("# TYPE pdn_mqtt_published_total counter");
        body.Should().Contain("pdn_mqtt_published_total 2");
        body.Should().Contain("# TYPE pdn_mqtt_publish_failures_total counter");
        body.Should().Contain("pdn_mqtt_publish_failures_total 1");
        body.Should().Contain("# TYPE pdn_mqtt_pending_messages gauge");
        body.Should().Contain("pdn_mqtt_pending_messages 0", "no sink is live outside the background loop");
    }

    [Fact]
    public void A_fresh_emitter_exports_zeroed_counters_so_rate_works_from_first_scrape()
    {
        var (emitter, _, _) = Build();

        var body = Render(emitter);
        body.Should().Contain("pdn_mqtt_published_total 0");
        body.Should().Contain("pdn_mqtt_publish_failures_total 0");
    }

    [Fact]
    public void A_stripped_embedder_without_the_emitter_emits_no_mqtt_bucket()
    {
        Render(null).Should().BeEmpty();
    }
}
