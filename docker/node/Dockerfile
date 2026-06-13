# syntax=docker/dockerfile:1
# pdn node-host image (m0lte/pdn). Built from the SELF-CONTAINED app tree that
# scripts/build-deb.sh stages (binary + dlls + the built wwwroot) — the same content the
# .deb ships, so the image carries no separate .NET runtime. Build context is the repo ROOT:
#
#   scripts/build-deb.sh linux-x64 <ver>                  # stages artifacts/deb/linux-x64/...
#   docker build -f docker/node/Dockerfile --build-arg RID=linux-x64 -t m0lte/pdn:<ver> .
#
# (publish-docker.yml does this per-arch on a node-v* tag — reusing the cross-published
# trees build-deb.sh already produced for the .debs, so no double build.)
# debian-slim, not dotnet/runtime-deps: the app is self-contained (carries its own
# runtime), so we only need the few native libs .NET depends on — and there's no
# net10 runtime-deps tag anyway. InvariantGlobalization is baked in, so no libicu.
ARG BASE=debian:bookworm-slim
FROM ${BASE}
ARG RID=linux-x64

# .NET self-contained native deps + curl (HEALTHCHECK) + tini (clean PID 1). Then the
# unprivileged service user + the config/state dirs (writable by it).
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      libc6 libgcc-s1 libstdc++6 libssl3 zlib1g ca-certificates curl tini \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --no-create-home --home-dir /var/lib/packetnet --shell /usr/sbin/nologin packetnet \
 && mkdir -p /opt/packetnet/app /etc/packetnet /var/lib/packetnet \
 && chown -R packetnet:packetnet /etc/packetnet /var/lib/packetnet

# The staged self-contained app tree (binary + dlls + wwwroot) and the container config.
COPY artifacts/deb/${RID}/opt/packetnet/app/ /opt/packetnet/app/
COPY docker/node/packetnet.container.yaml /etc/packetnet/packetnet.yaml
RUN chown -R packetnet:packetnet /etc/packetnet

# The single-file binary extracts native libs at startup, and .NET puts HOME-based state
# (data-protection keys) under HOME — both must be writable (point at the state dir).
# InvariantGlobalization is baked in (no libicu); gcServer=0 keeps memory modest on Pi/VMs.
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/packetnet \
    HOME=/var/lib/packetnet \
    DOTNET_gcServer=0

USER packetnet
WORKDIR /var/lib/packetnet
EXPOSE 8080
# Config + state are volumes so they survive `docker rm` / image upgrades. A named volume
# inherits the baked default config on first run; bind-mount your own to override it.
VOLUME ["/etc/packetnet", "/var/lib/packetnet"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["/usr/bin/tini", "--", "/opt/packetnet/app/packetnet", "--config", "/etc/packetnet/packetnet.yaml"]
