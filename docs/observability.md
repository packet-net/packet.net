# Observability: the Prometheus `/metrics` exporter

The node exposes a Prometheus-compatible scrape endpoint at **`GET /metrics`** ([#457](https://github.com/packet-net/packet.net/issues/457)) so operators can graph and alert on node health, per-port behaviour, and NET/ROM forwarding throughput with standard tooling (Prometheus + Grafana). It complements — it does not replace — the REST/SSE `/api/v1/*` telemetry the web control panel consumes.

## Where it lives, and the single source of truth

The exporter is `src/Packet.Node/Api/PdnMetricsApi.cs`. Every value it emits is read from the **same live state** that backs the REST/SSE telemetry — there is no second counter store:

- **`NodeTelemetry`** (the frame-trace tap) — per-port frame totals and per-(port, peer) byte / REJ / SREJ rollups. The same source `GET /api/v1/links` and `GET /api/v1/ports` project from.
- **The live `Ax25Session` timer state** — current retry counter (RC), pending I-frame queue depth, and outstanding (sent-but-unacked) I-frames, summed over a port's sessions. The same monitor-v2 source `/api/v1/links` reads `SmoothedRttMs` / `Retries` from.
- **`NetRomService.ForwardingStats`** — the L3 transit-forwarding counters (frames/bytes forwarded + drops by reason), bumped on the same `ForwardDatagram` path that does the routing.
- **`PdnReadApi.BuildStatus` / `BuildPorts`** — node identity, version, uptime, ports up/total, session count, NET/ROM neighbour/destination counts. Literally the same projection helpers the `/api/v1/status` and `/api/v1/ports` endpoints call.

So a number on `/metrics` and the corresponding number in the JSON API can never diverge: they are computed from one set of counters.

## Exposure / auth posture

`/metrics` is mapped on the **same Kestrel listener** as the REST API (simplest; no second port to bind or firewall) and is gated by the **same `read` scope policy** (`PdnAuthPolicies.Read`) as the rest of the read surface. Concretely:

- With `management.auth.enabled` **off** (the default), `/metrics` is **unauthenticated** — and the node **binds 127.0.0.1 by default**, so the out-of-the-box posture is the standard localhost-scrape one (run the Prometheus agent on the same host, or scrape across a Tailscale tailnet).
- With auth **on**, `/metrics` requires a `read`-scoped bearer token, exactly like `/api/v1/status`.

The endpoint is read-only and has no side effects. The response content type is `text/plain; version=0.0.4; charset=utf-8` (the Prometheus text exposition content type).

## Exporter mechanics

In-process, **no Prometheus client dependency**. A hand-rolled `PrometheusTextWriter` (~70 lines, in the same file) emits `# HELP` / `# TYPE` headers and value samples, escaping label values per the exposition format. A new package for a read-only scrape surface wasn't worth the dependency footprint.

## Label cardinality — bounded by design

The primary label is **`port`** (one value per *configured* port — a closed set the operator controls), plus a 3-value `reason` label on the forward-drops series. A per-(port, peer) link is keyed by the *remote* callsign, so the **byte / REJ / SREJ / queue-depth / retry** counters are **aggregated up to the port** before export — a busy or hostile channel can never blow up those series' count. Per-peer detail for those stays on the bounded-by-request `GET /api/v1/links` JSON surface, where the client asks for it explicitly.

### The one peer-labelled series: `pdn_link_snr_db`

There is exactly **one** series that carries a `peer` (remote-callsign) label: the per-partner SNR gauge `pdn_link_snr_db{port,peer}`. This is a **deliberate, documented exception** to the aggregate-to-port policy above.

The rationale is a judgement call about the real world (Tom's call): **amateur packet radio will never be that popular.** The number of distinct stations a node hears is naturally small and slowly-changing — a per-callsign SNR label is not going to explode Prometheus's series count in practice the way a per-callsign label on a high-churn frame/byte counter could. The operational value of seeing SNR *per link partner* (which neighbour's signal is degrading?) is worth the one closed-eye on cardinality, where the same label on the byte/REJ counters is not (you'd never alert on per-peer byte totals, and they churn with every casual passer-by).

So `pdn_link_snr_db` is emitted for **every** station the node has heard *with a measured SNR* — no bounding to configured neighbours or active links, no top-N cap. A station heard on a port with no radio attached (or heard before any SNR could be attributed) simply has no SNR reading and contributes no series, so the whole bucket is absent on a node with no radio telemetry. The value is the SNR of the newest frame heard from that (port, callsign), read from the same MHeard log (`GET /api/v1/heard`, `lastSnrDb`) — no second source of truth.

If a specific deployment ever did prove this wrong (a node parked on an unusually busy channel hearing thousands of distinct calls), the fix is a Prometheus-side `metric_relabel_configs` drop/keep on the `peer` label, or reintroducing a node-side cap — but that is explicitly not built today.

## The metric set

All metrics use the `pdn_` namespace. Counters are monotonic over the process lifetime; gauges are point-in-time.

### Node health

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `pdn_build_info` | gauge | `version`, `callsign`, `alias` | Constant `1`; the build/version + node identity live in the labels (the conventional info-gauge pattern). |
| `pdn_uptime_seconds` | gauge | — | Node process uptime in seconds. |
| `pdn_ports_total` | gauge | — | Number of configured radio ports. |
| `pdn_ports_up` | gauge | — | Number of configured ports currently up. |
| `pdn_sessions` | gauge | — | Active connected-mode sessions across all ports. |
| `pdn_netrom_neighbours` | gauge | — | Directly-heard NET/ROM neighbours. |
| `pdn_netrom_destinations` | gauge | — | Known NET/ROM destinations in the routing table. |
| `pdn_process_resident_memory_bytes` | gauge | — | Working-set memory of the node process. |
| `pdn_process_cpu_seconds_total` | counter | — | Total CPU time consumed by the node process. |
| `pdn_process_threads` | gauge | — | OS threads in the node process. |
| `pdn_process_start_time_seconds` | gauge | — | Process start time as Unix epoch seconds. |
| `pdn_dotnet_gc_heap_bytes` | gauge | — | Managed GC heap size. |
| `pdn_traffic_log_dropped_frames_total` | counter | — | Frames the persistent traffic-log writer dropped (writer behind — never the radio path's loss). |

### Per-port / per-link (aggregated to the port)

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `pdn_port_up` | gauge | `port` | Port up (1) / not up (0). |
| `pdn_port_sessions` | gauge | `port` | Active sessions on the port. |
| `pdn_port_frames_received_total` | counter | `port` | AX.25 frames received on the port. |
| `pdn_port_frames_transmitted_total` | counter | `port` | AX.25 frames transmitted on the port. |
| `pdn_port_info_bytes_received_total` | counter | `port` | Information-field bytes received (summed over peers). |
| `pdn_port_info_bytes_transmitted_total` | counter | `port` | Information-field bytes transmitted (summed over peers). |
| `pdn_port_rej_total` | counter | `port` | REJ (go-back-N reject) frames seen (summed over peers). |
| `pdn_port_srej_total` | counter | `port` | SREJ (selective reject) frames seen (summed over peers). |
| `pdn_port_transport_reconnecting` | gauge | `port` | `1` while the port's self-healing transport (the reconnect decorator on a kiss-tcp / nino-tnc-tcp port) has lost its link and is re-dialling, else `0`. The honest counterpart to `pdn_port_up`, which stays `1` through a far-end bounce (the listener itself never goes down). Emitted only for reconnect-supervised (networked) ports; absent on local-serial / AXUDP ports. |
| `pdn_port_retries` | gauge | `port` | Sum of the current retry counter (RC) over the port's live sessions. |
| `pdn_port_tx_queue_depth` | gauge | `port` | Sum of pending (unsent) I-frames queued over the port's live sessions. |
| `pdn_port_outstanding_iframes` | gauge | `port` | Sum of sent-but-unacknowledged I-frames over the port's live sessions. |

### Forwarding throughput

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `pdn_netrom_forwarded_frames_total` | counter | — | NET/ROM transit datagrams forwarded toward their destination. |
| `pdn_netrom_forwarded_bytes_total` | counter | — | NET/ROM transit datagram bytes forwarded. |
| `pdn_netrom_forward_drops_total` | counter | `reason` (`ttl_expired` \| `looped` \| `no_route`) | Transit datagrams dropped on the forward path, by reason. |

The forwarding bucket is all-zero on an endpoint-only or NET/ROM-disabled node (nothing is ever forwarded).

### Per-port radio control (radio-attached ports only)

Emitted **only** for a port whose radio the node currently has open and is polling — the same live `RadioStatus` / `RadioHealth` projection `GET /api/v1/radios` and `GET /api/v1/ports/{id}/radio` serve (no second source of truth). A configured-but-not-attached radio (port down, or a failed open that degraded the port) contributes nothing, so **the whole bucket is absent on a node with no radios** — identical output to before it existed. Each `gauge` below omits a port whose radio hasn't produced that reading yet: a null renders as an *absent sample*, never a misleading `0` (0 dBm RSSI, 0 °C, "channel idle" are all wrong-but-plausible).

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `pdn_radio_connection_state` | gauge | `port` | Control-link health: `1` healthy (answering), `0` faulted (serial link dead/unresponsive), `-1` unknown (a radio kind that doesn't track it). Always present for an attached radio. |
| `pdn_radio_channel_busy` | gauge | `port` | Hardware carrier-sense (DCD): `1` = RF on channel, `0` = idle. Omitted until first reported. |
| `pdn_radio_rssi_dbm` | gauge | `port` | The radio's own most-recent sliding-average RSSI, in dBm (receive samples only). |
| `pdn_radio_rssi_averaged_dbm` | gauge | `port` | Median RSSI over the radio health monitor's rolling window, in dBm. |
| `pdn_radio_pa_temperature_celsius` | gauge | `port` | Power-amplifier temperature, in °C (null on radios that report only an ADC value, e.g. TM8200). |
| `pdn_radio_forward_trend_millivolts` | gauge | `port` | Offset-corrected forward-power detector reading, in mV. A per-station **trend** on transmit, **not** a power measurement. |
| `pdn_radio_reverse_trend_millivolts` | gauge | `port` | Offset-corrected reverse-power detector reading, in mV. A **trend**, not a power measurement. |
| `pdn_radio_reverse_forward_ratio` | gauge | `port` | Offset-corrected reverse/forward detector ratio. A per-station **trend, never VSWR** — the detectors are uncalibrated + √P-scaled; alert on *change*, never the absolute value. |

**Why no per-port `pdn_radio_snr_db` / `pdn_radio_noise_floor_dbm`.** SNR and noise-floor are *per-frame* concepts (the RSSI-tagging transport tracks an idle-channel noise-floor EMA and derives each frame's SNR from it). The radio's own *health* telemetry — what `RadioHealth` carries and what this bucket reads — samples averaged RSSI, PA temperature and the TX power-detector trends, and does **not** carry a port-level SNR or noise floor. Rather than fabricate a port-level number the API doesn't have, SNR is surfaced **per link partner** below, where the per-frame metadata genuinely provides it.

### Per-partner SNR (the one peer-labelled series)

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `pdn_link_snr_db` | gauge | `port`, `peer` | SNR (dB) of the newest frame heard from a link partner, by port + remote callsign. One series per (port, callsign) the node has heard *with a measured SNR*. See [the cardinality note](#the-one-peer-labelled-series-pdn_link_snr_db) — this is the single deliberate peer label. |

Absent entirely on a node with no radio telemetry (no partner has a measured SNR).

### Head-end fleet (split-station nodes only)

Read from the `HeadEndHealthMonitor`'s rolling snapshot — a background ~30 s poll of each
configured/referenced head-end's HTTP control plane (`GET /statusz`, falling back to `GET /healthz`
on a pre-0.1.4 daemon; #583) — the same data the `GET /api/v1/radios/headends` `reachableNow` /
`lastSeen` enrichment serves, never probed on the scrape path. The `instance` label is the
head-end's stable instance id — a closed, operator-controlled set, so cardinality stays bounded
like `port`. (Note: a Prometheus scrape also attaches its own target-level `instance` label; with
the default `honor_labels: false` this series label lands as `exported_instance` on ingest.) The
whole bucket is **absent on a node with no head-ends** (or before the monitor's first cycle).

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `pdn_headend_reachable` | gauge | `instance` | The head-end's control plane answered the most recent health poll (`1`) or not (`0`). |
| `pdn_headend_devices` | gauge | `instance` | Devices (serial bridges) the head-end currently exposes. Omitted while unreachable, and on an older daemon that only answers `/healthz` — an absent sample, never a stale count. |
| `pdn_headend_poll_failures_total` | counter | `instance` | Failed health polls since the node started tracking the instance. Always emitted (from `0`) for every monitored instance, so `rate()` works from the first scrape. |

## Example scrape

```sh
curl -s http://127.0.0.1:8080/metrics
```

```
# HELP pdn_uptime_seconds Node process uptime in seconds.
# TYPE pdn_uptime_seconds gauge
pdn_uptime_seconds 3612
# HELP pdn_port_frames_received_total AX.25 frames received on the port.
# TYPE pdn_port_frames_received_total counter
pdn_port_frames_received_total{port="vhf"} 14823
...
```

A minimal Prometheus scrape config:

```yaml
scrape_configs:
  - job_name: pdn
    static_configs:
      - targets: ["127.0.0.1:8080"]
```
