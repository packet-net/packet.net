// ============================================================
// The client half of the client/server contract gate (review item C018, packet.net#692).
//
// tests/Packet.Node.Tests/Contract/ClientContractFixtureTests.cs serialises a representative
// instance of every DTO the panel consumes, with the app's REAL wire options, into
// src/test/contract/*.json. This file reads those files back and checks them against
// lib/types.ts, using the TypeScript compiler API to read the declarations rather than a
// hand-maintained mirror (which would itself drift).
//
// Three failure modes, all previously invisible:
//   1. the server sends a field types.ts does not model  -> EXTRA
//   2. types.ts requires a field the server never sends  -> MISSING
//   3. the value's kind (or union member) is wrong       -> a kind mismatch
// And the same check runs over lib/mock.ts, so the fake node cannot describe a node the real
// server could not produce. Because types.ts is pinned to the fixtures, that transitively
// pins the mock to the server: a field the mock has and the server does not shows up as EXTRA,
// and one the server has and the mock does not as MISSING.
//
// The compile-time half lives in test/contract/fixtures.ts (a typed loader), which `tsc
// --noEmit` checks as part of `npm run build`.
//
// To adopt a deliberate server change: regenerate the fixtures
// (`PDN_UPDATE_CONTRACT=1 dotnet test tests/Packet.Node.Tests --filter Contract`), then fix
// types.ts and mock.ts here until this file is green again. Never edit a fixture by hand and
// never relax a check to make it pass - that is the exact hole this closes.
import { describe, it, expect } from "vitest";
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import ts from "typescript";
import * as mock from "@/lib/mock";

const SRC = join(__dirname, "..");
const CONTRACT_DIR = join(__dirname, "contract");

// ---- read types.ts ---------------------------------------------------------------
interface PropSpec { optional: boolean; type: string }
type Interfaces = Record<string, Record<string, PropSpec>>;
type Aliases = Record<string, string>;

function readTypes(): { interfaces: Interfaces; aliases: Aliases } {
  const path = join(SRC, "lib", "types.ts");
  const sf = ts.createSourceFile(path, readFileSync(path, "utf8"), ts.ScriptTarget.Latest, true);
  const interfaces: Interfaces = {};
  const aliases: Aliases = {};
  const heritage: Record<string, string[]> = {};

  sf.forEachChild((n) => {
    if (ts.isInterfaceDeclaration(n)) {
      const props: Record<string, PropSpec> = {};
      for (const m of n.members) {
        if (ts.isPropertySignature(m)) {
          props[m.name.getText(sf)] = {
            optional: !!m.questionToken,
            type: m.type ? m.type.getText(sf) : "unknown",
          };
        }
      }
      interfaces[n.name.text] = props;
      heritage[n.name.text] = (n.heritageClauses ?? [])
        .flatMap((h) => h.types.map((t) => t.expression.getText(sf)));
    } else if (ts.isTypeAliasDeclaration(n)) {
      aliases[n.name.text] = n.type.getText(sf);
    }
  });

  // Fold `extends` bases into the child's property table (the transports share a
  // TransportComputed base for the server-computed echo fields).
  for (const [name, bases] of Object.entries(heritage)) {
    for (const base of bases) {
      Object.assign(interfaces[name], { ...interfaces[base], ...interfaces[name] });
    }
  }

  return { interfaces, aliases };
}

const { interfaces: I, aliases: A } = readTypes();

// ---- the structural checker ------------------------------------------------------
function kindOf(v: unknown): string {
  if (v === null) return "null";
  if (Array.isArray(v)) return "array";
  return typeof v;
}

/** Split a union type at the TOP level only (a nested `{ a: b | c }` must not split). */
function unionParts(t: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < t.length; i++) {
    const c = t[i];
    if (c === "{" || c === "(" || c === "[" || c === "<") depth++;
    else if (c === "}" || c === ")" || c === "]" || c === ">") depth--;
    else if (c === "|" && depth === 0) { parts.push(t.slice(start, i)); start = i + 1; }
  }
  parts.push(t.slice(start));
  return parts.map((p) => p.trim()).filter(Boolean);
}

class Checker {
  readonly problems: string[] = [];

  check(type: string, value: unknown, path: string, ctx: string): void {
    if (!this.accepts(type, value, path, ctx)) {
      this.problems.push(
        `${ctx}: ${path}: expected ${type}, got ${JSON.stringify(value)?.slice(0, 100)}`);
    }
  }

  private accepts(type: string, value: unknown, path: string, ctx: string): boolean {
    let t = type.trim();
    // Follow a plain alias chain (FrameType -> "UI" | "SABM" | ...).
    let guard = 0;
    while (A[t] !== undefined && A[t] !== t && guard++ < 20) t = A[t];

    const k = kindOf(value);
    // Object-shaped arms (a discriminated union like TransportConfig) are TRIED, not taken in
    // declaration order: each is checked into a scratch collector, and only if none matches do
    // we report - from whichever arm came closest, which for a `kind:`-discriminated union is
    // always the arm with the matching discriminant.
    const objectArms: string[] = [];
    for (const p of unionParts(t)) {
      if (p === "unknown" || p === "any") return true;
      if (p === "null") { if (k === "null") return true; continue; }
      if (p === "undefined") { if (value === undefined) return true; continue; }
      if (p === "string") { if (k === "string") return true; continue; }
      if (p === "number") { if (k === "number") return true; continue; }
      if (p === "boolean") { if (k === "boolean") return true; continue; }
      if (p === "Date") { if (k === "string" || value instanceof Date) return true; continue; }
      if (/^"[^"]*"$/.test(p)) { if (value === JSON.parse(p)) return true; continue; }
      if (p.endsWith("[]")) {
        if (k !== "array") continue;
        (value as unknown[]).forEach((e, i) => this.check(p.slice(0, -2), e, `${path}[${i}]`, ctx));
        return true;
      }
      if (p.startsWith("Record<")) { if (k === "object") return true; continue; }
      if (p.startsWith("{") || I[p]) { objectArms.push(p); continue; }
      if (A[p] !== undefined) { if (this.accepts(A[p], value, path, ctx)) return true; continue; }
      // A named type this reader cannot resolve is a hole in the check, not a pass.
      this.problems.push(`${ctx}: ${path}: cannot resolve type '${p}'`);
      return true;
    }

    if (objectArms.length === 0 || k !== "object") return false;

    let best: string[] | null = null;
    for (const arm of objectArms) {
      const trial = new Checker();
      if (arm.startsWith("{")) trial.inline(arm, value as Record<string, unknown>, path, ctx);
      else trial.object(arm, value as Record<string, unknown>, path, ctx);
      if (trial.problems.length === 0) return true;
      if (best === null || trial.problems.length < best.length) best = trial.problems;
    }
    this.problems.push(...(best ?? []));
    return true;
  }

  /** Check an object against a named interface: required present, none extra, kinds right. */
  object(iface: string, obj: Record<string, unknown>, path: string, ctx: string): void {
    const props = I[iface];
    if (!props) { this.problems.push(`${ctx}: unknown interface ${iface}`); return; }
    for (const [name, spec] of Object.entries(props)) {
      if (!(name in obj) || obj[name] === undefined) {
        if (!spec.optional) {
          this.problems.push(`${ctx}: ${path}.${name} MISSING (types.ts requires ${spec.type})`);
        }
        continue;
      }
      this.check(spec.type, obj[name], `${path}.${name}`, ctx);
    }
    for (const key of Object.keys(obj)) {
      if (!(key in props)) {
        this.problems.push(
          `${ctx}: ${path}.${key} EXTRA (not modelled by ${iface}) = ${JSON.stringify(obj[key])?.slice(0, 80)}`);
      }
    }
  }

  /** Check an object against an inline `{ a: T; b: U }` literal type. */
  inline(literal: string, obj: Record<string, unknown>, path: string, ctx: string): void {
    const body = literal.slice(literal.indexOf("{") + 1, literal.lastIndexOf("}"));
    const members = new Map<string, string>();
    let depth = 0;
    let start = 0;
    const pieces: string[] = [];
    for (let i = 0; i < body.length; i++) {
      const c = body[i];
      if (c === "{" || c === "(" || c === "<") depth++;
      else if (c === "}" || c === ")" || c === ">") depth--;
      else if ((c === ";" || c === ",") && depth === 0) { pieces.push(body.slice(start, i)); start = i + 1; }
    }
    pieces.push(body.slice(start));
    for (const piece of pieces) {
      const trimmed = piece.trim();
      if (!trimmed) continue;
      const colon = trimmed.indexOf(":");
      if (colon < 0) continue;
      members.set(trimmed.slice(0, colon).trim().replace(/\?$/, ""), trimmed.slice(colon + 1).trim());
    }
    for (const [name, type] of members) {
      if (!(name in obj)) { this.problems.push(`${ctx}: ${path}.${name} MISSING (${type})`); continue; }
      this.check(type, obj[name], `${path}.${name}`, ctx);
    }
    for (const key of Object.keys(obj)) {
      if (!members.has(key)) {
        this.problems.push(`${ctx}: ${path}.${key} EXTRA (not in the inline type)`);
      }
    }
  }

  /** Check a top-level fixture value: one object, or an array of them. */
  top(iface: string, body: unknown, ctx: string): void {
    if (Array.isArray(body)) {
      if (body.length === 0) { this.problems.push(`${ctx}: empty array - ${iface} unverified`); return; }
      body.forEach((e, i) => this.object(iface, e as Record<string, unknown>, `[${i}]`, ctx));
      return;
    }
    this.object(iface, body as Record<string, unknown>, "$", ctx);
  }
}

// ---- fixture -> the types.ts interface it must satisfy ---------------------------
// Every DTO the SPA consumes. The C# side owns the file list (its own test fails if a fixture
// is orphaned); this map fails if a fixture arrives with no client type to check it against.
const FIXTURES: Record<string, string> = {
  "NodeConfig.json": "NodeConfig",
  "NodeStatus.json": "NodeStatus",
  "PortStatus.json": "PortStatus",
  "SessionInfo.json": "SessionInfo",
  "LinkStats.json": "LinkStats",
  "PeerCapability.json": "PeerCapability",
  "MonitorEvent.json": "MonitorEvent",
  "HeardStation.json": "HeardStation",
  "NetRomRoutingSnapshot.json": "NetRomRoutingSnapshot",
  "UserSummary.json": "UserSummary",
  "LoginResult.json": "LoginResult",
  "SetupState.json": "SetupState",
  "SetupResult.json": "SetupResult",
  "WebAuthnCredential.json": "WebAuthnCredential",
  "RegisterCompleteResponse.json": "RegisterCompleteResponse",
  "TotpEnrollState.json": "TotpEnrollState",
  "TotpEnrollBeginResponse.json": "TotpEnrollBeginResponse",
  "TotpEnrollCompleteResponse.json": "TotpEnrollCompleteResponse",
  "RadioStatus.json": "RadioStatus",
  "RadioScanResult.json": "RadioScanResult",
  "RigStatus.json": "RigStatus",
  "RigScan.json": "RigScan",
  "RigModelCatalogue.json": "RigModelCatalogue",
  "SoundModemQualitySnapshot.json": "SoundModemQualitySnapshot",
  "DoctorReport.json": "DoctorReport",
  "HeadEndScan.json": "HeadEndScan",
  "HeadEndKeyupResult.json": "HeadEndKeyupResult",
  "PingResult.json": "PingResult",
  "TuningSessionInfo.json": "TuningSessionInfo",
  "TuningEvent.json": "TuningEvent",
  "NodeApp.json": "NodeApp",
  "AppPackage.json": "AppPackage",
  "AvailableApp.json": "AvailableApp",
  "InstallOutcome.json": "InstallOutcome",
  "SystemInfo.json": "SystemInfo",
  "TailscaleStatus.json": "TailscaleStatus",
  "ReconcileResult.json": "ReconcileResult",
  "ValidationProblem.json": "ValidationProblem",
};

function loadFixture(file: string): unknown {
  return JSON.parse(readFileSync(join(CONTRACT_DIR, file), "utf8")) as unknown;
}

describe("contract: the server's JSON satisfies types.ts", () => {
  it("every checked-in fixture is mapped to a client type", () => {
    const onDisk = readdirSync(CONTRACT_DIR).filter((f) => f.endsWith(".json")).sort();
    const mapped = [...Object.keys(FIXTURES), "closed-sets.json"].sort();
    expect(onDisk).toEqual(mapped);
  });

  for (const [file, iface] of Object.entries(FIXTURES)) {
    it(`${file} matches ${iface}`, () => {
      const checker = new Checker();
      checker.top(iface, loadFixture(file), file);
      expect(checker.problems).toEqual([]);
    });
  }
});

// ---- the closed sets --------------------------------------------------------------
// Each union in types.ts must name EXACTLY the members the server can produce. The C# side
// reads them from the server's own const tables, enums and (for the frame taxonomy) from the
// classifier swept over all 256 control octets, so an arm added on the server fails here.
const CLOSED_SETS: Record<string, string> = {
  transportKinds: "TransportKind",
  compatPresets: "CompatPreset",
  linkDial: "LinkDialPreference",
  linkPreConnectXid: "LinkPreConnectXid",
  netRomRouting: "NetRomRouting",
  netRomForwardMode: "NetRomForwardMode",
  appPackageStates: "AppPackageState",
  portStates: "PortState",
  appUiModes: "AppUiMode",
  frameTypes: "FrameType",
  frameClasses: "FrameClass",
  headEndDeviceKinds: "HeadEndDeviceKind",
};

describe("contract: the closed sets", () => {
  const sets = loadFixture("closed-sets.json") as Record<string, string[]>;

  for (const [key, alias] of Object.entries(CLOSED_SETS)) {
    it(`${alias} names exactly the server's ${key}`, () => {
      expect(A[alias], `types.ts declares no type alias ${alias}`).toBeDefined();
      const declared = unionParts(A[alias])
        .map((p) => (/^"[^"]*"$/.test(p) ? (JSON.parse(p) as string) : p))
        .sort();
      expect(declared).toEqual([...sets[key]].sort());
    });
  }

  it("the radio + rig kinds a port may carry match the server's tables", () => {
    // These two are not standalone aliases - they are inline unions on RadioConfig.kind /
    // RigConfig.kind, so they are read off the interface rather than the alias table.
    const radioKinds = unionParts(I.RadioConfig.kind.type).map((p) => JSON.parse(p) as string).sort();
    expect(radioKinds).toEqual([...sets.radioKinds].sort());
    const rigKinds = unionParts(I.RigConfig.kind.type).map((p) => JSON.parse(p) as string).sort();
    expect(rigKinds).toEqual([...sets.rigKinds].sort());
  });

  it("PortConfig.profile only ever carries a name the server's ChannelProfiles knows", () => {
    // profile is a free string on both sides (the server validates it), so the assertion is
    // that the mock does not invent one - the C002 bug, where the Ports editor defaulted a new
    // port to a UI-catalogue profile id the server rejected with a 422.
    const known = new Set(sets.channelProfiles);
    for (const p of mock.NODE_CONFIG.ports) {
      if (p.profile !== null && p.profile !== undefined) {
        expect(known, `mock port '${p.id}' names profile '${p.profile}'`).toContain(p.profile);
      }
    }
  });
});

// ---- the mock, against the same shapes -------------------------------------------
// mock.ts is the fake NODE behind VITE_API_MODE=mock. Every screen renders against it, so a
// fixture whose shape the real server could not produce means the screens are developed and
// smoke-tested against a node that cannot exist.
const MOCKS: [string, string, unknown][] = [
  ["NODE_CONFIG", "NodeConfig", mock.NODE_CONFIG],
  ["NODE_STATUS", "NodeStatus", mock.NODE_STATUS],
  ["PORT_STATUS", "PortStatus", Object.values(mock.PORT_STATUS)],
  ["SESSIONS", "SessionInfo", mock.SESSIONS],
  ["LINK_STATS", "LinkStats", mock.LINK_STATS],
  ["CAPABILITIES", "PeerCapability", mock.CAPABILITIES],
  ["NETROM", "NetRomRoutingSnapshot", mock.NETROM],
  ["RADIOS", "RadioStatus", mock.RADIOS],
  ["RADIO_SCAN", "RadioScanResult", mock.RADIO_SCAN],
  ["RIGS", "RigStatus", mock.RIGS],
  ["RIG_SCAN", "RigScan", mock.RIG_SCAN],
  ["RIG_MODELS", "RigModelCatalogue", mock.RIG_MODELS],
  ["SOUNDMODEM_QUALITY", "SoundModemQualitySnapshot", mock.SOUNDMODEM_QUALITY],
  ["HEADEND_SCAN", "HeadEndScan", mock.HEADEND_SCAN],
  ["HEARD_STATIONS", "HeardStation", mock.HEARD_STATIONS],
  ["APPS", "NodeApp", mock.APPS],
  ["APP_PACKAGES", "AppPackage", mock.APP_PACKAGES],
  ["AVAILABLE_APPS", "AvailableApp", mock.AVAILABLE_APPS],
  ["SYSTEM_INFO", "SystemInfo", mock.SYSTEM_INFO],
  ["TAILSCALE_STATUS", "TailscaleStatus", mock.TAILSCALE_STATUS],
];

describe("contract: the mock node describes a node the server could actually be", () => {
  for (const [name, iface, value] of MOCKS) {
    it(`mock.${name} satisfies ${iface}`, () => {
      const checker = new Checker();
      checker.top(iface, value, `mock.${name}`);
      expect(checker.problems).toEqual([]);
    });
  }

  it("the mock's generated frames and doctor report satisfy their wire shapes", () => {
    const checker = new Checker();
    checker.top("MonitorEvent", mock.seedFrames(24), "mock.seedFrames");
    checker.top("DoctorReport", mock.doctorReport("vhf-1", false), "mock.doctorReport");
    checker.top("DoctorReport", mock.doctorReport("vhf-1", true), "mock.doctorReport(interrupt)");
    checker.top("HeadEndKeyupResult", mock.headEndKeyup("garage-pi"), "mock.headEndKeyup");
    checker.top("TuningSessionInfo", mock.tuneSession("vhf-1", { role: "tuned", peerSdmId: "12345678" }), "mock.tuneSession");
    expect(checker.problems).toEqual([]);
  });

  it("the mock config's top-level blocks are exactly the server's", () => {
    // Spelled out because the config PUT replaces the document wholesale: a block the mock
    // does not carry is one the Forms editor was never exercised against keeping.
    const fromServer = Object.keys(loadFixture("NodeConfig.json") as object).sort();
    expect(Object.keys(mock.NODE_CONFIG).sort()).toEqual(fromServer);
  });
});
