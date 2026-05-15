# Interop Docker stack

This directory holds the docker-compose stack used by `tests/Packet.Interop.Tests`
and by the `interop` GitHub Actions workflow. It also doubles as a useful local
dev environment for end-to-end work.

## Quick start

```sh
docker compose -f docker/compose.interop.yml up -d --wait
```

Once everything is healthy:

| Service | Host port | Purpose |
|---|---|---|
| LinBPQ web UI | http://localhost:8008 | admin/admin |
| LinBPQ telnet | localhost:8010 | node prompt (user/pass) |
| LinBPQ AGW | localhost:8000 | external app socket |
| LinBPQ AXUDP | localhost:8093/udp | AXUDP port (BPQAXIP driver) |
| Xrouter telnet | localhost:8023 | node prompt |
| Xrouter web UI | http://localhost:8086 | + `/exec?cmd=…` |
| Xrouter AXUDP | localhost:8095/udp | peer-pair listener (UDPREMOTE=8094) |
| net-sim web UI | http://localhost:8080 | topology + start/stop |
| net-sim KISS A | localhost:8100 | afsk1200 node A |
| net-sim KISS B | localhost:8101 | afsk1200 node B |
| rax25 | (no host port) | Habets's Rust AX.25 engine; dials netsim:8104 as a KISS-TCP client (built from source at image-build time) |

Tear down:

```sh
docker compose -f docker/compose.interop.yml down -v
```

## Image pinning

Image references in `compose.interop.yml` are pinned to **sha256 digests**
rather than floating tags. This makes a CI run two months from now behave
identically to one today — useful for keeping interop scenarios reproducible
when an upstream image rebases or changes behaviour.

To refresh against a newer upstream image:

```sh
# 1. Pull the floating tag locally.
docker pull m0lte/linbpq:latest

# 2. Read the new digest.
docker inspect --format='{{index .RepoDigests 0}}' m0lte/linbpq:latest

# 3. Replace the corresponding `image:` line in compose.interop.yml.
#    The `image:` value should be of the form
#      <repo>@sha256:<hex>
#    Keep the comment above it noting the floating tag and the date you
#    pulled, so future-you can see how stale the pin is.

# 4. Open a small PR. The PR description should call out what changed
#    upstream (release notes, behavioural deltas) so reviewers can weigh
#    whether the bump is safe.
```

Same procedure applies to `ghcr.io/packethacking/xrouter` and
`ghcr.io/packethacking/net-sim`.

## Files

```
docker/
  compose.interop.yml      stack definition
  linbpq/
    bpq32.cfg              minimal LinBPQ config
  xrouter/
    XROUTER.CFG            minimal XRouter config
  rax25/
    Dockerfile             builds Habets's rax25 async_server example from source
  netsim/
    network.yaml           multi-node topology for interop tests
```
