# 5. Radio metrics (Prometheus / Grafana)

**Goal:** graph and alert on your radios' health with standard tooling. If you run
Prometheus + Grafana, the node exposes everything from [chapter 2](02-see-your-link-quality.md)
as scrapeable metrics.

This page is a quick operator reference. The full observability write-up is
[`docs/observability.md`](../docs/observability.md).

## The endpoint

```
GET /metrics
```

- It's on the **same web port** as the control panel - no second port to bind or
  firewall.
- It is **always unauthenticated**, even when management auth is on (which it is by
  default). That's the Prometheus contract - a scraper holds a static config, not a
  login - and it means a stock node is scrapeable with no token wrangling. The flip
  side: on the default `0.0.0.0` bind, anyone who can reach the node's web port can
  read it, and it carries **heard callsigns**, per-peer SNR, port and radio health,
  and your running version. If that matters at your site, bind the panel to
  `127.0.0.1` and scrape locally, keep the node on a Tailscale tailnet, or put a
  reverse proxy in front.
- Numbers here and in the JSON API can never disagree - they're computed from one
  set of counters (the same `RadioHealth` projection `GET /api/v1/radios` serves).

Quick check:

```sh
curl -s http://127.0.0.1:8080/metrics | grep pdn_radio_
```

## The radio metrics

All use the `pdn_` namespace and are labelled `{port}`. They are emitted **only for
currently-attached radios** - the whole bucket is absent on a node with no radios.
Null readings are **omitted** (never a misleading `0`).

| Metric | Type | Meaning |
|---|---|---|
| `pdn_radio_connection_state` | gauge | Control-link health: `1` healthy, `0` faulted, `-1` unknown. Always present for an attached radio. |
| `pdn_radio_channel_busy` | gauge | Hardware carrier-sense (DCD): `1` = RF on channel, `0` = idle. Omitted until first reported. |
| `pdn_radio_rssi_dbm` | gauge | The radio's most-recent sliding-average RSSI, dBm (receive only). |
| `pdn_radio_rssi_averaged_dbm` | gauge | Median RSSI over the health monitor's rolling window, dBm. |
| `pdn_radio_pa_temperature_celsius` | gauge | Power-amplifier temperature, °C (absent on radios that report only an ADC value, e.g. some TM8200s). |
| `pdn_radio_forward_trend_millivolts` | gauge | Forward-power detector, mV. A **trend**, not a power reading. |
| `pdn_radio_reverse_trend_millivolts` | gauge | Reverse-power detector, mV. A **trend**. |
| `pdn_radio_reverse_forward_ratio` | gauge | Reverse/forward detector ratio. A **trend, never VSWR**. |

> [!CAUTION]
> Same caveat as the dashboard: the forward/reverse/ratio metrics are an
> **uncalibrated antenna-health trend, not VSWR or power.** Alert on *change over
> time* per station, never on the absolute value.

There is deliberately **no** `pdn_radio_snr_db` or `pdn_radio_noise_floor_dbm`. SNR
and noise floor are *per-frame* concepts; the radio's *health* projection doesn't
carry a port-level SNR, so rather than fabricate one, SNR is surfaced **per link
partner** instead, alongside that partner's TXDELAY as heard:

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `pdn_link_snr_db` | gauge | `port`, `peer` | SNR (dB) of the newest frame heard from a link partner, by port + remote callsign. |
| `pdn_link_predata_carrier_ms` | gauge | `port`, `peer` | Rolling median carrier-rise-to-data lead (ms) of frames heard from a link partner, its effective TXDELAY as heard, by port + remote callsign. Includes a small constant rig overhead, so trend it rather than reading it as the peer's configured TXDELAY. |

> [!NOTE]
> `pdn_link_snr_db` and `pdn_link_predata_carrier_ms` are the **two** series that
> carry a per-callsign (`peer`) label. Both are emitted for **every** station
> heard with a measurement, no cap and no bounding, on the reasoning that a node
> hears a naturally small, slowly-changing set of stations. (If your deployment
> ever disproves that, drop or relabel them Prometheus-side with
> `metric_relabel_configs`.)

## A couple of alerts worth having

- **Radio went faulted** - the control link died:
  `pdn_radio_connection_state == 0` for a few minutes.
- **Antenna trend shifted** - the reverse/forward ratio moved off its baseline for a
  station (compare `pdn_radio_reverse_forward_ratio` to its own recent average - a
  *change*, not a threshold).
- **A partner degraded** - `pdn_link_snr_db{peer="..."}` dropped below where it
  usually sits.

The metric set also includes node-wide, per-port and NET/ROM series beyond radios;
see [`docs/observability.md`](../docs/observability.md) for the full list.

## Next

No TNC, just two Tait radios? [6. TNC-less Tait-to-Tait links →](06-tnc-less-tait-links.md)
