# 12. Put your node on the UK packet map

**Goal:** have your node show up on [nodes.ukpacketradio.network](https://nodes.ukpacketradio.network/), with its ports and neighbours, the way a BPQ node with `EnableM0LTEMap=1` does.

It is off until you turn it on.

## Turn it on

1. Make sure **Identity, Grid** holds your 6-character Maidenhead locator (for example `IO91lk`). Without one the node is not reported, and the log says so once.
2. In the control panel, open **Config, UK Packet Map**, switch on **Report to the UK packet map**, and if you like add a short **Map comment** such as a link to your node's page.
3. For each port, open it on the **Ports** screen and fill in the **UK packet map** fields: link type, frequency in MHz, modulation, baud, bit rate, usage and a description. Leave any field blank to leave it off the map. Switch off **Show this port on the map** for a port you would rather keep private.

The first report goes about two minutes later, then one every 15 minutes. Nothing restarts and nothing goes out on the air.

Or in YAML, with GB7RDG's 2 m port as the example:

```yaml
m0lteMap:
  enabled: true
  comment: https://ukpacketradio.network/nodes:gb7rdg
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

Coming from BPQ, `M0LTEMapInfo=RF,144.9375,QPSK,1800,3600,Mixed` is those six fields in that order, the port's `ID=` text is the `comment`, and `SendtoM0LTEMap=0` is `enabled: false`. `DIAL+n` frequencies are not supported: type the frequency.

## What gets published

Your node's callsign, alias, locator, software version and comment; each port that is on the air, numbered as `PORTS` numbers them, with the details you gave it; and your NET/ROM neighbours on those ports, with their quality.

Never published: the callsigns of stations your node hears (BPQ sends its heard list; pdn does not), any address or password, anything about your users.

This is separate from the **OARC Network Map** tab, which reports to a different service (the one BPQ's `ENABLEOARCAPI` feeds). GB7RDG uses both.

## If it is not showing up

Look in the node's log for lines starting `UK packet map`. A problem reaching the map is one warning, then it retries on its own (after 1, 2, 4 and 8 minutes, then every 15). A report the map turns away is one warning with the reason it gave.

Design detail: [`docs/m0lte-map-reporting.md`](../docs/m0lte-map-reporting.md).
