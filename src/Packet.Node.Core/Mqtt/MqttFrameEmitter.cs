using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Kiss;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Telemetry;

namespace Packet.Node.Core.Mqtt;

/// <summary>
/// The kissproxy-compatible MQTT frame emitter: a background service that publishes every AX.25 frame
/// the node sends/receives to an MQTT broker in <a href="https://github.com/M0LTE/kissproxy">kissproxy</a>'s
/// native topic + payload format, so pdn can replace a kissproxy instance at a site without losing the
/// downstream <c>kiss-collector</c> capture. <b>Default-off, self-gating</b> on
/// <see cref="MqttConfig.Enabled"/> (registration is unconditional, like the OARC reporter). See
/// <c>docs/research/pdn-mqtt-frame-emission.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Rides the same <see cref="NodeTelemetry"/> frame-trace stream the SSE monitor, the traffic log, and
/// the OARC reporter consume (no second decode path) via a bounded drop-oldest subscription, so a slow
/// broker can never back-pressure the radio pump threads. Per frame it emits <b>two</b> topics: the
/// <c>unframed</c> AX.25 payload the collector ingests, and the full <c>framed</c> KISS frame (incl.
/// FEND) for parity.
/// </para>
/// <para>
/// The broker connection is captured at first-enabled-frame (like the traffic log's startup-bound
/// path); the payload/topic knobs (node name, instance, base64, QoS, RF-only, prefix) are re-read from
/// the live config on every frame, so those are hot edits. While disabled the stream is drained and
/// discarded — nothing is published and nothing backs up.
/// </para>
/// </remarks>
public sealed partial class MqttFrameEmitter : BackgroundService
{
    /// <summary>The KISS multi-drop port for these single-port modems — used both for the
    /// <c>port{N}</c> topic segment and the KISS type byte of the <c>framed</c> payload. kissproxy's
    /// captured traffic is all <c>port0</c>; a small const rather than a config knob (v1).</summary>
    internal const byte KissPort = 0;

    /// <summary>The <c>{Cmd}</c> topic segment for a data frame. The AX.25-layer tap can't distinguish
    /// the G8BPQ ACKMODE wrapper, so v1 emits <c>DataFrame</c> for all data traffic (the bulk of the
    /// collector's captured frames). See the fidelity caveats in the design doc.</summary>
    internal const string DataFrameCmd = "DataFrame";

    private readonly NodeTelemetry telemetry;
    private readonly IConfigProvider config;
    private readonly ILogger<MqttFrameEmitter> logger;
    private readonly Func<MqttConfig, string, CancellationToken, ValueTask<IMqttPublishSink>> sinkFactory;

    /// <summary>Production constructor — the sink is a real started <see cref="ManagedMqttPublishSink"/>.</summary>
    public MqttFrameEmitter(NodeTelemetry telemetry, IConfigProvider config, ILogger<MqttFrameEmitter>? logger = null)
        : this(telemetry, config, logger, ManagedMqttPublishSink.CreateAsync)
    {
    }

    /// <summary>Test seam (InternalsVisibleTo Packet.Node.Tests): inject a capturing publish sink so
    /// the topic + payload contract can be asserted without a broker.</summary>
    internal MqttFrameEmitter(
        NodeTelemetry telemetry,
        IConfigProvider config,
        ILogger<MqttFrameEmitter>? logger,
        Func<MqttConfig, string, CancellationToken, ValueTask<IMqttPublishSink>> sinkFactory)
    {
        this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.logger = logger ?? NullLogger<MqttFrameEmitter>.Instance;
        this.sinkFactory = sinkFactory ?? throw new ArgumentNullException(nameof(sinkFactory));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't hold up host start.
        await Task.Yield();

        using var subscription = telemetry.Subscribe(out var reader);
        IMqttPublishSink? sink = null;
        try
        {
            await foreach (var frame in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                var cfg = config.Current.Mqtt;
                if (!cfg.Enabled)
                {
                    continue;   // drain + discard while off — nothing published, nothing backs up.
                }

                if (sink is null)
                {
                    try
                    {
                        sink = await sinkFactory(cfg, ClientId(cfg), stoppingToken).ConfigureAwait(false);
                        LogStarted(cfg.BrokerHost, cfg.BrokerPort);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogStartFault(ex, cfg.BrokerHost, cfg.BrokerPort);
                        continue;   // retry on the next frame; the managed client normally never throws here.
                    }
                }

                try
                {
                    await EmitAsync(sink, cfg, frame, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogPublishFault(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            if (sink is not null)
            {
                try { await sink.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>Publish one traced frame's two kissproxy sub-topics (<c>unframed</c> + <c>framed</c>)
    /// through <paramref name="sink"/>. Internal so a test can drive it directly with a capturing sink
    /// (no background loop, no broker). Honours the RF-only filter and the base64 flag.</summary>
    internal async ValueTask EmitAsync(IMqttPublishSink sink, MqttConfig cfg, MonitorEvent frame, CancellationToken ct)
    {
        // RF-only filter, mirroring OarcReporter.MapTrace: every current pdn transport is a real AX.25
        // RF port, so isRf is always true and the filter is pass-through until a non-RF transport
        // exists — honoured anyway so it's correct the day one is added.
        const bool isRf = true;
        if (cfg.RfOnly && !isRf)
        {
            return;
        }

        var ax25 = ToBytes(frame.Raw);
        var (unframedTopic, framedTopic) = BuildTopics(cfg, frame);

        // unframed: the SLIP-decoded AX.25 bytes = MonitorEvent.Raw (frame.ToBytes()) — the topic the
        // collector actually ingests.
        await sink.PublishAsync(unframedTopic, Encode(cfg, ax25), cfg.Qos, retain: false, ct).ConfigureAwait(false);

        // framed: the full raw KISS frame WITH FEND framing — 0xC0 | (port<<4)|Data | SLIP(ax25) | 0xC0
        // via the shared Packet.Kiss encoder (no hand-rolled SLIP).
        var framed = KissEncoder.Encode(KissPort, KissCommand.Data, ax25);
        await sink.PublishAsync(framedTopic, Encode(cfg, framed), cfg.Qos, retain: false, ct).ConfigureAwait(false);
    }

    /// <summary>Build the (unframed, framed) topic strings for a frame:
    /// <c>[{prefix}/]kissproxy/{node}/{instance}/{fromModem|toModem}/…</c>.</summary>
    internal (string Unframed, string Framed) BuildTopics(MqttConfig cfg, MonitorEvent frame)
    {
        string node = ResolveNodeName(cfg);
        string instance = ResolveInstance(frame.PortId);
        string dir = frame.Direction == "out" ? "toModem" : "fromModem";
        string prefix = string.IsNullOrWhiteSpace(cfg.TopicPrefix) ? "" : cfg.TopicPrefix.Trim().TrimEnd('/') + "/";
        string baseTopic = $"{prefix}kissproxy/{node}/{instance}/{dir}";
        return ($"{baseTopic}/unframed/port{KissPort}/{DataFrameCmd}KissCmd", $"{baseTopic}/framed");
    }

    /// <summary>The <c>{instance}</c> segment for a port: its <see cref="PortConfig.MqttInstance"/>
    /// label if set, else the port's <see cref="PortConfig.Id"/> (or the raw id if the port is not in
    /// the live config, which shouldn't happen).</summary>
    internal string ResolveInstance(string portId)
    {
        foreach (var p in config.Current.Ports)
        {
            if (string.Equals(p.Id, portId, StringComparison.Ordinal))
            {
                return string.IsNullOrWhiteSpace(p.MqttInstance) ? p.Id : p.MqttInstance!;
            }
        }
        return portId;
    }

    private static string ResolveNodeName(MqttConfig cfg)
        => string.IsNullOrWhiteSpace(cfg.NodeName) ? Environment.MachineName : cfg.NodeName!.Trim();

    /// <summary>The managed client id. kissproxy runs one client per band
    /// (<c>{host}_kissproxy_{instance}</c>); pdn runs one client per node covering all ports, so the
    /// id is node-scoped. Cosmetic for broker identity only — the collector keys off topics.</summary>
    private static string ClientId(MqttConfig cfg) => $"{ResolveNodeName(cfg)}_pdn";

    /// <summary>Payload encoding: raw binary by default; base64 (with .NET
    /// <see cref="Base64FormattingOptions.InsertLineBreaks"/>) when <see cref="MqttConfig.Base64"/> —
    /// byte-for-byte kissproxy.</summary>
    private static byte[] Encode(MqttConfig cfg, byte[] bytes)
        => cfg.Base64
            ? Encoding.UTF8.GetBytes(Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks))
            : bytes;

    private static byte[] ToBytes(IReadOnlyList<int> raw)
    {
        var bytes = new byte[raw.Count];
        for (int i = 0; i < raw.Count; i++)
        {
            bytes[i] = (byte)raw[i];
        }
        return bytes;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT frame emission started; publishing to {Host}:{Port} in kissproxy format.")]
    private partial void LogStarted(string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT frame emitter: failed to start the managed client for {Host}:{Port} (will retry on the next frame).")]
    private partial void LogStartFault(Exception ex, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT frame emitter: publish faulted (frame dropped from the MQTT feed; the radio path is unaffected).")]
    private partial void LogPublishFault(Exception ex);
}
