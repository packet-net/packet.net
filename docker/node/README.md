# pdn node host — Docker image (`m0lte/pdn`)

The Packet.NET node host (pdn) as a container. Self-contained (no .NET runtime needed), Debian-slim base, runs as an unprivileged user.

```sh
docker run -d --name pdn \
  -p 8080:8080 \
  -v pdn-config:/etc/packetnet \
  -v pdn-state:/var/lib/packetnet \
  m0lte/pdn:latest
```

Then open `http://<host>:8080/`. The web panel binds `0.0.0.0` in the container so it's reachable via `-p`.

> ⚠️ **The panel is exposed** on whatever you publish the port to, and ships with **auth off**. Before putting it on anything but a trusted LAN, set `management.auth.enabled: true` (then bootstrap an admin via `/setup`) or front it with an authenticating reverse proxy.

## Configure

- **Config** lives at `/etc/packetnet/packetnet.yaml` (the `pdn-config` volume). A named volume inherits the baked default on first run; edit it there, or bind-mount your own. Set your **callsign** and add your **ports** (KISS-TCP / serial / AXUDP).
- **State** (`pdn.db`, TLS cert, per-app state) lives in `/var/lib/packetnet` (the `pdn-state` volume) — keep it to preserve users/keys across upgrades.
- **Health:** `GET /healthz` → `{"status":"ok"}` (used by the container `HEALTHCHECK`).

## Tags

`m0lte/pdn:<version>` (e.g. matching a `node-v*` release) and `m0lte/pdn:latest`. **amd64** today; arm64/armhf are a planned buildx matrix (Pi users can use the `.deb` from the GitHub release meanwhile).

Built from `docker/node/Dockerfile` (in `m0lte/packet.net`), published by `publish-node.yml` on a `node-v*` tag.
