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
  LinkStats, PeerCapability, MonitorEvent, User, LogLine, ReconcileResult, ValidationProblem,
  RadioStatus, RadioScanResult, DoctorReport, HeadEndScan, HeadEndAdoptRequest,
  PingResult, PingReply, UserSummary, LoginResult, SetupState, SetupRequest, SetupResult,
  WebAuthnCredential, AssertBeginResponse, RegisterCompleteResponse,
  TotpEnrollBeginResponse, TotpEnrollCompleteResponse, TotpEnrollState, NodeApp, AppPackage,
  AppIdentityRequest, AvailableApp, InstallOutcome, TailscaleStatus, SystemInfo,
  TuningStartRequest, TuningSessionInfo, TuningEvent,
} from "./types";
import * as mock from "./mock";
import { passkeysAvailable } from "./secureContext";
import { startRegistration, startAuthentication } from "@simplewebauthn/browser";
import type {
  PublicKeyCredentialCreationOptionsJSON, PublicKeyCredentialRequestOptionsJSON,
} from "@simplewebauthn/browser";
import { UNAUTHORIZED_EVENT, setLogoutRevoker, setFocusRefresher } from "@/app/auth";

const MODE: "mock" | "live" =
  (import.meta.env.VITE_API_MODE as "mock" | "live") ?? "mock";
const BASE = "/api/v1";

export const apiMode = MODE;

// ---- auth glue ---------------------------------------------
// api.ts is plain TS (no React). It reads the persisted token straight from the
// same localStorage slot AuthProvider writes, and signals a 401 by dispatching
// a window event the provider listens for (→ logout → relogin). This keeps the
// fetch path free of a context dependency while staying in lock-step with auth.tsx.
const SESSION_KEY = "pdn.session";

interface PersistedSession {
  token?: string | null;
  refreshToken?: string | null;
  username?: string | null;
  scope?: string | null;
}

function readSession(): PersistedSession {
  try {
    const raw = localStorage.getItem(SESSION_KEY);
    return raw ? (JSON.parse(raw) as PersistedSession) : {};
  } catch {
    return {};
  }
}

/** The current JWT, or null when there's no session (auth off / pre-login / mock). */
function token(): string | null {
  return readSession().token ?? null;
}

/** The current opaque refresh token, or null when there's no session. */
function refreshToken(): string | null {
  return readSession().refreshToken ?? null;
}

/** Update ONLY the access + refresh tokens in the persisted session (preserving the
 *  username/scope AuthProvider also wrote), after a successful silent renew. The React
 *  auth context keeps its own copy; this write keeps localStorage — the source the
 *  fetch path reads — in lock-step so the next request and a page reload both see the
 *  rotated pair. */
function setTokens(accessToken: string, newRefreshToken: string | null): void {
  try {
    const s = readSession();
    s.token = accessToken;
    s.refreshToken = newRefreshToken;
    localStorage.setItem(SESSION_KEY, JSON.stringify(s));
  } catch {
    /* private-mode / quota — non-fatal; the in-flight retry still uses the new token */
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

// --- silent access-token renewal -----------------------------------------------
// On a 401 we attempt ONE refresh-token rotation and retry the original request once.
// A single shared in-flight promise dedupes concurrent 401s WITHIN this tab (a burst of
// parallel requests all expiring at once triggers exactly one /auth/refresh, and they
// all await it) — no stampede. The refresh call itself uses a BARE fetch (never
// authFetch), so a 401 from /auth/refresh can never recurse into another refresh → no
// infinite loop.
//
// ACROSS tabs we serialise with the Web Locks API. The refresh token is one-time-use:
// if two tabs present the same one concurrently the server treats the second as token
// theft and burns the whole token family → every tab is logged out (this was the cause
// of the "logged out every ~hour" bug — the node's auth audit logged a REUSE-DETECTED
// for every silent refresh). The lock lets exactly one tab rotate at a time; a tab that
// loses the race finds, on acquiring the lock, that the token has already been rotated
// in shared localStorage and adopts it rather than replaying the consumed one.
let refreshInFlight: Promise<boolean> | null = null;

/** Rotate the stored refresh token once; on success persist the new pair and resolve
 *  true. Resolves false (does NOT throw) on any failure — the caller then logs out. */
function refreshAccessToken(): Promise<boolean> {
  // Coalesce concurrent callers in THIS tab onto one in-flight rotation, then clear the
  // slot once it settles so a later 401 can refresh again.
  refreshInFlight ??= rotateRefreshTokenAcrossTabs().finally(() => { refreshInFlight = null; });
  return refreshInFlight;
}

function rotateRefreshTokenAcrossTabs(): Promise<boolean> {
  const tokenAtStart = refreshToken();
  if (!tokenAtStart) return Promise.resolve(false);   // nothing to renew with (auth off / pre-login)

  const rotate = async (): Promise<boolean> => {
    // Another tab may have rotated while we waited for the lock: if the stored refresh
    // token changed, it's already fresh in localStorage — adopt it (the retry reads the
    // new JWT) instead of replaying the now-consumed token (→ server theft response).
    const current = refreshToken();
    if (current && current !== tokenAtStart) return true;
    if (!current) return false;
    try {
      // BARE fetch — must not go through authFetch (that would recurse on a 401).
      const res = await fetch(`${BASE}/auth/refresh`, {
        method: "POST",
        headers: { "content-type": "application/json", accept: "application/json" },
        body: JSON.stringify({ refreshToken: current }),
      });
      if (!res.ok) return false;                 // 401/expired/reused → caller logs out
      const next = (await res.json()) as LoginResult;
      if (!next.token) return false;
      setTokens(next.token, next.refreshToken ?? null);
      return true;
    } catch {
      return false;                              // network error → treat as not-renewed
    }
  };

  // Serialise across same-origin tabs when the Web Locks API is available; otherwise
  // fall back to a direct rotation (this tab's in-flight dedup still applies).
  const locks = typeof navigator !== "undefined" ? navigator.locks : undefined;
  if (locks && typeof locks.request === "function") {
    // The DOM typings model the granted-callback as returning its value synchronously
    // (`(lock) => T`), so an async callback infers `T = Promise<boolean>` and request()
    // types as `Promise<Promise<boolean>>`. At runtime the lock manager AWAITS the
    // callback and resolves with its boolean — so the result really is `Promise<boolean>`.
    return locks.request("pdn-auth-refresh", rotate) as unknown as Promise<boolean>;
  }
  return rotate();
}

// --- proactive (tab-focus) refresh ---------------------------------------------
// Tom's exact trigger: flip back to the panel tab after a while and the access token may
// have expired (or be about to). Rather than wait for the next request to 401 (which on a
// slot iframe yields an unrenderable response — see Program.cs OnChallenge), the auth
// provider refreshes ON FOCUS when the token is near/at expiry. The decode is local
// (read the JWT `exp` claim); the refresh reuses the SAME shared, locked, deduped
// refreshAccessToken() the 401 path uses, so a focus-refresh and an in-flight 401-refresh
// can never double-rotate the one-time-use token.

/** Decode a JWT's `exp` (epoch seconds) without verifying the signature — we only read it to
 *  decide whether to PROACTIVELY refresh; the server is still the authority on validity.
 *  Returns null for a non-JWT / unparseable token (mock tokens, auth-off). */
function jwtExpEpochSeconds(jwt: string | null): number | null {
  if (!jwt) return null;
  const parts = jwt.split(".");
  if (parts.length !== 3) return null;            // not a JWS — mock token / opaque
  try {
    // base64url → base64, then decode the payload and read `exp`.
    const b64 = parts[1].replace(/-/g, "+").replace(/_/g, "/");
    const pad = b64.length % 4 === 0 ? "" : "=".repeat(4 - (b64.length % 4));
    const payload = JSON.parse(atob(b64 + pad)) as { exp?: number };
    return typeof payload.exp === "number" ? payload.exp : null;
  } catch {
    return null;
  }
}

/** Whether the stored access token is at/near expiry — within `skewSeconds` of now, or already
 *  past. A token with no readable `exp` (mock / opaque / auth-off) returns false: there is
 *  nothing to proactively renew. */
export function accessTokenExpiringSoon(skewSeconds = 60): boolean {
  const exp = jwtExpEpochSeconds(token());
  if (exp === null) return false;
  return exp * 1000 - Date.now() <= skewSeconds * 1000;
}

/** Proactively renew the access token IF it's near/at expiry and we hold a refresh token.
 *  Shares the single in-flight, cross-tab-locked rotation with the 401 path. Resolves the
 *  freshly-persisted { token, refreshToken } pair on a successful renew, or null when no
 *  renew happened (not near expiry, no refresh token, or the rotation failed). NEVER throws
 *  and NEVER logs out on its own — a failed proactive refresh just resolves null; the next
 *  real request's 401 path makes the logout decision. */
export async function refreshIfExpiringSoon(
  skewSeconds = 60,
): Promise<{ token: string; refreshToken: string | null } | null> {
  if (MODE === "mock") return null;               // no real token lifetime to track
  if (!refreshToken()) return null;               // auth off / pre-login — nothing to renew with
  if (!accessTokenExpiringSoon(skewSeconds)) return null;
  const ok = await refreshAccessToken();
  if (!ok) return null;
  const s = readSession();
  return s.token ? { token: s.token, refreshToken: s.refreshToken ?? null } : null;
}

/** Auth-aware fetch — attaches the bearer token; on a 401 it tries ONE silent refresh
 *  and retries the original request once, only falling back to on401() (→ logout) if
 *  the refresh fails. When auth is OFF the server 200s tokenless, so this transparently
 *  "just works" with token() === null (the 401 path is never hit). */
async function authFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const headers = (init.headers as Record<string, string>) ?? {};
  const res = await fetch(`${BASE}${path}`, { ...init, headers: authHeaders(headers) });
  if (res.status !== 401) return res;

  // 401: attempt a single silent renew. If it works, retry the original request ONCE
  // with the freshly-rotated bearer token; otherwise surface the 401 (→ logout).
  const renewed = await refreshAccessToken();
  if (!renewed) on401();
  const retry = await fetch(`${BASE}${path}`, { ...init, headers: authHeaders(headers) });
  if (retry.status === 401) on401();             // still 401 after a good refresh → give up
  return retry;
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
  // The node's running version + install channel + available-update view (read-gated).
  // The control panel's "About this node" panel shows version + channel; when
  // updateAvailable it banners "vX → vY" with an admin Apply button. The Apply flow
  // (systemUpdate + polling this until the version changes) is the fire-and-acknowledge
  // reconnect — see api.systemUpdate.
  systemInfo: () => get<SystemInfo>("/system/info", () => mock.SYSTEM_INFO),
  // The embedded Tailscale sidecar's live status (read-gated). The "Remote access"
  // config panel polls this while open: state, the assigned .ts.net FQDN, and any
  // pending interactive-login URL.
  tailscaleStatus: () => get<TailscaleStatus>("/system/tailscale", () => mock.TAILSCALE_STATUS),
  linkStats: () => get<LinkStats[]>("/links", () => mock.LINK_STATS),
  // The learned per-peer AX.25 capability cache (read-gated like the other read endpoints):
  // which neighbours speak v2.2 / answer a pre-connect XID. The array may be empty (nothing
  // learned yet / default-off host) → the screen shows an empty state.
  capabilities: () => get<PeerCapability[]>("/capabilities", () => mock.CAPABILITIES),
  // Forget one learned (port, peer) capability by id (port:peer) so the next dial re-probes
  // it (operate scope). Resolves on 204; a 404 (unknown / malformed) throws Error.
  clearCapability: (id: string) => clearCapability(id),
  // ---- radio-control status + scan (read-gated) ----
  // Every port's radio-control attachment + live health (GET /api/v1/radios). The array may be empty
  // (no port has a radio: block) → the Radios panel renders an empty/absent state.
  getRadios: () => get<RadioStatus[]>("/radios", () => mock.RADIOS),
  // One port's radio status (GET /api/v1/ports/{id}/radio). A 404 (unknown port, or a port with no
  // radio block) surfaces the server's message as an Error.
  getPortRadio: (id: string) => getPortRadio(id),
  // Bus discovery scan (GET /api/v1/radios/scan): probe candidate serial ports for attached radios,
  // keyed by CCDI serial (the stable bind key). The PortEditor's "Scan for radios" button drives this.
  scanRadios: () => scanRadios(),
  // ---- split-station head-end fleet scan + adopt (read + operate) ----
  // Discover every head-end instance (config ∪ mDNS), reach through each free device to identify it,
  // and preview the matched TNC↔radio pairs + any duplicate-instance-id conflicts (GET
  // /api/v1/radios/headends, read-gated). The Head-ends screen renders this as the adopt surface.
  getHeadEnds: () => get<HeadEndScan>("/radios/headends", () => mock.HEADEND_SCAN),
  // Adopt a chosen pairing on an instance (POST /api/v1/radios/headends/{instanceId}/adopt,
  // operate-scoped): create ONE matched port through the same validate→preview→apply seam a hand-edit
  // uses. Returns the ReconcileResult; a 422 throws ConfigRejected (declared-reference / co-location
  // rule), a 400 (missing device ids) surfaces its { error } as an Error.
  adoptHeadEnd: (instanceId: string, body: HeadEndAdoptRequest) => adoptHeadEnd(instanceId, body),
  // Capability doctor (GET/POST /api/v1/ports/{id}/doctor). runDoctor(id, false) = the safe,
  // read-scoped, non-transmitting check; runDoctor(id, true) = the admin/audited full check that
  // briefly transmits (POST ?interrupt=true). A 404 (unknown/not-running port) surfaces as an Error.
  runDoctor: (id: string, interrupt = false) => runDoctor(id, interrupt),
  // ---- guided deviation tuning (POST/GET/DELETE /api/v1/ports/{id}/tuning/*) ----
  // Arm a session (admin scope, audited). It TRANSMITS and pauses the port's normal traffic. 404
  // unknown/not-running · 400 not a NinoTNC / no Tait radio / bad role or peer / SDM disabled · 409 a
  // session is already active — each surfaces its { error } as a thrown Error. Subscribe to the live
  // feed with subscribeTune(id, ...).
  startTune: (id: string, body: TuningStartRequest) => startTune(id, body),
  // The tuned operator's "I've adjusted the pot — run the next round" (admin scope, audited). 404 no
  // session · 409 no round is awaiting input (or a meter-role session).
  tuneNext: (id: string) => tuneNext(id),
  // Stop the session and restore the port (admin scope, audited). Resolves true when a session was
  // stopped, false when none was active.
  tuneStop: (id: string) => tuneStop(id),
  // Recent frames (oldest→newest) the monitor seeds with so it isn't empty on open.
  recentFrames: (limit = 250) => get<MonitorEvent[]>(`/monitor/recent?limit=${limit}`, () => mock.seedFrames(limit)),
  users: () => get<User[]>("/users", () => mock.USERS),
  log: () => get<LogLine[]>("/log", () => mock.LOG_TAIL),
  // Registered apps that expose a web UI (read-gated like the other read endpoints).
  // The array may be empty (no apps registered) → the launcher renders an empty state.
  apps: () => get<NodeApp[]>("/apps", () => mock.APPS),

  // ---- app packages (app-platform package management) ----
  // Every app package + inline app the node knows about, with manifest summary +
  // supervisor state (read-gated like the other read endpoints).
  appPackages: () => get<AppPackage[]>("/apps/packages", () => mock.APP_PACKAGES),
  // Enable / disable a package (admin scope). The POST returns the updated entry;
  // a broken package 409s with { error }, an inline app 404s (config-authored).
  appPackageEnable: (id: string) => appPackageAction(id, "enable"),
  appPackageDisable: (id: string) => appPackageAction(id, "disable"),
  // Restart a managed package's service (admin scope). 503 { error } when there is
  // no supervisor; 409 { error } when the package has no restartable service.
  appPackageRestart: (id: string) => appPackageAction(id, "restart"),
  // Set a package's packet identity — command verb / callsign pin / NET/ROM advert (admin
  // scope). Returns the updated entry; a 404 (inline/unknown) or 422 (validation, e.g. a
  // callsign collision) surfaces its { error } as an Error. See docs/app-packages.md.
  appPackageSetIdentity: (id: string, body: AppIdentityRequest) => appPackageSetIdentity(id, body),

  // ---- app catalog: available apps (app-catalog Slice 6b) ----
  // The available-apps list (catalog ⋈ this node's installed state). Read-gated; the
  // array may be empty (no catalog / everything installed) → the section's empty state.
  availableApps: () => get<AvailableApp[]>("/apps/available", () => mock.AVAILABLE_APPS),
  // Install (or update) a catalog app by id (admin scope). The server fetches +
  // sha256-verifies the artifact for this node's architecture, stages it disabled, and
  // returns { ok, id, version }; a 422/404/409 surfaces its { error } as a thrown Error.
  appInstall: (id: string) => appInstall(id),
  // Remove a catalog/upload-installed package (admin scope). 409 { error } when it must
  // be disabled first, or was hand-sideloaded (no install marker); 404 when unknown.
  appUninstall: (id: string) => appUninstall(id),
  // Upload a .pdnapp tarball (admin scope) — the air-gapped install path. Same staging
  // pipeline as install, bytes from a multipart file. 422 { error } on a bad package.
  appUpload: (file: File) => appUpload(file),

  // ---- node self-update (Phase 7) ----
  // Trigger a channel-aware self-update (admin scope, fire-and-acknowledge). The launch
  // returns 202 — the update job restarts the very process that handled the request, so
  // the outcome can't come back in-band; resolves void on a successful dispatch. A 409
  // (unknown channel — won't self-update) / 501 (no launcher) / 503 (launch failed)
  // surfaces the server's message as an Error so the UI can banner it. The UI then polls
  // systemInfo (+ nodeHealthy) until the version changes — see waitForRestart below.
  systemUpdate: () => systemUpdate(),
  // Probe GET /healthz (the unauthenticated liveness endpoint at the app root, NOT under
  // /api/v1). Resolves true when the node answers 200, false otherwise — never throws, so
  // the restart-poll can use it as a "node is back" gate without a try/catch at the call site.
  nodeHealthy: () => nodeHealthy(),

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

  // Operator-initiated (admin) RP-id adoption: point WebAuthn at the Tailscale FQDN so
  // passkeys work remotely over the .ts.net cert. Reads the LIVE config, sets
  // management.auth.webAuthn.relyingPartyId = fqdn and adds https://<fqdn> to
  // allowedOrigins (idempotent), then PUTs it through the same config-write reconcile path
  // (a 422 throws ConfigRejected). NEVER automatic — only this explicit action writes it
  // (changing the RP id invalidates existing passkeys, so it must be deliberate).
  useFqdnForPasskeys: (fqdn: string) => useFqdnForPasskeys(fqdn),

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

  // ---- node command console (browser sysop shell) ----
  // Open a NEW node command console session (the telnet-equivalent sysop shell). Admin-scope;
  // returns the minted id the stream/input/close calls address. 401 → relogin.
  openConsole: () => openConsole(),
  // Feed raw input to a console (forwarded verbatim; the node's line discipline splits it).
  // Resolves on 202; 404 (closed / unknown id) throws Error.
  consoleInput: (id: string, data: string) => consoleInput(id, data),
  // Close + dispose a console session (tears down the running NodeCommandService). 204.
  closeConsole: (id: string) => closeConsole(id),

  // ---- auth + setup + user management (node-auth-ui) ----
  // Whether first-run setup is still required (zero users). Always open (no token).
  setupState: () => setupState(),
  // First-run bootstrap: create the admin + apply identity (+ optional first port).
  // Always open; one-shot (403 once a user exists). Returns the created admin summary
  // (no token — the operator then logs in).
  setup: (payload: SetupRequest) => setup(payload),
  // Password login → JWT + refresh token. Resolves the LoginResult
  // ({ token, expiresAt, scopes, refreshToken }) on 200; throws Unauthorized on 401
  // (caller shows an inline error — note this 401 is expected and NOT a session expiry,
  // so login() does not dispatch the logout event); 429 throws a plain Error (locked out).
  login: (username: string, password: string) => login(username, password),
  // Rotate the stored refresh token → a fresh pair (the explicit surface; authFetch
  // renews silently on its own). 401 throws Unauthorized.
  refresh: () => refresh(),
  // Best-effort server-side logout (revoke the refresh-token family). Never throws.
  logout: () => logoutServerSide(),
  // Admin-scope user management.
  usersList: () => usersList(),
  userCreate: (username: string, password: string, scope: string) => userCreate(username, password, scope),
  userDelete: (username: string) => userDelete(username),

  // ---- WebAuthn / passkeys (node-passkeys) ----
  // Whether passkeys can be exercised in this environment: a real WebAuthn ceremony
  // needs a secure context (HTTPS, or localhost over plain HTTP) + the browser API.
  // In mock mode there is no real ceremony to run, so it reports false (the login
  // passkey button stays disabled — we never FAKE a ceremony).
  webauthnSupported: () => webauthnSupported(),
  // Passwordless sign-in: assert/begin → startAuthentication → assert/complete → the
  // SAME LoginResult a password login returns ({token,expiresAt,scopes,refreshToken}).
  // The optional username scopes the allow-list; omit for a discoverable credential.
  passkeyAssert: (username?: string) => passkeyAssert(username),
  // Enrol a passkey for the signed-in user: register/begin → startRegistration →
  // register/complete. Gated; the username comes from the server's principal.
  passkeyRegister: () => passkeyRegister(),
  // The signed-in user's enrolled passkeys.
  passkeyList: () => passkeyList(),
  // Delete one of the signed-in user's passkeys by its base64url credential id.
  passkeyDelete: (credentialId: string) => passkeyDelete(credentialId),

  // ---- Over-RF sysop code / TOTP (node-sysop-totp) ----
  // The over-RF sysop code is the rolling 6-digit code a sysop presents to elevate a
  // session over a plain packet link (no browser there). A user enrols / inspects /
  // removes their OWN code; the server scopes every call to the authenticated principal.
  // Whether enrolment can be exercised here. In mock mode there is no node to enrol
  // against, so it reports false (the affordance shows an explanatory disabled state — we
  // never fake the begin/verify round trip).
  totpSupported: () => MODE !== "mock",
  // Current over-RF enrolment state for the signed-in user.
  totpState: () => totpState(),
  // Begin enrolment: mint a fresh secret + otpauth URI (shown once; not yet persisted).
  totpEnrollBegin: () => totpEnrollBegin(),
  // Confirm enrolment: the current code from the authenticator app + the callsign to bind.
  totpEnrollComplete: (code: string, callsign: string) => totpEnrollComplete(code, callsign),
  // Remove the signed-in user's over-RF code.
  totpRemove: () => totpRemove(),
};

/** List the node's registered apps that expose a web UI. Thin named alias over
 *  api.apps() (same authFetch/base-URL/mock path as every other read call) — the
 *  Apps launcher imports this directly. */
export const listApps = (): Promise<NodeApp[]> => api.apps();

/** Fired (on window) after any app mutation (enable/disable/install/uninstall) so the
 *  left-nav app list re-fetches without a full browser refresh — the nav fetches
 *  api.apps once on mount, and the Apps manager lives on a different route, so it
 *  signals the nav this way (mirrors SESSION_REFRESHED_EVENT). */
export const APPS_CHANGED_EVENT = "pdn:apps-changed";

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

// Forget one learned (port, peer) capability by id (port:peer). Resolves on 204; a 404/other
// surfaces as Error. Mock mode removes the matching fixture in place (mirroring the live
// mutate-then-refetch flow: a refetch then shows the row gone), like appPackageAction.
async function clearCapability(id: string): Promise<void> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 100));
    const i = mock.CAPABILITIES.findIndex((c) => `${c.portId}:${c.peer}` === id);
    if (i < 0) throw new Error(`Unknown capability '${id}'.`);
    mock.CAPABILITIES.splice(i, 1);
    return;
  }
  const res = await authFetch(`/capabilities/${encodeURIComponent(id)}`, { method: "DELETE" });
  if (res.status === 204) return;
  throw new Error(await errorMessage(res, `Forget failed (${res.status}).`));
}

// One port's radio-control status (GET /api/v1/ports/{id}/radio). Mock mode resolves from the RADIOS
// fixture (throwing when the port has no radio, mirroring the live 404); live mode maps a 404
// (unknown port / no radio block) to an Error the caller can surface.
async function getPortRadio(id: string): Promise<RadioStatus> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 60));
    const found = mock.RADIOS.find((x) => x.portId === id);
    if (!found) throw new Error(`No radio attached to port '${id}'.`);
    return structuredClone(found);
  }
  const res = await authFetch(`/ports/${encodeURIComponent(id)}/radio`, { headers: { accept: "application/json" } });
  if (res.status === 404) throw new Error(await errorMessage(res, `No radio for port '${id}'.`));
  if (!res.ok) throw new Error(`/ports/${id}/radio: ${res.status} ${res.statusText}`);
  return (await res.json()) as RadioStatus;
}

// Bus discovery scan (GET /api/v1/radios/scan). Mock mode returns the RADIO_SCAN fixture after a short
// delay so the PortEditor's scan spinner is exercised with no node; live mode fetches the scan rows.
async function scanRadios(): Promise<RadioScanResult[]> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 350));
    return structuredClone(mock.RADIO_SCAN);
  }
  const res = await authFetch("/radios/scan", { headers: { accept: "application/json" } });
  if (!res.ok) throw new Error(await errorMessage(res, `Radio scan failed (${res.status}).`));
  return (await res.json()) as RadioScanResult[];
}

// Adopt a head-end pairing → create one matched port through the config-write seam. Mock mode returns
// a synthetic reconcile so the surface demos with no node; live mode POSTs the chosen device ids and
// maps a 422 (validation — declared-reference / co-location pairing rule) to ConfigRejected and any
// other failure (400 missing ids, etc.) to a thrown Error carrying the server's { error }. Mirrors
// writePort — the adopt endpoint reuses the identical validate→preview→apply seam.
async function adoptHeadEnd(instanceId: string, body: HeadEndAdoptRequest): Promise<ReconcileResult> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 120));
    const portId = (body.portId?.trim() || instanceId);
    return {
      valid: true,
      live: [{ path: `ports.${portId}`, impact: "port-restart", summary: `Head-end port ${portId} created (${body.tncDeviceId} + ${body.radioDeviceId}).` }],
      portRestart: [], nodeReset: [], applied: true,
    };
  }
  const res = await authFetch(`/radios/headends/${encodeURIComponent(instanceId)}/adopt`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify(body),
  });
  if (res.status === 422) {
    throw new ConfigRejected((await res.json()) as ValidationProblem);
  }
  if (!res.ok) throw new Error(await errorMessage(res, `Could not adopt on '${instanceId}' (${res.status}).`));
  return (await res.json()) as ReconcileResult;
}

// Capability doctor. The SAFE form is a read-scoped GET that never transmits; the FULL form is an
// admin-scoped, audited POST ?interrupt=true that briefly keys the transmitter. Mock mode
// synthesises a believable report (so the surface renders with no node); live mode hits the endpoint
// and maps a 404 (unknown / not-running port) to an Error the caller can surface.
async function runDoctor(id: string, interrupt: boolean): Promise<DoctorReport> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, interrupt ? 400 : 120));
    return structuredClone(mock.doctorReport(id, interrupt));
  }
  const path = `/ports/${encodeURIComponent(id)}/doctor`;
  const res = interrupt
    ? await authFetch(`${path}?interrupt=true`, { method: "POST", headers: { accept: "application/json" } })
    : await authFetch(path, { headers: { accept: "application/json" } });
  if (res.status === 404) throw new Error(await errorMessage(res, `Port '${id}' is not running.`));
  if (!res.ok) throw new Error(`${path}: ${res.status} ${res.statusText}`);
  return (await res.json()) as DoctorReport;
}

// ---- guided deviation tuning -----------------------------------
// Arm a session on a port. Mock mode returns a believable armed session (the SSE feed is faked in
// subscribeTune) so the surface renders with no node; live mode POSTs and maps the server's { error }
// (400/404/409) to a thrown Error the caller surfaces.
async function startTune(id: string, body: TuningStartRequest): Promise<TuningSessionInfo> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 200));
    return mock.tuneSession(id, body);
  }
  const res = await authFetch(`/ports/${encodeURIComponent(id)}/tuning/session`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Could not start tuning on '${id}' (${res.status}).`));
  return (await res.json()) as TuningSessionInfo;
}

// The tuned operator's "next round" signal. Resolves on 200; a 404/409 surfaces its { error }.
async function tuneNext(id: string): Promise<void> {
  if (MODE === "mock") { mock.tuneAdvance(id); return; }
  const res = await authFetch(`/ports/${encodeURIComponent(id)}/tuning/next`, {
    method: "POST", headers: { accept: "application/json" },
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Next round failed (${res.status}).`));
}

// Stop a session + restore the port. Resolves true when one was stopped, false (404) when none.
async function tuneStop(id: string): Promise<boolean> {
  if (MODE === "mock") { await new Promise((r) => setTimeout(r, 100)); return true; }
  const res = await authFetch(`/ports/${encodeURIComponent(id)}/tuning/session`, {
    method: "DELETE", headers: { accept: "application/json" },
  });
  if (res.status === 404) return false;
  if (!res.ok) throw new Error(await errorMessage(res, `Stop failed (${res.status}).`));
  return true;
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

// ---- node command console -----------------------------------
// Open a node command console session. Live mode POSTs /console and returns the minted id; mock
// mode synthesises one so the screen demos with no node (the stream is faked in
// subscribeConsoleOutput). A 401 → relogin via authFetch; any other failure surfaces as Error.
async function openConsole(): Promise<string> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 120));
    return "console:mock";
  }
  const res = await authFetch("/console", {
    method: "POST",
    headers: { accept: "application/json" },
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Could not open the console (${res.status}).`));
  return ((await res.json()) as { id: string }).id;
}

// Feed raw input to a console. Resolves on 202; 404 (closed / unknown) or other surfaces as Error.
async function consoleInput(id: string, data: string): Promise<void> {
  if (MODE === "mock") { await new Promise((r) => setTimeout(r, 20)); return; }
  const res = await authFetch(`/console/${encodeURIComponent(id)}/input`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ data }),
  });
  if (res.status === 202) return;
  throw new Error(await errorMessage(res, `Console input failed (${res.status}).`));
}

// Close + dispose a console session. Resolves on 204 (the node tears the NodeCommandService down).
// Best-effort from the caller's view (a failed close still happens server-side on disconnect).
async function closeConsole(id: string): Promise<void> {
  if (MODE === "mock") { await new Promise((r) => setTimeout(r, 20)); return; }
  try {
    await authFetch(`/console/${encodeURIComponent(id)}`, { method: "DELETE" });
  } catch {
    /* close is best-effort — the server also tears the session down on SSE disconnect */
  }
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

// Enable/disable/restart an app package. Live mode POSTs the action and returns the
// server's updated AppPackage entry; any failure (404 unknown id / inline app, 409
// broken package or no restartable service, 503 no supervisor, 422 validation)
// surfaces the server's { error } message as an Error so the screen can banner it.
// Mock mode mutates the in-memory fixture list in place — a refetch then shows the
// new state, mirroring the live mutate-then-reload flow.
async function appPackageAction(
  id: string, action: "enable" | "disable" | "restart",
): Promise<AppPackage> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 120));
    const p = mock.APP_PACKAGES.find((x) => x.id === id);
    // Inline apps are config-authored — the live API answers 404 for them too.
    if (!p || p.source === "inline") throw new Error(`Unknown package '${id}'.`);
    if (action === "enable") {
      if (p.error) throw new Error(p.error); // broken → live 409 { error }
      p.enabled = true;
      if (p.service === "managed") { p.state = "Running"; p.pid = 20000 + Math.floor(Math.random() * 9999); p.detail = null; }
    } else if (action === "disable") {
      p.enabled = false;
      if (p.service === "managed") { p.state = "Stopped"; p.pid = null; p.detail = null; }
    } else {
      if (p.service !== "managed") throw new Error(`'${id}' has no managed service to restart.`);
      p.state = "Running"; p.pid = 20000 + Math.floor(Math.random() * 9999); p.detail = null;
    }
    return structuredClone(p);
  }
  const res = await authFetch(`/apps/packages/${encodeURIComponent(id)}/${action}`, {
    method: "POST",
    headers: { accept: "application/json" },
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Could not ${action} '${id}' (${res.status}).`));
  return (await res.json()) as AppPackage;
}

// Set a package's packet identity (command verb / callsign pin / NET/ROM advert). Live mode
// PUTs the identity body and returns the server's updated AppPackage; a 404 (inline/unknown
// id) or 422 (validation — e.g. a callsign/alias collision) surfaces its { error } as an
// Error. Mock mode patches the in-memory fixture in place so a refetch shows the new identity.
async function appPackageSetIdentity(id: string, body: AppIdentityRequest): Promise<AppPackage> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 120));
    const p = mock.APP_PACKAGES.find((x) => x.id === id);
    if (!p || p.source === "inline") throw new Error(`Unknown package '${id}'.`);
    p.command = body.command?.trim() || null;
    p.callsign = body.callsign?.trim() || p.callsign; // a blank pin falls back to auto-assign (kept as-is in the mock)
    p.netromAlias = body.netromAlias?.trim() || null;
    p.netromQuality = p.netromAlias ? (body.netromQuality ?? 255) : null;
    return structuredClone(p);
  }
  const res = await authFetch(`/apps/packages/${encodeURIComponent(id)}/identity`, {
    method: "PUT",
    headers: { accept: "application/json", "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Could not update '${id}' identity (${res.status}).`));
  return (await res.json()) as AppPackage;
}

// Install (or update) a catalog app. Live mode POSTs the install endpoint and returns the
// server's { ok, id, version }; any failure (404 unknown id, 409 conflict, 422 fetch/
// verify/stage) surfaces the server's { error } as an Error so the screen can banner it
// (mirrors appPackageAction's error extraction). Mock mode returns a synthetic success.
async function appInstall(id: string): Promise<InstallOutcome> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 150));
    return { ok: true, id, version: "(mock)" };
  }
  const res = await authFetch(`/apps/available/${encodeURIComponent(id)}/install`, {
    method: "POST",
    headers: { accept: "application/json" },
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Could not install '${id}' (${res.status}).`));
  return (await res.json()) as InstallOutcome;
}

// Uninstall a catalog/upload-installed package. Live mode POSTs the uninstall endpoint and
// returns the server's { ok, id }; a 409 (must disable first / hand-sideloaded) or 404
// surfaces its { error } as an Error. Mock mode returns a synthetic success.
async function appUninstall(id: string): Promise<InstallOutcome> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 150));
    return { ok: true, id };
  }
  const res = await authFetch(`/apps/packages/${encodeURIComponent(id)}/uninstall`, {
    method: "POST",
    headers: { accept: "application/json" },
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Could not uninstall '${id}' (${res.status}).`));
  return (await res.json()) as InstallOutcome;
}

// Upload a .pdnapp tarball as multipart/form-data (field name `file`). We DON'T set a
// content-type header — the browser fills it in with the multipart boundary; forcing
// application/json (as the JSON POSTs do) would corrupt the body. Live mode returns the
// server's { ok, id, version }; a 422 surfaces its { error } as an Error. Mock returns a
// synthetic success keyed by the file name.
async function appUpload(file: File): Promise<InstallOutcome> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 200));
    return { ok: true, id: file.name };
  }
  const form = new FormData();
  form.append("file", file);
  const res = await authFetch("/apps/packages/upload", {
    method: "POST",
    headers: { accept: "application/json" },
    body: form,
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Upload failed (${res.status}).`));
  return (await res.json()) as InstallOutcome;
}

// Trigger a channel-aware self-update. Live mode POSTs /system/update; a 202 is the
// fire-and-acknowledge success (the node will restart) → resolve void. Any non-2xx
// (409 unknown channel / 501 no launcher / 503 launch failed) surfaces the server's
// problem message as an Error so the caller can banner it. Mock mode resolves after a
// short delay so the busy/spinner state is exercised with no node.
async function systemUpdate(): Promise<void> {
  if (MODE === "mock") { await new Promise((r) => setTimeout(r, 150)); return; }
  const res = await authFetch("/system/update", {
    method: "POST",
    headers: { accept: "application/json" },
  });
  // 202 Accepted is the only success — a detached update job was dispatched.
  if (res.status === 202) return;
  // RFC7807 problem responses carry the human message under `detail`, not `error`.
  let message = `Update could not be started (${res.status}).`;
  try {
    const body = (await res.json()) as { detail?: string; error?: string };
    message = body.detail ?? body.error ?? message;
  } catch { /* keep the default */ }
  throw new Error(message);
}

// Liveness probe: GET /healthz at the app root (NOT under /api/v1, and never with a
// token — it's the unauthenticated health endpoint). Resolves true on a 200, false on
// anything else or a network error (the node still restarting). Mock mode reports true.
async function nodeHealthy(): Promise<boolean> {
  if (MODE === "mock") return true;
  try {
    const res = await fetch("/healthz", { headers: { accept: "application/json" }, cache: "no-store" });
    return res.ok;
  } catch {
    return false;
  }
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

// Adopt the Tailscale FQDN as the WebAuthn relying-party id: read the live config, set
// management.auth.webAuthn.relyingPartyId = fqdn and add https://<fqdn> to allowedOrigins
// (idempotent — a re-run is a no-op), then PUT it. Returns the ReconcileResult; a 422 throws
// ConfigRejected.
async function useFqdnForPasskeys(fqdn: string): Promise<ReconcileResult> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 80));
    return mockReconcile(true);
  }
  const live = await get<NodeConfig>("/config", () => mock.NODE_CONFIG);
  const origin = `https://${fqdn}`;
  const webAuthn = live.management.auth.webAuthn;
  const next: NodeConfig = {
    ...live,
    management: {
      ...live.management,
      auth: {
        ...live.management.auth,
        webAuthn: {
          ...webAuthn,
          relyingPartyId: fqdn,
          allowedOrigins: webAuthn.allowedOrigins.includes(origin)
            ? webAuthn.allowedOrigins
            : [...webAuthn.allowedOrigins, origin],
        },
      },
    },
  };
  return writeConfig("/config", "PUT", JSON.stringify(next), "application/json", false);
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
    return mockTokens("admin", username);
  }
  const res = await fetch(`${BASE}/auth/login`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ username, password }),
  });
  if (res.status === 429) throw new Error(await errorMessage(res, "Too many login attempts. Try again later."));
  if (res.status === 401) throw new Unauthorized(await errorMessage(res, "Invalid username or password."));
  if (!res.ok) throw new Error(await errorMessage(res, `Login failed (${res.status}).`));
  return (await res.json()) as LoginResult;
}

// Synthesise a fresh token pair (mock mode) — a unique refresh token each call so a
// mock /auth/refresh visibly "rotates" the pair, mirroring the live one-time-use shape.
function mockTokens(scope: string, username: string): LoginResult {
  return {
    token: "mock.jwt." + Math.random().toString(36).slice(2),
    expiresAt: new Date(Date.now() + 36e5).toISOString(),
    scopes: scope,
    refreshToken: "mock.rt." + Math.random().toString(36).slice(2),
    username,
  };
}

// Rotate the stored refresh token → a fresh token pair. Live mode POSTs /auth/refresh
// (a bare fetch — the access token may have expired); mock mode returns a fresh synthetic
// pair. 401 → Unauthorized (the caller logs out). NOTE: authFetch's own silent-renew uses
// refreshAccessToken() directly, not this — this is the explicit API surface for callers.
async function refresh(): Promise<LoginResult> {
  if (MODE === "mock") {
    await new Promise((r) => setTimeout(r, 80));
    return mockTokens("admin", "tom");
  }
  const rt = refreshToken();
  const res = await fetch(`${BASE}/auth/refresh`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ refreshToken: rt }),
  });
  if (res.status === 401) throw new Unauthorized(await errorMessage(res, "Your session has expired."));
  if (!res.ok) throw new Error(await errorMessage(res, `Refresh failed (${res.status}).`));
  return (await res.json()) as LoginResult;
}

// Best-effort server-side logout: revoke the stored refresh token's family. Fire-and-
// forget from the caller's perspective (a failed/absent revoke must never block the
// local logout). Mock mode is a no-op. Registered as the AuthProvider's logout revoker.
async function logoutServerSide(): Promise<void> {
  if (MODE === "mock") return;
  const rt = refreshToken();
  if (!rt) return;
  try {
    await fetch(`${BASE}/auth/logout`, {
      method: "POST",
      headers: { "content-type": "application/json", accept: "application/json" },
      body: JSON.stringify({ refreshToken: rt }),
    });
  } catch {
    /* logout is best-effort — a network failure still clears the local session */
  }
}

// Wire the AuthProvider's logout() to the best-effort server revoke (read the refresh
// token from localStorage + POST /auth/logout). A registration hook avoids an
// auth.tsx → api.ts import cycle. Fired-and-forgotten so logout never blocks on it.
setLogoutRevoker(() => { void logoutServerSide(); });

// Wire the AuthProvider's tab-focus proactive refresh to api.ts's near-expiry renew (same
// registration-hook pattern, same no-import-cycle reason). The provider calls this when the
// tab regains focus; it renews the access token only when it's near/at expiry, reusing the
// single locked/deduped rotation, and resolves the freshly-persisted pair (or null).
setFocusRefresher(() => refreshIfExpiringSoon());

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

// ---- WebAuthn / passkeys -----------------------------------
// A real WebAuthn ceremony only runs in a "potentially trustworthy" origin (HTTPS, or
// localhost over plain HTTP) with the browser credentials API present. We probe that
// rather than the API mode, so the login passkey button lights up exactly when a
// ceremony could succeed. In mock mode there is no node to talk to, so it's always
// false (we never fake a ceremony — see CLAUDE-task scope).
function webauthnSupported(): boolean {
  if (MODE === "mock") return false;
  // The platform secure-context probe (lib/secureContext) is the single source of
  // truth for "could a ceremony run?"; mock mode layers on top (no node to talk to).
  return passkeysAvailable();
}

// Passwordless sign-in. assert/begin (always open) hands back a session id + the
// WebAuthn request options; startAuthentication drives the authenticator; assert/complete
// verifies and returns the SAME token pair a password login does. A user gesture must
// have triggered this (the browser requires it for credentials.get).
async function passkeyAssert(username?: string): Promise<LoginResult> {
  if (MODE === "mock") throw new Error("Passkeys are not available in mock mode.");
  // 1) begin — always open (no token; this IS the login).
  const beginRes = await fetch(`${BASE}/auth/webauthn/assert/begin`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify(username ? { username } : {}),
  });
  if (!beginRes.ok) throw new Error(await errorMessage(beginRes, `Passkey sign-in failed (${beginRes.status}).`));
  const begin = (await beginRes.json()) as AssertBeginResponse;

  // 2) drive the authenticator (navigator.credentials.get) over the server's options.
  const assertion = await startAuthentication({
    optionsJSON: begin.options as PublicKeyCredentialRequestOptionsJSON,
  });

  // 3) complete — verify + issue the token pair (401 on any failure: unknown credential,
  //    bad signature, clone-detected counter regression — all generic, no oracle).
  const completeRes = await fetch(`${BASE}/auth/webauthn/assert/complete`, {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ sessionId: begin.sessionId, response: assertion }),
  });
  if (completeRes.status === 401) throw new Unauthorized(await errorMessage(completeRes, "Passkey sign-in failed."));
  if (!completeRes.ok) throw new Error(await errorMessage(completeRes, `Passkey sign-in failed (${completeRes.status}).`));
  return (await completeRes.json()) as LoginResult;
}

// Enrol a passkey for the signed-in user. Both halves are gated (authFetch attaches the
// bearer token); the username is taken from the server principal, never sent.
async function passkeyRegister(): Promise<RegisterCompleteResponse> {
  if (MODE === "mock") throw new Error("Passkeys are not available in mock mode.");
  const beginRes = await authFetch("/auth/webauthn/register/begin", {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: "{}",
  });
  if (!beginRes.ok) throw new Error(await errorMessage(beginRes, `Could not start passkey enrolment (${beginRes.status}).`));
  const options = (await beginRes.json()) as PublicKeyCredentialCreationOptionsJSON;

  const attestation = await startRegistration({ optionsJSON: options });

  const completeRes = await authFetch("/auth/webauthn/register/complete", {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ response: attestation }),
  });
  if (!completeRes.ok) throw new Error(await errorMessage(completeRes, `Passkey enrolment failed (${completeRes.status}).`));
  return (await completeRes.json()) as RegisterCompleteResponse;
}

async function passkeyList(): Promise<WebAuthnCredential[]> {
  if (MODE === "mock") return [];
  const res = await authFetch("/auth/webauthn/credentials", { headers: { accept: "application/json" } });
  if (!res.ok) throw new Error(`/auth/webauthn/credentials: ${res.status} ${res.statusText}`);
  return (await res.json()) as WebAuthnCredential[];
}

async function passkeyDelete(credentialId: string): Promise<void> {
  if (MODE === "mock") return;
  const res = await authFetch(`/auth/webauthn/credentials/${encodeURIComponent(credentialId)}`, { method: "DELETE" });
  if (res.status === 204) return;
  throw new Error(await errorMessage(res, `Delete passkey failed (${res.status}).`));
}

// ---- Over-RF sysop code / TOTP -----------------------------
// All four endpoints are gated (authFetch attaches the bearer token); the username is
// taken from the server principal, never sent. Mock mode has no node to enrol against, so
// totpState reports "not enrolled" and begin/complete/remove are guarded by the UI (which
// only enables them when totpSupported() is true) — we never fake the verify round trip.

// Whether the signed-in user has an over-RF code enrolled (+ the bound callsign).
async function totpState(): Promise<TotpEnrollState> {
  if (MODE === "mock") return { enrolled: false, callsign: null };
  const res = await authFetch("/auth/totp/enroll", { headers: { accept: "application/json" } });
  if (!res.ok) throw new Error(`/auth/totp/enroll: ${res.status} ${res.statusText}`);
  return (await res.json()) as TotpEnrollState;
}

// Begin enrolment → the server mints a secret + otpauth URI and stashes the secret pending
// confirmation. Returns both to show (QR + manual key); nothing is persisted yet.
async function totpEnrollBegin(): Promise<TotpEnrollBeginResponse> {
  if (MODE === "mock") throw new Error("Over-RF enrolment is not available in mock mode.");
  const res = await authFetch("/auth/totp/enroll/begin", {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: "{}",
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Could not start enrolment (${res.status}).`));
  return (await res.json()) as TotpEnrollBeginResponse;
}

// Confirm enrolment with the current code + the callsign to bind. 400 (bad code / no
// pending) and 409 (callsign in use) surface the server's message.
async function totpEnrollComplete(code: string, callsign: string): Promise<TotpEnrollCompleteResponse> {
  if (MODE === "mock") throw new Error("Over-RF enrolment is not available in mock mode.");
  const res = await authFetch("/auth/totp/enroll/complete", {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json" },
    body: JSON.stringify({ code, callsign }),
  });
  if (!res.ok) throw new Error(await errorMessage(res, `Enrolment failed (${res.status}).`));
  return (await res.json()) as TotpEnrollCompleteResponse;
}

// Remove the signed-in user's over-RF code. Resolves on 204.
async function totpRemove(): Promise<void> {
  if (MODE === "mock") return;
  const res = await authFetch("/auth/totp/enroll", { method: "DELETE" });
  if (res.status === 204) return;
  throw new Error(await errorMessage(res, `Could not remove the code (${res.status}).`));
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

// ---- the live node-command-console output stream (SSE `output`) --
// Subscribes to a node command console session's output (same SSE contract as the session stream:
// JSON-encoded chunks, replayed-backlog-then-live, `output` events). onChunk is called per decoded
// chunk; onError fires once if the stream errors/closes (so the screen can show a "closed" state).
// Returns an unsubscribe. Mock mode synthesises a banner + prompt + an echo on input so the
// terminal demos with no node (input is fed via the returned `feed` for the mock echo).
export function subscribeConsoleOutput(
  id: string,
  onChunk: (text: string) => void,
  onError?: () => void,
): () => void {
  if (MODE === "mock") {
    onChunk("Welcome to LONDON (M0LTE-1)  [Packet.NET mock]\r\nM0LTE-1> ");
    return () => {};
  }
  // Token as a query param (see subscribeFrames) — EventSource has no header API.
  const es = new EventSource(withTokenParam(`${BASE}/console/${encodeURIComponent(id)}/stream`));
  const handler = (e: MessageEvent) => {
    try { onChunk(JSON.parse(e.data) as string); } catch { /* ignore malformed */ }
  };
  es.addEventListener("output", handler as EventListener);
  // EventSource fires `error` on a transient drop (it auto-reconnects) AND on a terminal close.
  // The node ends the response when the console exits (Bye) or is closed; surface that to the UI.
  es.addEventListener("error", () => {
    if (es.readyState === EventSource.CLOSED) onError?.();
  });
  return () => { es.removeEventListener("output", handler as EventListener); es.close(); };
}

// ---- the live tuning-session stream (SSE `tuning` events) --
// Subscribes to a port's guided-deviation-tuning feed: each round ({ burstIndex, decoded/total,
// levelDb?, advice, note }) plus lifecycle transitions ({ kind: armed | peer-connected |
// awaiting-adjustment | ended | error }). onEvent is called per event; onError fires once when the
// stream terminally closes (the session ended). Returns an unsubscribe. Mock mode scripts a
// converging session on a timer, gated by the "next round" signal (mirrors the real gate).
export function subscribeTune(
  id: string,
  onEvent: (e: TuningEvent) => void,
  onError?: () => void,
): () => void {
  if (MODE === "mock") {
    return mock.driveTuneStream(id, onEvent, onError);
  }
  // Token as a query param (see subscribeFrames) — EventSource has no header API.
  const es = new EventSource(withTokenParam(`${BASE}/ports/${encodeURIComponent(id)}/tuning/events`));
  const handler = (e: MessageEvent) => {
    try { onEvent(JSON.parse(e.data) as TuningEvent); } catch { /* ignore malformed */ }
  };
  es.addEventListener("tuning", handler as EventListener);
  es.addEventListener("error", () => {
    if (es.readyState === EventSource.CLOSED) onError?.();
  });
  return () => { es.removeEventListener("tuning", handler as EventListener); es.close(); };
}

// A small live frames-buffer hook for the monitor (ring buffer, newest first).
export function useFrameStream(cap = 500): {
  frames: MonitorEvent[];
  paused: boolean;
  setPaused: (p: boolean) => void;
  clear: () => void;
} {
  const [frames, setFrames] = useState<MonitorEvent[]>([]);
  const [paused, setPaused] = useState(false);
  const pausedRef = useRef(paused);
  pausedRef.current = paused;
  // Bootstrap with recent history so the table isn't empty on open: fetch the ring
  // (oldest→newest), flip to newest-first, and slot it UNDER any live frames that
  // already arrived during the fetch (deduped by seq). If the fetch fails the monitor
  // still works live-only.
  useEffect(() => {
    let alive = true;
    api.recentFrames(cap).then((recent) => {
      if (!alive) return;
      setFrames((prev) => {
        const seen = new Set(prev.map((f) => f.seq));
        const history = [...recent].reverse().filter((f) => !seen.has(f.seq));
        return [...prev, ...history].slice(0, cap);
      });
    }).catch(() => { /* live-only fallback */ });
    return () => { alive = false; };
  }, [cap]);
  // Live stream; dedupe the bootstrap/live overlap by seq.
  useEffect(() => {
    return subscribeFrames((f) => {
      if (pausedRef.current) return;
      setFrames((prev) => (prev.some((p) => p.seq === f.seq) ? prev : [f, ...prev].slice(0, cap)));
    });
  }, [cap]);
  return { frames, paused, setPaused, clear: () => setFrames([]) };
}
