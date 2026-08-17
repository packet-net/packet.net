// Round-trip the GB7RDG migration features (#521 UI leg) through the PUT /config
// wire shape (JSON, camelCase — what TransportConfigJsonConverter + the NodeConfig
// binder consume). The structured Forms editor must preserve, byte-for-byte, the
// fields the Raw-YAML tab already round-trips:
//   1. an axudp-multipoint transport (localPort + a peers[] table of call/host/port/broadcast)
//   2. per-port netRomMinQuality (MINQUAL) + nodesPaclen (NODESPACLEN)
// A regression here = a multipoint port (or the new knobs) silently dropped on a
// Forms load→save, the exact bug this UI work closes.
//
// Every case here runs the EDITOR: openEdit's draft, then portDraftToConfig, then the wire.
// #692 C032 found the first four cases applying JSON.parse(JSON.stringify(x)) to fixture
// literals instead - true of any JSON-safe object, and reachable without the editor existing
// at all, while the header above framed a failure as a Forms load→save drop. The third GB7RDG
// feature, netRom.compress, is node-level rather than per-port and so has no reconstruction
// function to exercise; it is now covered by mounting the Config screen and reading the PUT
// body (src/test/screens.smoke.test.tsx, "toggling NET/ROM compression PUTs netRom.compress").
import { describe, it, expect } from "vitest";
import { NODE_CONFIG } from "@/lib/mock";
import type { AxudpMultipointTransport, PortConfig, TransportConfig } from "@/lib/types";
import { portDraftToConfig, radioPairsWith, type PortDraft } from "@/screens/ports";

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
    profile: null,
    ax25: { t1Ms: 3000, t2Ms: 300, t3Ms: 180000, n2: 8, windowSize: 4 },
    kiss: { txDelay: 30, slotTime: 10, txTail: 5, persistence: 63 },   // wire units: 10 ms each
    setup: { radio: null, channel: "shared", difficulty: "moderate", custom: true },
    beacon: null,
    compat: null,
    link: null,
    radio,
    rig,
    netRomQuality: null,
    netRomMinQuality: null,
    nodesPaclen: null,
    _new: true,
  };
}

// What the Ports screen's openEdit builds from a loaded port (see screens/ports.tsx). Every
// "does a load→save preserve X" question in this file goes through here.
function draftOf(p: PortConfig): PortDraft {
  return {
    id: p.id,
    enabled: p.enabled,
    transport: p.transport,
    profile: p.profile ?? null,
    ax25: p.ax25 ?? null,
    kiss: p.kiss ?? null,
    setup: { radio: null, channel: "shared", difficulty: "moderate", custom: false },
    beacon: p.beacon,
    compat: p.compat ?? null,
    link: p.link ?? null,
    radio: p.radio ?? null,
    rig: p.rig ?? null,
    netRomQuality: p.netRomQuality ?? null,
    netRomMinQuality: p.netRomMinQuality ?? null,
    nodesPaclen: p.nodesPaclen ?? null,
    _origId: p.id,
    _orig: p,
  };
}

// One editor load→save: open the port, reconstruct it, put it on the wire.
function editorRoundTrip(p: PortConfig, edit: (d: PortDraft) => PortDraft = (d) => d): PortConfig {
  return wireRoundTrip(portDraftToConfig(edit(draftOf(p))));
}

// A no-op save must not change the port. The one licence the reconstruction has is to SPELL an
// absent optional field as an explicit null (the server binder reads null and absent the same);
// dropping a value, or inventing one, is the bug this file exists to catch.
function expectUnchanged(out: PortConfig, src: PortConfig, label: string): void {
  expect(out, `${label}: a field the server sent changed or was dropped`).toMatchObject(src);
  const invented = Object.keys(out)
    .filter((k) => !(k in src))
    .filter((k) => (out as unknown as Record<string, unknown>)[k] !== null);
  expect(invented, `${label}: the save invented values the server never sent`).toEqual([]);
}

describe("config round-trip preserves the GB7RDG features", () => {
  it("a multipoint-AXUDP port with 2 peers survives a Forms load -> save", () => {
    const mp = NODE_CONFIG.ports.find((p) => p.transport.kind === "axudp-multipoint");
    expect(mp, "the mock seeds a multipoint port").toBeDefined();

    const out = editorRoundTrip(mp!);
    // Nothing changed, nothing dropped by the reconstruction, including the nested peers[].
    expectUnchanged(out, mp!, "mp-net");

    const t = out.transport as AxudpMultipointTransport;
    expect(t.kind).toBe("axudp-multipoint");
    expect(t.localPort).toBeGreaterThan(0);
    expect(t.peers).toHaveLength(2);
    // Each peer keeps call/host/port/broadcast (the BPQ MAP line).
    expect(t.peers[0]).toEqual({ call: "N0CALL-1", host: "44.131.10.1", port: 10093, broadcast: true });
    expect(t.peers[1]).toEqual({ call: "N0CALL-7", host: "44.131.10.2", port: 10094, broadcast: false });
  });

  it("editing one peer row rewrites that peer and leaves the other alone", () => {
    // The peers table is the editor's own control, so a save must carry the operator's edit
    // AND the row they never touched - the whole table is replaced on every save.
    const mp = NODE_CONFIG.ports.find((p) => p.id === "mp-net")!;
    const out = editorRoundTrip(mp, (d) => {
      const t = d.transport as AxudpMultipointTransport;
      return {
        ...d,
        transport: { ...t, peers: [{ ...t.peers[0], host: "44.131.10.9", broadcast: false }, t.peers[1]] },
      };
    });

    const peers = (out.transport as AxudpMultipointTransport).peers;
    expect(peers).toHaveLength(2);
    expect(peers[0]).toEqual({ call: "N0CALL-1", host: "44.131.10.9", port: 10093, broadcast: false });
    expect(peers[1]).toEqual({ call: "N0CALL-7", host: "44.131.10.2", port: 10094, broadcast: false });
  });

  it("per-port netRomMinQuality + nodesPaclen survive a Forms load -> save, edited or not", () => {
    const mp = NODE_CONFIG.ports.find((p) => p.id === "mp-net")!;

    // Untouched: MINQUAL 100 / NODESPACLEN 160 come back exactly as the server sent them.
    const untouched = editorRoundTrip(mp);
    expect(untouched.netRomMinQuality).toBe(100);
    expect(untouched.nodesPaclen).toBe(160);

    // Edited: the operator's numbers reach the wire (these are per-port overrides, so a save
    // that silently re-derived them from the node-level defaults would be the bug).
    const edited = editorRoundTrip(mp, (d) => ({ ...d, netRomMinQuality: 143, nodesPaclen: 120 }));
    expect(edited.netRomMinQuality).toBe(143);
    expect(edited.nodesPaclen).toBe(120);
  });

  it("a port that leaves the new per-port knobs unset round-trips them as absent/null", () => {
    // A blank field maps to null, not 0 - 0 is a meaningful MINQUAL ("accept everything"),
    // so the two must stay distinguishable through the reconstruction.
    const out = wireRoundTrip(portDraftToConfig(draftWith(
      { kind: "axudp-multipoint", localPort: 10093, peers: [{ call: "N0CALL", host: "44.0.0.1", port: 10093, broadcast: false }] },
      null,
    )));
    expect(out.netRomMinQuality).toBeNull();
    expect(out.nodesPaclen).toBeNull();
    expect((out.transport as AxudpMultipointTransport).peers).toHaveLength(1);
  });

  it("every port in the mock config survives an untouched open -> save unchanged", () => {
    // The load→save identity across the whole fixture: transports of five different kinds,
    // radios, rigs, beacons and the per-port NET/ROM knobs, each through the real editor path.
    expect(NODE_CONFIG.ports.length).toBeGreaterThan(3);
    for (const p of NODE_CONFIG.ports) {
      expectUnchanged(editorRoundTrip(p), p, `port ${p.id}`);
    }
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

  it("the reconstruction never drops a radio the operator did not remove (#690 C004)", () => {
    // The save path carries every block through verbatim. Dropping a radio by transport KIND here
    // silently detached an adopted head-end port's radio (the editor cannot even show that kind's
    // radio section); the drop now happens only when the operator RE-TYPES the transport to one
    // that cannot carry the radio, which is the setKind path guarded by radioPairsWith below.
    const draft = draftWith(
      { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 },
      { kind: "tait-ccdi", serial: "19925328", baud: 28800 },
    );
    const out = portDraftToConfig(draft);
    expect(out.radio).toEqual({ kind: "tait-ccdi", serial: "19925328", baud: 28800 });
  });

  it("radioPairsWith mirrors the server's radio<->transport pairing rules", () => {
    const local = { kind: "tait-ccdi" as const, serial: "19925328", baud: 28800 };
    const headEnd = { kind: "tait-ccdi" as const, headEndId: "shack-pi", deviceId: "tait-0" };
    const rigBacked = { kind: "rig" as const };

    // A locally-cabled radio needs a local serial-modem transport.
    expect(radioPairsWith(local, "serial-kiss")).toBe(true);
    expect(radioPairsWith(local, "nino-tnc")).toBe(true);
    expect(radioPairsWith(local, "kiss-tcp")).toBe(false);
    expect(radioPairsWith(local, "nino-tnc-tcp")).toBe(false);
    // A head-end-bound radio pairs with the co-located full-control NinoTNC, and nothing else.
    expect(radioPairsWith(headEnd, "nino-tnc-tcp")).toBe(true);
    expect(radioPairsWith(headEnd, "serial-kiss")).toBe(false);
    // A rig-backed radio has no cable at all: any transport.
    expect(radioPairsWith(rigBacked, "kiss-tcp")).toBe(true);
    expect(radioPairsWith(rigBacked, "soundmodem")).toBe(true);
    // No radio pairs with everything.
    expect(radioPairsWith(null, "axudp")).toBe(true);
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

  it("the mock config's rig-attached ports round-trip byte-for-byte through the editor", () => {
    // The fixture's real rigs (a BYO flrig daemon and a BYO rigctld), opened and saved the way
    // the screen does. Asserting this over the fixture literal instead proved only that the
    // fixture is JSON-safe and never touched the reconstruction that used to drop the block.
    const rigPorts = NODE_CONFIG.ports.filter((x) => x.rig);
    expect(rigPorts.length, "the fixture seeds at least one rig-attached port").toBeGreaterThan(0);
    for (const p of rigPorts) {
      expect(editorRoundTrip(p).rig, `port ${p.id} lost its rig on save`).toEqual(p.rig);
    }
  });
});

// The NET/ROM block's JSON wire dialect (#688). Two shapes are decided on the server
// and mirrored here: an enum crosses as its member NAME and a duration crosses as a
// number of SECONDS. A regression either way is a save the node rejects with a 400.
describe("the netRom block round-trips in the server's config dialect", () => {
  const netRom = NODE_CONFIG.netRom;

  it("routing + forwardMode are enum member names, not integers", () => {
    const out = wireRoundTrip(netRom);

    expect(typeof out.routing).toBe("string");
    expect(["None", "Endpoint", "Transit"]).toContain(out.routing);
    // The real C# enum is BestRoute | PerFlow - "Single" never existed on the server.
    expect(["BestRoute", "PerFlow"]).toContain(out.forwardMode);
    expect(out.routing).toBe(netRom.routing);
    expect(out.forwardMode).toBe(netRom.forwardMode);
  });

  it("the four INP3 timers are numbers of seconds, never duration strings", () => {
    const out = wireRoundTrip(netRom.inp3);

    for (const v of [out.l3RttInterval, out.l3RttResetWindow, out.rifInterval, out.positiveDebounce]) {
      expect(typeof v).toBe("number");
      expect(Number.isFinite(v)).toBe(true);
      expect(v).toBeGreaterThan(0);
    }
    // The server's own guard: a reset window shorter than one probe interval would
    // tear a live neighbour down before it could answer (422 on save).
    expect(out.l3RttResetWindow).toBeGreaterThan(out.l3RttInterval);
    expect(out.positiveDebounce).toBeLessThan(out.rifInterval);
    expect(out).toEqual(netRom.inp3);
  });

  it("an edited routing role + INP3 timer survive the save serialisation", () => {
    // What the Forms editor hands api.putConfig: the draft with two fields replaced.
    const edited = { ...netRom, routing: "Endpoint" as const, inp3: { ...netRom.inp3, l3RttInterval: 120 } };
    const out = wireRoundTrip(edited);

    expect(out.routing).toBe("Endpoint");
    expect(out.inp3.l3RttInterval).toBe(120);
    expect(JSON.stringify(out)).toContain('"routing":"Endpoint"');
    expect(JSON.stringify(out)).toContain('"l3RttInterval":120');
  });
});

// #690 C003/C004/C005: a PUT replaces the port entry WHOLESALE, so the save body must be the port
// the server sent plus what the operator changed - not a field-by-field re-build that quietly drops
// whatever the editor does not model. The draft carries the loaded PortConfig (_orig) and the
// reconstruction spreads it.
describe("the save body is the loaded port plus the operator's edits", () => {
  // An adopted split-station port exactly as HeadEndAdoption writes it: a nino-tnc-tcp transport
  // bound to a head-end device, the co-located head-end radio, and the MQTT {instance} label.
  const ADOPTED: PortConfig = {
    id: "2m",
    enabled: true,
    transport: { kind: "nino-tnc-tcp", headEndId: "shack-pi", deviceId: "nino-0", mode: 4 },
    profile: null,
    ax25: null,
    kiss: { txDelay: 30, persistence: 63, slotTime: 10, txTail: 0, ackMode: true },
    beacon: null,
    radio: { kind: "tait-ccdi", headEndId: "shack-pi", deviceId: "tait-0" },
    mqttInstance: "2m",
  };

  it("an adopted head-end port round-trips through open -> save unchanged", () => {
    const out = wireRoundTrip(portDraftToConfig(draftOf(ADOPTED)));
    // Every field the server sent comes back with the same value (the editor may additionally
    // spell an absent optional field as an explicit null - the server reads them the same).
    expect(out).toMatchObject(ADOPTED);
    // Spelled out, because each of these was individually dropped before:
    expect(out.transport).toEqual({ kind: "nino-tnc-tcp", headEndId: "shack-pi", deviceId: "nino-0", mode: 4 });
    expect(out.radio).toEqual({ kind: "tait-ccdi", headEndId: "shack-pi", deviceId: "tait-0" });
    expect(out.kiss).toEqual({ txDelay: 30, persistence: 63, slotTime: 10, txTail: 0, ackMode: true });
    expect(out.mqttInstance).toBe("2m");
  });

  it("mqttInstance survives an ordinary edit of an ordinary port", () => {
    const port: PortConfig = {
      id: "70cm", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 },
      profile: null, ax25: null, kiss: null, beacon: null, mqttInstance: "70cm",
    };
    const draft = draftOf(port);
    const out = wireRoundTrip(portDraftToConfig({ ...draft, enabled: false }));
    expect(out.mqttInstance).toBe("70cm");
    expect(out.enabled).toBe(false);
  });

  it("an untouched edit of a port with ax25/kiss null PUTs those blocks back as null", () => {
    const port: PortConfig = {
      id: "sim", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 },
      profile: "slow-afsk1200", ax25: null, kiss: null, beacon: null,
    };
    const out = wireRoundTrip(portDraftToConfig(draftOf(port)));
    expect(out.ax25).toBeNull();
    expect(out.kiss).toBeNull();
    // ... and the server channel profile is carried verbatim, never re-derived from a UI preset.
    expect(out.profile).toBe("slow-afsk1200");
    expect(out).toMatchObject(port);
  });

  it("editing ONE parameter of a null block sends only that parameter", () => {
    const port: PortConfig = {
      id: "sim", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 },
      profile: null, ax25: null, kiss: null, beacon: null,
    };
    // The editor's setAx/setKiss shape: spread the (null) block, set the one key.
    const draft = draftOf(port);
    const out = wireRoundTrip(portDraftToConfig({ ...draft, ax25: { ...(draft.ax25 ?? {}), t1Ms: 4000 } }));
    expect(out.ax25).toEqual({ t1Ms: 4000 });
    expect(out.kiss).toBeNull();
  });

  it("a server field the editor has never heard of still rides through a save", () => {
    // The forward-compatibility contract: the reconstruction spreads the loaded port, so a field
    // added to PortConfig server-side is preserved by a Forms save before the UI models it.
    const port = {
      id: "future", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 },
      profile: null, ax25: null, kiss: null, beacon: null,
      somethingTheUiDoesNotKnow: { nested: [1, 2, 3] },
    } as unknown as PortConfig;
    const out = wireRoundTrip(portDraftToConfig(draftOf(port))) as unknown as Record<string, unknown>;
    expect(out.somethingTheUiDoesNotKnow).toEqual({ nested: [1, 2, 3] });
  });
});
