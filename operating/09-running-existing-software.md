# 9. Running existing software over a node

**Goal:** point *unmodified* software (native AX.25 apps, or ordinary IP tools
like `ssh` and `ping`) at your node, so it works over packet radio without being
rewritten.

> [!NOTE]
> **This chapter is advanced and opt-in. Most operators never need it.** The web
> control panel and everything in chapters 1 to 8 stand on their own. Reach for
> this only when you specifically want to drive the node from *other* software:
> an existing AX.25 program, or an IP-only tool that has no idea what a callsign
> is. Two small companion packages bridge those two worlds, and you install
> whichever one matches the software you are trying to run.

## What you need first: the node's RHPv2 server

Both companion packages are **clients of your node over RHPv2** (Radio Host
Protocol v2), a small JSON-over-TCP host API. They do not touch your radios or
modems directly: they open and accept packet connections *through* the node's
AX.25 engine. So before either one can do anything, your node needs:

1. **An `ax25` port up** (a TNC, a soundmodem, a Tait, whatever you set up in
   [chapter 1](01-attach-a-radio.md)). This is the actual bearer.
2. **The RHPv2 server switched on.** It is **off by default**. Add an `rhp:`
   block to your node config:

   ```yaml
   rhp:
     enabled: true        # default false: a node that doesn't opt in serves no RHP
     bind: 127.0.0.1      # loopback is the trust boundary; RHP has no TLS, never expose it publicly
     port: 9000           # the RHPv2 convention
     requireAuth: false   # when true, `auth` is validated against the node's user store first
   ```

   The default bind is loopback `127.0.0.1:9000`, which is exactly what you want:
   the companion packages run **on the same machine as the node** and talk to it
   over loopback. Leave it on loopback unless you have a specific reason not to
   (and if you do, put it behind a trusted network, because RHP has no transport
   security of its own).

Both packages install from a **GitHub Release** (there is **no apt repo**): download
the `.deb` for your architecture and install it with `apt` (or `dpkg` offline).

## Native AX.25 apps: `pdn-libax25`

[`pdn-libax25`](https://github.com/packet-net/pdn-libax25) (LGPL-3.0) lets
**unmodified native AX.25 applications** (`axcall`, `ax25d`, FBB/BBS clients, and
the rest of `ax25-apps` / `ax25-tools`) connect through your node, now that the
Linux kernel `AF_AX25` stack is gone. It is an `LD_PRELOAD` shim: it intercepts
the socket calls an AX.25 app makes and routes them to the node over RHPv2. You
**address a callsign**, exactly as you always did.

Install the `.deb`:

```sh
sudo apt install ./pdn-libax25_<ver>_<arch>.deb
#   ...or, offline:  sudo dpkg -i ./pdn-libax25_<ver>_<arch>.deb
```

Then run any AX.25 app *through the shim* with the **`pdn-ax25`** wrapper. The
wrapper sets up the interposer for that one command and execs your program:

```sh
# axcall (from ax25-apps), unmodified, calling GB7RDG on the axports port "radio":
PDN_RHP_ADDR=127.0.0.1:9000 pdn-ax25 axcall radio GB7RDG
```

`PDN_RHP_ADDR` is optional: it defaults to loopback `127.0.0.1:9000`, so you only
need it when your node's RHP server is on a non-default address. Your
`/etc/ax25/axports` is read as usual for the port name and callsign.

> [!IMPORTANT]
> **It is opt-in, per command.** Installing `pdn-libax25` does **not** replace
> your system `libax25` or change how anything else on the box behaves. Only the
> commands you actually prefix with `pdn-ax25` go through the node. Everything
> else is untouched.

## IP over radio: `pdn-net`

[`pdn-net`](https://github.com/packet-net/pdn-net) (AGPL-3.0) is the other half:
it lets **ordinary IP software** (`ssh`, `mosh`, `mqtt`, `ping`, your own UDP
app) run over packet radio. It brings up a TUN interface (`pdn0`), and carries
each IP packet to the callsign you have mapped to its destination address,
tunnelled through the node over RHPv2. Here you **address an IP**, and the
program never knows it is talking over radio.

Install the `.deb`:

```sh
sudo apt install ./pdn-net_<ver>_<arch>.deb
#   ...or, offline:  sudo dpkg -i ./pdn-net_<ver>_<arch>.deb
```

The service is **disabled by default**. Configure it first, then enable it. Edit
`/etc/pdn-net/pdn-net.json`:

```json
{
  "myCallsign": "N0CALL-10",
  "rhpAddress": "127.0.0.1:9000",
  "tunName": "pdn0",
  "mtu": 256,
  "routes": [
    { "ip": "44.0.0.2",    "callsign": "M0LTE-10" },
    { "ip": "44.0.0.0/24", "callsign": "GB7RDG-10" }
  ]
}
```

- `myCallsign`: your station callsign (with SSID) for this IP link.
- `rhpAddress`: your node's RHPv2 address, matching the `rhp:` block above
  (default `127.0.0.1:9000`).
- `tunName` / `mtu`: the TUN interface name and its MTU (a small MTU is expected,
  see the limits below).
- `routes`: the IP-to-callsign map. Each entry sends traffic for an `ip` (a host
  address or a CIDR) to a `callsign`. Longest-prefix wins, so a specific host
  route overrides a broader subnet route.

Then bring it up:

```sh
sudo systemctl enable --now pdn-net
```

That starts the `pdn0` interface and routes matching IP traffic over the node.
From then on, plain IP just works to any mapped address:

```sh
ping 44.0.0.2          # or: ssh / mosh / your mqtt client, to a mapped address
```

> [!NOTE]
> **The honest limits.** This is IP over a narrow radio channel, and it behaves
> like it. The MTU is small (~256 bytes) and traffic rides **best-effort UI
> datagrams** (standard IP-over-AX.25, PID `0xCC`), not a reliable connected
> session, so end-to-end reliability is the application's own (TCP handles this
> fine). It is genuinely good for `ssh`, `mosh`, `mqtt`, and small
> request/response tools. It is **not** for web browsing or anything bulky. Both
> ends also need matching static routes today (there is no dynamic IP-to-callsign
> discovery yet).

## Which one?

If your program speaks callsigns, use **`pdn-libax25`**. If it only speaks IP,
use **`pdn-net`**.

For the full rationale (why there are two seams, and where IP is and is not
actually required), see packet.net's
[`docs/network-integration-adr.md`](../docs/network-integration-adr.md).

## Talking to a BPQ or LinBPQ node on air

The other "existing software" you have to live with is what is already on the
air. Here is a symptom worth naming: your node connects to some stations
perfectly well, but every connect to the **BPQ / LinBPQ node down the road** sits
there and times out. There is nothing useful in the log, no rejection and no
"busy" from the far end, just the timeout. It works the other way round, though:
that node can still call yours.

**Why.** Your node opens outbound connections with the newer AX.25 **v2.2**
first. A station that only speaks the older **v2.0** is meant to refuse the
offer, which your node sees at once and retries the old way. Plenty of BPQ builds
and older TNCs do neither: they ignore the v2.2 opener completely, so your node
just re-sends it until it gives up. The tell, if you can see the other operator's
`bpq32.cfg`, is `MAXFRAME 7` or lower on that port.

**The fix.** Tell that port to dial the old way:

```yaml
ports:
  - id: vhf
    link:
      dial: v20        # always open connects as plain AX.25 v2.0
```

The control panel has the same setting: **Ports → edit the port → Advanced
parameters**, then the dial-preference control. Either way it applies **live**,
to new outbound connects only; nothing restarts and no session drops.

> [!NOTE]
> You do not have to set this. The default, `dial: auto`, learns for itself: the
> first call to a silent station stalls once, then your node remembers it and
> dials v2.0 from then on (re-checking about a month later). Setting `v20` just
> skips that one-off stall. Its companion setting `preConnectXid` decides whether
> a v2.0 call leads with a short capability probe asking for selective
> retransmission (cheaper recovery when a frame is lost); leave it on `auto`.

---

Return to the [operating guide index](index.md).
