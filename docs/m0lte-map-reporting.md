# UK packet map reporting (#453)

**Status:** built 2026-10-18 (node-host only, no `Packet.Ax25` change, no ax25-ts leg).
**Operator page:** [`operating/12-put-your-node-on-the-map.md`](../operating/12-put-your-node-on-the-map.md).

This is the pdn form of LinBPQ's `EnableM0LTEMap=1`: it puts a pdn node on the UK packet map at [nodes.ukpacketradio.network](https://nodes.ukpacketradio.network/) the same way a LinBPQ node gets there.

## What LinBPQ actually sends

Read from the LinBPQ source (`CommonCode.c` `SendLocation`, `SendDataToPktMapThread`, `SendWebRequest`, `BuildPortMH`; `config.c`; `LinBPQ.c`) and checked against GB7RDG's own last report, as the collector's public diagnostics page shows it (`GET https://packetnodes.spots.radio/api/nodegraph/diagnostics/GB7RDG`).

- **Where:** `POST http://packetnodes.spots.radio/api/NodeData/{NODECALL}`, plain HTTP on port 80, `Content-Type: application/json`, `User-Agent: linbpq/<version>`. No authentication of any kind. The same service answers HTTPS, and as `nodes.ukpacketradio.network`.
- **When:** from `SendLocation()`, which runs 2 minutes after start and then every 15 minutes (`REPORTINTERVAL`), and only when `LOCATOR` is set. Each send runs on its own thread.
- **Failure handling:** none. A non-200 is one debug line and the report is dropped; the next one comes 15 minutes later.
- **Body:** `nodeAlias`, `location` (`{"locator": ...}` for a 6-character locator, else `{"coords": {lat, lon}}`), `software` (`LINBPQ`/`BPQ32` and the version), `source: "ReportedByNode"`, then:
  - `ports[]`, one per active port not hidden and not `SendtoM0LTEMap=0`: `id` (the `PORTNUM`), `linkType`, `freq` (Hz), `mode` (`ax.25` for KISS, the driver name for ARDOP/VARA, empty for Telnet), `modulation`, `baud`, `bitrate`, `usage`, `comment` (the port's `ID=`). The per-port `M0LTEMapInfo=Type,Freq,Modulation,Baud,Bitrate,Usage` line overrides the derived values (freq in MHz, or `DIAL+n`/`DIAL-n` relative to the rig). With no line, LinBPQ sends placeholders: freq 0, modulation `FSK`, baud and bitrate 0, usage `Access`. The last `M0LTEMapInfo` line on a port wins.
  - `neighbours[]`: NET/ROM neighbours whose L2 link is up (`L2STATE >= 5`): `node`, `port`, `quality`. Left out when there are none.
  - `mheard[]`: every station heard on each reported port, with packet count and last-heard time.
  - All numbers go as quoted strings; the collector accepts them.
- **`MAPCOMMENT` is not part of this report.** It goes in the separate UDP location beacon to G8BPQ's own map (`update.g8bpq.net:81`), which is sent whenever `LOCATOR` is set, map switch or not. The collector's schema does have a `sysopComment` field, which LinBPQ never fills.
- **`ENABLEOARCAPI` / `EnableEvents` are a different service:** UDP datagrams to the OARC node API (`node-api.packet.oarc.uk:13579`). pdn already reports there through its `oarc:` block (#459, [`oarc-reporting-design.md`](oarc-reporting-design.md)). The two do not share a transport, so this is a second reporter, not an extension of the OARC one.
- The mail side (`SendBBSDataToPktMap`, `/api/bbsdata/{bbs}`) is the BBS's own report and belongs to the BBS (linmail-pdn inherits it from LinBPQ); the node does not send it.

## What pdn sends, and where it differs

`src/Packet.Node.Core/M0lteMap/`: `M0lteMapReportBuilder` (pure, golden-tested), `M0lteMapClient` (HTTP), `M0lteMapReporter` (schedule), `NodeM0lteMapStateSource` (the live node).

- **Same:** endpoint path, field names and meanings, `source`, the 2 minute then 15 minute cadence, port numbering (pdn's port number is its position in the config, the number `PORTS` shows), `mode: "ax.25"`, every port reported while it is on the air, neighbours only while their link is up (a NET/ROM routing-table neighbour with a live AX.25 session to it on that port, LinBPQ's `L2STATE >= 5`), neighbours left out when there are none.
- **Different, on purpose:**
  - HTTPS to `https://packetnodes.spots.radio/`, and `User-Agent: pdn/<version>`. Software name `pdn`, as for OARC.
  - Numbers are JSON numbers, as the collector's OpenAPI schema types them.
  - An unset detail is left out instead of LinBPQ's placeholders, which the map would otherwise show as fact (an AXIP port "on FSK at 0 Hz").
  - **No `mheard`.** It is other people's callsigns, and the issue rules them out. This is the one real loss: the map derives "heard" links from it.
  - No per-port opt-out (LinBPQ's `SendtoM0LTEMap`) and no node comment: neither was asked for, and LinBPQ never sends `MAPCOMMENT` here. pdn opens interlinks only when it has traffic, so a pdn node may show fewer neighbour links than a LinBPQ node, whose links stay up.
  - If no port is up at report time, the report is skipped (and tried again a minute later) rather than sent with no ports.
  - A failed delivery (no route, timeout, 429, 5xx) is retried after 1, 2, 4 and 8 minutes, then at the normal interval; a refusal (other 4xx) waits for the next report. Each kind of failure is one warning per outage (so a refusal after a spell of being unreachable still warns), later ones are debug, recovery is one information line.
  - Location is the locator only: a 6-character `identity.grid` is required, or nothing is sent (one warning). `DIAL+n` frequencies are not supported; a frequency is typed in MHz.
- **Not sent:** anything from a transport endpoint (hosts, ports, AXUDP peers), any secret (there is none), user callsigns.

## Config

Node level, `m0lteMap:` (default off): `enabled`. Per port, `ports[].m0lteMap:` (optional): `linkType` (`RF`, `PrivateNet`, `Internet`, `Other`; unset = Internet for AXUDP, else RF), `frequencyMhz`, `modulation`, `baud`, `bitrate`, `usage`, `comment` (unset = the port id). All read live: an edit changes the next report and nothing else. Panel: Config, UK Packet Map tab; each port's editor on the Ports screen.

GB7RDG's lines map like this:

```yaml
m0lteMap:                 # EnableM0LTEMap=1
  enabled: true
ports:
  - id: 2m                # PORTNUM=1, ID=144.9375 FM QPSK3600 IL2Pc
    # ...
    m0lteMap:             # M0LTEMapInfo=RF,144.9375,QPSK,1800,3600,Mixed
      linkType: RF
      frequencyMhz: 144.9375
      modulation: QPSK
      baud: 1800
      bitrate: 3600
      usage: Mixed
      comment: 144.9375 FM QPSK3600 IL2Pc
```

## Not verified

No test traffic was sent to the live collector. The schema, the stored GB7RDG record and GB7RDG's raw LinBPQ body were read with GETs of public pages only. Unverified until a real pdn node reports: that the collector accepts a POST over HTTPS with JSON numbers where LinBPQ quotes them (the schema says it should, and XRouter's accepted reports use bare numbers), and how the map shows a port with no frequency.
