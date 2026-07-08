# PDN MQTT frame emission (kissproxy-compatible)

**Status:** ✅ **shipped** (PR #558 — `MqttFrameEmitter`, default-off; released in **node-v0.28.0**,
2026-07-05). Emitter hardening (client-id collision between same-hostname nodes, unbounded
managed-client pending queue while the broker is down) is tracked in
[#582](https://github.com/packet-net/packet.net/issues/582); the fidelity caveats below are
**accepted limitations** of the shipped v1, noted inline. Goal: PDN emits every KISS/AX.25 frame it
sends/receives to an MQTT broker in **kissproxy's native wire format**, so PDN can replace kissproxy
at a site (e.g. `gb7rdg-node`) without losing the downstream `kiss-collector` capture pipeline.

## Grounded contract

Verified two ways: kissproxy's publisher source (`M0LTE/kissproxy` `KissProxy.cs`) **and** the live
`kiss-collector` DB (`gb7rdg-node`, 137 k frames since 2026-06-22), which stores topics like:

```
kissproxy/gb7rdg-node/70cm/fromModem/unframed/port0/DataFrameKissCmd
kissproxy/gb7rdg-node/40m/toModem/unframed/port0/AckModeKissCmd
```

Topic: `[{prefix}/]kissproxy/{host}/{instance}/{fromModem|toModem}/{sub}` where:

- `{host}` = machine name (here `gb7rdg-node`).
- `{instance}` = the modem id — **operationally the band** (`70cm`/`40m`/`2m`/`6m`). This is how the
  collector's `band` column is populated. **PDN must emit the band here or it fragments the DB.**
- direction `fromModem` = RX (heard), `toModem` = TX (sent).
- `{sub}` is one of three: `framed` (full KISS frame incl. FEND + type byte), `unframed/port{p}/{Cmd}KissCmd`
  (SLIP-decoded AX.25 bytes — **the topic the collector actually ingests**), `decoded/port{p}/`
  (single-line ax2txt human decode; collector does **not** ingest this).
- `{Cmd}` ∈ {DataFrame, AckMode, TxDelay, Persistence, SlotTime, TxTail, FullDuplex, SetHardware, ExitKissMode}.

Publish semantics: **QoS 2 (ExactlyOnce)**, **retain false**, managed client, auto-reconnect ~5 s,
client-id `{host}_kissproxy_{instance}`, plain TCP default port 1883 (no TLS/LWT in kissproxy).
Payload encoding honours a per-modem **base64** flag (default raw binary; base64 uses .NET
`InsertLineBreaks`). `main` and `web-interface` branches are byte-identical on this contract;
`web-interface` merely adds a non-essential `.../timing/ackmode` JSON topic.

**Key alignment:** PDN's `MonitorEvent.Raw` = `frame.ToBytes()` = the unframed AX.25 bytes — a
direct match for the `unframed` payload the collector reads.

## PDN design

- **`MqttFrameEmitter : BackgroundService`** subscribing `NodeTelemetry.Subscribe(out ChannelReader<MonitorEvent>)`
  — the same both-directions, port-stamped, RSSI/SNR-bearing stream the OARC reporter and traffic
  log already ride. Clone `TrafficLogService`. ~a few dozen lines + the publish mapping.
- **`MqttConfig`** (default **off**) next to `OarcConfig`/`TrafficConfig` (record + `NodeConfig`
  property + `MqttConfigValidator` composed in `NodeConfigValidator` + a self-gating hosted service
  in `Program.cs`). Fields: `Enabled`, `BrokerHost`, `BrokerPort` (1883/8883), `UseTls`,
  `Username`, `Password`, `TopicPrefix` (default `""`), `NodeName` (default = machine name),
  `Base64` (default false), `Qos` (default 2), `RfOnly`. **First integration with a broker
  credential** — password lives in gitignored `appsettings.Local.json`, validator checks
  host/creds coherence only when enabled.
- **Per-port instance label** — a per-port field feeds the `{instance}` topic segment (default =
  port id); set to the band for `gb7rdg-node` DB continuity. (See open decision below.)
- **Direction map** `in→fromModem`, `out→toModem` (mirrors the OARC reporter's `in→rcvd` map).
- **MQTTnet** already pinned (`4.3.7.1207`, `Directory.Packages.props`) — a consumer spike used it;
  the publisher is new. Consider a bump to MQTTnet 5 for a product dependency.

## Fidelity caveats (accepted limitations of the shipped v1)

These shipped as-is with #558 and are the accepted contract; robustness (as opposed to fidelity)
gaps in the emitter are tracked separately in
[#582](https://github.com/packet-net/packet.net/issues/582).

1. `MonitorEvent.Raw` is a **re-encode of the parsed frame**, and `FrameTraced` fires only for
   frames that parsed — so **parse-rejects are invisible** and malformed frames aren't byte-exact.
   *Accepted*: fine for the collector's parseable-traffic purpose; if byte-exact / unparseable
   capture is later wanted, add a thin `IAx25Transport` decorator tap (sees exact wire bytes) and
   hand it the same publisher. (Template: `InboundRadioTap`.)
2. **AckMode vs DataFrame**: kissproxy keys the `{Cmd}` segment on the KISS command; PDN's
   AX.25-level tap does not distinguish the G8BPQ ACKMODE wrapper. *Accepted*: v1 emits `DataFrame`
   for data traffic (the bulk of the 137 k); AckMode fidelity would need the KISS-level tap or the
   existing ACKMODE/`ack_timing` path.
3. PDN can attach **RSSI/SNR** per RX frame (richer than kissproxy) — but **not inside the
   kissproxy topics** (would break the collector's parser). *Not implemented*: if wanted, emit it
   on a separate additive topic / JSON envelope, leaving the kissproxy topics byte-identical.

## Decisions (Tom, 2026-07-05)

- **`{instance}` = band → per-port label.** A free-text per-port label feeds the `{instance}`
  segment, defaulting to the port id; the operator sets it to the band name when migrating a port
  off kissproxy (matching the existing `gb7rdg-node` DB). No first-class `band` schema concept added.
- **Sub-topics: `unframed` + `framed`.** Emit the `unframed` topic the collector ingests, plus
  `framed` (full raw KISS incl. FEND) for parity. **Skip `decoded`** (the collector doesn't ingest
  it; a PDN-native decode could be added later if wanted).
