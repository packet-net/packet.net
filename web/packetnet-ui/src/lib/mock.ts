// ============================================================
// pdn — mock data + domain models + help copy + formatters.
// Typed port of the design handoff's pdn/data.jsx; field names match the
// real records (see types.ts / docs/node-ui-design.md §6). Used by the API
// client's mock backend (lib/api.ts) until the Slice-3 endpoints are live.
// ============================================================
import type {
  NodeConfig, NetRomRoutingSnapshot, NodeStatus, PortStatus, SessionInfo,
  LinkStats, PeerCapability, MonitorEvent, FrameType, ApplyImpact, NinoMode, RadioProfile,
  ChannelMode, LinkDifficulty, PortSetup, ParamHelp, NinoTest,
  User, LogLine, ToggleHelp, FieldHelp, NodeApp, AppPackage, AvailableApp,
  TailscaleStatus, SystemInfo, NetRomRouting,
} from "./types";

// 6.1 NodeConfig tree ----------------------------------------
export const NODE_CONFIG: NodeConfig = {
  schemaVersion: 3,
  identity: { callsign: "GB7RDG", alias: "RDGGW", grid: "IO91nl" },
  ports: [
    { id: "vhf-1", enabled: true, transport: { kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 }, profile: "fast-il2p-1200", ax25: { t1Ms: 3000, t2Ms: 300, t3Ms: 180000, n2: 8, windowSize: 4, maxCachedPeers: 64 }, kiss: { txDelay: 300, persistence: 63, slotTime: 100, txTail: 50 }, beacon: { enabled: true, intervalMinutes: null, text: null } },
    { id: "uhf-2", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 }, profile: "slow-afsk1200", ax25: { t1Ms: 4000, t2Ms: 500, t3Ms: 180000, n2: 10, windowSize: 4, maxCachedPeers: 64 }, kiss: { txDelay: 400, persistence: 63, slotTime: 100, txTail: 80 }, beacon: { enabled: true, intervalMinutes: 15, text: "{node}:{call} UHF 9k6 data gateway QRV" } },
    { id: "link-dn", enabled: true, transport: { kind: "axudp", host: "44.131.91.2", port: 10093, localPort: 10093 }, profile: null, ax25: { t1Ms: 2000, t2Ms: 200, t3Ms: 180000, n2: 8, windowSize: 7, maxCachedPeers: 32 }, kiss: null, beacon: { enabled: false, intervalMinutes: null, text: null } },
    { id: "hf-300", enabled: false, transport: { kind: "serial-kiss", device: "/dev/ttyUSB1", baud: 38400 }, profile: "robust-hf", ax25: { t1Ms: 8000, t2Ms: 1500, t3Ms: 300000, n2: 12, windowSize: 2, maxCachedPeers: 16 }, kiss: { txDelay: 250, persistence: 32, slotTime: 100, txTail: 100 }, beacon: null },
  ],
  services: { banner: "{node}:{call} — Reading & District packet gateway", prompt: "{node}:{call}}" },
  management: {
    telnet: { enabled: true, bind: "127.0.0.1", port: 8011 },
    http: { bind: "0.0.0.0", port: 8080 },
    https: { enabled: false, bind: "0.0.0.0", port: 8443, certificatePath: null, certificatePassword: null, generateSelfSignedOnMissing: true },
    auth: { enabled: false, accessTokenMinutes: null, refreshTokenMinutes: null, sysopElevationMinutes: null, webAuthn: { relyingPartyId: "localhost", relyingPartyName: "pdn node", allowedOrigins: [] } },
  },
  netRom: {
    enabled: true, broadcast: true, routing: "Transit", forwardMode: "PerFlow",
    alias: "RDGGW", defaultNeighbourQuality: 192, minQuality: 40,
    obsoleteInitial: 6, obsoleteMinimum: 4, sweepIntervalSeconds: 300,
    window: 4, transportTimeoutSeconds: 60, transportRetries: 3, timeToLive: 25,
    inp3: { enabled: true, preferInp3Routes: true, l3RttInterval: 3600, l3RttResetWindow: 5, rifInterval: 60, positiveDebounce: 3 },
  },
  beacon: { enabled: true, intervalMinutes: 30, text: "{node}:{call} pdn node — Reading & District ARS" },
  tailscale: { enabled: false, authKey: null, authKeyFile: null, hostname: "pdn", tags: [], stateDir: "/var/lib/packetnet/tsnet", target: "127.0.0.1:8080", funnel: false },
  oarc: {
    enabled: false, baseUrl: "https://node-api.packet.oarc.uk/",
    reportNodeStatus: true, reportLinks: true, reportCircuits: true,
    reportTraces: false, tracesRfOnly: true, publishExactPosition: false,
    statusIntervalSecs: 300, sessionStatusIntervalSecs: 60,
  },
};

// The node's version + install channel + available-update view (GET /api/v1/system/info).
// The mock shows a github-channel node WITH an update available, so the About panel's
// version/channel line AND the "update available" banner both demo with no node. The
// version matches NODE_STATUS so the two surfaces agree.
export const SYSTEM_INFO: SystemInfo = {
  version: "0.7.0-rc2 (b57f327)",
  channel: "github",
  updateMechanism: "github",
  updateAvailable: true,
  latestVersion: "0.8.0",
};

// The embedded Tailscale sidecar's status — the mock shows a connected node so the
// "Remote access" panel demos with no node. A live node returns this from
// GET /api/v1/system/tailscale.
export const TAILSCALE_STATUS: TailscaleStatus = {
  enabled: true, state: "running", fqdn: "pdn.tail-scale.ts.net", authUrl: null, funnel: false,
};

// field apply-impact map (hot vs disruptive) → per-field badges + reconcile
export const APPLY_IMPACT: Record<string, ApplyImpact> = {
  "identity.callsign": "node-reset",
  "identity.alias": "node-reset",
  "identity.grid": "live",
  "port.transport": "port-restart",
  "port.ax25": "live",
  "port.kiss": "live",
  "port.enabled": "port-restart",
  "netRom": "live",
  "services": "live",
  "oarc": "live",
  "management.http": "node-reset",
  "management.telnet": "port-restart",
};

// 6.2 NET/ROM routing snapshot -------------------------------
export const NETROM: NetRomRoutingSnapshot = {
  generatedAt: "2026-06-08T14:21:07Z",
  neighbours: [
    { neighbour: "GB7BNS", alias: "BNSGW", portId: "vhf-1", pathQuality: 203, lastHeard: "0:00:14" },
    { neighbour: "MB7UWS", alias: "UWSNOD", portId: "vhf-1", pathQuality: 168, lastHeard: "0:01:52" },
    { neighbour: "GB7CIP", alias: "CIPGW", portId: "uhf-2", pathQuality: 188, lastHeard: "0:00:41" },
    { neighbour: "G8PZT-7", alias: "KIDDER", portId: "link-dn", pathQuality: 222, lastHeard: "0:00:03" },
  ],
  destinations: [
    { destination: "GB7BNS", alias: "BNSGW", bestRoute: 0, routes: [{ neighbour: "GB7BNS", quality: 203, obsolescence: 6, inp3: { targetTimeMs: 240, hopCount: 1 } }] },
    { destination: "GB7CIP", alias: "CIPGW", bestRoute: 0, routes: [{ neighbour: "GB7CIP", quality: 188, obsolescence: 6, inp3: { targetTimeMs: 410, hopCount: 1 } }, { neighbour: "GB7BNS", quality: 142, obsolescence: 4, inp3: { targetTimeMs: 980, hopCount: 2 } }] },
    { destination: "G1MNW-2", alias: "READNG", bestRoute: 0, routes: [{ neighbour: "G8PZT-7", quality: 199, obsolescence: 6, inp3: { targetTimeMs: 180, hopCount: 1 } }] },
    { destination: "GB7MAX", alias: "MAXGW", bestRoute: 0, routes: [{ neighbour: "G8PZT-7", quality: 176, obsolescence: 5, inp3: { targetTimeMs: 620, hopCount: 3 } }, { neighbour: "GB7CIP", quality: 151, obsolescence: 4, inp3: null }] },
    { destination: "MB7UWS", alias: "UWSNOD", bestRoute: 0, routes: [{ neighbour: "MB7UWS", quality: 168, obsolescence: 6, inp3: null }] },
    { destination: "GB7SAN", alias: "SANGW", bestRoute: 0, routes: [{ neighbour: "GB7BNS", quality: 133, obsolescence: 3, inp3: { targetTimeMs: 1340, hopCount: 4 } }] },
  ],
};

// 6.4 node status -------------------------------------------
export const NODE_STATUS: NodeStatus = {
  callsign: "GB7RDG", alias: "RDGGW", grid: "IO91nl",
  version: "0.7.0-rc2 (b57f327)",
  uptimeSeconds: 1987260,
  portsUp: 3, portsTotal: 4, sessionCount: 4,
  netrom: { neighbours: 4, destinations: 6, inp3Enabled: true },
};

// 6.4 port status -------------------------------------------
export const PORT_STATUS: Record<string, PortStatus> = {
  "vhf-1": { id: "vhf-1", enabled: true, state: "up", sessionCount: 2, lastError: null, framesIn: 184213, framesOut: 95120 },
  "uhf-2": { id: "uhf-2", enabled: true, state: "up", sessionCount: 1, lastError: null, framesIn: 52109, framesOut: 30877 },
  "link-dn": { id: "link-dn", enabled: true, state: "up", sessionCount: 1, lastError: null, framesIn: 421882, framesOut: 410337 },
  "hf-300": { id: "hf-300", enabled: false, state: "faulted", sessionCount: 0, lastError: "serial: /dev/ttyUSB1 not present", framesIn: 0, framesOut: 0 },
};

// 6.4 sessions ----------------------------------------------
export const SESSIONS: SessionInfo[] = [
  { id: "s1", portId: "vhf-1", peer: "M0LTE", role: "console", state: "Connected", vs: 12, vr: 11, window: 4, uptimeSeconds: 842, bytesIn: 4821, bytesOut: 19233, lastActivity: "0:00:02" },
  { id: "s2", portId: "vhf-1", peer: "2E0XYZ", role: "console", state: "TimerRecovery", vs: 3, vr: 7, window: 4, uptimeSeconds: 121, bytesIn: 980, bytesOut: 1422, lastActivity: "0:00:09" },
  { id: "s3", portId: "uhf-2", peer: "G4APL-1", role: "bridge", state: "Connected", vs: 88, vr: 90, window: 4, uptimeSeconds: 5403, bytesIn: 71204, bytesOut: 60891, lastActivity: "0:00:01" },
  { id: "s4", portId: "link-dn", peer: "G8PZT-7", role: "interlink", state: "Connected", vs: 401, vr: 398, window: 7, uptimeSeconds: 91244, bytesIn: 2104882, bytesOut: 1988401, lastActivity: "0:00:00" },
];

// 6.3 monitor frame generation ------------------------------
export const FRAME_TYPES: FrameType[] = ["UI", "SABM", "SABME", "I", "RR", "RNR", "REJ", "SREJ", "FRMR", "UA", "DISC", "DM", "XID"];
export const CALLS = ["M0LTE", "2E0XYZ", "G4APL-1", "G8PZT-7", "GB7BNS", "GB7CIP", "MB7UWS", "G1MNW-2", "M7ABC", "GB7RDG", "2E1FOX", "G0HWC"];
export const PORTS_LIST = ["vhf-1", "uhf-2", "link-dn"];
export const PIDS: Record<string, string> = { "0xF0": "No layer 3", "0xCF": "NET/ROM", "0xCC": "ARPA IP", "0x08": "Segmentation" };

export function randItem<T>(a: T[]): T { return a[Math.floor(Math.random() * a.length)]; }

let _frameSeq = 9000;
export function makeFrame(now: Date): MonitorEvent {
  const type = randItem(FRAME_TYPES);
  const dir: "in" | "out" = Math.random() > 0.5 ? "in" : "out";
  const port = randItem(PORTS_LIST);
  let source = randItem(CALLS);
  let dest = randItem(CALLS);
  if (dir === "out") source = "GB7RDG";
  if (source === dest) dest = randItem(CALLS);
  const isI = type === "I";
  const isU = ["UI", "SABM", "SABME", "UA", "DISC", "DM", "XID", "FRMR"].includes(type);
  const pidKey = isI || type === "UI" ? randItem(Object.keys(PIDS)) : null;
  const ns = isI ? Math.floor(Math.random() * 8) : null;
  const nr = ["I", "RR", "RNR", "REJ", "SREJ"].includes(type) ? Math.floor(Math.random() * 8) : null;
  const pf = Math.random() > 0.5 ? 1 : 0;
  const length = isI ? 20 + Math.floor(Math.random() * 236) : type === "UI" ? 12 + Math.floor(Math.random() * 60) : 15;
  let summary: string;
  if (isI) summary = `I N(S)=${ns} N(R)=${nr} P=${pf} pid=${pidKey} len=${length - 17}`;
  else if (["RR", "RNR", "REJ", "SREJ"].includes(type)) summary = `${type} N(R)=${nr} ${pf ? "P/F" : ""}`.trim();
  else if (type === "UI") summary = `UI pid=${pidKey} len=${length - 15}`;
  else if (type === "SABM" || type === "SABME") summary = `${type} request (connect)`;
  else if (type === "UA") summary = "UA (acknowledge)";
  else if (type === "DISC") summary = "DISC (disconnect)";
  else if (type === "DM") summary = "DM (disconnected mode)";
  else if (type === "FRMR") summary = "FRMR (frame reject)";
  else if (type === "XID") summary = "XID (parameter negotiation)";
  else summary = type;

  const raw: number[] = [];
  const nbytes = Math.min(length, 32);
  for (let i = 0; i < nbytes; i++) raw.push(Math.floor(Math.random() * 256));

  return {
    seq: _frameSeq++, timestamp: now, portId: port, direction: dir, source, dest, type,
    classKind: isI ? "I" : isU ? "U" : "S",
    pid: pidKey, pidName: pidKey ? PIDS[pidKey] : null,
    ns, nr, pf, command: dir === "out", length, summary, raw,
    path: Math.random() > 0.7 ? [randItem(["GB7BNS", "GB7CIP", "MB7UWS"])] : [],
  };
}
export function seedFrames(n: number): MonitorEvent[] {
  const out: MonitorEvent[] = [];
  const base = Date.now() - n * 700;
  for (let i = 0; i < n; i++) out.push(makeFrame(new Date(base + i * 700)));
  return out;
}

// 6.4 link stats --------------------------------------------
export const LINK_STATS: LinkStats[] = [
  { portId: "vhf-1", peer: "M0LTE", smoothedRttMs: 612, retries: 0, rejCount: 1, srejCount: 0, framesIn: 1204, framesOut: 1190 },
  { portId: "vhf-1", peer: "2E0XYZ", smoothedRttMs: 1880, retries: 3, rejCount: 5, srejCount: 2, framesIn: 88, framesOut: 71 },
  { portId: "uhf-2", peer: "G4APL-1", smoothedRttMs: 740, retries: 1, rejCount: 0, srejCount: 0, framesIn: 9011, framesOut: 8804 },
  { portId: "link-dn", peer: "G8PZT-7", smoothedRttMs: 38, retries: 0, rejCount: 0, srejCount: 0, framesIn: 210442, framesOut: 198330 },
];

// The learned per-peer AX.25 capability cache (GET /api/v1/capabilities). One row per
// (port, peer); the booleans are three-state so the screen demos every badge: a v2.2 peer
// that answered SREJ-via-XID, a peer that degraded an extended dial (v2.0 + a refusal stamp),
// and a never-probed peer (both unknown → the "?" badges). The relative-ago strings match the
// server's "h:mm:ss" style. The Forget action removes a row in mock mode (see api.clearCapability).
export const CAPABILITIES: PeerCapability[] = [
  { portId: "vhf-1", peer: "M0LTE", supportsExtended: true, supportsSrejViaXid: true, lastProbed: "0:02:14", lastRefused: null },
  { portId: "vhf-1", peer: "2E0XYZ", supportsExtended: false, supportsSrejViaXid: false, lastProbed: "1:41:08", lastRefused: "1:41:08" },
  { portId: "uhf-2", peer: "G4APL-1", supportsExtended: null, supportsSrejViaXid: null, lastProbed: "5:09:52", lastRefused: null },
];

export const LOG_TAIL: LogLine[] = [
  { t: "14:21:07", lvl: "info", msg: "netrom: sweep complete — 6 destinations, 4 neighbours" },
  { t: "14:20:58", lvl: "info", msg: "ax25 vhf-1: SABM from M0LTE → connected (console)" },
  { t: "14:20:41", lvl: "warn", msg: "ax25 vhf-1: 2E0XYZ entered TimerRecovery (T1 expiry, retry 3/8)" },
  { t: "14:19:12", lvl: "info", msg: "link-dn: AXUDP peer G8PZT-7 RTT 38ms" },
  { t: "14:05:33", lvl: "error", msg: "port hf-300: serial /dev/ttyUSB1 not present — port faulted" },
  { t: "13:58:02", lvl: "info", msg: "config: reloaded (netRom.inp3.rifInterval 60→60, no restart)" },
];

export const USERS: User[] = [
  { name: "tom", role: "admin", scopes: ["read", "operate", "admin"], passkeys: 2, lastLogin: "2026-06-08 14:02" },
];

// Enabled, web-capable apps (GET /api/v1/apps). These become first-class left-nav entries
// (rendered with their icon + name) AND were the old Apps-page launcher grid. Each links to
// its reverse-proxied URL. Icons are lucide-react names; an app with no icon falls back to a
// generic glyph. `uiMode` tells the nav how to open the app — standalone (full navigation, the
// default), embedded (in-panel iframe of the app's own page) or slot (in-panel iframe with
// ?pdn_embed=1 so the app renders chrome-less). `state` is the live supervisor state — the nav
// shows a not-running warning when an enabled app is Stopped/Backoff/Faulted. WALL is a
// standalone app running cleanly; lobby is a slot app (chrome-less, single-chrome); quiz is an
// embedded app that is Faulted (its nav entry + its management row both warn).
export const APPS: NodeApp[] = [
  { id: "wall", name: "WALL", icon: "message-square", url: "/apps/wall/", uiMode: "standalone", state: "Running" },
  { id: "lobby", name: "LOBBY", icon: "users", url: "/apps/lobby/", uiMode: "slot", state: "Running" },
  { id: "quiz", name: "QUIZ", icon: null, url: "/apps/quiz/", uiMode: "embedded", state: "Faulted" },
];

// Every app package the node knows about (GET /api/v1/apps/packages) — the
// management section's list. One fixture per interesting state: a running managed
// service, a stopped disabled package, a Faulted one with its crash-loop detail, an
// externally-run service, a broken package (manifest error — never enableable), an
// inline config-authored app (read-only here; 404 from the mutation endpoints), and
// a service-less package with no declared capabilities (the confirm still shows).
// The api.ts mock mutation path updates these in place so a refetch shows the result.
export const APP_PACKAGES: AppPackage[] = [
  { id: "wall", name: "WALL", version: "1.2.0", description: "Shared message wall — leave a note for the next station", icon: "message-square", capabilities: ["session", "web"], enabled: true, source: "package", error: null, service: "managed", state: "Running", pid: 4711, detail: null, forwards: [], command: "WALL", callsign: "M0ABC-1", netromAlias: null, netromQuality: null },
  { id: "lobby", name: "LOBBY", version: "0.9.1", description: "Multi-user chat lobby", icon: "users", capabilities: ["session"], enabled: false, source: "package", error: null, service: "managed", state: "Stopped", pid: null, detail: null, forwards: [], command: "LOBBY", callsign: null, netromAlias: null, netromQuality: null },
  { id: "quiz", name: "QUIZ", version: "2.0.0", description: "Trivia over packet", icon: null, capabilities: ["session"], enabled: true, source: "package", error: null, service: "managed", state: "Faulted", pid: null, detail: "exited 5 times in 30s (exit code 1) — giving up until restarted", forwards: [], command: "QUIZ", callsign: "M0ABC-2", netromAlias: null, netromQuality: null },
  { id: "bbs-bridge", name: "BBS bridge", version: "0.3.0", description: "Bridges sessions to an externally-run BBS process", icon: null, capabilities: ["session"], enabled: true, source: "package", error: null, service: "external", state: "External", pid: null, detail: null, forwards: [], command: "BBS", callsign: "M0ABC-3", netromAlias: "RDGBBS", netromQuality: 255 },
  { id: "wx", name: "wx", version: null, description: null, icon: null, capabilities: [], enabled: false, source: "package", error: "pdn-app.yaml: missing required field 'command'", service: "none", state: null, pid: null, detail: null, forwards: [], command: null, callsign: null, netromAlias: null, netromQuality: null },
  { id: "motd", name: "MOTD", version: null, description: null, icon: null, capabilities: ["session"], enabled: true, source: "inline", error: null, service: "none", state: null, pid: null, detail: null, forwards: [], command: "MOTD", callsign: null, netromAlias: null, netromQuality: null },
  { id: "notes", name: "Notes", version: "1.0.0", description: "Static node notice board — no service process", icon: "sticky-note", capabilities: [], enabled: false, source: "package", error: null, service: "none", state: null, pid: null, detail: null, forwards: [], command: null, callsign: null, netromAlias: null, netromQuality: null },
  // A BBS-style app that asks pdn to expose mail ports on the tailnet (a capability the
  // owner sees in the enable confirm — docs/network-access.md § App-declared port forwarding).
  { id: "mail", name: "Mail", version: "1.0.0", description: "IMAP/SMTP mailbox over the tailnet", icon: "inbox", capabilities: ["network"], enabled: false, source: "package", error: null, service: "managed", state: "Stopped", pid: null, detail: null, forwards: [{ listen: 993, target: "127.0.0.1:1430", tls: "terminate" }, { listen: 465, target: "127.0.0.1:1465", tls: "terminate" }], command: null, callsign: null, netromAlias: null, netromQuality: null },
];

// The app catalog projected with this node's view (GET /api/v1/apps/available) — the
// "Available apps" section's source. One fixture per interesting state: a not-installed
// app ready to install, an installed-but-out-of-date one offering an Update, and one with
// no artifact for this node's architecture (installable:false → the button is disabled
// with a hint). The api.ts mock install path returns a synthetic success.
export const AVAILABLE_APPS: AvailableApp[] = [
  { id: "dapps", name: "DAPPS", version: "0.34.1", description: "Distributed Asynchronous Packet Pub/Sub — store-and-forward messaging.", icon: "inbox", capabilities: ["network", "web"], homepage: "https://github.com/packet-net/dapps", kind: "assets", installed: false, installedVersion: null, updateAvailable: false, installable: true },
  { id: "bpqchat", name: "BPQ Chat", version: "0.1.0", description: "BPQ-Chat-compatible chat node — RF + web chat, peering with the BPQ Chat network.", icon: "message-square", capabilities: ["network", "web"], homepage: "https://github.com/packet-net/pdn-bpqchat", kind: "deb", installed: true, installedVersion: "0.0.9", updateAvailable: true, installable: true },
  { id: "convers", name: "Convers", version: "0.1.2", description: "Classic CONVERS multi-user conference bridge.", icon: "users", capabilities: ["network", "web"], homepage: "https://github.com/packet-net/pdn-convers", kind: "deb", installed: false, installedVersion: null, updateAvailable: false, installable: false },
];

// formatters -------------------------------------------------
export function fmtUptime(s: number): string {
  const d = Math.floor(s / 86400); s %= 86400;
  const h = Math.floor(s / 3600); s %= 3600;
  const m = Math.floor(s / 60);
  if (d > 0) return `${d}d ${h}h ${m}m`;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}
export function fmtBytes(n: number): string {
  if (n < 1024) return n + " B";
  if (n < 1048576) return (n / 1024).toFixed(1) + " KB";
  return (n / 1048576).toFixed(1) + " MB";
}
export function hex(n: number, w?: number): string { return n.toString(16).toUpperCase().padStart(w || 2, "0"); }

// operator-facing config model ------------------------------
export const KIND_LABEL: Record<string, string> = { "kiss-tcp": "kiss-tcp", "serial-kiss": "serial-kiss", "nino-tnc": "ninotnc", "axudp": "axudp" };
export const KIND_USES_KISS: Record<string, boolean> = { "kiss-tcp": true, "serial-kiss": true, "nino-tnc": true, "axudp": false };

export const NINO_MODES: NinoMode[] = [
  { mode: 0, label: "300 baud · AFSK · AX.25 (HF/NBEMS)" },
  { mode: 1, label: "1200 baud · AFSK · AX.25" },
  { mode: 2, label: "1200 baud · AFSK · IL2P" },
  { mode: 3, label: "2400 baud · AFSK · IL2P" },
  { mode: 4, label: "9600 baud · GFSK · IL2P" },
  { mode: 5, label: "9600 baud · GFSK · AX.25 (G3RUH)" },
  { mode: 6, label: "4800 baud · GFSK · IL2P" },
  { mode: 7, label: "19200 baud · GFSK · IL2P" },
  { mode: 8, label: "38400 baud · GFSK · IL2P" },
];

export const RADIO_PROFILES: RadioProfile[] = [
  { id: "vhf-fm-1200", name: "VHF FM · 1200 AFSK", ninoMode: 1, baseline: { t1Ms: 3000, t2Ms: 300, t3Ms: 180000, n2: 8, windowSize: 4, txDelay: 300, slotTime: 100, txTail: 50, persistence: 63 } },
  { id: "vhf-fm-9600", name: "VHF FM · 9600 G3RUH", ninoMode: 5, baseline: { t1Ms: 2500, t2Ms: 200, t3Ms: 180000, n2: 8, windowSize: 4, txDelay: 150, slotTime: 100, txTail: 30, persistence: 63 } },
  { id: "uhf-data-9600", name: "UHF data · 9600 GFSK IL2P", ninoMode: 4, baseline: { t1Ms: 2500, t2Ms: 200, t3Ms: 180000, n2: 8, windowSize: 4, txDelay: 150, slotTime: 100, txTail: 30, persistence: 63 } },
  { id: "hf-robust-300", name: "HF robust · 300 AFSK", ninoMode: 0, baseline: { t1Ms: 8000, t2Ms: 1500, t3Ms: 300000, n2: 12, windowSize: 2, txDelay: 250, slotTime: 100, txTail: 100, persistence: 32 } },
];
export const CHANNEL_MODES: ChannelMode[] = [
  { id: "shared", name: "Shared", help: "Several stations share this RF channel. pdn listens before transmitting and backs off (CSMA) to avoid collisions." },
  { id: "dedicated", name: "Dedicated", help: "A point-to-point link with no other users. Faster turnaround — minimal back-off and TX delay." },
];
export const LINK_DIFFICULTY: LinkDifficulty[] = [
  { id: "easy", name: "Easy", help: "Strong, reliable path. Fewer retries and shorter timers for snappy recovery." },
  { id: "moderate", name: "Moderate", help: "Occasional loss. Balanced retries and timers." },
  { id: "hard", name: "Marginal", help: "Weak or noisy path. More retries, longer timers, smaller window to ride out fades." },
];
export const PORT_SETUP: Record<string, PortSetup> = {
  "vhf-1": { radio: "uhf-data-9600", channel: "shared", difficulty: "moderate", custom: false },
  "uhf-2": { radio: "vhf-fm-1200", channel: "shared", difficulty: "moderate", custom: true },
  "link-dn": { radio: null, channel: "dedicated", difficulty: "easy", custom: false },
  "hf-300": { radio: "hf-robust-300", channel: "shared", difficulty: "hard", custom: false },
};
export const PARAM_HELP: Record<string, ParamHelp> = {
  t1Ms: { label: "Ack timeout", unit: "ms", help: "How long pdn waits for the other station to acknowledge a frame before sending it again. Too short wastes airtime on needless resends; too long is slow to recover from a lost frame. (Protocol name: T1.)" },
  t2Ms: { label: "Reply delay", unit: "ms", help: "A short pause before replying, so several received frames can be acknowledged together rather than one at a time. (Protocol name: T2.)" },
  t3Ms: { label: "Keep-alive poll", unit: "ms", help: "When a connected link goes quiet, how long before pdn pokes the other station to check it's still there. (Protocol name: T3.)" },
  n2: { label: "Retries", unit: "", help: "How many times pdn resends a frame with no acknowledgement before giving up and dropping the link. (Protocol name: N2.)" },
  windowSize: { label: "Window", unit: "frames", help: "How many frames may be in flight (sent but not yet acknowledged) at once. Bigger = more throughput on a clean link; smaller is safer on a lossy one." },
  n1: { label: "Max frame (PACLEN)", unit: "bytes", help: "Largest information-field a frame carries (PACLEN / N1). Smaller frames are shorter on the air and recover faster on a noisy/slow medium — set ~80 on an HF port; leave it at 256 on VHF/UHF. The far station can negotiate it lower via XID but never higher." },
  netRomQuality: { label: "NET/ROM quality", unit: "", help: "Route quality this port advertises for a directly-heard neighbour (0–255). Higher = a better link the network prefers. Leave blank to inherit the node-wide default. Set per port on a mixed-grade node (e.g. 191 on one link, 192 on another)." },
  txDelay: { label: "TX delay", unit: "ms", help: "Silence held after keying the transmitter before data starts, giving the far radio's receiver time to lock on. In software-control mode pdn sets this on the modem." },
  txTail: { label: "TX tail", unit: "ms", help: "Extra carrier held after the last byte before the transmitter unkeys, so the final bits aren't clipped." },
  slotTime: { label: "Slot time", unit: "ms", help: "The back-off slot length used when sharing the channel — how long pdn waits between 'is the channel free?' checks." },
  persistence: { label: "Persistence", unit: "%", help: "When the channel is free, the chance pdn transmits in each slot. Lower is more polite on a busy shared channel; 100% is fine on a dedicated link. (Stored as a 0–255 byte.)" },
};
export const AX25_DEFAULTS: Record<string, number> = { t1Ms: 3000, t2Ms: 300, t3Ms: 180000, n2: 8, windowSize: 4, n1: 256 };
export const KISS_DEFAULTS: Record<string, number> = { txDelay: 300, slotTime: 100, txTail: 50, persistence: 63 };

export function persistPct(v: number): number { return Math.round((v / 255) * 100); }
export function pctToPersist(p: number): number { return Math.round((p / 100) * 255); }

export const NINO_TEST: NinoTest = {
  portId: "vhf-1", receivedAt: "just now", firmware: "NinoTNC A3 · fw 2.3.1",
  mode: 4, modeLabel: "9600 baud · GFSK · IL2P",
  txdelaySource: "hardware DIP switches", softwareControl: false, rssiDbm: -71, crcOk: true,
};

export const NETROM_TOGGLE_HELP: Record<string, ToggleHelp> = {
  enabled: { label: "NET/ROM networking", desc: "The layer that lets your node route across the wider packet network, not just direct AX.25 links. Turn this off and the node only handles point-to-point connections." },
  broadcast: { label: "Advertise my routes", desc: "Tell neighbours which destinations your node can reach, so they'll route through you. Turn off to be a silent leaf that uses the network but doesn't carry others' traffic." },
};
// The single routing-role control (replaces the old connect + forward toggles, which
// had an inert combination). Each option is a clean escalation of how much routing work
// the node does. `routing` is the picker's own label/help; the rest are the per-option copy.
export const NETROM_ROUTING_HELP: { label: string; help: string; options: { value: NetRomRouting; label: string; desc: string }[] } = {
  label: "Routing role",
  help: "How much your node takes part in routing across the network. Hearing routes (above) is always on; this controls whether your node opens links to other nodes and relays traffic.",
  options: [
    { value: "None", label: "Listen only", desc: "Passive — your node learns the network's routes but opens no links to other nodes and carries no traffic. The safe default." },
    { value: "Endpoint", label: "Connect out", desc: "Your node may open links so you can connect <alias> to a distant node across the network — but it won't relay other stations' traffic." },
    { value: "Transit", label: "Full router", desc: "Your node opens links AND relays other stations' traffic onward toward its destination. This is what makes you a useful relay rather than just an endpoint." },
  ],
};
export const NETROM_FIELD_HELP: Record<string, FieldHelp> = {
  alias: { label: "Node alias", unit: "", help: "A short friendly name for your node on the network (e.g. RDGGW), shown alongside your callsign." },
  defaultNeighbourQuality: { label: "New-neighbour quality", unit: "0–255", help: "The starting quality score given to a neighbour you've just heard, before its path has been measured. Higher = more willing to route through unproven neighbours." },
  minQuality: { label: "Minimum usable quality", unit: "0–255", help: "Routes scoring below this are ignored — a noise floor that keeps poor, unreliable paths out of your routing table." },
  sweepIntervalSeconds: { label: "Routing sweep", unit: "seconds", help: "How often the node re-checks its routing table and ages out routes it hasn't heard about recently." },
  timeToLive: { label: "Hop limit", unit: "hops", help: "The most nodes a frame may cross before the network gives up on it. Stops traffic looping around the network forever. (Protocol name: TTL.)" },
  window: { label: "Transport window", unit: "frames", help: "How many NET/ROM frames may be in flight (unacknowledged) on a circuit at once. Bigger = more throughput on a clean path." },
};
export const INP3_FIELD_HELP: Record<string, FieldHelp> = {
  l3RttInterval: { label: "Time-probe interval", unit: "seconds", help: "How often the node measures the real round-trip time to its neighbours." },
  l3RttResetWindow: { label: "Probe reset window", unit: "probes", help: "How many missed time-probes before a neighbour's measured time is treated as unknown again." },
  rifInterval: { label: "Share-timing interval", unit: "seconds", help: "How often your node passes its measured route timings on to neighbours, so the whole network's time map stays current." },
  positiveDebounce: { label: "Switch-route patience", unit: "probes", help: "How many consistent 'this route is faster' measurements are needed before the node actually switches to it — stops it flapping between routes on momentary blips." },
};
