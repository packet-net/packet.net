# 0. Install pdn

**Goal:** get the node host (pdn) installed on a box, open its control panel, and have a node on the air with your callsign on it.

This is the page to start on if you have never run pdn before. It assumes nothing except a Linux machine and a terminal. Everything after it in this guide - attaching a radio, reading link quality, tuning - assumes you have finished this page.

## What you need

- **A Linux box** that stays on: a Raspberry Pi (3 or newer), a small x86 machine, or a VM. Debian, Raspberry Pi OS, and Ubuntu are the tested targets; anything with systemd works.
- **An architecture pdn ships for**: `amd64`, `arm64`, or `armhf`. (`dpkg --print-architecture` tells you which you are on.)
- **About 120 MB of disk** for the install and **~100 MB of RAM** at idle. pdn bundles its own .NET runtime - you do **not** need to install .NET, mono, or anything else.
- **A callsign.** N0CALL works for a bench test, but nothing should transmit on it.
- **A modem, eventually** - a NinoTNC, a KISS TNC, a sound card, or another node over the network. You can install and explore pdn with no radio attached at all; it just has nothing to talk to.

> [!NOTE]
> pdn is the **node host**. If what you want is to *write software* against the AX.25 engine rather than run a node, you want the [developer guide](../guide/index.md) and `dotnet add package Packet.Ax25` instead - no install needed.

## Pick an install route

pdn is distributed as exactly two things, both attached to every [release](https://github.com/packet-net/packet.net/releases): a `.deb` and a `.tar.gz`. There is no installer script and no package repository to add.

| Route | Use it when | Updates |
|---|---|---|
| **A. Debian package** | You are on Debian / Ubuntu / Raspberry Pi OS. The common case, and the supported one. | Install a newer `.deb`, or the panel's **Apply** button |
| **B. Archive** | You are on a Linux box that is not Debian-ish, or you want the node somewhere of your own choosing. | Unpack the next release over it, by hand |

Both carry the same node. Pick one.

## Route A - the Debian package

Grab the `.deb` for your architecture from [Releases](https://github.com/packet-net/packet.net/releases) and install it:

```sh
arch=$(dpkg --print-architecture)
ver=$(curl -fsSL https://api.github.com/repos/packet-net/packet.net/releases/latest \
        | sed -n 's/.*"tag_name": *"node-v\([^"]*\)".*/\1/p')
curl -fsSLO "https://github.com/packet-net/packet.net/releases/download/node-v$ver/packetnet_${ver}_${arch}.deb"
sudo apt install "./packetnet_${ver}_${arch}.deb"
```

> [!IMPORTANT]
> Use `apt install ./file.deb`, not `dpkg -i`. The package declares real dependencies (`adduser`, `polkitd`, `libhamlib-utils`) and apt resolves them; `dpkg -i` will stop on the first missing one.

That is the whole install. The package:

- creates an unprivileged `packetnet` system user,
- installs the node at `/opt/packetnet/app/packetnet` and a hardened systemd unit,
- enables and starts `packetnet.service` immediately,
- and finishes by printing the control panel's address - the next step happens there, in a browser on another machine.

Check it came up:

```sh
systemctl status packetnet
curl -fsS http://127.0.0.1:8080/healthz     # -> {"status":"ok"}
```

If the node is not running, `journalctl -u packetnet -n 50` will say why.

## Route B - the archive

The `.tar.gz` for your architecture is the same node as the `.deb`, minus the packaging: the binary, its bundled runtime, the web panel, an example systemd unit, and the annotated config template. Nothing in it is managed by anything, so everything the package does for you is yours to do.

```sh
sudo install -d /opt/packetnet/app
sudo tar -C /opt/packetnet/app -xzf packetnet_<version>_<arch>.tar.gz
sudo adduser --system --group --no-create-home --home /var/lib/packetnet \
     --shell /usr/sbin/nologin packetnet
sudo install -d -o packetnet -g packetnet -m 0750 /var/lib/packetnet
sudo install -m 0644 /opt/packetnet/app/packetnet.service.example \
     /etc/systemd/system/packetnet.service
sudo systemctl daemon-reload && sudo systemctl enable --now packetnet
```

Check the checksum against the release's `SHA256SUMS` before you unpack it (`sha256sum -c --ignore-missing SHA256SUMS`).

The example unit assumes `/opt/packetnet/app`; edit `ExecStart` if you put the node elsewhere. `packetnet.yaml.example` in the archive is the same annotated template the package installs to `/usr/share/packetnet` - the node seeds its database from its built-in copy on first boot either way, so the file is documentation rather than a required input.

Because nothing owns an archive install, the control panel reports its install channel as **unmanaged** and offers no **Apply** button. To upgrade, stop the node, unpack the newer archive over `/opt/packetnet/app`, and start it again; `/var/lib/packetnet` carries your config and users across untouched.

There is no package here to print the panel's address, but the node names it in its journal at every start: `journalctl -u packetnet | grep "Control panel"`.

## First contact: open the control panel

Almost everything you do with pdn is in its web control panel. Browse to it from any machine on your network:

**<http://your-node-address:8080>**

The install prints the exact addresses (and the node repeats them in its journal at every start - `journalctl -u packetnet | grep "Control panel"`). The panel binds every interface (`0.0.0.0:8080`) so a headless box is usable without a tunnel, and it requires a login - those two defaults go together, and the first-run wizard below is what creates that login.

> [!TIP]
> Passkeys need a *secure origin*: HTTPS with a trusted certificate, or `http://localhost`. On a plain-HTTP LAN address the panel offers a password login and simply doesn't show the passkey buttons - nothing is broken, and you can add a passkey later from **Users**. If you want one on day one, turn on [Tailscale](#what-is-exposed-and-how-to-change-it), which gets you a real certificate, or reach the panel through an SSH tunnel - `ssh -L 8080:127.0.0.1:8080 you@your-node`, then <http://localhost:8080>.

## The first-run wizard

A node with no users sends you straight to a three-step wizard:

1. **Station identity** - your callsign (e.g. `M0LTE-1`), an optional node alias (≤6 characters, what neighbours see on the network map and over NET/ROM), and an optional Maidenhead locator.
2. **Create admin** - the first administrator account, and a passkey if you are on a secure origin. Passwords are at least 8 characters.
3. **First port** - optional, and covered below. Skip it if your modem is not wired up yet.

Finish, and you are in the panel with a node running under your own callsign, and a login guarding it.

> [!IMPORTANT]
> Between installing and finishing the wizard, whoever reaches the node first can claim it - the setup endpoint is deliberately open while zero users exist, so that a headless box can be set up at all. Do the wizard now rather than later, and do it on a network you trust.

## What is exposed, and how to change it

A stock node listens on two ports, and they are not equally open:

| Port | What | Who can reach it |
|---|---|---|
| 8080 | Control panel + REST API | Any host on your network - but everything except the setup wizard, `/healthz`, and `/metrics` needs your login |
| 8011 | Telnet node console | `127.0.0.1` only - a shell on the node itself |

`/metrics` is unauthenticated on purpose (that's how Prometheus scrapes), and it carries heard callsigns, per-peer SNR, port and radio health, and your version. Nothing there lets anyone *change* anything, but it is readable - [chapter 5](05-radio-metrics.md) covers the trade and how to close it.

To narrow the panel to loopback (and go back to reaching it over an SSH tunnel), set it in **Config**:

```yaml
management:
  http:
    bind: 127.0.0.1
```

The web listener is bound when the process starts, so a bind change needs `sudo systemctl restart packetnet` - unlike most config, saving is not enough. Auth changes *do* take effect immediately.

Two options worth knowing about:

- **Reaching it from anywhere, safely** - turn on the built-in [Tailscale](https://tailscale.com) sidecar (`tailscale:` in the config). The node joins your tailnet, gets a real browser-trusted certificate for `<name>.ts.net`, and passkeys work remotely with no port forwarding, no public DNS, and no certificate management. The blessed remote path: [`docs/network-access.md`](../docs/network-access.md).
- **TLS on the LAN** - `management.https` serves the panel over TLS, self-signed by default (browsers warn until you trust it) or from your own `.pfx`. Worth it if passwords crossing your LAN in clear bothers you.

There is no built-in ACME/Let's Encrypt for a public hostname; a public, VPN-free certificate is your own reverse proxy's job.

## Add a port

A port is one modem. Add them under **Ports** in the panel (or in the wizard's step 3). The common kinds:

| `kind:` | What it is |
|---|---|
| `nino-tnc` | A NinoTNC over USB serial - full control (mode, RSSI, ACKMODE) |
| `serial-kiss` | Any generic KISS TNC on a serial port |
| `kiss-tcp` | A KISS-over-TCP endpoint - Dire Wolf, QtSoundModem, a simulator |
| `soundmodem` | pdn's own in-process soundcard modem - a sound card and a radio, no TNC at all ([`docs/soundmodem.md`](../docs/soundmodem.md)) |
| `axudp` / `axudp-multipoint` | AX.25 over UDP to another node - no RF, good for linking and for testing |

Two more exist for particular setups: `tait-transparent` (a Tait radio as the modem, no TNC - [chapter 6](06-tnc-less-tait-links.md)) and `nino-tnc-tcp` (a NinoTNC on a [split-station head-end](08-split-station-head-end.md)).

Ports reconcile live: adding, editing, or removing one brings that port up or down without restarting the node.

### If your modem is on a serial port

The node runs as the unprivileged `packetnet` user, which is **not** in `dialout` after install - so a USB TNC will not open until you say it may:

```sh
sudo usermod -aG dialout packetnet
sudo systemctl restart packetnet
```

If it still cannot open the device, drop in a unit override (`sudo systemctl edit packetnet`) with `SupplementaryGroups=dialout` and `DeviceAllow=/dev/ttyACM0` - the reasoning is in the comments at the foot of `/lib/systemd/system/packetnet.service`. Ports over TCP, UDP, or a sound card need none of this.

Bind serial devices by a stable path (`/dev/serial/by-id/...`) rather than `/dev/ttyUSB0`, which moves around between reboots - [chapter 1](01-attach-a-radio.md#why-bind-by-serial-not-device-path) explains why in more detail.

## Prove it works

Without any radio, from a shell on the node:

```sh
curl -fsS http://127.0.0.1:8080/healthz            # the node is up
curl -fsS http://127.0.0.1:8080/metrics | head     # Prometheus surface
telnet 127.0.0.1 8011                              # the node's own console
```

The telnet console answers with your banner and prompt - that is the same command surface a station reaching you over RF gets:

```
Welcome to M0LTE-1 (M0LTE-1)  [Packet.NET 0.36.2]
M0LTE-1>
```

With a port up, the panel's **Monitor** shows every frame heard on the channel, and **Routes** fills in with NET/ROM neighbours as they announce themselves. If frames are arriving, you are on the air.

## Where things live

| Path | What |
|---|---|
| `/opt/packetnet/app/packetnet` | The node binary (self-contained; `--config`, `mcp`, and `config` subcommands) |
| `/var/lib/packetnet/pdn.db` | **Config, users, routing, heard list** - the thing to back up |
| `/var/lib/packetnet/traffic.db` | Traffic/telemetry history |
| `/usr/share/packetnet/packetnet.yaml.example` | The annotated config template, seeded into the DB on first boot. Read it - every option is documented there |
| `/lib/systemd/system/packetnet.service` | The unit (`systemctl status\|restart packetnet`) |

Note that pdn has **no `/etc/packetnet/packetnet.yaml` to hand-edit**: live config lives in `pdn.db` and is edited through the panel or the API, which is what removed the old dpkg conffile prompts on every upgrade ([`docs/config-in-db.md`](../docs/config-in-db.md)).

If you would rather edit config as text, round-trip it:

```sh
sudo -u packetnet /opt/packetnet/app/packetnet config export --db /var/lib/packetnet/pdn.db --out /tmp/pdn.yaml
sudoedit /tmp/pdn.yaml
sudo -u packetnet /opt/packetnet/app/packetnet config import /tmp/pdn.yaml --db /var/lib/packetnet/pdn.db
sudo systemctl restart packetnet
```

An import validates before it applies, and rejects the file whole if anything in it is wrong. The restart is what makes the running node pick it up.

## Updating

The panel's **Config** screen shows the running version and how this node updates - and, on a package install, offers **Apply** when a newer release exists. The node downloads the next release `.deb`, verifies its checksum, installs it through dpkg, and rolls back if the new version does not come up healthy; it restarts itself and the panel reconnects. dpkg stays the owner of every file it installed - the node never overwrites them itself.

An archive install has no owner to defer to, so the panel reports it as **unmanaged** and shows no **Apply**.

By hand instead:

- **Package install** - install the newer `.deb` exactly as you installed the first one. Config and users live in `/var/lib/packetnet` and are untouched.
- **Archive install** - `sudo systemctl stop packetnet`, unpack the newer `.tar.gz` over `/opt/packetnet/app`, `sudo systemctl start packetnet`. Same state directory, same config.

## Uninstalling

From a package install:

```sh
sudo apt remove packetnet     # removes the software, KEEPS /var/lib/packetnet
sudo apt purge  packetnet     # also deletes /var/lib/packetnet and the packetnet user
```

Purge takes your config, users, routing table, and history with it - there is nothing left to reinstall onto. Copy `/var/lib/packetnet/pdn.db` somewhere first if you might want it back.

From an archive install there is no package to remove; stop it and delete what you laid down:

```sh
sudo systemctl disable --now packetnet.service
sudo rm -f /etc/systemd/system/packetnet.service
sudo rm -rf /opt/packetnet
sudo rm -rf /var/lib/packetnet    # the state - only if you want it gone
sudo systemctl daemon-reload
sudo deluser --system packetnet
```

## When it does not work

| Symptom | Look at |
|---|---|
| Nothing on port 8080 | `systemctl status packetnet`, then `journalctl -u packetnet -n 100`. A config the node refuses to boot on is logged loudly. |
| Panel reachable on the node, "connection refused" from another machine | Something narrowed the bind to `127.0.0.1` - check **Config → Management → HTTP**, and remember a bind change needs a service restart. Or a firewall on the box is dropping 8080. |
| Wizard never appears / it goes straight to a login | Setup is one-shot - a user already exists. Log in, or reset by stopping the node and removing `pdn.db` (this drops **all** config). |
| Passkey buttons are missing | Not a secure origin - expected on a plain-HTTP LAN address. Password login works; for a passkey use the SSH tunnel to `http://localhost:8080`, HTTPS, or Tailscale. |
| A serial port never opens | The `packetnet` user needs access to the device: `sudo usermod -aG dialout packetnet`, then restart. `journalctl` names the device it tried. |
| `apt install` fails on dependencies | You used `dpkg -i`, or the box has no network to fetch the dependencies from its own distro mirrors. Use `apt install ./file.deb`. |

## Where next

- **[1. Attach a radio](01-attach-a-radio.md)** - let the node read the radio behind the TNC: RSSI, SNR, hardware carrier-sense, CAT control.
- **[2. See your link quality](02-see-your-link-quality.md)** and **[3. Check your setup](03-check-your-setup-doctor.md)** - is this link any good, and if not, why.
- **[9. Running existing software](09-running-existing-software.md)** - point native AX.25 applications or IP tooling at your node.
- **[The operator guide index](index.md)** - the full chapter list.
