// ============================================================
// lib/api.ts, LIVE mode (VITE_API_MODE=live), table-driven over EVERY exported api.*
// member - review item C030 (packet.net#692).
//
// The screen suites all run api.ts's MOCK branch (vitest sets no VITE_API_MODE) and then
// spyOn the api.* members they touch, which REPLACES the implementation - so before this
// file the live branch of every call except the 401-refresh path was unexercised: the
// method, the path, the body the screens build, and the status mapping (422 ->
// ConfigRejected, 409 -> PortLifecycleUnavailable, 501 -> PingUnavailable, 401 ->
// Unauthorized) were asserted nowhere.
//
// The table below IS the client's half of the wire contract. `COVERAGE` at the bottom
// fails if a member of `api` has no case here, so adding an endpoint to api.ts without a
// case fails the build rather than silently widening the untested surface.
//
// The SSE subscribers are driven through the FakeEventSource seam in test/helpers/live.
// api.stream.test.ts already covers subscribeFrames' token-rotation heal and the session
// backlog reset (C023/C045); this file covers the subscribers it does not - rigs, console,
// tuning, spectrum - plus the transient-blip and retry-budget behaviours of openStream.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import {
  loadLiveApi, installLocalStorage, seedSession, jsonResponse,
  installFakeEventSource, FakeEventSource,
} from "./helpers/live";
import type { NodeConfig, PortConfig } from "@/lib/types";

// The WebAuthn ceremonies talk to the authenticator through @simplewebauthn/browser;
// there is none in jsdom, so the two drivers are stubbed and the test asserts the HTTP
// halves either side of them (begin -> ceremony -> complete).
vi.mock("@simplewebauthn/browser", () => ({
  startAuthentication: vi.fn(async () => ({ id: "cred-1", rawId: "cred-1", response: {}, type: "public-key" })),
  startRegistration: vi.fn(async () => ({ id: "cred-1", rawId: "cred-1", response: {}, type: "public-key" })),
}));

type ApiModule = Awaited<ReturnType<typeof loadLiveApi>>;
type Api = ApiModule["api"];

/** One request the stubbed fetch saw. */
interface Seen {
  url: string;
  method: string;
  headers: Record<string, string>;
  rawBody: unknown;
}

/** Parse a recorded JSON body (undefined when the request carried none). */
function bodyOf(seen: Seen): unknown {
  if (seen.rawBody === undefined || seen.rawBody === null) return undefined;
  if (typeof seen.rawBody !== "string") return seen.rawBody;
  try { return JSON.parse(seen.rawBody); } catch { return seen.rawBody; }
}

/** Stub global fetch with a queue of responses (the last one repeats), recording calls. */
function stubFetch(responses: Response[]): Seen[] {
  const seen: Seen[] = [];
  let i = 0;
  vi.stubGlobal("fetch", vi.fn((url: string | URL, init?: RequestInit) => {
    seen.push({
      url: String(url),
      method: init?.method ?? "GET",
      headers: (init?.headers as Record<string, string>) ?? {},
      rawBody: init?.body,
    });
    const res = responses[Math.min(i, responses.length - 1)];
    i++;
    return Promise.resolve(res.clone());
  }));
  return seen;
}

const OK = () => jsonResponse({ ok: true });
const NO_CONTENT = () => new Response(null, { status: 204 });
const ACCEPTED = () => new Response(null, { status: 202 });

// A minimal NodeConfig the write paths carry; only the shape useFqdnForPasskeys reads matters.
const CONFIG = {
  schemaVersion: 3,
  identity: { callsign: "M0LTE-1", alias: "TEST", grid: null },
  ports: [],
  services: { banner: "b", prompt: "p" },
  management: {
    telnet: { enabled: false, bind: "127.0.0.1", port: 8011 },
    http: { bind: "127.0.0.1", port: 8080 },
    https: { enabled: false, bind: "0.0.0.0", port: 8443, certificatePath: null, certificatePassword: null, generateSelfSignedOnMissing: true },
    auth: { enabled: true, accessTokenMinutes: null, refreshTokenMinutes: null, sysopElevationMinutes: null, webAuthn: { relyingPartyId: "localhost", relyingPartyName: "pdn", allowedOrigins: [] } },
  },
  netRom: {
    enabled: true, broadcast: true, routing: "Transit", forwardMode: "PerFlow",
    compress: false,
    inp3: { enabled: true, preferInp3Routes: true, l3RttInterval: 60, l3RttResetWindow: 180, rifInterval: 300, positiveDebounce: 5 },
  },
  beacon: { enabled: false, intervalMinutes: 30, text: "" },
  tailscale: { enabled: false, authKey: null, authKeyFile: null, hostname: "pdn", tags: [], stateDir: "/tmp", target: "127.0.0.1:8080", funnel: false },
  oarc: { enabled: false, baseUrl: "https://x/", reportNodeStatus: true, reportLinks: true, reportCircuits: true, reportTraces: false, tracesRfOnly: true, publishExactPosition: false, statusIntervalSecs: 300, sessionStatusIntervalSecs: 60 },
  ardop: { enabled: false, device: "default", captureRate: 48000, bind: "127.0.0.1", port: 8515, ptt: "" },
  paging: { enabled: false, device: "default", captureRate: 48000, bind: "127.0.0.1", port: 8106, baud: 1200, invertPolarity: false, ptt: "" },
} as unknown as NodeConfig;

const PORT: PortConfig = {
  id: "sim", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 },
  profile: null, ax25: null, kiss: null, beacon: null,
};

interface Case {
  /** The api.* member this case covers (drives the coverage guard). */
  key: keyof Api;
  /** A suffix when one member needs several cases. */
  label?: string;
  call: (api: Api) => Promise<unknown>;
  /** Queued responses, in order; the last repeats. Defaults to a single 200 { ok: true }. */
  responses?: Response[];
  method: string;
  /** The full URL of the request under test (including any query string). */
  url: string;
  /** The parsed JSON body expected on that request; omit for a bodyless request. */
  body?: unknown;
  /** false for the always-open bootstrap calls that must NOT carry the bearer token. */
  bearer?: boolean;
  /** Which recorded request is the one under test (default: the last). */
  at?: number;
  /** Extra assertions over the resolved value and the whole recorded call list. */
  then?: (result: unknown, seen: Seen[]) => void;
}

const CASES: Case[] = [
  // ---- plain reads (the get<T> helper: authFetch + accept: application/json) ----
  { key: "status", call: (a) => a.status(), method: "GET", url: "/api/v1/status" },
  { key: "ports", call: (a) => a.ports(), method: "GET", url: "/api/v1/ports" },
  { key: "sessions", call: (a) => a.sessions(), method: "GET", url: "/api/v1/sessions" },
  { key: "routes", call: (a) => a.routes(), method: "GET", url: "/api/v1/netrom/routes" },
  { key: "config", call: (a) => a.config(), method: "GET", url: "/api/v1/config" },
  { key: "systemInfo", call: (a) => a.systemInfo(), method: "GET", url: "/api/v1/system/info" },
  { key: "tailscaleStatus", call: (a) => a.tailscaleStatus(), method: "GET", url: "/api/v1/system/tailscale" },
  { key: "linkStats", call: (a) => a.linkStats(), method: "GET", url: "/api/v1/links" },
  { key: "capabilities", call: (a) => a.capabilities(), method: "GET", url: "/api/v1/capabilities" },
  { key: "getRadios", call: (a) => a.getRadios(), method: "GET", url: "/api/v1/radios" },
  { key: "getRigs", call: (a) => a.getRigs(), method: "GET", url: "/api/v1/rigs" },
  { key: "scanRigs", call: (a) => a.scanRigs(), method: "GET", url: "/api/v1/rigs/scan" },
  { key: "getRigModels", call: (a) => a.getRigModels(), method: "GET", url: "/api/v1/rigs/models" },
  { key: "getNinoModes", call: (a) => a.getNinoModes(), method: "GET", url: "/api/v1/modems/nino-tnc/modes" },
  { key: "getHeadEnds", call: (a) => a.getHeadEnds(), method: "GET", url: "/api/v1/radios/headends" },
  { key: "recentFrames", call: (a) => a.recentFrames(), method: "GET", url: "/api/v1/monitor/recent?limit=250" },
  { key: "recentFrames", label: "explicit limit", call: (a) => a.recentFrames(40), method: "GET", url: "/api/v1/monitor/recent?limit=40" },
  { key: "users", call: (a) => a.users(), method: "GET", url: "/api/v1/users" },
  { key: "log", call: (a) => a.log(), method: "GET", url: "/api/v1/log" },
  { key: "apps", call: (a) => a.apps(), method: "GET", url: "/api/v1/apps" },
  { key: "appPackages", call: (a) => a.appPackages(), method: "GET", url: "/api/v1/apps/packages" },
  { key: "availableApps", call: (a) => a.availableApps(), method: "GET", url: "/api/v1/apps/available" },

  // ---- per-port reads with a 404 that means "no such thing here" ----
  { key: "getPortRadio", call: (a) => a.getPortRadio("vhf-1"), method: "GET", url: "/api/v1/ports/vhf-1/radio" },
  { key: "portQuality", call: (a) => a.portQuality("sm-1"), method: "GET", url: "/api/v1/ports/sm-1/quality" },
  { key: "getPortRig", call: (a) => a.getPortRig("hf-300"), method: "GET", url: "/api/v1/ports/hf-300/rig" },
  { key: "scanRadios", call: (a) => a.scanRadios(), method: "GET", url: "/api/v1/radios/scan" },

  // ---- rig control (operate) ----
  {
    key: "setRigFrequency", call: (a) => a.setRigFrequency("hf-300", 14_074_000),
    method: "POST", url: "/api/v1/ports/hf-300/rig/frequency", body: { frequencyHz: 14_074_000 },
  },
  {
    key: "setRigMode", call: (a) => a.setRigMode("hf-300", "PKTUSB", 3000),
    method: "POST", url: "/api/v1/ports/hf-300/rig/mode", body: { mode: "PKTUSB", passbandHz: 3000 },
  },
  {
    key: "setRigMode", label: "passband omitted", call: (a) => a.setRigMode("hf-300", "USB"),
    method: "POST", url: "/api/v1/ports/hf-300/rig/mode", body: { mode: "USB" },
  },

  // ---- head-ends ----
  {
    key: "adoptHeadEnd",
    call: (a) => a.adoptHeadEnd("shack north", { tncDeviceId: "usb-0", radioDeviceId: "usb-1", portId: "2m" }),
    method: "POST", url: "/api/v1/radios/headends/shack%20north/adopt",
    body: { tncDeviceId: "usb-0", radioDeviceId: "usb-1", portId: "2m" },
  },
  {
    key: "pairHeadEndByKeyup", call: (a) => a.pairHeadEndByKeyup("garage-pi"),
    method: "POST", url: "/api/v1/radios/headends/garage-pi/pair-by-keyup",
  },

  // ---- doctor: the safe GET vs the transmitting POST ----
  { key: "runDoctor", label: "safe", call: (a) => a.runDoctor("vhf-1"), method: "GET", url: "/api/v1/ports/vhf-1/doctor" },
  {
    key: "runDoctor", label: "interrupt", call: (a) => a.runDoctor("vhf-1", true),
    method: "POST", url: "/api/v1/ports/vhf-1/doctor?interrupt=true",
  },

  // ---- guided deviation tuning ----
  {
    key: "startTune", call: (a) => a.startTune("vhf-1", { role: "tuned", peerSdmId: "12345678", burstFrames: 5 }),
    method: "POST", url: "/api/v1/ports/vhf-1/tuning/session",
    body: { role: "tuned", peerSdmId: "12345678", burstFrames: 5 },
  },
  { key: "tuneNext", call: (a) => a.tuneNext("vhf-1"), method: "POST", url: "/api/v1/ports/vhf-1/tuning/next" },
  {
    key: "tuneStop", call: (a) => a.tuneStop("vhf-1"), method: "DELETE",
    url: "/api/v1/ports/vhf-1/tuning/session",
    then: (r) => expect(r).toBe(true),
  },
  {
    key: "tuneStop", label: "404 means no session was active", call: (a) => a.tuneStop("vhf-1"),
    responses: [new Response(null, { status: 404 })],
    method: "DELETE", url: "/api/v1/ports/vhf-1/tuning/session",
    then: (r) => expect(r).toBe(false),
  },

  // ---- config write ----
  {
    key: "putConfig", call: (a) => a.putConfig(CONFIG), method: "PUT",
    url: "/api/v1/config?dryRun=false", body: CONFIG,
  },
  {
    key: "putConfig", label: "dry run", call: (a) => a.putConfig(CONFIG, { dryRun: true }),
    method: "PUT", url: "/api/v1/config?dryRun=true", body: CONFIG,
  },
  {
    key: "getConfigRaw", call: (a) => a.getConfigRaw(),
    responses: [new Response("schemaVersion: 1\n", { status: 200, headers: { "content-type": "text/plain" } })],
    method: "GET", url: "/api/v1/config/raw",
    then: (r, seen) => {
      expect(r).toBe("schemaVersion: 1\n");
      expect(seen[0].headers.accept).toBe("text/plain");
    },
  },
  {
    key: "putConfigRaw", call: (a) => a.putConfigRaw("schemaVersion: 1\n"),
    method: "PUT", url: "/api/v1/config/raw?dryRun=false", body: "schemaVersion: 1\n",
    then: (_r, seen) => expect(seen[0].headers["content-type"]).toBe("text/plain"),
  },
  {
    // Read-modify-PUT: it must GET the LIVE config first and PUT that config with only the
    // relying-party id + origin changed - never a locally-assembled one.
    key: "useFqdnForPasskeys", call: (a) => a.useFqdnForPasskeys("pdn.tail.ts.net"),
    responses: [jsonResponse(CONFIG), jsonResponse({ ok: true })],
    method: "PUT", url: "/api/v1/config?dryRun=false",
    then: (_r, seen) => {
      expect(seen).toHaveLength(2);
      expect(seen[0].url).toBe("/api/v1/config");
      expect(seen[0].method).toBe("GET");
      const put = bodyOf(seen[1]) as NodeConfig;
      expect(put.management.auth.webAuthn.relyingPartyId).toBe("pdn.tail.ts.net");
      expect(put.management.auth.webAuthn.allowedOrigins).toContain("https://pdn.tail.ts.net");
      // Everything else rides through untouched.
      expect(put.identity).toEqual(CONFIG.identity);
      expect(put.netRom).toEqual(CONFIG.netRom);
    },
  },

  // ---- port management ----
  { key: "addPort", call: (a) => a.addPort(PORT), method: "POST", url: "/api/v1/ports", body: PORT },
  {
    key: "addPort", label: "dry run", call: (a) => a.addPort(PORT, { dryRun: true }),
    method: "POST", url: "/api/v1/ports?dryRun=true", body: PORT,
  },
  { key: "editPort", call: (a) => a.editPort("sim", PORT), method: "PUT", url: "/api/v1/ports/sim", body: PORT },
  { key: "removePort", call: (a) => a.removePort("sim"), method: "DELETE", url: "/api/v1/ports/sim" },
  {
    key: "portLifecycle", call: (a) => a.portLifecycle("sim", "restart"),
    method: "POST", url: "/api/v1/ports/sim/lifecycle", body: { action: "restart" },
  },

  // ---- sessions + ping ----
  {
    key: "connectSession", call: (a) => a.connectSession("GB7CIP", "vhf-1"),
    method: "POST", url: "/api/v1/sessions", body: { target: "GB7CIP", portId: "vhf-1" },
  },
  {
    key: "connectSession", label: "no port named", call: (a) => a.connectSession("GB7CIP"),
    method: "POST", url: "/api/v1/sessions", body: { target: "GB7CIP" },
  },
  {
    // The connect-out dialog's auto choice is the EMPTY STRING, and it has to reach the wire
    // as a body with no portId at all: a portId means a direct AX.25 dial on that port, so a
    // stray "" would cost the caller the node's NET/ROM routing (#727).
    key: "connectSession", label: "auto (empty port id) omits portId",
    call: (a) => a.connectSession("GB7CIP", ""),
    method: "POST", url: "/api/v1/sessions", body: { target: "GB7CIP" },
  },
  {
    key: "disconnectSession", call: (a) => a.disconnectSession("vhf-1:GB7CIP"),
    responses: [NO_CONTENT()], method: "DELETE", url: "/api/v1/sessions/vhf-1%3AGB7CIP",
  },
  {
    key: "sendSessionLine", call: (a) => a.sendSessionLine("vhf-1:GB7CIP", "HELP"),
    responses: [ACCEPTED()], method: "POST", url: "/api/v1/sessions/vhf-1%3AGB7CIP/send",
    body: { line: "HELP" },
  },
  {
    key: "clearCapability", call: (a) => a.clearCapability("vhf-1:M0LTE"),
    responses: [NO_CONTENT()], method: "DELETE", url: "/api/v1/capabilities/vhf-1%3AM0LTE",
  },
  {
    key: "pingTarget", call: (a) => a.pingTarget("M0LTE", "vhf-1", 3),
    method: "POST", url: "/api/v1/ping", body: { station: "M0LTE", portId: "vhf-1", count: 3 },
  },

  // ---- node command console ----
  {
    key: "openConsole", call: (a) => a.openConsole(), responses: [jsonResponse({ id: "console:7" })],
    method: "POST", url: "/api/v1/console",
    then: (r) => expect(r).toBe("console:7"),
  },
  {
    key: "consoleInput", call: (a) => a.consoleInput("console:7", "PORTS\r"),
    responses: [ACCEPTED()], method: "POST", url: "/api/v1/console/console%3A7/input",
    body: { data: "PORTS\r" },
  },
  {
    key: "closeConsole", call: (a) => a.closeConsole("console:7"),
    responses: [NO_CONTENT()], method: "DELETE", url: "/api/v1/console/console%3A7",
  },

  // ---- the always-open bootstrap path: NO bearer token may be attached ----
  {
    key: "setupState", call: (a) => a.setupState(), responses: [jsonResponse({ needsSetup: true })],
    method: "GET", url: "/api/v1/setup/state", bearer: false,
    then: (r) => expect(r).toEqual({ needsSetup: true }),
  },
  {
    key: "setup",
    call: (a) => a.setup({ identity: { callsign: "M0LTE-1", alias: "TEST", grid: null }, admin: { username: "admin", password: "hunter2hunter2" }, firstPort: PORT }),
    responses: [jsonResponse({ username: "admin", scope: "admin" })],
    method: "POST", url: "/api/v1/setup", bearer: false,
    body: { identity: { callsign: "M0LTE-1", alias: "TEST", grid: null }, admin: { username: "admin", password: "hunter2hunter2" }, firstPort: PORT },
  },
  {
    // The wizard's device picker. Open like the rest of the bootstrap path, and the ONE api
    // member that must never reject: a scan failure falls back to typing a path, it does not
    // strand the operator halfway through claiming a node.
    key: "setupDevices", call: (a) => a.setupDevices(),
    responses: [jsonResponse({ devices: [{ devicePath: "/dev/ttyACM0", kernelPath: "/dev/ttyACM0", descriptor: null, kind: "nino-tnc", firmwareVersion: "3.44", claimedBy: null, probeError: null }], permissionDenied: false })],
    method: "GET", url: "/api/v1/setup/devices", bearer: false,
    then: (r) => expect((r as { devices: unknown[] }).devices).toHaveLength(1),
  },
  {
    key: "login", call: (a) => a.login("admin", "pw"),
    responses: [jsonResponse({ token: "jwt", expiresAt: "2026-08-17T00:00:00Z", scopes: "admin", refreshToken: "rt", username: "admin" })],
    method: "POST", url: "/api/v1/auth/login", bearer: false,
    body: { username: "admin", password: "pw" },
    then: (r) => expect((r as { username: string }).username).toBe("admin"),
  },
  {
    key: "refresh", call: (a) => a.refresh(),
    responses: [jsonResponse({ token: "jwt2", expiresAt: "x", scopes: "admin", refreshToken: "rt2", username: "admin" })],
    method: "POST", url: "/api/v1/auth/refresh", bearer: false, body: { refreshToken: "rt-1" },
  },
  {
    key: "logout", call: (a) => a.logout(), responses: [NO_CONTENT()],
    method: "POST", url: "/api/v1/auth/logout", bearer: false, body: { refreshToken: "rt-1" },
  },

  // ---- admin user management ----
  { key: "usersList", call: (a) => a.usersList(), method: "GET", url: "/api/v1/users" },
  {
    key: "userCreate", call: (a) => a.userCreate("bob", "hunter2hunter2", "operate"),
    method: "POST", url: "/api/v1/users",
    body: { username: "bob", password: "hunter2hunter2", scope: "operate" },
  },
  {
    key: "userDelete", call: (a) => a.userDelete("bob"), responses: [NO_CONTENT()],
    method: "DELETE", url: "/api/v1/users/bob",
  },

  // ---- passkeys ----
  { key: "passkeyList", call: (a) => a.passkeyList(), responses: [jsonResponse([])], method: "GET", url: "/api/v1/auth/webauthn/credentials" },
  {
    key: "passkeyDelete", call: (a) => a.passkeyDelete("cred/1"), responses: [NO_CONTENT()],
    method: "DELETE", url: "/api/v1/auth/webauthn/credentials/cred%2F1",
  },
  {
    key: "passkeyAssert", call: (a) => a.passkeyAssert("tom"),
    responses: [
      jsonResponse({ sessionId: "s-1", options: { challenge: "c" } }),
      jsonResponse({ token: "jwt", expiresAt: "x", scopes: "admin", refreshToken: "rt", username: "tom" }),
    ],
    method: "POST", url: "/api/v1/auth/webauthn/assert/complete", bearer: false,
    then: (r, seen) => {
      expect(seen).toHaveLength(2);
      expect(seen[0].url).toBe("/api/v1/auth/webauthn/assert/begin");
      expect(bodyOf(seen[0])).toEqual({ username: "tom" });
      // The complete half echoes the begin half's session id alongside the ceremony result.
      expect(bodyOf(seen[1])).toMatchObject({ sessionId: "s-1" });
      // The identity comes from the SERVER, not the typed box.
      expect((r as { username: string }).username).toBe("tom");
    },
  },
  {
    key: "passkeyRegister", call: (a) => a.passkeyRegister(),
    responses: [jsonResponse({ challenge: "c" }), jsonResponse({ registered: true, credentialId: "cred-1" })],
    method: "POST", url: "/api/v1/auth/webauthn/register/complete",
    then: (_r, seen) => {
      expect(seen[0].url).toBe("/api/v1/auth/webauthn/register/begin");
      // Both halves are gated - the username is the server principal, never sent.
      expect(seen[0].headers.authorization).toBe("Bearer access");
      expect(bodyOf(seen[0])).toEqual({});
      expect(bodyOf(seen[1])).not.toHaveProperty("username");
    },
  },

  // ---- over-RF sysop code (TOTP) ----
  { key: "totpState", call: (a) => a.totpState(), responses: [jsonResponse({ enrolled: false, callsign: null })], method: "GET", url: "/api/v1/auth/totp/enroll" },
  {
    key: "totpEnrollBegin", call: (a) => a.totpEnrollBegin(),
    responses: [jsonResponse({ secret: "AAAA", otpauthUri: "otpauth://x" })],
    method: "POST", url: "/api/v1/auth/totp/enroll/begin", body: {},
  },
  {
    key: "totpEnrollComplete", call: (a) => a.totpEnrollComplete("123456", "M0LTE"),
    responses: [jsonResponse({ enrolled: true, callsign: "M0LTE" })],
    method: "POST", url: "/api/v1/auth/totp/enroll/complete", body: { code: "123456", callsign: "M0LTE" },
  },
  {
    key: "totpRemove", call: (a) => a.totpRemove(), responses: [NO_CONTENT()],
    method: "DELETE", url: "/api/v1/auth/totp/enroll",
  },

  // ---- app packages + catalog ----
  { key: "appPackageEnable", call: (a) => a.appPackageEnable("wall"), method: "POST", url: "/api/v1/apps/packages/wall/enable" },
  { key: "appPackageDisable", call: (a) => a.appPackageDisable("wall"), method: "POST", url: "/api/v1/apps/packages/wall/disable" },
  { key: "appPackageRestart", call: (a) => a.appPackageRestart("wall"), method: "POST", url: "/api/v1/apps/packages/wall/restart" },
  {
    key: "appPackageSetIdentity", call: (a) => a.appPackageSetIdentity("wall", { command: "WALL", callsign: "M0LTE-3", netromAlias: null, netromQuality: null }),
    method: "PUT", url: "/api/v1/apps/packages/wall/identity",
    body: { command: "WALL", callsign: "M0LTE-3", netromAlias: null, netromQuality: null },
  },
  { key: "appInstall", call: (a) => a.appInstall("dapps"), responses: [jsonResponse({ ok: true, id: "dapps", version: "1.0" })], method: "POST", url: "/api/v1/apps/available/dapps/install" },
  { key: "appUninstall", call: (a) => a.appUninstall("dapps"), responses: [jsonResponse({ ok: true, id: "dapps" })], method: "POST", url: "/api/v1/apps/packages/dapps/uninstall" },
  {
    key: "appUpload", call: (a) => a.appUpload(new File(["tarball"], "wall.pdnapp")),
    responses: [jsonResponse({ ok: true, id: "wall", version: "1.0" })],
    method: "POST", url: "/api/v1/apps/packages/upload",
    then: (_r, seen) => {
      // multipart: the browser must set the content-type (with the boundary), so we must not.
      expect(seen[0].rawBody).toBeInstanceOf(FormData);
      expect((seen[0].rawBody as FormData).get("file")).toBeInstanceOf(File);
      expect(seen[0].headers["content-type"]).toBeUndefined();
    },
  },

  // ---- node self-update ----
  { key: "systemUpdate", call: (a) => a.systemUpdate(), responses: [ACCEPTED()], method: "POST", url: "/api/v1/system/update" },
  {
    // /healthz is at the app ROOT (not under /api/v1) and is never given a token.
    key: "nodeHealthy", call: (a) => a.nodeHealthy(), responses: [jsonResponse({ status: "ok" })],
    method: "GET", url: "/healthz", bearer: false,
    then: (r) => expect(r).toBe(true),
  },
];

/** The two synchronous capability predicates - no fetch, so they are covered separately. */
const SYNC_KEYS: (keyof Api)[] = ["webauthnSupported", "totpSupported"];

beforeEach(() => {
  const store = installLocalStorage();
  seedSession(store, { token: "access", refreshToken: "rt-1", username: "tom", scope: "admin" });
  // jsdom has no Web Locks API; the cross-tab refresh serializer then takes the direct path.
  if (typeof navigator !== "undefined") {
    delete (navigator as { locks?: unknown }).locks;
  }
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

describe("api.* live requests - method, path, headers and body", () => {
  for (const c of CASES) {
    const name = c.label ? `${String(c.key)} (${c.label})` : String(c.key);
    it(`${name} -> ${c.method} ${c.url}`, async () => {
      const seen = stubFetch(c.responses ?? [OK()]);
      const mod = await loadLiveApi();

      const result = await c.call(mod.api);

      expect(seen.length).toBeGreaterThan(0);
      const idx = c.at ?? seen.length - 1;
      const req = seen[idx];
      expect(req.url).toBe(c.url);
      expect(req.method).toBe(c.method);
      if ("body" in c) {
        expect(bodyOf(req)).toEqual(c.body);
      }
      if (c.bearer === false) {
        expect(req.headers.authorization).toBeUndefined();
      } else {
        expect(req.headers.authorization).toBe("Bearer access");
      }
      c.then?.(result, seen);
    });
  }

  it("covers every member of the api surface", async () => {
    const mod = await loadLiveApi();
    const covered = new Set<string>([...CASES.map((c) => String(c.key)), ...SYNC_KEYS.map(String)]);
    const missing = Object.keys(mod.api).filter((k) => !covered.has(k));
    // A new api.* member with no case here means a new untested live branch - the exact
    // gap C030 recorded. Add a row to CASES rather than relaxing this.
    expect(missing).toEqual([]);
  });
});

describe("api.* status mapping", () => {
  const problem = { errors: [{ path: "ports[0].profile", message: "not a known channel profile" }] };

  it.each([
    ["putConfig", (a: Api) => a.putConfig(CONFIG)],
    ["putConfigRaw", (a: Api) => a.putConfigRaw("x: 1")],
    ["addPort", (a: Api) => a.addPort(PORT)],
    ["editPort", (a: Api) => a.editPort("sim", PORT)],
    ["removePort", (a: Api) => a.removePort("sim")],
    ["adoptHeadEnd", (a: Api) => a.adoptHeadEnd("p", { tncDeviceId: "t", radioDeviceId: "r" })],
    ["setup", (a: Api) => a.setup({ identity: { callsign: "X" }, admin: { username: "a", password: "b" } })],
  ])("%s maps a 422 to ConfigRejected carrying the field problems", async (_name, call) => {
    stubFetch([jsonResponse(problem, 422)]);
    const mod = await loadLiveApi();

    await expect(call(mod.api)).rejects.toBeInstanceOf(mod.ConfigRejected);
    await call(mod.api).catch((e: unknown) => {
      expect(e).toBeInstanceOf(mod.ConfigRejected);
      const rejected = e as InstanceType<typeof mod.ConfigRejected>;
      expect(rejected.problem.errors[0].path).toBe("ports[0].profile");
      expect(rejected.message).toContain("not a known channel profile");
    });
  });

  it("portLifecycle maps a 409 to PortLifecycleUnavailable with the server's reason", async () => {
    stubFetch([jsonResponse({ error: "the node is still starting" }, 409)]);
    const mod = await loadLiveApi();

    await expect(mod.api.portLifecycle("sim", "restart")).rejects.toBeInstanceOf(mod.PortLifecycleUnavailable);
    await mod.api.portLifecycle("sim", "restart").catch((e: unknown) => {
      const unavailable = e as InstanceType<typeof mod.PortLifecycleUnavailable>;
      expect(unavailable.action).toBe("restart");
      expect(unavailable.message).toBe("the node is still starting");
    });
  });

  it("pingTarget maps a 501 to PingUnavailable so the tool degrades instead of crashing", async () => {
    stubFetch([jsonResponse({ error: "TEST ping is not implemented" }, 501)]);
    const mod = await loadLiveApi();

    await expect(mod.api.pingTarget("M0LTE", "vhf-1")).rejects.toBeInstanceOf(mod.PingUnavailable);
  });

  it("login maps 401 to Unauthorized and 429 to a plain Error (a lockout is not an expiry)", async () => {
    stubFetch([jsonResponse({ error: "Invalid username or password." }, 401)]);
    const unauth = await loadLiveApi();
    await expect(unauth.api.login("admin", "wrong")).rejects.toBeInstanceOf(unauth.Unauthorized);

    stubFetch([jsonResponse({ error: "Too many attempts, try again in 60 s." }, 429)]);
    const locked = await loadLiveApi();
    const err = await locked.api.login("admin", "wrong").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(Error);
    expect(err).not.toBeInstanceOf(locked.Unauthorized);
    expect((err as Error).message).toContain("Too many attempts");
  });

  it("a 401 on a MUTATION refreshes once and replays it with the rotated token", async () => {
    // api.auth.test.tsx proves this for a read; the mutations go through the same authFetch,
    // and a replay that dropped the body would be silently wrong.
    const seen: Seen[] = [];
    let refreshes = 0;
    vi.stubGlobal("fetch", vi.fn((url: string | URL, init?: RequestInit) => {
      const u = String(url);
      seen.push({ url: u, method: init?.method ?? "GET", headers: (init?.headers as Record<string, string>) ?? {}, rawBody: init?.body });
      if (u.includes("/auth/refresh")) {
        refreshes++;
        return Promise.resolve(jsonResponse({ token: "fresh", refreshToken: "rt-2", scopes: "admin", username: "tom" }));
      }
      const auth = (init?.headers as Record<string, string> | undefined)?.authorization;
      return Promise.resolve(auth === "Bearer fresh" ? jsonResponse({ ok: true }) : new Response("", { status: 401 }));
    }));

    const mod = await loadLiveApi();
    await mod.api.editPort("sim", PORT);

    expect(refreshes).toBe(1);
    const replay = seen[seen.length - 1];
    expect(replay.url).toBe("/api/v1/ports/sim");
    expect(replay.method).toBe("PUT");
    expect(replay.headers.authorization).toBe("Bearer fresh");
    expect(bodyOf(replay)).toEqual(PORT);
  });

  it("a plain read surfaces a 5xx as an Error naming the path and status", async () => {
    stubFetch([new Response("", { status: 503 })]);
    const mod = await loadLiveApi();
    await expect(mod.api.status()).rejects.toThrow(/\/status: 503/);
  });

  it("a per-port 404 becomes the server's message, not a bare status", async () => {
    stubFetch([jsonResponse({ error: "no radio attached to port 'vhf-1'" }, 404)]);
    const mod = await loadLiveApi();
    await expect(mod.api.getPortRadio("vhf-1")).rejects.toThrow("no radio attached to port 'vhf-1'");
  });

  it("logout never throws, even when the revoke fails", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("network down"))));
    const mod = await loadLiveApi();
    await expect(mod.api.logout()).resolves.toBeUndefined();
  });

  it("nodeHealthy reports false rather than throwing when the node is still restarting", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("ECONNREFUSED"))));
    const mod = await loadLiveApi();
    await expect(mod.api.nodeHealthy()).resolves.toBe(false);
  });

  it("the capability predicates report the live truth (a ceremony needs a secure context)", async () => {
    const mod = await loadLiveApi();
    // jsdom is not a secure context and exposes no WebAuthn API.
    expect(mod.api.webauthnSupported()).toBe(false);
    // In LIVE mode there IS a node to enrol against, so over-RF enrolment is offered.
    expect(mod.api.totpSupported()).toBe(true);
  });
});

// ---- the SSE subscribers ------------------------------------------------------------
// api.stream.test.ts covers subscribeFrames' token-rotation heal (C023) and the session
// backlog reset (C045). These cover the rest of openStream's contract and the subscribers
// that had no test at all.
describe("openStream - the SSE subscribers", () => {
  beforeEach(() => {
    installFakeEventSource();
  });

  it("every subscriber opens its own path with the access token in the query string", async () => {
    const mod = await loadLiveApi();
    const stops = [
      mod.subscribeFrames(() => {}),
      mod.subscribeRigs(() => {}),
      mod.subscribeSessionOutput("vhf-1:GB7CIP", () => {}),
      mod.subscribeConsoleOutput("console:7", () => {}),
      mod.subscribeTune("vhf-1", () => {}),
      mod.subscribeSpectrum("vhf-1", () => {}),
    ];

    expect(FakeEventSource.instances.map((e) => e.path)).toEqual([
      "/api/v1/events",
      "/api/v1/rigs/events",
      "/api/v1/sessions/vhf-1%3AGB7CIP/stream",
      "/api/v1/console/console%3A7/stream",
      "/api/v1/ports/vhf-1/tuning/events",
      "/api/v1/ports/vhf-1/spectrum/events",
    ]);
    // EventSource cannot set headers, so the JWT rides in the query string on every feed.
    for (const es of FakeEventSource.instances) expect(es.accessToken).toBe("access");
    for (const stop of stops) stop();
  });

  it("subscribeRigs decodes each `rig` tick and reports connect/disconnect status", async () => {
    const mod = await loadLiveApi();
    const ticks: { portId: string; frequencyHz: number | null }[] = [];
    const statuses: boolean[] = [];
    const stop = mod.subscribeRigs((r) => ticks.push({ portId: r.portId, frequencyHz: r.frequencyHz }), {
      onStatus: (c) => statuses.push(c),
    });

    const es = FakeEventSource.instances[0];
    es.open();
    es.send("rig", { portId: "hf-300", frequencyHz: 14_074_000 });
    es.sendRaw("rig", "{not json");     // a malformed tick is skipped, never thrown
    es.send("rig", { portId: "hf-300", frequencyHz: 14_100_000 });

    expect(ticks).toEqual([
      { portId: "hf-300", frequencyHz: 14_074_000 },
      { portId: "hf-300", frequencyHz: 14_100_000 },
    ]);
    expect(statuses).toEqual([true]);
    stop();
  });

  it("subscribeConsoleOutput resets on the replayed backlog and streams live output", async () => {
    const mod = await loadLiveApi();
    let buffer = "";
    const stop = mod.subscribeConsoleOutput("c1", (t) => { buffer += t; }, undefined, {
      onReset: () => { buffer = "<reset>"; },
    });

    const es = FakeEventSource.instances[0];
    es.open();
    es.send("backlog", "M0LTE-1> ");
    es.send("output", "PORTS\r\n");
    expect(buffer).toBe("<reset>M0LTE-1> PORTS\r\n");
    stop();
  });

  it("subscribeTune decodes tuning events and subscribeSpectrum base64-decodes the bins", async () => {
    const mod = await loadLiveApi();
    const events: string[] = [];
    const stopTune = mod.subscribeTune("vhf-1", (e) => events.push(e.kind));
    FakeEventSource.instances[0].open();
    FakeEventSource.instances[0].send("tuning", { kind: "armed", at: "t", state: "armed" });
    FakeEventSource.instances[0].send("tuning", { kind: "round", at: "t", state: "peer-connected", decoded: 4, total: 5 });
    expect(events).toEqual(["armed", "round"]);
    stopTune();

    const lines: { bins: number[]; hz: number }[] = [];
    const stopSpec = mod.subscribeSpectrum("vhf-1", (bins, hz) => lines.push({ bins: [...bins], hz }));
    const spec = FakeEventSource.instances[FakeEventSource.instances.length - 1];
    spec.open();
    spec.send("spectrum", { seq: 1, binHz: 2.93, bins: btoa(String.fromCharCode(0, 128, 255)) });
    expect(lines).toEqual([{ bins: [0, 128, 255], hz: 2.93 }]);
    stopSpec();
  });

  it("a transient drop is left to the browser; only a CLOSED stream is re-opened by us", async () => {
    vi.useFakeTimers();
    stubFetch([jsonResponse({ token: "fresh", refreshToken: "rt-2", scopes: "admin", username: "tom" })]);
    const mod = await loadLiveApi();
    const stop = mod.subscribeFrames(() => {});

    const first = FakeEventSource.instances[0];
    first.open();
    // readyState CONNECTING: the browser is already re-dialling with the frozen URL.
    first.blip();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(FakeEventSource.instances).toHaveLength(1);

    stop();
    vi.useRealTimers();
  });

  it("a session-scoped feed gives up after its retry budget and tells the screen it is over", async () => {
    vi.useFakeTimers();
    stubFetch([new Response("", { status: 401 })]);   // the renew fails; we re-dial regardless
    const mod = await loadLiveApi();
    let gone = 0;
    const stop = mod.subscribeConsoleOutput("c1", () => {}, () => { gone++; });

    // Three reconnect attempts (SESSION_STREAM_RETRIES) then onGone. The backoff is 1s, 2s, 4s
    // capped at 30s plus jitter, so 60 s covers each wait. The fourth failure spends the budget.
    for (let i = 0; i < 4; i++) {
      FakeEventSource.instances[FakeEventSource.instances.length - 1].fail();
      await vi.advanceTimersByTimeAsync(60_000);
    }
    expect(FakeEventSource.instances).toHaveLength(4);   // the original + 3 retries
    expect(gone).toBe(1);

    stop();
    vi.useRealTimers();
  });

  it("the unsubscribe stops the reconnect loop for good", async () => {
    vi.useFakeTimers();
    stubFetch([jsonResponse({ token: "fresh", refreshToken: "rt-2", scopes: "admin", username: "tom" })]);
    const mod = await loadLiveApi();
    const stop = mod.subscribeFrames(() => {});

    FakeEventSource.instances[0].open();
    stop();
    FakeEventSource.instances[0].fail();
    await vi.advanceTimersByTimeAsync(120_000);
    expect(FakeEventSource.instances).toHaveLength(1);
    vi.useRealTimers();
  });

  it("seedFrames serves nothing in live mode (the monitor seeds from /monitor/recent)", async () => {
    const mod = await loadLiveApi();
    expect(mod.seedFrames(10)).toEqual([]);
  });
});
