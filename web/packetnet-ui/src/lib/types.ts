// ============================================================
// pdn domain types — mirror the real records (docs/node-ui-design.md §6):
//   Packet.Node.Core.Configuration (NodeConfig tree),
//   Packet.NetRom.Routing (routing model),
//   the new Slice-3 read models (NodeStatus/PortStatus/SessionInfo/LinkStats),
//   the MonitorEvent derived from Ax25Listener.FrameTraced.
// Plus the operator-facing helper models introduced by the design (profiles,
// nino modes, beacons, help copy) — these are UI-layer concepts.
// ============================================================

// ---- 6.1 NodeConfig tree -----------------------------------
export type TransportKind = "kiss-tcp" | "serial-kiss" | "nino-tnc" | "axudp";

export interface KissTcpTransport { kind: "kiss-tcp"; host: string; port: number }
export interface SerialKissTransport { kind: "serial-kiss"; device: string; baud: number }
export interface NinoTncTransport { kind: "nino-tnc"; device: string; baud: number; mode: number }
export interface AxudpTransport { kind: "axudp"; host: string; port: number; localPort: number }
export type TransportConfig =
  | KissTcpTransport
  | SerialKissTransport
  | NinoTncTransport
  | AxudpTransport;

export interface Ax25PortParams {
  t1Ms?: number; t2Ms?: number; t3Ms?: number;
  n2?: number; windowSize?: number; maxCachedPeers?: number;
}
export interface KissParams {
  txDelay?: number; persistence?: number; slotTime?: number; txTail?: number;
}
export interface PortConfig {
  id: string;
  enabled: boolean;
  transport: TransportConfig;
  profile: string | null;
  ax25: Ax25PortParams | null;
  kiss: KissParams | null;
  beacon: PortBeacon | null;
}
// The system-default ID beacon (Packet.Node.Core.Configuration.BeaconConfig).
// enabled defaults false (a node that never beaconed keeps not beaconing).
export interface BeaconConfig { enabled: boolean; intervalMinutes: number; text: string }
export interface IdentityConfig { callsign: string; alias?: string | null; grid?: string | null }
export interface ServicesConfig { banner: string; prompt: string }
export interface TelnetConfig { enabled: boolean; bind: string; port: number }
export interface HttpConfig { bind: string; port: number }
export interface HttpsConfig {
  enabled: boolean;
  bind: string;
  port: number;
  certificatePath: string | null;
  certificatePassword: string | null;
  generateSelfSignedOnMissing: boolean;
}
export interface ManagementConfig { telnet: TelnetConfig; http: HttpConfig; https: HttpsConfig }
export interface Inp3Config {
  enabled: boolean; preferInp3Routes: boolean;
  l3RttInterval: number; l3RttResetWindow: number;
  rifInterval: number; positiveDebounce: number;
}
export type NetRomForwardMode = "PerFlow" | "Single";
export interface NetRomConfig {
  enabled: boolean; broadcast: boolean; connect: boolean;
  forward: boolean; forwardMode: NetRomForwardMode;
  alias?: string | null;
  defaultNeighbourQuality?: number; minQuality?: number;
  obsoleteInitial?: number; obsoleteMinimum?: number; sweepIntervalSeconds?: number;
  window?: number; transportTimeoutSeconds?: number; transportRetries?: number; timeToLive?: number;
  inp3: Inp3Config;
}
export interface NodeConfig {
  schemaVersion: number;
  identity: IdentityConfig;
  ports: PortConfig[];
  services: ServicesConfig;
  management: ManagementConfig;
  netRom: NetRomConfig;
  beacon: BeaconConfig;
}

// which edits are hot vs disruptive
export type ApplyImpact = "live" | "port-restart" | "node-reset";

// ---- config-write: reconcile preview + validation (Slice-3 step 2) ----
export interface ReconcileChange { path: string; impact: ApplyImpact; summary: string }
export interface ReconcileResult {
  valid: boolean;
  live: ReconcileChange[];
  portRestart: ReconcileChange[];
  nodeReset: ReconcileChange[];
  applied: boolean;
}
export interface ConfigValidationError { path: string; message: string }
export interface ValidationProblem { errors: ConfigValidationError[] }

// ---- 6.2 NET/ROM routing snapshot --------------------------
export interface Inp3RouteMetric { targetTimeMs: number; hopCount: number }
export interface NetRomRoute {
  neighbour: string; quality: number; obsolescence: number; inp3: Inp3RouteMetric | null;
}
export interface NetRomDestination {
  destination: string; alias: string; bestRoute: number; routes: NetRomRoute[];
}
export interface NetRomNeighbour {
  neighbour: string; alias: string; portId: string; pathQuality: number; lastHeard: string;
}
export interface NetRomRoutingSnapshot {
  generatedAt: string;
  neighbours: NetRomNeighbour[];
  destinations: NetRomDestination[];
}

// ---- 6.4 read models ---------------------------------------
export interface NodeStatus {
  callsign: string; alias: string; grid: string;
  version: string; uptimeSeconds: number;
  portsUp: number; portsTotal: number; sessionCount: number;
  netrom: { neighbours: number; destinations: number; inp3Enabled: boolean };
}
export type PortState = "up" | "down" | "faulted";
export interface PortStatus {
  id: string; enabled: boolean; state: PortState;
  sessionCount: number; lastError: string | null;
  framesIn: number; framesOut: number;
}
export type SessionRole = "console" | "interlink" | "bridge";
export interface SessionInfo {
  id: string; portId: string; peer: string; role: SessionRole; state: string;
  vs: number; vr: number; window: number;
  uptimeSeconds: number; bytesIn: number; bytesOut: number; lastActivity: string;
}
export interface LinkStats {
  portId: string; peer: string; smoothedRttMs: number;
  retries: number; rejCount: number; srejCount: number;
  framesIn: number; framesOut: number;
}

// ---- connectionless TEST ping (docs/node-api.yaml PingResult) ----
// One TEST-frame round trip. `timeout` true ⇒ no echo came back; `rttMs` is null
// in that case. A peer that doesn't implement TEST simply never answers → every
// reply times out and lossPct is 100 (a normal result to display, not an error).
export interface PingReply { seq: number; rttMs: number | null; timeout: boolean }
export interface PingResult {
  replies: PingReply[];
  minMs: number; avgMs: number; maxMs: number;
  lossPct: number;
}

// ---- 6.3 monitor event (derived from FrameTraced) ----------
export type FrameType =
  | "UI" | "SABM" | "SABME" | "I" | "RR" | "RNR" | "REJ" | "SREJ"
  | "FRMR" | "UA" | "DISC" | "DM" | "XID";
export type FrameClass = "I" | "U" | "S";
export type FrameDirection = "in" | "out";
export interface MonitorEvent {
  seq: number;
  timestamp: string | Date;
  portId: string;
  direction: FrameDirection;
  source: string;
  dest: string;
  type: FrameType;
  classKind: FrameClass;
  pid: string | null;
  pidName: string | null;
  ns: number | null;
  nr: number | null;
  pf: number;
  command: boolean;
  length: number;
  summary: string;
  raw: number[];
  path: string[];
}

// ---- operator-facing helper models (UI layer) --------------
export interface NinoMode { mode: number; label: string }
export interface RadioProfile {
  id: string; name: string; ninoMode: number;
  baseline: Record<string, number>;
}
export interface ChannelMode { id: string; name: string; help: string }
export interface LinkDifficulty { id: string; name: string; help: string }
export interface PortSetup {
  radio: string | null; channel: string; difficulty: string; custom: boolean;
}
export interface ParamHelp { label: string; unit: string; help: string }
export interface PortHealth {
  level: "good" | "degraded" | "faulted";
  reason?: string;
}
export interface NinoTest {
  portId: string; receivedAt: string; firmware: string;
  mode: number; modeLabel: string; txdelaySource: string;
  softwareControl: boolean; rssiDbm: number; crcOk: boolean;
}
// A per-port beacon override (Packet.Node.Core.Configuration.PortBeaconConfig).
// enabled is authoritative for the port; null intervalMinutes / text inherit the
// system default (BeaconConfig).
export interface PortBeacon { enabled: boolean; intervalMinutes: number | null; text: string | null }
export interface User {
  name: string; role: string; scopes: string[]; passkeys: number; lastLogin: string;
}

// ---- auth wire shapes (mirror Packet.Node.Api.PdnAuthApi DTOs) ----
// A user projected for the API — no password hash. Matches UserSummary (server).
export interface UserSummary {
  username: string;
  /** The single granted scope: "read" | "operate" | "admin". */
  scope: string;
  createdUtc: string;
  lastLoginUtc: string | null;
}
// POST /auth/login + POST /auth/refresh success body. `scopes` is the single granted
// scope string; `refreshToken` is the opaque one-time-use token the client stores and
// presents to /auth/refresh (may be null only if the node couldn't persist one — the
// access token still works until it expires).
export interface LoginResult { token: string; expiresAt: string; scopes: string; refreshToken: string | null }
// GET /setup/state body.
export interface SetupState { needsSetup: boolean }
// POST /setup request body (identity + first admin + optional first port).
export interface SetupIdentityInput { callsign: string; alias?: string | null; grid?: string | null }
export interface SetupAdminInput { username: string; password: string }
export interface SetupRequest { identity: SetupIdentityInput; admin: SetupAdminInput; firstPort?: PortConfig | null }
// POST /setup success body (the created admin — no token).
export interface SetupResult { username: string; scope: string }

// ---- WebAuthn / passkeys (mirror Packet.Node.Api.PdnWebAuthnApi DTOs) ----
// One enrolled passkey, projected for the API (no key material). Matches
// WebAuthnCredentialSummary (server). `id` is the base64url credential id (the handle
// for DELETE /auth/webauthn/credentials/{id}).
export interface WebAuthnCredential {
  id: string;
  transports: string | null;
  createdUtc: string;
  lastUsedUtc: string | null;
}
// POST /auth/webauthn/assert/begin response: a per-attempt session id (echoed back at
// complete) + the assertion options (passed to startAuthentication). `options` is the
// raw WebAuthn JSON (PublicKeyCredentialRequestOptionsJSON) Fido2 emitted.
export interface AssertBeginResponse { sessionId: string; options: unknown }
// POST /auth/webauthn/register/complete success body.
export interface RegisterCompleteResponse { registered: boolean; credentialId: string }

// ---- Over-RF sysop code / TOTP (mirror Packet.Node.Api.PdnTotpApi DTOs) ----
// POST /auth/totp/enroll/begin response: a freshly-minted base32 secret (shown ONCE for
// manual entry) + the otpauth:// URI to render as a QR code. Neither is persisted until
// enroll/complete succeeds.
export interface TotpEnrollBeginResponse { secret: string; otpauthUri: string }
// POST /auth/totp/enroll/complete success body.
export interface TotpEnrollCompleteResponse { enrolled: boolean; callsign: string }
// GET /auth/totp/enroll body: whether the signed-in user has an over-RF code enrolled, and
// the bound callsign (null when not enrolled). Never the secret.
export interface TotpEnrollState { enrolled: boolean; callsign: string | null }
export interface LogLine { t: string; lvl: "info" | "warn" | "error"; msg: string }
export interface ToggleHelp { label: string; desc: string }
export interface FieldHelp { label: string; unit: string; help: string }
