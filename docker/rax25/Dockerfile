# rax25 (Thomas Habets's Rust AX.25 engine) container for Packet.NET
# interop tests. Runs the `async_server` example which accepts inbound
# L2 connects on a KISS-TCP endpoint. We point it at net-sim's KISS
# listener on port 8104 (node e); our Ax25Session attaches to net-sim
# on port 8100 (node a) and the afsk1200 sim bridges the two.
#
# Built from source — upstream publishes no pre-built image. We pin to
# a specific commit SHA so a CI run two months from now sees the same
# rax25 behaviour as today. To bump:
#
#   git -C <some clone of rax25> log -1 --format=%H main
#
# and replace RAX25_REV below, ideally in a small PR that calls out
# behavioural deltas since the previous pin.
#
# License: rax25's Cargo.toml declares `license = "MIT"`. We build
# from source at image-build time and don't push the resulting image
# to any registry — local docker / CI only.
#
# Caveats acknowledged: rax25's README flags REJ/SREJ as "untested /
# probably broken" and notes segmentation is not implemented. Our
# tests against rax25 are handshake-only (SABM/UA + DISC/UA), so they
# don't exercise those paths — but a future I-frame test would need
# care.

# ─── Stage 1: build ─────────────────────────────────────────────────
#
# rax25 uses Rust edition 2024 and declares rust-version = "1.91", so
# we need a recent Rust toolchain image. `rust:1-slim-bookworm` follows
# the latest stable release.

FROM rust:1-slim-bookworm AS build

# Build-time deps:
#   - git, ca-certificates: fetch source over HTTPS
#   - pkg-config: cargo build scripts probe system libs through this
#   - libssl-dev: `openssl-sys` (pulled transitively via tokio features)
#   - libudev-dev: `libudev-sys` (pulled transitively by `serialport`)
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        git \
        pkg-config \
        libssl-dev \
        libudev-dev \
        ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# Pinned to upstream HEAD on 2026-05-15 — refresh procedure in the
# header comment above.
ARG RAX25_REV=3cc22e59d8e4c1311a6543fe9e25874bfb857a21

WORKDIR /src
RUN git clone https://github.com/ThomasHabets/rax25 . \
 && git checkout "${RAX25_REV}"

# `cargo install --git --example` is not supported by cargo, so we
# build the example with `cargo build --release --example async_server`
# and copy the resulting binary out manually in the runtime stage.
RUN cargo build --release --example async_server

# ─── Stage 2: runtime ───────────────────────────────────────────────
#
# Slim runtime — just what's needed to exec the static-ish binary.
# `libssl3` covers the runtime side of openssl-sys; `ca-certificates`
# is harmless overhead and useful if the binary ever grows TLS use.

FROM debian:bookworm-slim AS runtime

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        libssl3 \
        ca-certificates \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /src/target/release/examples/async_server /usr/local/bin/async_server

# Default to verbose-debug logging so `docker logs pn-rax25` is useful
# for diagnosing interop hangs. Override the CMD in compose if needed.
ENTRYPOINT ["/usr/local/bin/async_server"]
CMD ["-v", "debug", "-p", "tcp://netsim:8104", "-s", "PN0RAX-1"]
