# 12. Sending NODES broadcasts

**Goal:** choose which ports carry your node's NET/ROM NODES broadcast.

A NET/ROM node tells its neighbours which destinations it can reach by broadcasting a **NODES** frame every so often (hourly by default, `netRom.sweepIntervalSeconds`). Neighbours that hear it can route through you. Hearing other nodes' NODES costs you no airtime and is always on; this page is only about **sending** your own.

> [!IMPORTANT]
> **Upgrading a node that already sends NODES?** Before this switch existed, turning on `netRom.broadcast` sent NODES on every port. Now **no port sends NODES until you tick it**, so after the upgrade your node goes quiet until you do. Tick **Send NODES** on each port that should carry them (or set `nodesBroadcast: true` on those ports). Until at least one port has it, the node logs a config note at start-up and on every config change:
>
> ```
> Config note: NET/ROM: netRom.broadcast is on but no enabled port has nodesBroadcast on, so this node sends NO NODES broadcasts. ...
> ```

## Two switches, both needed

| Switch | Where | Default |
|---|---|---|
| **Advertise my routes** (`netRom.broadcast`) | Control panel, **Config > NET/ROM** | Off |
| **Send NODES** (`nodesBroadcast`) | Control panel, **Ports**, a port's editor, under **NET/ROM** | Off, on every port |

NODES go out on a port only when **both** are on. The node-wide switch is the master: turn it off and every port goes quiet at once, whatever is ticked. The per-port box picks which ports carry the broadcast. Ticking or unticking a port applies to the next broadcast; nothing restarts.

Why per port: a slow shared channel, such as an HF port, may not have the airtime to spare for an hourly NODES broadcast, while your VHF or internet ports should carry it. If you know LinBPQ, it is close to giving a port `QUALITY=0`, except that pdn still learns the routes it hears there.

## In the config file

```yaml
ports:
  - id: vhf
    # ...
    nodesBroadcast: true    # this port carries NODES
  - id: hf
    # ...                   # no nodesBroadcast: this port sends none

netRom:
  broadcast: true           # the master switch
```

## Things worth knowing

- **Hearing is unaffected.** A port with Send NODES off still hears NODES and learns routes from them.
- **AXUDP multipoint ports** still send the broadcast only to peers marked **broadcast** (the BPQ `B` flag). Ticking Send NODES on a multipoint port with no broadcast peer sends nothing, and the node logs a config note saying so.
- **INP3 is separate.** INP3 routing updates travel inside links your node already has open to its INP3 neighbours; they are not NODES broadcasts and this switch does not stop them.

---

[Back to the guide index](index.md)
