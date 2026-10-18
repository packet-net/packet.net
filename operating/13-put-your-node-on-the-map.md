# 13. Put your node on the UK packet map

**Goal:** have your node show up on [nodes.ukpacketradio.network](https://nodes.ukpacketradio.network/), with its ports and neighbours, the way a BPQ node with `EnableM0LTEMap=1` does.

It is off until you turn it on.

## Turn it on

1. Make sure **Identity, Grid** holds your 6-character Maidenhead locator (for example `IO91lk`). Without one the node is not reported, and the log says so once.
2. In the control panel, open **Config, UK Packet Map** and switch on **Report to the UK packet map**.
3. For each port, open it on the **Ports** screen and fill in the **UK packet map** fields: link type, frequency in MHz, modulation, baud, bit rate, usage and a description. Leave any field blank to leave it off the map.

The first report goes about two minutes later, then one every 15 minutes. Nothing restarts and nothing goes out on the air.

Or in YAML, with GB7RDG's 2 m port as the example:

```yaml
m0lteMap:
  enabled: true
ports:
  - id: 2m
    # transport and the rest as before
    m0lteMap:
      linkType: RF          # RF, PrivateNet, Internet or Other; blank = Internet for AXUDP, else RF
      frequencyMhz: 144.9375
      modulation: QPSK
      baud: 1800
      bitrate: 3600
      usage: Mixed
      comment: 144.9375 FM QPSK3600 IL2Pc
```

Coming from BPQ, `M0LTEMapInfo=RF,144.9375,QPSK,1800,3600,Mixed` is those six fields in that order, and the port's `ID=` text is the `comment`. Every port that is on the air is reported. `MAPCOMMENT` has no equivalent: BPQ sends it to a different map. `DIAL+n` frequencies are not supported: type the frequency.

## What gets published

Your node's callsign, alias, locator and software version; each port that is on the air, numbered as `PORTS` numbers them, with the details you gave it; and the NET/ROM neighbours your node has a link up to, with their quality. If no port is up when a report is due, that report is skipped.

Never published: the callsigns of stations your node hears (BPQ sends its heard list; pdn does not), any address or password, anything about your users.

This is separate from the **OARC Network Map** tab, which reports to a different service (the one BPQ's `ENABLEOARCAPI` feeds). GB7RDG uses both.

## If it is not showing up

Look in the node's log for lines starting `UK packet map`. A problem reaching the map is one warning, then it retries on its own (after 1, 2, 4 and 8 minutes, then every 15). A report the map turns away is one warning with the reason it gave, even in the middle of an outage.

Design detail: [`docs/m0lte-map-reporting.md`](../docs/m0lte-map-reporting.md).
