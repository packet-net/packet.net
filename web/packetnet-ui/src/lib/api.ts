// ============================================================
// pdn API client — the boundary the screens talk to.
//
// Two backends behind one typed surface:
//   - "mock" (default): resolves from lib/mock.ts; the monitor frame stream is
//     timer-driven. Lets every screen render + demo with no node.
//   - "live": real fetch against /api/v1 + an EventSource on /api/v1/events.
// Toggle with VITE_API_MODE=live (see vite proxy). The Slice-3 backend (locked
// in docs/node-api.yaml) lands behind the "live" path with no screen changes.
// ============================================================
import { useEffect, useRef, useState } from "react";
import type {
  NodeStatus, PortStatus, PortConfig, SessionInfo, NetRomRoutingSnapshot, NodeConfig,
  LinkStats, MonitorEvent, User, LogLine, ReconcileResult, ValidationProblem,
  PingResult, PingReply, UserSummary, LoginResult, SetupState, SetupRequest, SetupResult,
} from "./types";
import * as mock from "./mock";
import { UNAUTHORIZED_EVENT } from "@/app/auth";

const MODE: "mock" | "live" =
  (import.meta.env.VITE_API_MODE as "mock" | "live") ?? "mock";
const BASE = "/api/v1";

export const apiMode = MODE;

// ---- auth glue ---------------------------------------------
// api.ts is plain TS (no React). It reads the persisted token straight from the
// same sessionStorage slot AuthProvider writes, and signals a 401 by dispatching
// a window event the provider listens for (→ logout → relogin). This keeps the
// fetch path free of a context dependency while staying in lock-step with auth.tsx.
const SESSION_KEY = "pdn.session";

/** The current JWT, or null when there's no session (auth off / pre-login / mock). */
function token(): string | null {
  try {
    const raw = sessionStorage.getItem(SESSION_KEY);
    if (!raw) return null;
    return (JSON.parse(raw) as { token?: string | null }).token ?? null;
  } catch {
    return null;
  }
}

/** Build request headers, attaching the bearer token when we have one. */
function authHeaders(extra: Record<string, string> = {}): Record<string, string> {
  const t = token();
  return t ? { ...extra, authorization: `Bearer ${t}` } : extra;
}

/** Append ?access_token=<jwt> to an SSE URL (EventSource can't set headers). */
function withTokenParam(url: string): string {
  const t = token();
  if (!t) return url;
  const u = new URL(url, window.location.origin);
  u.searchParams.set("access_token", t);
  return u.pathname + u.search;
}

/** Raised when the session is rejected (401). The provider has already been told
 * to log out (via the window event); callers usually just let this propagate. */
export class Unauthorized extends Error {
  constructor(message = "Your session has expired. Please sign in again.") {
    super(message);
    this.name = "Unauthorized";
  }
}

/** A single 401 chokepoint: tell the auth provider to log out, then throw. */
function on401(): never {
  window.dispatchEvent(new Event(UNAUTHORIZED_EVENT));
  throw new Unauthorized();
}

/** Auth-aware fetch — attaches the bearer token and funnels every 401 to on401().
 *  When auth is OFF the server 200s tokenless, so this transparently "just works"
 *  with token() === null. */
async function authFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: authHeaders((init.headers as Record<string, string>) ?? {}),
  });
  if (res.status === 401) on401();
  return res;
}

async function get<T>(path: string, mockValue: () => T): Promise<T> {
  if (MODE === "mock") {
    // tiny delay so loading states are exercised
    await new Promise((r) => setTimeout(r, 60));
    return structuredClone(mockValue());
  }
  const res = await authFetch(path, { headers: { accept: "application/json" } });
  if (!res.ok) throw new Error(`${path}: ${res.status} ${res.statusText}`);
  return (await res.json()) as T;
}

// A rejected config edit (HTTP 422) — carries the server's per-field validation
// problems so the editor can surface them inline.
export class ConfigRejected extends Error {
  constructor(public readonly problem: ValidationProblem) {
    super(problem.errors.map((e) => `${e.path}: ${e.message}`).join("; ") || "config rejected");
    this.name = "ConfigRejected";
  }
}

// A synthetic reconcile result for mock mode (no backend to ask). Marks everything
// "live" so the preview renders; real grouping comes from the server in live mode.
function mockReconcile(applied: boolean): ReconcileResult {
  return { valid: true, live: [{ path: "(mock)", impact: "live", summary: "Mock mode — no node to reconcile against." }], portRestart: [], nodeReset: [], applied };
}

// ---- read endpoints ----------------------------------------
export const api = {
  status: () => get<NodeStatus>("/status", () => mock.NODE_STATUS),
  ports: () => get<PortStatus[]>("/ports", () => Object.values(mock.PORT_STATUS)),
  sessions: () => get<SessionInfo[]>("/sessions", () => mock.SESSIONS),
  routes: () => get<NetRomRoutingSnapshot>("/netrom/routes", () => mock.NETROM),
  config: () => get<NodeConfig>("/config", () => mock.NODE_CONFIG),
  linkStats: () => get<LinkStats[]>("/links", () => mock.LINK_STATS),
  users: () => get<User[]>("/users", () => mock.USERS),
  log: () => get<LogLine[]>("/log", () => mock.LOG_TAIL),

  // ---- config write (Slice-3 step 2) ----
  // PUT the whole config; dryRun returns the reconcile preview without applying.
  // A 422 throws ConfigRejected carrying the validation problems.
  putConfig: (cfg: NodeConfig, opts: { dryRun?: boolean } = {}) =>
    writeConfig("/config", "PUT", JSON.stringify(cfg), "application/json", opts.dryRun ?? false),
  // The raw YAML the advanced editor round-trips.
  getConfigRaw: async (): Promise<string> => {
    if (MODE === "mock") return "# raw YAML round-trips against a live node\nschemaVersion: 1\n";
    const res = await authFetch("/config/raw", { headers: { accept: "text/plain" } });
    if (!res.ok) throw new Error(`/config/raw: ${res.status}`);
    return res.text();
  },
  putConfigRaw: (yaml: string, opts: { dryRun?: boolean } = {}) =>
    writeConfig("/config/raw", "PUT", yaml, "text/plain", opts.dryRun ?? false),

  // ---- port management (Slice-3 step 3) ----
  // Each mutation flows through the same config-write reconcile path as putConfig:
  // a 422 throws ConfigRejected; success returns the ReconcileResult.
  addPort: (p: PortConfig) => writePort("/ports", "POST", JSON.stringify(p)),
  editPort: (id: string, p: PortConfig) =>
    writePort(`/ports/${encodeURIComponent(id)}`, "PUT", JSON.stringify(p)),
  removePort: (id: string) =>
    writePort(`/ports/${encodeURIComponent(id)}`, "DELETE"),
  // Bring a port up/down/restart (persisted via the config seam for up/down; restart
  // drives the supervisor's serialized RestartPortAsync). Returns the port's PortStatus.
  portLifecycle: (id: string, action: "up" | "down" | "restart") =>
    portLifecycle(id, action),

  // ---- session actions + ping (Slice-3 step 4) ----
  // Connect out to a callsign (AX.25 dial) or NET/ROM alias (network route). Returns the
  // new session's SessionInfo. 400 (bad target) / 404 (no port) / 502 / 504 throw Error.
  connectSession: (target: string, portId?: string) => connectSession(target, portId),
  // Disconnect a session by id (portId:peer). Resolves on 204; 404 throws Error.
  disconnectSession: (id: string) => disconnectSession(id),
  // Send one text line into a connected-mode session (CR-terminated on the wire).
  // Resolves on 202; 404 throws Error.
  sendSessionLine: (id: string, line: string) => sendSessionLine(id, line),
  // Connectionless TEST ping. Live mode POSTs /ping and returns the node's PingResult.
  // Mock mode synthesises a believable result so the tool demos with no node. A graceful
  // PingUnavailable fallback remains for the case a node ever 501s this again.
  pingTarget: (station: string, portId: string, count?: number) =>
    pingTarget(station, portId, count),

  // ---- auth + setup + user management (node-auth-ui) ----
  // Whether first-run setup is still required (zero users). Always open (no token).
  setupState: () => setupState(),
  // First-run bootstrap: create the admin + apply identity (+ optional first port).
  // Always open; one-shot (403 once a user exists). Returns the created admin summary
  // (no token — the operator then logs in).
  setup: (payload: SetupRequest) => setup(payload),
  // Password login → JWT. Resolves the LoginResult ({ token, expiresAt, scopes }) on
  // 200; throws Unauthorized on 401 (caller shows an inline error — note this 401 is
  // expected and NOT a session expiry, so login() does not dispatch the logout event).
  login: (username: string, password: string) => login(username, password),
  // Admin-scope user management.
  usersList: () => usersList(),
  userCreate: (username: string, password: string, scope: string) => userCreate(username, password, scope),
  userDelete: (username: string) => userDelete(username),
};

// The connectionless-ping result shape lives in ./types (PingResult); re-exported here so
// callers importing from the API surface keep working.
export type { PingResult } from "./types";

// A node that hasn't implemented /ping returns 501. The ping tool catches this to surface a
// graceful "not available yet" message rather than crash. The endpoint is now implemented,
// but the guard stays so an older/partial node degrades gracefully.
export class PingUnavailable extends Error {
  constructor(message: string) {
    super(message);
    this.name = "PingUnavailable";
  }
}

// Pull a server-supplied { error } message off a non-OK JSON response, or fall back.
async function errorMessage(res: Response, fallback: string): Promise<string> {
  try {
    const body = (await res.json()) as { error?: string };
    return body.error ?? fallback;
  } catch {
    return fallback;
  }
}

// Connect out. Mock mode synthesises a Connected session so the Sessions screen demos the
// flow with no node; live mode POSTs /sessions and returns the server's SessionInfo.
async function connectSession(target: string, portId?: string): Promise<SessionInfo> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 200));
    return {
      id: `${portId ?? "vhf"}:${target}`, portId: portId ?? "vhf", peer: target,
      role: "console", state: "Connected", vs: 0, vr: 0, window: 4,
      uptimeSeconds: 0, bytesIn: 0, bytesOut: 0, lastActivity: "0:00:00",
    };
  }
  const res = await authFetch("/sessions", {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify(portId ? { target, portId } : { target }),
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Connect failed (${res.status}).`));
  return (await res.json()) as SessionInfo;
}

// Disconnect a session by id. Resolves on 204; a 404/other surfaces as Error.
async function disconnectSession(id: string): Promise<void> {
  if (MODE === "mock") { await new Promise((r) => setTimeout(r, 120)); return; }
  const res = await authFetch(`/sessions/${encodeURIComponent(id)}`, { method: "DELETE" });
  if (res.status === 204) return;
  throw new Error(await errorMessage(res, `Disconnect failed (${res.status}).`));
}

// Send one line into a session. Resolves on 202; a 404/other surfaces as Error.
async function sendSessionLine(id: string, line: string): Promise<void> {
  if (MODE === "mock") { await new Promise((r) => setTimeout(r, 80)); return; }
  const res = await authFetch(`/sessions/${encodeURIComponent(id)}/send`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ line }),
  });
  if (res.status === 202) return;
  throw new Error(await errorMessage(res, `Send failed (${res.status}).`));
}

// Connectionless TEST ping. Live mode POSTs /ping and returns the node's PingResult; a 501
// (a node that hasn't implemented TEST ping) still surfaces as PingUnavailable so the tool
// degrades gracefully. Mock mode synthesises a believable result so the tool demos with no
// node — a few fast replies plus one timeout, with the summary stats computed off them.
async function pingTarget(station: string, portId: string, count = 5): Promise<PingResult> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 250));
    return mockPing(portId, count);
  }
  const res = await authFetch("/ping", {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ station, portId, count }),
  });
  if (res.status === 501) {
    throw new PingUnavailable(await errorMessage(res, "AX.25 ping is not available on this node yet."));
  }
  if (!res.ok) throw new Error(await errorMessage(res, `Ping failed (${res.status}).`));
  return (await res.json()) as PingResult;
}

// Synthesise a plausible PingResult for mock mode. A fast link-dn (AXUDP) replies cleanly;
// other ports get a slower RTT and drop one reply mid-run, so the loss/timeout rendering is
// exercised. Summary stats are computed off the non-timed-out replies (lossPct=100 ⇒ 0/0/0).
function mockPing(portId: string, count: number): PingResult {
  const base = portId === "link-dn" ? 42 : portId === "hf-300" ? 2600 : 720;
  const jitter = portId === "link-dn" ? 14 : portId === "hf-300" ? 900 : 280;
  // drop a reply mid-run on the slower ports so a timeout shows in the demo
  const dropAt = portId === "link-dn" ? -1 : Math.min(count, 3);
  const replies: PingReply[] = [];
  for (let seq = 1; seq <= count; seq++) {
    const timeout = seq === dropAt;
    const rttMs = timeout ? null : Math.max(20, Math.round(base + (Math.random() * 2 - 1) * jitter));
    replies.push({ seq, rttMs, timeout });
  }
  const rtts = replies.filter((r) => r.rttMs != null).map((r) => r.rttMs as number);
  const lossPct = Math.round(((count - rtts.length) / count) * 100);
  return rtts.length
    ? {
        replies,
        minMs: Math.min(...rtts),
        avgMs: Math.round(rtts.reduce((a, b) => a + b, 0) / rtts.length),
        maxMs: Math.max(...rtts),
        lossPct,
      }
    : { replies, minMs: 0, avgMs: 0, maxMs: 0, lossPct: 100 };
}

// Shared PUT helper for the config write endpoints: returns the ReconcileResult,
// or throws ConfigRejected on 422 (validation), Error on other failures.
async function writeConfig(
  path: string, method: string, body: string, contentType: string, dryRun: boolean,
): Promise<ReconcileResult> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 80));
    return mockReconcile(!dryRun);
  }
  const res = await authFetch(`${path}?dryRun=${dryRun}`, {
    method,
    headers: { "content-type": contentType, accept: "application/json" },
    body,
  });
  if (res.status === 422) {
    throw new ConfigRejected((await res.json()) as ValidationProblem);
  }
  if (!res.ok) throw new Error(`${path}: ${res.status} ${res.statusText}`);
  return (await res.json()) as ReconcileResult;
}

// A lifecycle action the node can't perform right now (a `restart` while the node is still
// booting → HTTP 409, or a disabled port → 409). The screen catches this to toast the
// affordance rather than crash. (Retained from when `restart` was deferred 501; restart now
// works, but a 409 — booting / disabled — still surfaces through this graceful path.)
export class PortLifecycleUnavailable extends Error {
  constructor(public readonly action: string, message: string) {
    super(message);
    this.name = "PortLifecycleUnavailable";
  }
}

// Add/edit/remove a port through the config-write reconcile path. 404 (unknown id) and
// 422 (rejected candidate) map to ConfigRejected/Error; success returns the
// ReconcileResult. Mirrors writeConfig — the server reuses the same seam.
async function writePort(path: string, method: string, body?: string): Promise<ReconcileResult> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 80));
    return mockReconcile(true);
  }
  const res = await authFetch(path, {
    method,
    headers: body
      ? { "content-type": "application/json", accept: "application/json" }
      : { accept: "application/json" },
    body,
  });
  if (res.status === 422) {
    throw new ConfigRejected((await res.json()) as ValidationProblem);
  }
  if (!res.ok) throw new Error(`${path}: ${res.status} ${res.statusText}`);
  return (await res.json()) as ReconcileResult;
}

// Bring a port up/down/restart. up/down apply via the config seam; restart drives the
// supervisor's serialized RestartPortAsync — all three return the resulting PortStatus. A
// 409 (node still booting, or a disabled port can't be restarted) throws
// PortLifecycleUnavailable so the caller can surface it gracefully rather than crash.
async function portLifecycle(
  id: string, action: "up" | "down" | "restart",
): Promise<PortStatus> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 80));
    const base = mock.PORT_STATUS[id] ?? Object.values(mock.PORT_STATUS)[0];
    if (action === "restart") return { ...base, id };
    return { ...base, id, enabled: action === "up", state: action === "up" ? "up" : "down" };
  }
  const res = await authFetch(`/ports/${encodeURIComponent(id)}/lifecycle`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ action }),
  });
  if (res.status === 409) {
    let message = "This action is not available right now.";
    try { message = ((await res.json()) as { error?: string }).error ?? message; } catch { /* keep default */ }
    throw new PortLifecycleUnavailable(action, message);
  }
  if (!res.ok) throw new Error(`/ports/${id}/lifecycle: ${res.status} ${res.statusText}`);
  return (await res.json()) as PortStatus;
}

// ---- auth + setup + user management ------------------------
// These three (setupState/login/setup) are the always-open bootstrap path: they
// carry no token and are reachable before any account exists. usersList/userCreate/
// userDelete are admin-gated and go through authFetch (token attached; 401→relogin).
//
// Mock mode has no real auth: setupState reports "no setup needed", login returns a
// synthetic admin token, setup is a no-op success, and the user endpoints round-trip
// against the in-memory mock list so the Users screen demos CRUD with no node.

// Whether first-run setup is still required.
async function setupState(): Promise<SetupState> {
  if (MODE === "mock") return { needsSetup: false };
  // Always open — no token, and a 401 here would be unexpected; let it surface.
  const res = await fetch(`${BASE}/setup/state`, { headers: { accept: "application/json" } });
  if (!res.ok) throw new Error(`/setup/state: ${res.status} ${res.statusText}`);
  return (await res.json()) as SetupState;
}

// Password login → JWT. 200 resolves the LoginResult; 401 throws Unauthorized with
// the server's generic message. This is the ONE 401 that must NOT trigger the global
// logout event (there's no session to drop — the user is trying to create one), so it
// uses a bare fetch rather than authFetch.
async function login(username: string, password: string): Promise<LoginResult> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 200));
    if (!username || !password) throw new Unauthorized("Invalid username or password.");
    return { token: "mock.jwt.token", expiresAt: new Date(Date.now() + 36e5).toISOString(), scopes: "admin" };
  }
  const res = await fetch(`${BASE}/auth/login`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ username, password }),
  });
  if (res.status === 401) throw new Unauthorized(await errorMessage(res, "Invalid username or password."));
  if (!res.ok) throw new Error(await errorMessage(res, `Login failed (${res.status}).`));
  return (await res.json()) as LoginResult;
}

// First-run bootstrap. Always open; one-shot (403 once a user exists). Returns the
// created admin summary (no token — the caller sends the operator to login).
async function setup(payload: SetupRequest): Promise<SetupResult> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 200));
    return { username: payload.admin.username, scope: "admin" };
  }
  const res = await fetch(`${BASE}/setup`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify(payload),
  });
  if (res.status === 422) throw new ConfigRejected((await res.json()) as ValidationProblem);
  if (!res.ok) throw new Error(await errorMessage(res, `Setup failed (${res.status}).`));
  return (await res.json()) as SetupResult;
}

// In-memory mock user list, so the Users screen demos create/delete with no node.
const mockUsers: UserSummary[] = [
  { username: "tom", scope: "admin", createdUtc: "2026-01-01T00:00:00Z", lastLoginUtc: "2026-06-08T14:02:00Z" },
];

async function usersList(): Promise<UserSummary[]> {
  if (MODE === "mock") { await new Promise((r) => setTimeout(r, 60)); return structuredClone(mockUsers); }
  const res = await authFetch("/users", { headers: { accept: "application/json" } });
  if (!res.ok) throw new Error(`/users: ${res.status} ${res.statusText}`);
  return (await res.json()) as UserSummary[];
}

async function userCreate(username: string, password: string, scope: string): Promise<UserSummary> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 120));
    const u: UserSummary = { username, scope, createdUtc: new Date().toISOString(), lastLoginUtc: null };
    mockUsers.push(u);
    return structuredClone(u);
  }
  const res = await authFetch("/users", {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ username, password, scope }),
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Create user failed (${res.status}).`));
  return (await res.json()) as UserSummary;
}

async function userDelete(username: string): Promise<void> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 120));
    const i = mockUsers.findIndex((u) => u.username === username);
    if (i >= 0) mockUsers.splice(i, 1);
    return;
  }
  const res = await authFetch(`/users/${encodeURIComponent(username)}`, { method: "DELETE" });
  if (res.status === 204) return;
  throw new Error(await errorMessage(res, `Delete user failed (${res.status}).`));
}

// ---- generic data hook -------------------------------------
export interface Query<T> { data: T | null; loading: boolean; error: string | null; reload: () => void }

export function useQuery<T>(fetcher: () => Promise<T>, deps: unknown[] = []): Query<T> {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let live = true;
    setLoading(true);
    fetcher()
      .then((d) => { if (live) { setData(d); setError(null); } })
      .catch((e) => { if (live) setError(String(e?.message ?? e)); })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tick, ...deps]);
  return { data, loading, error, reload: () => setTick((t) => t + 1) };
}

// ---- the live frame stream (SSE `frame` events) ------------
// onFrame is called per arriving MonitorEvent. Returns an unsubscribe.
export function subscribeFrames(onFrame: (f: MonitorEvent) => void): () => void {
  if (MODE === "mock") {
    const id = setInterval(() => {
      const burst = 1 + Math.floor(Math.random() * 2);
      for (let i = 0; i < burst; i++) onFrame(mock.makeFrame(new Date()));
    }, 700);
    return () => clearInterval(id);
  }
  // EventSource can't set an Authorization header, so the token rides as a query
  // param (?access_token=<jwt>); the backend reads it. Tokenless (auth off) just omits it.
  const es = new EventSource(withTokenParam(`${BASE}/events`));
  const handler = (e: MessageEvent) => {
    try { onFrame(JSON.parse(e.data) as MonitorEvent); } catch { /* ignore malformed */ }
  };
  es.addEventListener("frame", handler as EventListener);
  return () => { es.removeEventListener("frame", handler as EventListener); es.close(); };
}

/** Seed the monitor with a recent backlog (mock only; live seeds from the stream). */
export function seedFrames(n: number): MonitorEvent[] {
  return MODE === "mock" ? mock.seedFrames(n) : [];
}

// ---- the live session-output stream (SSE `output` events) --
// Subscribes to a connected session's received text. Each `output` event's data is a
// JSON-encoded string (a chunk that may contain CR/LF); the backend replays a backlog
// first, then streams live. onChunk is called per decoded chunk. Returns an unsubscribe.
// Mock mode synthesises a fake banner + a line or two on a timer so the console drawer
// demos with no node (mirrors subscribeFrames).
export function subscribeSessionOutput(id: string, onChunk: (text: string) => void): () => void {
  if (MODE === "mock") {
    onChunk(`GB7RDG:GB7RDG} Welcome to the mock node — session ${id}.\r\n`);
    let n = 0;
    const lines = [
      "GB7RDG:GB7RDG} Type HELP for a list of commands.\r\n",
      "GB7RDG:GB7RDG} ",
    ];
    const timer = setInterval(() => {
      onChunk(lines[n % lines.length]);
      n++;
    }, 1500);
    return () => clearInterval(timer);
  }
  // Token as a query param (see subscribeFrames) — EventSource has no header API.
  const es = new EventSource(withTokenParam(`${BASE}/sessions/${encodeURIComponent(id)}/stream`));
  const handler = (e: MessageEvent) => {
    try { onChunk(JSON.parse(e.data) as string); } catch { /* ignore malformed */ }
  };
  es.addEventListener("output", handler as EventListener);
  return () => { es.removeEventListener("output", handler as EventListener); es.close(); };
}

// A small live frames-buffer hook for the monitor (ring buffer, newest first).
export function useFrameStream(cap = 500): {
  frames: MonitorEvent[];
  paused: boolean;
  setPaused: (p: boolean) => void;
  clear: () => void;
} {
  const [frames, setFrames] = useState<MonitorEvent[]>(() => seedFrames(40).reverse());
  const [paused, setPaused] = useState(false);
  const pausedRef = useRef(paused);
  pausedRef.current = paused;
  useEffect(() => {
    return subscribeFrames((f) => {
      if (pausedRef.current) return;
      setFrames((prev) => [f, ...prev].slice(0, cap));
    });
  }, [cap]);
  return { frames, paused, setPaused, clear: () => setFrames([]) };
}
