# INP3 two-node lab (docker)

A self-contained, reproducible lab that runs **two real `Packet.Node` hosts with the INP3 overlay ON**, sharing one net-sim `afsk1200` channel, and shows INP3 converging on the wire - independent of the shared packetdotnet lab.

INP3 (the time-based NET/ROM routing overlay) rides a connected-mode PID-0xCF interlink: the nodes discover each other via NODES, the first `connect` raises the interlink, then the L3RTT probe/reflect loop measures the link (SNTT) and RIFs propagate the measured time-routes. Both nodes end up holding a destination with **both** metrics - the NODES quality and the INP3 measured target time - coexisting on one route.

## Run it

```sh
# 1. build the self-contained node binary into ./app  (run from the repo root)
dotnet publish src/Packet.Node/Packet.Node.csproj -c Release -r linux-x64 \
  --self-contained true -o docker/inp3lab/app

# 2. bring the lab up (net-sim + node A + node B)
docker compose -f docker/inp3lab/compose.yml up --build -d

# 3. wait ~20-30 s for NODES discovery, then raise the interlink from node A:
#    (telnet to node A's console and connect to node B by callsign)
printf 'c GB7BBB\r' | nc -q2 127.0.0.1 9011        # or: telnet 127.0.0.1 9011

# 4. give INP3 ~30-40 s to converge (probe 5 s / RIF 10 s over slow afsk1200),
#    then read the routing table on either node:
telnet 127.0.0.1 9011   # node A console; type:  N
telnet 127.0.0.1 9012   # node B console

# tear down
docker compose -f docker/inp3lab/compose.yml down -v
```

## What you should see

After convergence, the `N` (Nodes) command shows the learned route carrying the INP3 metric alongside the quality pair - verified 2026-06-08:

```
GB7AAA> N
Node NODEA (GB7AAA)
...
NET/ROM routes:
  GB7BBB:GB7BBB: via GB7BBB(192,6) [inp3 1734ms/1h]
```

`(192,6)` is the NODES quality / obsolescence; `[inp3 1734ms/1h]` is the measured INP3 target time (≈ the real 1200-baud half-duplex round-trip ÷ 2) and hop count, learned from the L3RTT/RIF loop over the interlink. Node B symmetrically holds `GB7AAA … [inp3 1778ms/1h]`.

## Files

- `compose.yml` - net-sim + two pdn nodes (consoles on `127.0.0.1:9011`/`9012`, net-sim API on `9080`).
- `network.yaml` - net-sim topology: `a`↔`b` on one afsk1200 channel (KISS 8100/8101).
- `node-a.yaml` / `node-b.yaml` - pdn configs: KISS-TCP to net-sim, `netRom.{enabled,broadcast,connect}`, and `inp3.enabled` with compressed probe/RIF intervals for a quick demo.
- `Dockerfile` - runs the `./app` self-contained publish output (globalization-invariant; the `app/` dir is gitignored - build it per step 1).
