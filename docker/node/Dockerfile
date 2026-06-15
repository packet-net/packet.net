# syntax=docker/dockerfile:1
# pdn node-host image (ghcr.io/packet-net/packet.net). Multi-arch (amd64 + arm64) via buildx.
# Built from the SELF-CONTAINED app tree scripts/build-deb.sh publishes (binary + dlls +
# the built wwwroot) — the same content the .deb ships, so no separate .NET runtime.
# scripts/docker-image.sh stages the per-arch trees under pdn-app/<arch>/ (amd64, arm64)
# and buildx selects the right one via the auto-provided TARGETARCH:
#
#   scripts/docker-image.sh <version> [push]      # local build, or CI push
#
# (publish-docker.yml runs that on a node-v* tag or workflow_dispatch.)
#
# debian-slim, not dotnet/runtime-deps: the app is self-contained (carries its own
# runtime), so we only need the few native libs .NET depends on — and there's no
# net10 runtime-deps tag anyway. InvariantGlobalization is baked in, so no libicu.
ARG BASE=debian:bookworm-slim
FROM ${BASE}
# Provided automatically by buildx per target platform: amd64 | arm64.
ARG TARGETARCH

# .NET self-contained native deps + curl (HEALTHCHECK) + tini (clean PID 1). Then the
# unprivileged service user + the config/state dirs (writable by it).
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      libc6 libgcc-s1 libstdc++6 libssl3 zlib1g ca-certificates curl tini \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --no-create-home --home-dir /var/lib/packetnet --shell /usr/sbin/nologin packetnet \
 && mkdir -p /opt/packetnet/app /etc/packetnet /var/lib/packetnet \
 && chown -R packetnet:packetnet /etc/packetnet /var/lib/packetnet

# The per-arch self-contained app tree (binary + dlls + wwwroot), staged by
# scripts/docker-image.sh as pdn-app/<arch>/, and the container config.
COPY pdn-app/${TARGETARCH}/ /opt/packetnet/app/
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
