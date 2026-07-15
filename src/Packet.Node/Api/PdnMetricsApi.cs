using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Heard;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Radios;
using Packet.Node.Core.Telemetry;

namespace Packet.Node.Api;

/// <summary>
/// The Prometheus text-exposition exporter (<c>GET /metrics</c>, #457). A hand-rolled
/// <see cref="PrometheusTextWriter"/> formats the node's live counters into the
/// <see href="https://prometheus.io/docs/instrumenting/exposition_formats/">text exposition
/// format</see> — no Prometheus client dependency is taken (the formatter is &lt;200 lines and
/// avoids a new package; see docs/observability.md for the rationale).
/// </summary>
/// <remarks>
/// <para>
/// <b>Single source of truth.</b> Every value is read from the SAME live state that backs the
/// REST/SSE <c>/api/v1/*</c> telemetry: the <see cref="NodeTelemetry"/> frame tap (per-port frame
/// totals, per-link byte/REJ/SREJ rollups), the live <see cref="Ax25Session"/> timer state (RC /
/// SRTT / queue depth, via <see cref="PdnReadApi.SessionTimers"/>), the NET/ROM forwarding
/// counters (<see cref="NetRomService.ForwardingStats"/>), and <see cref="PdnReadApi.BuildStatus"/>
/// / <see cref="PdnReadApi.BuildPorts"/> for node health. There is no second counter store.
/// </para>
/// <para>
/// <b>Label cardinality.</b> The primary label is <c>port</c> (one value per <em>configured</em>
/// port — a closed set the operator controls). Per-link byte/REJ/SREJ counters (keyed by remote
/// callsign) are <em>aggregated up to the port</em> before export, so a busy channel can never blow
/// up those series. The <b>one</b> series that carries a <c>peer</c> (remote-callsign) label is the
/// per-partner <c>pdn_link_snr_db{port,peer}</c> gauge — a deliberate, documented exception: an
/// amateur-packet node hears a naturally small, slowly-changing set of stations, so the per-callsign
/// series count stays bounded in practice (Tom's call — see docs/observability.md). Radio per-port
/// health (<c>pdn_radio_*</c>) stays on the bounded <c>port</c> label. Per-peer RSSI/SNR detail is
/// also on the bounded-by-request <c>/api/v1/heard</c> / <c>/api/v1/links</c> JSON surfaces.
/// </para>
/// <para>
/// <b>Exposure posture.</b> <c>/metrics</c> is mapped on the same Kestrel listener as the REST API
/// and is gated by the same <see cref="PdnAuthPolicies.Read"/> scope policy — so it is unauthenticated
/// when <c>management.auth.enabled</c> is off (the node binds 127.0.0.1 by default, the standard
/// localhost-scrape posture) and requires a <c>read</c>-scoped token once auth is turned on, exactly
/// like the rest of the read surface. Documented in docs/observability.md.
/// </para>
/// </remarks>
public static class PdnMetricsApi
{
    /// <summary>The metric-name namespace prefix — every series is <c>pdn_*</c>.</summary>
    private const string Ns = "pdn_";

    // Node start instant on the monotonic clock, captured once at module load so the
    // process_start_time export is wall-clock independent (repo rule §2.7).
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// Map <c>GET /metrics</c>. Called from the composition root beside the other read endpoints.
    /// </summary>
    public static void MapPdnMetrics(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _ = StartTimestamp;   // touch at startup so the start instant is module-load, not first-scrape

        app.MapGet("/metrics", (NodeHostedService host, IConfigProvider config, TimeProvider clock,
                [FromServices] Packet.Node.Core.Traffic.TrafficLogService? traffic,
                [FromServices] Packet.Node.Core.Mqtt.MqttFrameEmitter? mqtt,
                [FromServices] Packet.Node.Core.HeadEnd.HeadEndHealthMonitor? headEnds) =>
            {
                var body = Render(host, config, clock, traffic, mqtt, headEnds);
                // text/plain; version=0.0.4 is the Prometheus exposition content type a scraper expects.
                return Results.Text(body, "text/plain; version=0.0.4; charset=utf-8");
            })
            .RequireAuthorization(PdnAuthPolicies.Read)
            .WithName("metrics");
    }

    /// <summary>
    /// Render the full exposition document. Pure over the supplied state — exposed (internal) so a
    /// test can format a known node and assert the output without standing up Kestrel.
    /// </summary>
    internal static string Render(
        NodeHostedService host, IConfigProvider config, TimeProvider clock,
        Packet.Node.Core.Traffic.TrafficLogService? traffic,
        Packet.Node.Core.Mqtt.MqttFrameEmitter? mqtt = null,
        Packet.Node.Core.HeadEnd.HeadEndHealthMonitor? headEnds = null)
    {
        var w = new PrometheusTextWriter();

        WriteNodeHealth(w, host, config, clock, traffic);
        WritePortAndLinkStats(w, host, config);
        WriteTransportReconnecting(w, CollectTransportLinkStates(host.Supervisor));
        WriteRadioStats(w, RadioReadModels.All(host.Supervisor, config.Current));
        WriteLinkSnr(w, host.Heard);
        WriteLinkPreDataCarrier(w, host.Heard);
        WriteForwarding(w, host);
        WriteMqttStats(w, mqtt);
        WriteHeadEndStats(w, headEnds?.Snapshot());

        return w.ToString();
    }

    // ─── node health bucket ───────────────────────────────────────────────────

    private static void WriteNodeHealth(
        PrometheusTextWriter w, NodeHostedService host, IConfigProvider config, TimeProvider clock,
        Packet.Node.Core.Traffic.TrafficLogService? traffic)
    {
        var status = PdnReadApi.BuildStatus(host, config, clock, traffic);

        // Build/version info: the conventional info gauge (value 1, the data is in the labels).
        w.Help(Ns + "build_info", "Node build/version info (constant 1; see labels).");
        w.Type(Ns + "build_info", "gauge");
        w.Sample(Ns + "build_info", 1,
            ("version", status.Version),
            ("callsign", status.Callsign),
            ("alias", status.Alias ?? string.Empty));

        w.Help(Ns + "uptime_seconds", "Node process uptime in seconds.");
        w.Type(Ns + "uptime_seconds", "gauge");
        w.Sample(Ns + "uptime_seconds", status.UptimeSeconds);

        w.Help(Ns + "ports_total", "Number of configured radio ports.");
        w.Type(Ns + "ports_total", "gauge");
        w.Sample(Ns + "ports_total", status.PortsTotal);

        w.Help(Ns + "ports_up", "Number of configured radio ports currently up.");
        w.Type(Ns + "ports_up", "gauge");
        w.Sample(Ns + "ports_up", status.PortsUp);

        w.Help(Ns + "sessions", "Number of active connected-mode sessions across all ports.");
        w.Type(Ns + "sessions", "gauge");
        w.Sample(Ns + "sessions", status.SessionCount);

        w.Help(Ns + "netrom_neighbours", "Directly-heard NET/ROM neighbours.");
        w.Type(Ns + "netrom_neighbours", "gauge");
        w.Sample(Ns + "netrom_neighbours", status.Netrom.Neighbours);

        w.Help(Ns + "netrom_destinations", "Known NET/ROM destinations in the routing table.");
        w.Type(Ns + "netrom_destinations", "gauge");
        w.Sample(Ns + "netrom_destinations", status.Netrom.Destinations);

        // Process/runtime stats (no extra dependency — System.Diagnostics + GC).
        using var proc = Process.GetCurrentProcess();

        w.Help(Ns + "process_resident_memory_bytes", "Resident (working-set) memory of the node process, in bytes.");
        w.Type(Ns + "process_resident_memory_bytes", "gauge");
        w.Sample(Ns + "process_resident_memory_bytes", proc.WorkingSet64);

        w.Help(Ns + "process_cpu_seconds_total", "Total CPU time consumed by the node process, in seconds.");
        w.Type(Ns + "process_cpu_seconds_total", "counter");
        w.Sample(Ns + "process_cpu_seconds_total", proc.TotalProcessorTime.TotalSeconds);

        w.Help(Ns + "process_threads", "Number of OS threads in the node process.");
        w.Type(Ns + "process_threads", "gauge");
        w.Sample(Ns + "process_threads", proc.Threads.Count);

        w.Help(Ns + "process_start_time_seconds", "Node process start time as Unix epoch seconds.");
        w.Type(Ns + "process_start_time_seconds", "gauge");
        // Derived from the monotonic start instant + the current wall clock, so it is stable
        // across scrapes without storing DateTime.Now in logic (§2.7).
        double upSeconds = (clock.GetTimestamp() - StartTimestamp) / (double)Stopwatch.Frequency;
        double startEpoch = clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0 - Math.Max(0, upSeconds);
        w.Sample(Ns + "process_start_time_seconds", startEpoch);

        w.Help(Ns + "dotnet_gc_heap_bytes", "Managed GC heap size in bytes (GC.GetTotalMemory).");
        w.Type(Ns + "dotnet_gc_heap_bytes", "gauge");
        w.Sample(Ns + "dotnet_gc_heap_bytes", GC.GetTotalMemory(forceFullCollection: false));

        // Traffic log writer health — the loss counter (writer behind), never the radio path's.
        w.Help(Ns + "traffic_log_dropped_frames_total", "Frames the persistent traffic-log writer dropped (writer behind).");
        w.Type(Ns + "traffic_log_dropped_frames_total", "counter");
        w.Sample(Ns + "traffic_log_dropped_frames_total", status.Traffic.Dropped);
    }

    // ─── per-port / per-link bucket (aggregated to the port — bounded cardinality) ─────

    private static void WritePortAndLinkStats(PrometheusTextWriter w, NodeHostedService host, IConfigProvider config)
    {
        var ports = PdnReadApi.BuildPorts(host, config);

        // Per-link counters keyed by (port, peer) rolled up to the port: peer is a remote
        // callsign, so a per-peer label would be unbounded — we sum to the bounded port label.
        var perPort = new Dictionary<string, LinkRollup>(StringComparer.Ordinal);
        foreach (var link in host.Telemetry.Links())
        {
            var roll = perPort.TryGetValue(link.PortId, out var existing) ? existing : new LinkRollup();
            roll.FramesIn += link.FramesIn;
            roll.FramesOut += link.FramesOut;
            roll.BytesIn += link.BytesIn;
            roll.BytesOut += link.BytesOut;
            roll.Rej += link.RejCount;
            roll.Srej += link.SrejCount;
            perPort[link.PortId] = roll;
        }

        // Live session-state roll-up per port (retries = current RC, queue depth + outstanding
        // I-frames) from the connected sessions on that port — the monitor-v2 source.
        var sessionRoll = new Dictionary<string, SessionRollup>(StringComparer.Ordinal);
        var supervisor = host.Supervisor;
        if (supervisor is not null)
        {
            foreach (var portId in supervisor.RunningPortIds)
            {
                var rp = supervisor.GetPort(portId);
                if (rp is null)
                {
                    continue;
                }
                var roll = new SessionRollup();
                foreach (var session in rp.Listener.ActiveSessions)
                {
                    var ctx = session.Context;
                    roll.Retries += ctx.RC;
                    roll.QueueDepth += ctx.IFrameQueue.Count;
                    roll.Outstanding += ctx.SentIFrames.Count;
                }
                sessionRoll[portId] = roll;
            }
        }

        w.Help(Ns + "port_up", "Port up (1) / not up (0).");
        w.Type(Ns + "port_up", "gauge");
        foreach (var p in ports)
        {
            w.Sample(Ns + "port_up", string.Equals(p.State, "up", StringComparison.Ordinal) ? 1 : 0, ("port", p.Id));
        }

        w.Help(Ns + "port_sessions", "Active connected-mode sessions on the port.");
        w.Type(Ns + "port_sessions", "gauge");
        foreach (var p in ports)
        {
            w.Sample(Ns + "port_sessions", p.SessionCount, ("port", p.Id));
        }

        // Port-level carrier sense: whichever source feeds the listener's gate (radio
        // hardware DCD or a channel-sensing transport such as the in-process soundmodem).
        // Only emitted for ports that have a source — absence of the series means "no
        // carrier sense here", mirroring the API's null.
        w.Help(Ns + "port_channel_busy", "Port carrier sense: channel busy (1) / clear (0). Absent when the port has no carrier-sense source.");
        w.Type(Ns + "port_channel_busy", "gauge");
        foreach (var p in ports)
        {
            if (p.ChannelBusy is bool busy)
            {
                w.Sample(Ns + "port_channel_busy", busy ? 1 : 0, ("port", p.Id));
            }
        }

        // Frame totals come from the per-port frame tap directly (PortStatus), independent of
        // whether any (port,peer) link row exists.
        w.Help(Ns + "port_frames_received_total", "AX.25 frames received on the port.");
        w.Type(Ns + "port_frames_received_total", "counter");
        foreach (var p in ports)
        {
            w.Sample(Ns + "port_frames_received_total", p.FramesIn, ("port", p.Id));
        }

        w.Help(Ns + "port_frames_transmitted_total", "AX.25 frames transmitted on the port.");
        w.Type(Ns + "port_frames_transmitted_total", "counter");
        foreach (var p in ports)
        {
            w.Sample(Ns + "port_frames_transmitted_total", p.FramesOut, ("port", p.Id));
        }

        // Byte totals + REJ/SREJ come from the per-link rollup (summed across peers on the port).
        w.Help(Ns + "port_info_bytes_received_total", "AX.25 information-field bytes received on the port (summed over peers).");
        w.Type(Ns + "port_info_bytes_received_total", "counter");
        foreach (var p in ports)
        {
            w.Sample(Ns + "port_info_bytes_received_total", Roll(perPort, p.Id).BytesIn, ("port", p.Id));
        }

        w.Help(Ns + "port_info_bytes_transmitted_total", "AX.25 information-field bytes transmitted on the port (summed over peers).");
        w.Type(Ns + "port_info_bytes_transmitted_total", "counter");
        foreach (var p in ports)
        {
            w.Sample(Ns + "port_info_bytes_transmitted_total", Roll(perPort, p.Id).BytesOut, ("port", p.Id));
        }

        w.Help(Ns + "port_rej_total", "REJ (go-back-N reject) frames seen on the port (summed over peers).");
        w.Type(Ns + "port_rej_total", "counter");
        foreach (var p in ports)
        {
            w.Sample(Ns + "port_rej_total", Roll(perPort, p.Id).Rej, ("port", p.Id));
        }

        w.Help(Ns + "port_srej_total", "SREJ (selective reject) frames seen on the port (summed over peers).");
        w.Type(Ns + "port_srej_total", "counter");
        foreach (var p in ports)
        {
            w.Sample(Ns + "port_srej_total", Roll(perPort, p.Id).Srej, ("port", p.Id));
        }

        // Live session-state gauges (current retries / queue depth / outstanding unacked I-frames).
        w.Help(Ns + "port_retries", "Sum of the current retry counter (RC) over the port's live sessions.");
        w.Type(Ns + "port_retries", "gauge");
        foreach (var p in ports)
        {
            w.Sample(Ns + "port_retries", SessionRoll(sessionRoll, p.Id).Retries, ("port", p.Id));
        }

        w.Help(Ns + "port_tx_queue_depth", "Sum of pending (unsent) I-frames queued over the port's live sessions.");
        w.Type(Ns + "port_tx_queue_depth", "gauge");
        foreach (var p in ports)
        {
            w.Sample(Ns + "port_tx_queue_depth", SessionRoll(sessionRoll, p.Id).QueueDepth, ("port", p.Id));
        }

        w.Help(Ns + "port_outstanding_iframes", "Sum of sent-but-unacknowledged I-frames over the port's live sessions.");
        w.Type(Ns + "port_outstanding_iframes", "gauge");
        foreach (var p in ports)
        {
            w.Sample(Ns + "port_outstanding_iframes", SessionRoll(sessionRoll, p.Id).Outstanding, ("port", p.Id));
        }
    }

    // ─── per-port transport link-state bucket (reconnect-supervised transports only) ─────

    // One (port, IsReconnecting) row per running port whose transport chain carries a reconnect
    // decorator (kiss-tcp / nino-tnc-tcp), read off the pre-decorator capture on RunningPort.
    private static List<(string PortId, bool Reconnecting)> CollectTransportLinkStates(PortSupervisor? supervisor)
    {
        var rows = new List<(string, bool)>();
        if (supervisor is null)
        {
            return rows;
        }
        foreach (var portId in supervisor.RunningPortIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (supervisor.GetPort(portId)?.LinkState is { } linkState)
            {
                rows.Add((portId, linkState.IsReconnecting));
            }
        }
        return rows;
    }

    /// <summary>
    /// The per-port transport-reconnecting gauge (#583): 1 while the port's self-healing transport
    /// (the reconnect decorator on a kiss-tcp / nino-tnc-tcp port) has lost its link and is
    /// re-dialling, else 0 — the honest counterpart to <c>pdn_port_up</c>, which stays 1 through a
    /// far-end bounce because the port (listener) itself never goes down. Ports with no reconnect
    /// supervision (local serial, AXUDP) emit nothing; the whole bucket is absent on a node with no
    /// networked ports. Exposed (internal) so a test can format synthetic rows directly.
    /// </summary>
    internal static void WriteTransportReconnecting(
        PrometheusTextWriter w, IReadOnlyList<(string PortId, bool Reconnecting)> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        w.Help(Ns + "port_transport_reconnecting", "Transport link down and re-dialling (1) / link established (0). Reconnect-supervised (networked) ports only.");
        w.Type(Ns + "port_transport_reconnecting", "gauge");
        foreach (var (portId, reconnecting) in rows)
        {
            w.Sample(Ns + "port_transport_reconnecting", reconnecting ? 1 : 0, ("port", portId));
        }
    }

    // ─── forwarding throughput bucket ──────────────────────────────────────────

    private static void WriteForwarding(PrometheusTextWriter w, NodeHostedService host)
    {
        var f = host.NetRom?.ForwardingStats
                ?? new NetRomForwardingStats(0, 0, 0, 0, 0);

        w.Help(Ns + "netrom_forwarded_frames_total", "NET/ROM transit datagrams forwarded toward their destination.");
        w.Type(Ns + "netrom_forwarded_frames_total", "counter");
        w.Sample(Ns + "netrom_forwarded_frames_total", f.ForwardedFrames);

        w.Help(Ns + "netrom_forwarded_bytes_total", "NET/ROM transit datagram bytes forwarded.");
        w.Type(Ns + "netrom_forwarded_bytes_total", "counter");
        w.Sample(Ns + "netrom_forwarded_bytes_total", f.ForwardedBytes);

        // Drops broken out by reason as one series with a bounded `reason` label (3 closed values).
        w.Help(Ns + "netrom_forward_drops_total", "NET/ROM transit datagrams dropped on the forward path, by reason.");
        w.Type(Ns + "netrom_forward_drops_total", "counter");
        w.Sample(Ns + "netrom_forward_drops_total", f.DroppedTtlExpired, ("reason", "ttl_expired"));
        w.Sample(Ns + "netrom_forward_drops_total", f.DroppedLooped, ("reason", "looped"));
        w.Sample(Ns + "netrom_forward_drops_total", f.DroppedNoRoute, ("reason", "no_route"));
    }

    // ─── per-port radio-control health bucket (bounded by the port label; attached radios only) ─────

    /// <summary>
    /// Per-port radio-control metrics, read from the SAME <see cref="RadioReadModels"/> projection the
    /// <c>/api/v1/radios</c> API serves (no second source of truth). Only ports whose radio the node
    /// currently has OPEN and is polling contribute — a configured-but-not-attached radio (port down,
    /// or a failed open that degraded the port) emits nothing, so the whole bucket is <em>absent</em>
    /// on a node with no radios (identical output to before this bucket existed). SNR / noise-floor are
    /// deliberately NOT here: they are per-frame concepts the radio's own health telemetry (averaged
    /// RSSI, PA temperature, TX detector trends) does not carry — SNR is surfaced <em>per partner</em>
    /// by <see cref="WriteLinkSnr"/> instead. See docs/observability.md. Exposed (internal) so a test
    /// can format synthetic <see cref="RadioStatus"/> records without standing up a live supervisor.
    /// </summary>
    internal static void WriteRadioStats(PrometheusTextWriter w, IReadOnlyList<RadioStatus> radios)
    {
        var attached = radios.Where(r => r.Attached).ToList();
        if (attached.Count == 0)
        {
            return;
        }

        // Control-link health as a small enum-ish gauge (always present for an attached radio):
        // 1 = healthy (answering), 0 = faulted (serial link dead / unresponsive), -1 = unknown (a
        // radio kind that doesn't track it, e.g. a non-Tait IRadioControl).
        w.Help(Ns + "radio_connection_state", "Radio control-link health: 1 healthy, 0 faulted, -1 unknown.");
        w.Type(Ns + "radio_connection_state", "gauge");
        foreach (var r in attached)
        {
            w.Sample(Ns + "radio_connection_state", ConnectionStateCode(r.ConnectionState), ("port", r.PortId));
        }

        // Hardware carrier-sense (DCD): 1 = RF on channel, 0 = idle. Omitted for a radio that hasn't
        // reported it yet (a bare 0 would read as "channel definitely idle", which we don't know).
        RadioMetric(w, attached, "radio_channel_busy",
            "Radio hardware carrier-sense (DCD): 1 = channel busy, 0 = idle.", "gauge",
            r => r.ChannelBusy is { } b ? (b ? 1 : 0) : (double?)null);

        // The health-sample projection (the same RadioHealth /api/v1/radios serves). Each series omits
        // a port whose radio hasn't produced that reading — a null renders as an absent sample, never
        // a misleading 0 (0 dBm RSSI or 0 °C would both be wrong-but-plausible).
        RadioMetric(w, attached, "radio_rssi_dbm",
            "Radio's own most-recent sliding-average RSSI, in dBm (receive samples only).", "gauge",
            r => r.Health?.RssiDbm);
        RadioMetric(w, attached, "radio_rssi_averaged_dbm",
            "Median RSSI over the radio health monitor's rolling window, in dBm.", "gauge",
            r => r.Health?.AveragedRssiDbm);
        RadioMetric(w, attached, "radio_pa_temperature_celsius",
            "Power-amplifier temperature, in degrees Celsius.", "gauge",
            r => r.Health?.PaTemperatureC);
        RadioMetric(w, attached, "radio_forward_trend_millivolts",
            "Offset-corrected forward-power detector reading, in mV (a per-station TREND on transmit, not a power measurement).", "gauge",
            r => r.Health?.ForwardTrendMillivolts);
        RadioMetric(w, attached, "radio_reverse_trend_millivolts",
            "Offset-corrected reverse-power detector reading, in mV (a TREND, not a power measurement).", "gauge",
            r => r.Health?.ReverseTrendMillivolts);
        RadioMetric(w, attached, "radio_reverse_forward_ratio",
            "Offset-corrected reverse/forward detector ratio (a per-station TREND, never VSWR — alert on change).", "gauge",
            r => r.Health?.ReverseForwardRatio);
    }

    // Emit one per-port radio gauge, sampling only the attached radios whose selector is non-null.
    // When no attached radio has the reading, the whole metric (HELP/TYPE included) is omitted.
    private static void RadioMetric(
        PrometheusTextWriter w, IReadOnlyList<RadioStatus> attached, string name, string help, string type,
        Func<RadioStatus, double?> select)
    {
        var rows = attached
            .Select(r => (r.PortId, Value: select(r)))
            .Where(x => x.Value is not null)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }
        w.Help(Ns + name, help);
        w.Type(Ns + name, type);
        foreach (var (portId, value) in rows)
        {
            w.Sample(Ns + name, value!.Value, ("port", portId));
        }
    }

    private static int ConnectionStateCode(string state) => state switch
    {
        "healthy" => 1,
        "faulted" => 0,
        _ => -1,   // "unknown", or any radio kind that doesn't track the control-link state
    };

    // ─── MQTT frame-emission bucket (#582 — the kissproxy-compatible emitter's health) ─────────────

    /// <summary>
    /// The MQTT frame emitter's publish counters + pending-queue gauge, read straight off the live
    /// <see cref="Packet.Node.Core.Mqtt.MqttFrameEmitter"/> (no second counter store). Counters are
    /// emitted whenever the emitter service is registered — from zero, so <c>rate()</c> works from
    /// the first scrape — even while the integration is disabled (everything just stays 0). Absent
    /// only in a stripped embedder that never registered the emitter. Exposed (internal) so a test
    /// can format an emitter it drove through a fake sink.
    /// </summary>
    internal static void WriteMqttStats(PrometheusTextWriter w, Packet.Node.Core.Mqtt.MqttFrameEmitter? mqtt)
    {
        if (mqtt is null)
        {
            return;
        }

        w.Help(Ns + "mqtt_published_total", "MQTT messages handed to the broker client by the frame emitter (two per frame: unframed + framed).");
        w.Type(Ns + "mqtt_published_total", "counter");
        w.Sample(Ns + "mqtt_published_total", mqtt.PublishedTotal);

        w.Help(Ns + "mqtt_publish_failures_total", "MQTT publish attempts that faulted (frames dropped from the MQTT feed only; the radio path is unaffected).");
        w.Type(Ns + "mqtt_publish_failures_total", "counter");
        w.Sample(Ns + "mqtt_publish_failures_total", mqtt.PublishFailuresTotal);

        w.Help(Ns + "mqtt_pending_messages", "Messages queued in the managed MQTT client awaiting the broker (bounded, drop-oldest).");
        w.Type(Ns + "mqtt_pending_messages", "gauge");
        w.Sample(Ns + "mqtt_pending_messages", mqtt.PendingMessages);
    }

    // ─── head-end fleet bucket (#583 — the background health poller's snapshot) ─────────────────────

    /// <summary>
    /// The split-station head-end fleet's health (#583), read straight off the
    /// <see cref="Packet.Node.Core.HeadEnd.HeadEndHealthMonitor"/>'s rolling snapshot (the same data
    /// the <c>GET /api/v1/radios/headends</c> enrichment serves — no probing on the scrape path). The
    /// <c>instance</c> label is the head-end's stable instance id — a closed, operator-controlled set,
    /// so cardinality is bounded. The whole bucket is absent on a node with no head-ends (or before
    /// the monitor's first cycle); the devices gauge is additionally omitted while an instance is
    /// unreachable or only answers the bare pre-<c>/statusz</c> <c>/healthz</c> (an absent sample,
    /// never a misleading stale count). The failures counter is always emitted for every monitored
    /// instance — from zero, so <c>rate()</c> works from the first scrape. Exposed (internal) so a
    /// test can format synthetic snapshots directly.
    /// </summary>
    internal static void WriteHeadEndStats(
        PrometheusTextWriter w, IReadOnlyList<Packet.Node.Core.HeadEnd.HeadEndHealth>? instances)
    {
        if (instances is null || instances.Count == 0)
        {
            return;
        }

        w.Help(Ns + "headend_reachable", "Head-end control plane answered the most recent health poll (1) or not (0).");
        w.Type(Ns + "headend_reachable", "gauge");
        foreach (var h in instances)
        {
            w.Sample(Ns + "headend_reachable", h.Reachable ? 1 : 0, ("instance", h.InstanceId));
        }

        // Only instances that are reachable AND statusz-capable report a live device count; an
        // absent sample is "unknown", never a stale number.
        var withDevices = instances.Where(h => h.Reachable && h.BridgeCount is not null).ToList();
        if (withDevices.Count > 0)
        {
            w.Help(Ns + "headend_devices", "Devices (serial bridges) the head-end currently exposes. Absent while unreachable or on a pre-/statusz daemon.");
            w.Type(Ns + "headend_devices", "gauge");
            foreach (var h in withDevices)
            {
                w.Sample(Ns + "headend_devices", h.BridgeCount!.Value, ("instance", h.InstanceId));
            }
        }

        w.Help(Ns + "headend_poll_failures_total", "Failed head-end health polls since the node started tracking the instance.");
        w.Type(Ns + "headend_poll_failures_total", "counter");
        foreach (var h in instances)
        {
            w.Sample(Ns + "headend_poll_failures_total", h.PollFailuresTotal, ("instance", h.InstanceId));
        }
    }

    // ─── per-partner SNR (the deliberate per-callsign label — see the class remarks + docs) ─────────

    /// <summary>
    /// The per-partner SNR gauge <c>pdn_link_snr_db{port,peer}</c> — one sample per (port, remote
    /// callsign) the node has heard <em>with a measured SNR</em>, straight from the heard log's
    /// last-heard SNR (fed by the per-frame radio metadata). This is the <b>one</b> series that carries
    /// a <c>peer</c> (remote-callsign) label, a deliberate exception to the exporter's aggregate-to-port
    /// cardinality policy: an amateur-packet node hears a naturally small, slowly-changing set of
    /// stations, so the per-callsign series count stays bounded in practice (Tom's call — see
    /// docs/observability.md). A partner with no measured SNR (a radio-less port, or never heard while a
    /// radio was attributing SNR) contributes nothing, so the whole bucket is absent on a node with no
    /// radio telemetry. Exposed (internal) so a test can format a seeded heard log directly.
    /// </summary>
    internal static void WriteLinkSnr(PrometheusTextWriter w, HeardLog? heard)
    {
        if (heard is null)
        {
            return;
        }

        // Per (port, callsign) heard row that carries an SNR reading; most-recently-heard first for a
        // stable, human-readable ordering. Rows without a measured SNR are simply skipped.
        var rows = heard.All()
            .Where(e => e.LastSnrDb is not null)
            .OrderByDescending(e => e.LastHeard)
            .ThenBy(e => e.PortId, StringComparer.Ordinal)
            .ThenBy(e => e.Callsign, StringComparer.Ordinal)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        w.Help(Ns + "link_snr_db", "SNR (dB) of the newest frame heard from a link partner, by port and remote callsign.");
        w.Type(Ns + "link_snr_db", "gauge");
        foreach (var e in rows)
        {
            w.Sample(Ns + "link_snr_db", e.LastSnrDb!.Value, ("port", e.PortId), ("peer", e.Callsign));
        }
    }

    // ─── per-partner pre-data carrier (TXDELAY-as-heard; same accepted per-peer cardinality
    //     policy as pdn_link_snr_db — see docs/research/txdelay-optimisation.md) ───────────────────

    /// <summary>
    /// The per-partner pre-data-carrier gauge <c>pdn_link_predata_carrier_ms{port,peer}</c> — one
    /// sample per (port, remote callsign) with a measured rolling median, straight from the heard
    /// log (fed by <c>RadioMetadata.PreDataCarrier</c> on burst-opening frames). The value is the
    /// peer's effective TXDELAY as heard on this port, plus a small constant rig overhead
    /// (~40–75 ms at 1200 Bd) — trend it, alert on peers persistently above the excess-TXDELAY
    /// threshold. Carries the <c>peer</c> label under the same deliberate, Tom-accepted
    /// cardinality exception as <see cref="WriteLinkSnr"/>. Peers with no measurement contribute
    /// nothing, so the whole bucket is absent on a node with no radio telemetry. Exposed
    /// (internal) so a test can format a seeded heard log directly.
    /// </summary>
    internal static void WriteLinkPreDataCarrier(PrometheusTextWriter w, HeardLog? heard)
    {
        if (heard is null)
        {
            return;
        }

        var rows = heard.All()
            .Where(e => e.MedianPreDataCarrierMs is not null)
            .OrderByDescending(e => e.LastHeard)
            .ThenBy(e => e.PortId, StringComparer.Ordinal)
            .ThenBy(e => e.Callsign, StringComparer.Ordinal)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        w.Help(Ns + "link_predata_carrier_ms",
            "Rolling median carrier-rise-to-data lead (ms) of frames heard from a link partner — its effective TXDELAY as heard — by port and remote callsign.");
        w.Type(Ns + "link_predata_carrier_ms", "gauge");
        foreach (var e in rows)
        {
            w.Sample(Ns + "link_predata_carrier_ms", e.MedianPreDataCarrierMs!.Value,
                ("port", e.PortId), ("peer", e.Callsign));
        }
    }

    private static LinkRollup Roll(Dictionary<string, LinkRollup> map, string portId)
        => map.TryGetValue(portId, out var r) ? r : new LinkRollup();

    private static SessionRollup SessionRoll(Dictionary<string, SessionRollup> map, string portId)
        => map.TryGetValue(portId, out var r) ? r : new SessionRollup();

    private sealed class LinkRollup
    {
        public long FramesIn;
        public long FramesOut;
        public long BytesIn;
        public long BytesOut;
        public long Rej;
        public long Srej;
    }

    private sealed class SessionRollup
    {
        public long Retries;
        public long QueueDepth;
        public long Outstanding;
    }
}

/// <summary>
/// A tiny hand-rolled Prometheus text-exposition writer (#457): emits <c># HELP</c> / <c># TYPE</c>
/// header lines and value samples with optional bounded labels, escaping label values per the
/// exposition spec (backslash, double-quote, newline). One <c>HELP</c>/<c>TYPE</c> pair precedes
/// each metric's samples. Deliberately minimal — it avoids taking a Prometheus client dependency
/// for a read-only scrape surface (see PdnMetricsApi remarks). Not thread-safe; build one per request.
/// </summary>
internal sealed class PrometheusTextWriter
{
    private readonly StringBuilder sb = new();

    /// <summary>Emit a <c># HELP &lt;metric&gt; &lt;text&gt;</c> line.</summary>
    public void Help(string metric, string help)
        => sb.Append("# HELP ").Append(metric).Append(' ').Append(EscapeHelp(help)).Append('\n');

    /// <summary>Emit a <c># TYPE &lt;metric&gt; &lt;type&gt;</c> line (counter|gauge|…).</summary>
    public void Type(string metric, string type)
        => sb.Append("# TYPE ").Append(metric).Append(' ').Append(type).Append('\n');

    /// <summary>Emit one sample line: <c>metric{labels} value</c>.</summary>
    public void Sample(string metric, double value, params (string Name, string Value)[] labels)
    {
        sb.Append(metric);
        if (labels.Length > 0)
        {
            sb.Append('{');
            for (int i = 0; i < labels.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(labels[i].Name).Append("=\"").Append(EscapeLabel(labels[i].Value)).Append('"');
            }
            sb.Append('}');
        }
        sb.Append(' ').Append(Format(value)).Append('\n');
    }

    public override string ToString() => sb.ToString();

    // Integral values render without a decimal point; non-integral use round-trip ("R") so a
    // double like CPU seconds reaches the scraper losslessly. Always invariant culture.
    private static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    // HELP text: only backslash + newline are special.
    private static string EscapeHelp(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    // Label values: backslash, double-quote, newline.
    private static string EscapeLabel(string s)
        => s.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
