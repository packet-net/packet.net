# pdn node host - Docker image (`ghcr.io/packet-net/packet.net`)

The Packet.NET node host (pdn) as a container. Self-contained (no .NET runtime needed), Debian-slim base, runs as an unprivileged user.

> **This image is a development tool.** It exists for testing against a disposable node and for the interop stack. It is **not** a distributed install route: pdn ships a `.deb` and a `.tar.gz`, and [Getting started](../../operating/00-install.md) covers both. Run a real station from the `.deb`. Nothing manages a container install, so the panel reports its channel as *unmanaged* and offers no in-app update - you pull a new image and recreate.

```sh
docker run -d --name pdn \
  -p 8080:8080 \
  -v pdn-config:/etc/packetnet \
  -v pdn-state:/var/lib/packetnet \
  ghcr.io/packet-net/packet.net:latest
```

Then open `http://<host>:8080/`. The web panel binds `0.0.0.0` in the container so it's reachable via `-p`.

The first visit lands on the setup wizard - callsign, admin account, first port - and every request after that needs a login: the image ships with `management.auth.enabled: true`, because the panel binds `0.0.0.0` and is published wherever `-p` puts it.

> ⚠️ `/metrics` is deliberately **not** authenticated (the Prometheus contract), and it carries heard callsigns, per-peer SNR, port/radio health, and the running version. If the published port is reachable from somewhere you would not want that, front it with a reverse proxy or keep it on a trusted network.

## Configure

- **Config** lives at `/etc/packetnet/packetnet.yaml` (the `pdn-config` volume). A named volume inherits the baked default on first run; edit it there, or bind-mount your own. Set your **callsign** and add your **ports** (KISS-TCP / serial / AXUDP).
- **State** (`pdn.db`, TLS cert, per-app state) lives in `/var/lib/packetnet` (the `pdn-state` volume) - keep it to preserve users/keys across upgrades.
- **Health:** `GET /healthz` → `{"status":"ok"}` (used by the container `HEALTHCHECK`).

## Tags

`ghcr.io/packet-net/packet.net:<version>` (e.g. matching a `node-v*` release) and `ghcr.io/packet-net/packet.net:latest`. **Multi-arch: amd64 + arm64** (armhf via the `.deb` for now).

Built from `docker/node/Dockerfile` via `scripts/docker-image.sh`, published by `publish-docker.yml` on a `node-v*` tag (or `workflow_dispatch`).
