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
import type { AxudpMultipointTransport, NodeConfig, PortConfig, TransportConfig } from "@/lib/types";
import { portDraftToConfig, type PortDraft } from "@/screens/ports";

// The PUT /config wire path is JSON; this is the same shape the server deserialises.
function wireRoundTrip<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

// A minimal editor draft with the given transport + radio/rig blocks — the shape saveDraft reconstructs.
function draftWith(transport: TransportConfig, radio: PortDraft["radio"], rig: PortDraft["rig"] = null): PortDraft {
  return {
    id: "vhf-x",
    enabled: true,
    transport,
    ax25: { t1Ms: 3000, t2Ms: 300, t3Ms: 180000, n2: 8, windowSize: 4 },
    kiss: { txDelay: 300, slotTime: 100, txTail: 50, persistence: 63 },
    setup: { radio: null, channel: "shared", difficulty: "moderate", custom: true },
    beacon: null,
    compat: null,
    radio,
    rig,
    netRomQuality: null,
    netRomMinQuality: null,
    nodesPaclen: null,
    _new: true,
  };
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

// The radio: block is the newest field the field-by-field saveDraft reconstruction must carry, and the
// original bug: saveDraft rebuilt PortConfig without it, so editing a radio-attached port silently
// DROPPED the radio on save. These exercise the real reconstruction (portDraftToConfig) + wire path.
describe("radio-control block survives the PortEditor save (saveDraft reconstruction)", () => {
  it("a serial-bound radio round-trips through portDraftToConfig + the wire", () => {
    const draft = draftWith(
      { kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 },
      { kind: "tait-ccdi", serial: "19925328", baud: 28800 },
    );
    const out = wireRoundTrip(portDraftToConfig(draft));
    // The block is present (not dropped) and byte-for-byte intact.
    expect(out.radio).toEqual({ kind: "tait-ccdi", serial: "19925328", baud: 28800 });
  });

  it("a device-path-bound radio (advanced fallback) round-trips intact", () => {
    const draft = draftWith(
      { kind: "serial-kiss", device: "/dev/ttyUSB1", baud: 38400 },
      { kind: "tait-ccdi", port: "/dev/ttyUSB2", baud: 28800, healthIntervalSeconds: 5 },
    );
    const out = wireRoundTrip(portDraftToConfig(draft));
    expect(out.radio).toEqual({ kind: "tait-ccdi", port: "/dev/ttyUSB2", baud: 28800, healthIntervalSeconds: 5 });
  });

  it("switching a radio-attached port to a non-serial transport drops the radio block", () => {
    // kiss-tcp / AXUDP ports have no radio beside the modem (server validation), so the reconstruction
    // must NOT emit a stale radio block if the transport was switched away from a serial-modem kind.
    const draft = draftWith(
      { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 },
      { kind: "tait-ccdi", serial: "19925328", baud: 28800 },
    );
    const out = portDraftToConfig(draft);
    expect(out.radio).toBeNull();
  });

  it("a port with no radio reconstructs radio as null (not undefined-dropped)", () => {
    const draft = draftWith({ kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 }, null);
    const out = wireRoundTrip(portDraftToConfig(draft));
    expect(out.radio).toBeNull();
  });
});

// The rig: block (plug-and-play rig, stage 2) rides the same field-by-field saveDraft
// reconstruction — a shape missing here is silently dropped on a Forms save (the radio: bug's
// sibling). Both server shapes must survive: node-managed (device + model [+ serialSpeed],
// hamlib only) and BYO daemon (host [+ port], either kind). Unlike radio:, the rig block is
// valid on EVERY transport kind (it never touches the packet path), so a kiss-tcp port keeps it.
describe("rig (CAT) block survives the PortEditor save (saveDraft reconstruction)", () => {
  it("a node-managed hamlib rig (device + model + serialSpeed) round-trips intact", () => {
    const rig = {
      kind: "hamlib" as const,
      device: "/dev/serial/by-id/usb-Icom_Inc._IC-7300_IC-7300_02012345-if00-port0",
      model: 3073,
      serialSpeed: 115200,
    };
    const draft = draftWith({ kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 }, null, rig);
    const out = wireRoundTrip(portDraftToConfig(draft));
    expect(out.rig).toEqual(rig);
  });

  it("a BYO rigctld daemon (host + port) round-trips intact", () => {
    const rig = { kind: "hamlib" as const, host: "127.0.0.1", port: 4532 };
    const draft = draftWith({ kind: "serial-kiss", device: "/dev/ttyUSB1", baud: 38400 }, null, rig);
    const out = wireRoundTrip(portDraftToConfig(draft));
    expect(out.rig).toEqual(rig);
  });

  it("a BYO flrig daemon with the port omitted round-trips intact (the kind default stays absent)", () => {
    const rig = { kind: "flrig" as const, host: "127.0.0.1" };
    const draft = draftWith({ kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 }, null, rig);
    const out = wireRoundTrip(portDraftToConfig(draft));
    expect(out.rig).toEqual(rig);
    expect(out.rig).not.toHaveProperty("port");
  });

  it("a rig on a kiss-tcp port is preserved (the rig block is not serial-transport-gated)", () => {
    const rig = { kind: "hamlib" as const, host: "127.0.0.1", port: 4532 };
    const draft = draftWith({ kind: "kiss-tcp", host: "127.0.0.1", port: 8001 }, null, rig);
    const out = wireRoundTrip(portDraftToConfig(draft));
    expect(out.rig).toEqual(rig);
  });

  it("YAML-set poll cadences ride through the reconstruction untouched", () => {
    // The editor never surfaces pollIntervalSeconds/meterIntervalSeconds — a YAML-set value
    // must survive a Forms load→save (the healthIntervalSeconds convention on radio:).
    const rig = { kind: "hamlib" as const, device: "/dev/ttyUSB5", model: 1, pollIntervalSeconds: 10, meterIntervalSeconds: 2 };
    const draft = draftWith({ kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 }, null, rig);
    const out = wireRoundTrip(portDraftToConfig(draft));
    expect(out.rig).toEqual(rig);
  });

  it("a port with no rig reconstructs rig as null (not undefined-dropped)", () => {
    const draft = draftWith({ kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 }, null, null);
    const out = wireRoundTrip(portDraftToConfig(draft));
    expect(out.rig).toBeNull();
  });

  it("the mock config's rig-attached ports round-trip byte-for-byte", () => {
    for (const p of NODE_CONFIG.ports.filter((x) => x.rig)) {
      const out = wireRoundTrip(p);
      expect(out.rig).toEqual(p.rig);
    }
    // The fixture seeds at least one rig-attached port, so the loop above really ran.
    expect(NODE_CONFIG.ports.some((x) => x.rig)).toBe(true);
  });
});
