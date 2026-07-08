using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Mqtt;
using Packet.Node.Core.Telemetry;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Mqtt;

/// <summary>
/// Behavioural tests for the <see cref="MqttFrameEmitter"/> (kissproxy-compatible MQTT emission):
/// the emitter is driven against a capturing <see cref="IMqttPublishSink"/> so the exact topic
/// strings + payloads the existing kiss-collector ingests are asserted byte-for-byte, without a
/// broker. Covers the two sub-topics, the direction map, the instance label, base64, QoS/retain, the
/// framed KISS round-trip, and the default-off gate. See <c>docs/research/pdn-mqtt-frame-emission.md</c>.
/// </summary>
public sealed class MqttFrameEmitterTests
{
    private static readonly Callsign Dest = Callsign.Parse("APRS");
    private static readonly Callsign Source = Callsign.Parse("M0LTE-1");
    private static readonly DateTimeOffset T0 = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Info = "hello world"u8.ToArray();

    // ── Harness ──────────────────────────────────────────────────────────

    private sealed class CapturingSink : IMqttPublishSink
    {
        private readonly object gate = new();
        public List<(string Topic, byte[] Payload, int Qos, bool Retain)> Published { get; } = new();
        public int DisposeCount;

        /// <summary>When set, every publish throws — the failure-counter path.</summary>
        public Exception? FailWith { get; set; }

        public long PendingMessageCount { get; set; }

        public ValueTask PublishAsync(string topic, byte[] payload, int qos, bool retain, CancellationToken ct)
        {
            if (FailWith is { } ex)
            {
                throw ex;
            }
            lock (gate)
            {
                Published.Add((topic, payload, qos, retain));
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            return ValueTask.CompletedTask;
        }

        public (string Topic, byte[] Payload, int Qos, bool Retain) At(string sub)
        {
            lock (gate)
            {
                return Published.Single(p => p.Topic.EndsWith(sub, StringComparison.Ordinal));
            }
        }
    }

    private static MqttConfig Enabled() => new()
    {
        Enabled = true,
        BrokerHost = "broker.example",
        NodeName = "gb7rdg-node",
    };

    private static PortConfig[] DefaultPorts() =>
    [
        new PortConfig { Id = "vhf", Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8001 }, MqttInstance = "70cm" },
        new PortConfig { Id = "hf", Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8002 } },
    ];

    private static (MqttFrameEmitter Emitter, CapturingSink Sink, TestConfigProvider Provider, StrongBox<int> FactoryCalls)
        Build(MqttConfig mqtt, IEnumerable<PortConfig>? ports = null, string machineSuffix = "1a2b3c4d")
    {
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Ports = (ports ?? DefaultPorts()).ToList(),
            Mqtt = mqtt,
        };
        var provider = new TestConfigProvider(config);
        var sink = new CapturingSink();
        var calls = new StrongBox<int>(0);
        var emitter = new MqttFrameEmitter(
            new NodeTelemetry(),
            provider,
            NullLogger<MqttFrameEmitter>.Instance,
            (_, _, _) => { calls.Value++; return ValueTask.FromResult<IMqttPublishSink>(sink); },
            machineSuffix);
        return (emitter, sink, provider, calls);
    }

    private static MonitorEvent Rx(string port, Ax25Frame frame) => MonitorEventFactory.From(
        1, port, new Ax25FrameEventArgs { Frame = frame, Direction = FrameDirection.Received, Timestamp = T0 });

    private static MonitorEvent Tx(string port, Ax25Frame frame) => MonitorEventFactory.From(
        2, port, new Ax25FrameEventArgs { Frame = frame, Direction = FrameDirection.Transmitted, Timestamp = T0 });

    private static Ax25Frame Ui() => Ax25Frame.Ui(Dest, Source, Info);

    // ── Topic + direction ────────────────────────────────────────────────

    [Fact]
    public async Task Rx_frame_publishes_the_two_kissproxy_subtopics_for_fromModem()
    {
        var (emitter, sink, provider, _) = Build(Enabled());

        await emitter.EmitAsync(sink, provider.Current.Mqtt, Rx("vhf", Ui()), default);

        sink.Published.Select(p => p.Topic).Should().BeEquivalentTo(new[]
        {
            "kissproxy/gb7rdg-node/70cm/fromModem/unframed/port0/DataFrameKissCmd",
            "kissproxy/gb7rdg-node/70cm/fromModem/framed",
        });
    }

    [Fact]
    public async Task Tx_frame_maps_to_toModem()
    {
        var (emitter, sink, provider, _) = Build(Enabled());

        await emitter.EmitAsync(sink, provider.Current.Mqtt, Tx("vhf", Ui()), default);

        sink.Published.Select(p => p.Topic).Should().BeEquivalentTo(new[]
        {
            "kissproxy/gb7rdg-node/70cm/toModem/unframed/port0/DataFrameKissCmd",
            "kissproxy/gb7rdg-node/70cm/toModem/framed",
        });
    }

    [Fact]
    public async Task Instance_defaults_to_the_port_id_when_no_label_is_set()
    {
        var (emitter, sink, provider, _) = Build(Enabled());

        await emitter.EmitAsync(sink, provider.Current.Mqtt, Rx("hf", Ui()), default);

        // Port "hf" has no MqttInstance → the id is used as the {instance} segment.
        sink.Published.Should().OnlyContain(p => p.Topic.StartsWith("kissproxy/gb7rdg-node/hf/fromModem/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Topic_prefix_is_prepended_when_set()
    {
        var (emitter, sink, provider, _) = Build(Enabled() with { TopicPrefix = "site7" });

        await emitter.EmitAsync(sink, provider.Current.Mqtt, Rx("vhf", Ui()), default);

        sink.Published.Should().OnlyContain(p => p.Topic.StartsWith("site7/kissproxy/gb7rdg-node/70cm/", StringComparison.Ordinal));
    }

    // ── Payloads ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Unframed_payload_is_the_raw_ax25_bytes()
    {
        var (emitter, sink, provider, _) = Build(Enabled());
        var frame = Ui();

        await emitter.EmitAsync(sink, provider.Current.Mqtt, Rx("vhf", frame), default);

        sink.At("/unframed/port0/DataFrameKissCmd").Payload.Should().Equal(frame.ToBytes());
    }

    [Fact]
    public async Task Framed_payload_round_trips_through_the_kiss_decoder_back_to_the_ax25_bytes()
    {
        var (emitter, sink, provider, _) = Build(Enabled());
        var frame = Ui();
        var ax25 = frame.ToBytes();

        await emitter.EmitAsync(sink, provider.Current.Mqtt, Rx("vhf", frame), default);

        var framed = sink.At("/framed").Payload;
        // Full raw KISS frame: FEND | type byte | SLIP(ax25) | FEND.
        framed[0].Should().Be(KissFraming.Fend);
        framed[^1].Should().Be(KissFraming.Fend);

        var decoded = new KissDecoder().Push(framed);
        decoded.Should().ContainSingle();
        decoded[0].Port.Should().Be(0);
        decoded[0].Command.Should().Be(KissCommand.Data);
        decoded[0].Payload.Should().Equal(ax25);
    }

    [Fact]
    public async Task Base64_encodes_both_payloads_when_enabled()
    {
        var (emitter, sink, provider, _) = Build(Enabled() with { Base64 = true });
        var frame = Ui();
        var expectedUnframed = Encoding.UTF8.GetBytes(
            Convert.ToBase64String(frame.ToBytes(), Base64FormattingOptions.InsertLineBreaks));

        await emitter.EmitAsync(sink, provider.Current.Mqtt, Rx("vhf", frame), default);

        sink.At("/unframed/port0/DataFrameKissCmd").Payload.Should().Equal(expectedUnframed);
        // framed is base64 of the KISS-framed bytes.
        var framedRaw = KissEncoder.Encode(0, KissCommand.Data, frame.ToBytes());
        sink.At("/framed").Payload.Should().Equal(
            Encoding.UTF8.GetBytes(Convert.ToBase64String(framedRaw, Base64FormattingOptions.InsertLineBreaks)));
    }

    [Fact]
    public async Task Qos_comes_from_config_and_retain_is_always_false()
    {
        var (emitter, sink, provider, _) = Build(Enabled() with { Qos = 1 });

        await emitter.EmitAsync(sink, provider.Current.Mqtt, Rx("vhf", Ui()), default);

        sink.Published.Should().OnlyContain(p => p.Qos == 1 && p.Retain == false);
    }

    [Fact]
    public async Task Qos_defaults_to_exactly_once()
    {
        // A default MqttConfig (only master + host + node set) keeps QoS 2, matching kissproxy.
        var (emitter, sink, provider, _) = Build(Enabled());

        await emitter.EmitAsync(sink, provider.Current.Mqtt, Rx("vhf", Ui()), default);

        sink.Published.Should().OnlyContain(p => p.Qos == 2);
    }

    [Fact]
    public async Task RfOnly_still_emits_because_every_pdn_transport_is_rf()
    {
        // Documents the pass-through: RfOnly is honoured, but isRf is always true today, so a frame
        // still publishes. (The filter becomes meaningful the day a non-RF transport exists.)
        var (emitter, sink, provider, _) = Build(Enabled() with { RfOnly = true });

        await emitter.EmitAsync(sink, provider.Current.Mqtt, Rx("vhf", Ui()), default);

        sink.Published.Should().HaveCount(2);
    }

    // ── Salted client id (#582) ──────────────────────────────────────────

    [Fact]
    public void Client_id_appends_the_machine_suffix_to_an_explicit_node_name()
    {
        // The salt guards duplicate configured NodeNames just as it guards duplicate hostnames —
        // the client id is broker identity only, so salting it never touches the topics.
        var (emitter, _, provider, _) = Build(Enabled(), machineSuffix: "1a2b3c4d");

        emitter.ClientId(provider.Current.Mqtt).Should().Be("gb7rdg-node_pdn_1a2b3c4d");
    }

    [Fact]
    public void Client_id_defaults_to_the_machine_name_plus_suffix()
    {
        var (emitter, _, provider, _) = Build(Enabled() with { NodeName = null }, machineSuffix: "1a2b3c4d");

        emitter.ClientId(provider.Current.Mqtt).Should().Be($"{Environment.MachineName}_pdn_1a2b3c4d");
    }

    [Fact]
    public void Client_ids_differ_across_machines_even_with_identical_config()
    {
        // The #582 failure mode: two image-cloned Pis, same hostname, same (default) config — their
        // client ids must differ so the broker never disconnects one session for the other.
        var (a, _, provider, _) = Build(Enabled(), machineSuffix: "1a2b3c4d");
        var (b, _, _, _) = Build(Enabled(), machineSuffix: "9f8e7d6c");

        a.ClientId(provider.Current.Mqtt).Should().NotBe(b.ClientId(provider.Current.Mqtt));
    }

    [Fact]
    public async Task The_salted_client_id_is_what_reaches_the_sink_factory()
    {
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Ports = DefaultPorts().ToList(),
            Mqtt = Enabled(),
        };
        var provider = new TestConfigProvider(config);
        var captured = new CapturingSink();
        string? clientId = null;
        var telemetry = new NodeTelemetry();
        var emitter = new MqttFrameEmitter(
            telemetry, provider, NullLogger<MqttFrameEmitter>.Instance,
            (_, id, _) => { clientId = id; return ValueTask.FromResult<IMqttPublishSink>(captured); },
            machineSuffix: "1a2b3c4d");

        await emitter.StartAsync(default);
        await Wait.ForAsync(() => telemetry.SubscriberCount >= 1, "the emitter subscribed to telemetry");
        telemetry.Observe("vhf", new Ax25FrameEventArgs { Frame = Ui(), Direction = FrameDirection.Received, Timestamp = T0 });
        await Wait.ForAsync(() => captured.Published.Count >= 2, "the traced frame published");
        await emitter.StopAsync(default);

        clientId.Should().Be("gb7rdg-node_pdn_1a2b3c4d");
    }

    // ── Publish counters (#582) ──────────────────────────────────────────

    [Fact]
    public async Task Published_counter_counts_both_subtopics_per_frame()
    {
        var (emitter, sink, provider, _) = Build(Enabled());

        await emitter.EmitAsync(sink, provider.Current.Mqtt, Rx("vhf", Ui()), default);
        await emitter.EmitAsync(sink, provider.Current.Mqtt, Tx("vhf", Ui()), default);

        emitter.PublishedTotal.Should().Be(4, "each frame emits unframed + framed");
        emitter.PublishFailuresTotal.Should().Be(0);
    }

    [Fact]
    public async Task Failure_counter_counts_a_faulted_publish()
    {
        var (emitter, sink, provider, _) = Build(Enabled());
        sink.FailWith = new InvalidOperationException("broker exploded");

        var act = () => emitter.EmitAsync(sink, provider.Current.Mqtt, Rx("vhf", Ui()), default).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
        emitter.PublishFailuresTotal.Should().Be(1, "the first sub-topic faulted (the second is never attempted)");
        emitter.PublishedTotal.Should().Be(0);
    }

    [Fact]
    public async Task Pending_gauge_reads_the_live_sink_queue_depth()
    {
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Ports = DefaultPorts().ToList(),
            Mqtt = Enabled(),
        };
        var provider = new TestConfigProvider(config);
        var captured = new CapturingSink { PendingMessageCount = 7 };
        var telemetry = new NodeTelemetry();
        var emitter = new MqttFrameEmitter(
            telemetry, provider, NullLogger<MqttFrameEmitter>.Instance,
            (_, _, _) => ValueTask.FromResult<IMqttPublishSink>(captured),
            machineSuffix: "1a2b3c4d");

        emitter.PendingMessages.Should().Be(0, "no sink is live before the loop starts one");

        await emitter.StartAsync(default);
        await Wait.ForAsync(() => telemetry.SubscriberCount >= 1, "the emitter subscribed to telemetry");
        telemetry.Observe("vhf", new Ax25FrameEventArgs { Frame = Ui(), Direction = FrameDirection.Received, Timestamp = T0 });
        await Wait.ForAsync(() => captured.Published.Count >= 2, "the traced frame published");

        emitter.PendingMessages.Should().Be(7, "the gauge reads the managed client's pending queue");

        await emitter.StopAsync(default);
        emitter.PendingMessages.Should().Be(0, "the sink is released on shutdown");
    }

    // ── Default-off gate (loop-driven) ───────────────────────────────────

    [Fact]
    public async Task Disabled_config_publishes_nothing_and_never_builds_a_sink()
    {
        // Master switch OFF (the default): the emitter drains the telemetry stream but publishes
        // nothing and never even constructs a broker client.
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Ports = DefaultPorts().ToList(),
            Mqtt = new MqttConfig(),   // Enabled == false
        };
        var provider = new TestConfigProvider(config);
        var sink = new CapturingSink();
        var calls = new StrongBox<int>(0);
        var telemetry = new NodeTelemetry();
        var emitter = new MqttFrameEmitter(
            telemetry, provider, NullLogger<MqttFrameEmitter>.Instance,
            (_, _, _) => { calls.Value++; return ValueTask.FromResult<IMqttPublishSink>(sink); });

        await emitter.StartAsync(default);
        // Wait for the loop's telemetry subscription so the Observed frame actually reaches the gate
        // (not dropped for lack of a subscriber) — then confirm the disabled gate suppresses it.
        await Wait.ForAsync(() => telemetry.SubscriberCount >= 1, "the emitter subscribed to telemetry");
        telemetry.Observe("vhf", new Ax25FrameEventArgs { Frame = Ui(), Direction = FrameDirection.Received, Timestamp = T0 });
        await Task.Delay(150);   // let the loop drain the frame through the disabled gate.
        await emitter.StopAsync(default);

        calls.Value.Should().Be(0);
        sink.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Enabled_loop_publishes_for_a_traced_frame_end_to_end()
    {
        // Drive the whole background loop against a real NodeTelemetry we Observe frames on.
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Ports = DefaultPorts().ToList(),
            Mqtt = Enabled(),
        };
        var provider = new TestConfigProvider(config);
        var captured = new CapturingSink();
        var telemetry = new NodeTelemetry();
        var emitter = new MqttFrameEmitter(
            telemetry, provider, NullLogger<MqttFrameEmitter>.Instance,
            (_, _, _) => ValueTask.FromResult<IMqttPublishSink>(captured));

        await emitter.StartAsync(default);
        // The emitter subscribes to telemetry inside ExecuteAsync (after StartAsync returns), so wait
        // for the subscription to register before Observing — else the broadcast has no subscriber yet.
        await Wait.ForAsync(() => telemetry.SubscriberCount >= 1, "the emitter subscribed to telemetry");
        telemetry.Observe("vhf", new Ax25FrameEventArgs { Frame = Ui(), Direction = FrameDirection.Received, Timestamp = T0 });
        await Wait.ForAsync(() => captured.Published.Count >= 2, "the traced frame's two topics were published");
        await emitter.StopAsync(default);

        captured.Published.Select(p => p.Topic).Should().Contain("kissproxy/gb7rdg-node/70cm/fromModem/framed");
        captured.DisposeCount.Should().Be(1);   // the sink is disposed on shutdown.
    }
}
