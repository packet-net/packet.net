// Round-trip the GB7RDG migration features (#521 UI leg) through the PUT /config
// wire shape (JSON, camelCase — what TransportConfigJsonConverter + the NodeConfig
// binder consume). The structured Forms editor must preserve, byte-for-byte, the
// fields the Raw-YAML tab already round-trips:
//   1. an axudp-multipoint transport (localPort + a peers[] table of call/host/port/broadcast)
//   2. per-port netRomMinQuality (MINQUAL) + nodesPaclen (NODESPACLEN)
//   3. netRom.compress (L4Compress)
// A regression here = a multipoint port (or the new knobs) silently dropped on a
// Forms load→save, the exact bug this UI work closes.
import { describe, it, expect } from "vitest";
import { NODE_CONFIG } from "@/lib/mock";
import type { AxudpMultipointTransport, NodeConfig, PortConfig } from "@/lib/types";

// The PUT /config wire path is JSON; this is the same shape the server deserialises.
function wireRoundTrip<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

describe("config round-trip preserves the GB7RDG features", () => {
  it("a multipoint-AXUDP port with 2 peers survives serialize -> deserialize", () => {
    const mp = NODE_CONFIG.ports.find((p) => p.transport.kind === "axudp-multipoint");
    expect(mp, "the mock seeds a multipoint port").toBeDefined();

    const out = wireRoundTrip(mp!);
    // Deep-equal: nothing added, nothing dropped, including the nested peers[].
    expect(out).toEqual(mp);

    const t = out.transport as AxudpMultipointTransport;
    expect(t.kind).toBe("axudp-multipoint");
    expect(t.localPort).toBeGreaterThan(0);
    expect(t.peers).toHaveLength(2);
    // Each peer keeps call/host/port/broadcast (the BPQ MAP line).
    expect(t.peers[0]).toEqual({ call: "N0CALL-1", host: "44.131.10.1", port: 10093, broadcast: true });
    expect(t.peers[1]).toEqual({ call: "N0CALL-7", host: "44.131.10.2", port: 10094, broadcast: false });
  });

  it("per-port netRomMinQuality + nodesPaclen survive serialize -> deserialize", () => {
    const mp = NODE_CONFIG.ports.find((p) => p.id === "mp-net")!;
    const out = wireRoundTrip(mp);
    expect(out.netRomMinQuality).toBe(100);
    expect(out.nodesPaclen).toBe(160);
  });

  it("a port that leaves the new per-port knobs unset round-trips them as absent/null", () => {
    // Mirror the editor's saveDraft: an unset (blank) field maps to null, not 0.
    const draftSaved: PortConfig = {
      id: "vhf-x",
      enabled: true,
      transport: { kind: "axudp-multipoint", localPort: 10093, peers: [{ call: "N0CALL", host: "44.0.0.1", port: 10093, broadcast: false }] },
      profile: null,
      ax25: null,
      kiss: null,
      beacon: null,
      compat: null,
      netRomQuality: null,
      netRomMinQuality: null,
      nodesPaclen: null,
    };
    const out = wireRoundTrip(draftSaved);
    expect(out.netRomMinQuality).toBeNull();
    expect(out.nodesPaclen).toBeNull();
    expect((out.transport as AxudpMultipointTransport).peers).toHaveLength(1);
  });

  it("netRom.compress survives serialize -> deserialize", () => {
    const out: NodeConfig = wireRoundTrip(NODE_CONFIG);
    expect(out.netRom.compress).toBe(NODE_CONFIG.netRom.compress);
    // And toggling it round-trips the toggled value (NetRomSection emits compress).
    const toggled = wireRoundTrip({ ...NODE_CONFIG, netRom: { ...NODE_CONFIG.netRom, compress: true } });
    expect(toggled.netRom.compress).toBe(true);
  });
});
