# Dire Wolf (WB2OSZ) connected-mode mod-128 interop peer for Packet.NET.
#
# WHY THIS LOOKS DIFFERENT FROM THE OTHER PEERS
# ─────────────────────────────────────────────
# LinBPQ / XRouter / rax25 all attach to net-sim as KISS-TCP *clients* and
# their connected-mode engines run off raw KISS frames. Dire Wolf cannot be
# wired that way, for two reasons established from its 1.8.1 source:
#
#   1. Dire Wolf's connected-mode AX.25 engine (`ax25_link.c`, the figc4.x
#      reference) is reachable ONLY via the AGW protocol + a registered
#      client app (`dl_register_callsign`). Raw KISS bypasses `ax25_link.c`
#      entirely (`kiss.c`/`kissnet.c` never touch it) — a SABME arriving on
#      Dire Wolf's KISS port is just modulated as a frame; nothing answers.
#
#   2. The only KISS-TCP-*client* mode Dire Wolf has is `NCHANNEL` (the v1.7+
#      "network TNC" feature), but NCHANNEL channels are `MEDIUM_NETTNC` and
#      live at channel numbers >= MAX_RADIO_CHANS (6). Every AGW connected-
#      mode request is hard-`assert`ed to `chan < MAX_RADIO_CHANS` AND the
#      `'X'` register handler additionally requires `chan_medium == MEDIUM_RADIO`
#      (server.c:1879, dlq.c:501). So you cannot drive connected mode over an
#      NCHANNEL at all — neither register (as responder) nor connect (as
#      initiator). The connected-mode engine only works on a real radio
#      (audio) channel.
#
# net-sim, in turn, exposes no external audio ingress in the pinned image
# (`external` is not a YAML field on `config.Port`), so an external Dire Wolf's
# modem cannot be attached to net-sim's RF mixer. The faithful realisation is
# therefore the issue's "two-direwolf audio loopback" path: TWO real Dire Wolf
# radio channels sharing one PulseAudio null sink as the simulated AFSK1200 RF
# channel —
#
#     our Ax25Session ──KISS-TCP──▶ direwolf-gw (radio ch0, pure KISS modem)
#                                        │  AFSK1200 over the PulseAudio
#                                        │  null sink (the "RF" channel)
#                                        ▼
#                                   direwolf-resp (radio ch0) ──AGW──▶ echo app
#                                   (ax25_link.c connected-mode engine;
#                                    answers SABME with UA at mod-128)
#
# direwolf-gw is a transparent KISS passthrough (KISS does not touch its
# `ax25_link.c`), so the AX.25 exchange under test is genuinely between our
# Ax25Session and direwolf-resp's `ax25_link.c` over a real 1200-baud AFSK
# modem link. Verified on the wire: SABME → UA, then a mod-128 (2-octet
# control, 7-bit N(S)/N(R)) I-frame round-trip.
#
# Built from a pinned source rev (mirroring docker/rax25/Dockerfile) — no
# usable upstream connected-mode image is published. The image is local-only
# and never pushed. Dire Wolf is GPL-2.0-or-later.
#
# To bump: change DIREWOLF_REV to a newer tag/commit of github.com/wb2osz/direwolf
# and call out behavioural deltas in the PR.

# ─── Stage 1: build Dire Wolf ───────────────────────────────────────
FROM debian:bookworm-slim AS build

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        git \
        cmake \
        gcc \
        g++ \
        make \
        ca-certificates \
        libasound2-dev \
        libudev-dev \
 && rm -rf /var/lib/apt/lists/*

# Pinned to the 1.8.1 release tag (commit a231971), 2025-11-11.
ARG DIREWOLF_REV=a231971a652bfb574a4bae9a5d875fbce53d2267

WORKDIR /src
RUN git clone https://github.com/wb2osz/direwolf . \
 && git checkout "${DIREWOLF_REV}"

# Only the `direwolf` target is needed (not the decode/encode utilities).
RUN mkdir build \
 && cd build \
 && cmake -DCMAKE_BUILD_TYPE=Release .. \
 && make -j"$(nproc)" direwolf

# ─── Stage 2: runtime ───────────────────────────────────────────────
#
# PulseAudio provides the in-container virtual sound card (a null sink whose
# monitor source is the shared "RF" channel both Dire Wolf instances use).
# Python 3 runs the tiny AGW accept/echo helper. socat / procps aid debugging.

FROM debian:bookworm-slim AS runtime

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        libasound2 \
        libasound2-plugins \
        pulseaudio \
        pulseaudio-utils \
        python3 \
        ca-certificates \
        procps \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /src/build/src/direwolf /usr/local/bin/direwolf
COPY --from=build /src/data/tocalls.yaml /usr/local/share/direwolf/tocalls.yaml

# The ALSA → PulseAudio bridge: makes ALSA's "default" PCM route to the
# running PulseAudio server, so Dire Wolf (an ALSA app) plays/records on
# the null sink.
COPY asound.conf /etc/asound.conf

# Per-instance Dire Wolf configs + the AGW echo helper + the launcher.
COPY direwolf-resp.conf /etc/direwolf-resp.conf
COPY direwolf-gw.conf   /etc/direwolf-gw.conf
COPY agw_echo.py        /usr/local/bin/agw_echo.py
COPY entrypoint.sh      /usr/local/bin/entrypoint.sh

# Dire Wolf refuses to run as root ("unnecessary security risk") and
# PulseAudio is happiest as a normal user. Run everything as an unprivileged
# user with a writable XDG runtime dir.
RUN useradd --create-home --uid 1000 dw \
 && mkdir -p /run/user/1000 \
 && chown -R dw:dw /run/user/1000 /home/dw \
 && chmod +x /usr/local/bin/entrypoint.sh

USER dw
ENV XDG_RUNTIME_DIR=/run/user/1000 \
    HOME=/home/dw

# KISSPORT for direwolf-gw (our Ax25Session attaches here).
EXPOSE 8001

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
