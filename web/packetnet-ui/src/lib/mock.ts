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
  RadioStatus, RadioScanResult, HeardStation, HeadEndScan, HeadEndKeyupResult,
  DoctorReport, DoctorProbe,
  TuningStartRequest, TuningSessionInfo, TuningEvent, TuningAdvice,
  RigStatus,
} from "./types";

// 6.1 NodeConfig tree ----------------------------------------
export const NODE_CONFIG: NodeConfig = {
  schemaVersion: 3,
  identity: { callsign: "GB7RDG", alias: "RDGGW", grid: "IO91nl" },
  ports: [
    { id: "vhf-1", enabled: true, transport: { kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 }, profile: "fast-il2p-1200", ax25: { t1Ms: 3000, t2Ms: 300, t3Ms: 180000, n2: 8, windowSize: 4, maxCachedPeers: 64 }, kiss: { txDelay: 300, persistence: 63, slotTime: 100, txTail: 50 }, beacon: { enabled: true, intervalMinutes: null, text: null }, radio: { kind: "tait-ccdi", serial: "19925328", baud: 28800 } },
    { id: "uhf-2", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 }, profile: "slow-afsk1200", ax25: { t1Ms: 4000, t2Ms: 500, t3Ms: 180000, n2: 10, windowSize: 4, maxCachedPeers: 64 }, kiss: { txDelay: 400, persistence: 63, slotTime: 100, txTail: 80 }, beacon: { enabled: true, intervalMinutes: 15, text: "{node}:{call} UHF 9k6 data gateway QRV" } },
    { id: "link-dn", enabled: true, transport: { kind: "axudp", host: "44.131.91.2", port: 10093, localPort: 10093 }, profile: null, ax25: { t1Ms: 2000, t2Ms: 200, t3Ms: 180000, n2: 8, windowSize: 7, maxCachedPeers: 32 }, kiss: null, beacon: { enabled: false, intervalMinutes: null, text: null } },
    { id: "mp-net", enabled: true, transport: { kind: "axudp-multipoint", localPort: 10093, peers: [{ call: "N0CALL-1", host: "44.131.10.1", port: 10093, broadcast: true }, { call: "N0CALL-7", host: "44.131.10.2", port: 10094, broadcast: false }] }, profile: null, ax25: { t1Ms: 2000, t2Ms: 200, t3Ms: 180000, n2: 8, windowSize: 7, maxCachedPeers: 32 }, kiss: null, beacon: null, netRomMinQuality: 100, nodesPaclen: 160 },
    { id: "hf-300", enabled: false, transport: { kind: "serial-kiss", device: "/dev/ttyUSB1", baud: 38400 }, profile: "robust-hf", ax25: { t1Ms: 8000, t2Ms: 1500, t3Ms: 300000, n2: 12, windowSize: 2, maxCachedPeers: 16 }, kiss: { txDelay: 250, persistence: 32, slotTime: 100, txTail: 100 }, beacon: null, radio: { kind: "tait-ccdi", port: "/dev/ttyUSB2", baud: 28800 } },
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
    defaultNeighbourQuality: 192, minQuality: 40,
    obsoleteInitial: 6, obsoleteMinimum: 4, sweepIntervalSeconds: 300,
    window: 4, transportTimeoutSeconds: 60, transportRetries: 3, timeToLive: 25,
    compress: false,
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

  // Per-frame radio metadata: only inbound (RX) frames on a radio-attached port carry RSSI. vhf-1 has
  // a radio in the mock, so RX frames on it get a plausible RSSI/noise-floor/SNR; everything else is
  // null (TX frame, or a port with no radio) so the monitor's em-dash rendering is exercised too.
  let rssiDbm: number | null = null;
  let noiseFloorDbm: number | null = null;
  let snrDb: number | null = null;
  if (dir === "in" && port === "vhf-1") {
    rssiDbm = -(62 + Math.floor(Math.random() * 44)); // -62..-105 dBm
    noiseFloorDbm = -(108 + Math.floor(Math.random() * 6)); // ~ -108..-113 dBm
    snrDb = +(rssiDbm - noiseFloorDbm).toFixed(1);
  }

  return {
    seq: _frameSeq++, timestamp: now, portId: port, direction: dir, source, dest, type,
    classKind: isI ? "I" : isU ? "U" : "S",
    pid: pidKey, pidName: pidKey ? PIDS[pidKey] : null,
    ns, nr, pf, command: dir === "out", length, summary, raw,
    path: Math.random() > 0.7 ? [randItem(["GB7BNS", "GB7CIP", "MB7UWS"])] : [],
    rssiDbm, snrDb, noiseFloorDbm,
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

// Radio-control status + health (GET /api/v1/radios). Two attached TM8110s, each with a live health
// sample, so the dashboard's Radios panel demos with no node. vhf-1 is the hero (strong RSSI, cool PA,
// channel busy); hf-300 is a second radio whose modem port faulted (its /dev/ttyUSB1 modem is absent)
// yet whose radio-control channel — a SEPARATE device, /dev/ttyUSB2 — is open and healthy, so its link
// quality is still visible (the product story). Bind keys differ: vhf-1 by CCDI serial, hf-300 by path.
export const RADIOS: RadioStatus[] = [
  {
    portId: "vhf-1", attached: true, kind: "tait-ccdi", controlPort: "/dev/ttyUSB0", serial: "19925328",
    identity: { model: "Tait TM8110", ccdiVersion: "1.10.0" }, connectionState: "healthy", channelBusy: true,
    health: {
      rssiDbm: -78.5, averagedRssiDbm: -80.2, paTemperatureC: 41,
      forwardTrendMillivolts: 2140, reverseTrendMillivolts: 190, reverseForwardRatio: 0.089,
      sampleAt: new Date(Date.now() - 4000).toISOString(),
    },
  },
  {
    portId: "hf-300", attached: true, kind: "tait-ccdi", controlPort: "/dev/ttyUSB2", serial: "1G000123",
    identity: { model: "Tait TM8110", ccdiVersion: "1.10.0" }, connectionState: "healthy", channelBusy: false,
    health: {
      rssiDbm: -95.0, averagedRssiDbm: -94.3, paTemperatureC: 52,
      forwardTrendMillivolts: 1980, reverseTrendMillivolts: 410, reverseForwardRatio: 0.207,
      sampleAt: new Date(Date.now() - 7000).toISOString(),
    },
  },
];

// Bus-scan result set (GET /api/v1/radios/scan) — what "Scan for radios" surfaces in the PortEditor.
// Two TM8110s: the first canonicalises to a /dev/serial/by-id symlink; the second is a shared-USB-
// serial CP2102 dongle whose symlink collides, so byIdPath is null — exactly why binding is by serial.
export const RADIO_SCAN: RadioScanResult[] = [
  { serial: "19925328", model: "Tait TM8110", ccdiVersion: "1.10.0", baud: 28800, devicePath: "/dev/ttyUSB0", byIdPath: "/dev/serial/by-id/usb-Silicon_Labs_CP2102_USB_to_UART_Bridge_Controller-if00-port0" },
  { serial: "1G000123", model: "Tait TM8110", ccdiVersion: "1.10.0", baud: 28800, devicePath: "/dev/ttyUSB2", byIdPath: null },
];

// Rig-control attachments (GET /api/v1/rigs) — the station-control (CAT) view. One attached
// hamlib rig mid-QSO shape (dial + mode + a last-TX meter sample) and one configured flrig
// whose daemon isn't up, so the card's not-attached projection renders.
export const RIGS: RigStatus[] = [
  {
    portId: "hf-300", attached: true, kind: "hamlib", endpoint: "127.0.0.1:4532",
    backend: "Hamlib rigctld", manufacturer: "Icom", model: "IC-7300",
    capabilities: [
      "frequencyGet", "frequencySet", "modeGet", "modeSet", "pttGet", "pttSet",
      "swrMeter", "rfPowerMeter", "rfPowerMeterWatts",
    ],
    connectionState: "healthy", frequencyHz: 14074000, mode: "PKTUSB", passbandHz: 3000,
    transmitting: false,
    meters: { swr: 1.3, rfPowerWatts: 42, rfPowerRelative: 0.42, sampleAt: new Date(Date.now() - 90_000).toISOString() },
    sampledAt: new Date(Date.now() - 3000).toISOString(),
  },
  {
    portId: "vhf-1", attached: false, kind: "flrig", endpoint: "127.0.0.1:12345",
    backend: null, manufacturer: null, model: null, capabilities: [],
    connectionState: "unknown", frequencyHz: null, mode: null, passbandHz: null,
    transmitting: null, meters: null, sampledAt: null,
  },
];

// Split-station head-end fleet scan (GET /api/v1/radios/headends) — the "plug into any port and go"
// preview the Head-ends screen renders. Covers every state the operator surface must handle:
//   shack-north — mDNS-discovered, reachable, exactly one free TNC + one free radio → an AUTO pairing
//                 (one-click adopt), plus a device already bound to a running port (free:false).
//                 The free radio carries its band (B1 → 2m) so the band badge + band-named adopt show.
//   garage-pi   — config-pinned, reachable, TWO free TNCs + TWO free radios → pairingAmbiguous: the
//                 operator picks a TNC + a radio before adopting (proposedPairs lists the combos) —
//                 or resolves it physically with keyup pairing (see headEndKeyup below). One TNC has
//                 an UNSTABLE dev-fallback id (idStable:false → the warning badge); one radio is 70cm.
//   attic-relay — mDNS-discovered but UNREACHABLE → its Error shows and no devices/pairs render.
// Plus a duplicate-instance-id CONFLICT (two boxes advertising "spare-pi" with no config address).
// idSource/idStable ride the daemon inventory (headend-v0.1.3+); a device from an older head-end
// would carry nulls (unknown — no badge either way).
export const HEADEND_SCAN: HeadEndScan = {
  instances: [
    {
      instanceId: "shack-north",
      host: "192.168.1.44",
      httpPort: 8080,
      source: "mdns",
      reachable: true,
      error: null,
      devices: [
        { deviceId: "usb-0", kind: "nino-tnc", model: "NinoTNC N9600A4", version: "3.44", serial: null, baud: 57600, free: true, bandCode: null, amateurBand: null, idSource: "by-path", idStable: true },
        { deviceId: "usb-1", kind: "tait-ccdi", model: "Tait TM8110", version: "1.10.0", serial: "19925328", baud: 28800, free: true, bandCode: "B1", amateurBand: "2m", idSource: "by-path", idStable: true },
        { deviceId: "usb-2", kind: "tait-ccdi", model: "Tait TM8115", version: "1.10.0", serial: "1G000999", baud: 28800, free: false, bandCode: null, amateurBand: null, idSource: "by-path", idStable: true },
      ],
      proposedPairs: [{ tncDeviceId: "usb-0", radioDeviceId: "usb-1", auto: true }],
      pairingAmbiguous: false,
    },
    {
      instanceId: "garage-pi",
      host: "192.168.1.51",
      httpPort: 8080,
      source: "config",
      reachable: true,
      error: null,
      devices: [
        { deviceId: "acm-0", kind: "nino-tnc", model: "NinoTNC N9600A4", version: "3.44", serial: null, baud: 57600, free: true, bandCode: null, amateurBand: null, idSource: "by-path", idStable: true },
        // A dev-fallback id: no by-path/by-id link, so the id is the kernel name — unstable across
        // replug. Exercises the "unstable id" warning badge.
        { deviceId: "ttyACM1", kind: "nino-tnc", model: "NinoTNC N9600A3", version: "3.41", serial: null, baud: 57600, free: true, bandCode: null, amateurBand: null, idSource: "dev", idStable: false },
        { deviceId: "usb-0", kind: "tait-ccdi", model: "Tait TM8110", version: "1.10.0", serial: "2G001111", baud: 28800, free: true, bandCode: "B1", amateurBand: "2m", idSource: "by-path", idStable: true },
        { deviceId: "usb-1", kind: "tait-ccdi", model: "Tait TM8200", version: "2.03.0", serial: "2G002222", baud: 19200, free: true, bandCode: "H5", amateurBand: "70cm", idSource: "by-path", idStable: true },
      ],
      proposedPairs: [
        { tncDeviceId: "acm-0", radioDeviceId: "usb-0", auto: false },
        { tncDeviceId: "acm-0", radioDeviceId: "usb-1", auto: false },
        { tncDeviceId: "ttyACM1", radioDeviceId: "usb-0", auto: false },
        { tncDeviceId: "ttyACM1", radioDeviceId: "usb-1", auto: false },
      ],
      pairingAmbiguous: true,
    },
    {
      instanceId: "attic-relay",
      host: "192.168.1.77",
      httpPort: 8080,
      source: "mdns",
      reachable: false,
      error: "connection refused — the head-end daemon is not answering on 192.168.1.77:8080",
      devices: [],
      proposedPairs: [],
      pairingAmbiguous: false,
    },
  ],
  conflicts: [
    { instanceId: "spare-pi", addresses: ["192.168.1.90:8080", "192.168.1.91:8080"] },
  ],
};

// The server's RF caveat (HeadEndKeyupCaveat.Text) — surfaced verbatim with every keyup response.
const KEYUP_CAVEAT =
  "RF WARNING: this action briefly keyed (transmitted through) each free NinoTNC on the head-end " +
  "to discover its physically-cabled radio by the PTT it asserts. It emits on-air and must only be " +
  "run by an operator on frequencies they are licensed and clear to key. It is never part of the " +
  "passive head-end scan.";

// Keyup-pairing result (POST /api/v1/radios/headends/{id}/pair-by-keyup) — the physical ground-truth
// map. garage-pi resolves its ambiguity (each keyup fired exactly one Tait's PTT); any other reachable
// instance pairs its first free TNC+radio; an unknown/unreachable id comes back reachable:false —
// exactly the live endpoint's honest-failure shape.
export function headEndKeyup(instanceId: string): HeadEndKeyupResult {
  if (instanceId === "garage-pi") {
    return {
      instanceId,
      reachable: true,
      error: null,
      pairs: [
        { tncDeviceId: "acm-0", radioDeviceId: "usb-1" },
        { tncDeviceId: "ttyACM1", radioDeviceId: "usb-0" },
      ],
      unpairedTncs: [],
      unpairedRadios: [],
      ambiguous: [],
      caveat: KEYUP_CAVEAT,
    };
  }
  const inst = HEADEND_SCAN.instances.find((i) => i.instanceId === instanceId && i.reachable);
  const tnc = inst?.devices.find((d) => d.free && d.kind === "nino-tnc");
  const radio = inst?.devices.find((d) => d.free && d.kind === "tait-ccdi");
  if (!inst || !tnc || !radio) {
    return {
      instanceId, reachable: false,
      error: `head-end '${instanceId}' was not found by the scan (or has no free TNC + radio)`,
      pairs: [], unpairedTncs: [], unpairedRadios: [], ambiguous: [], caveat: KEYUP_CAVEAT,
    };
  }
  return {
    instanceId, reachable: true, error: null,
    pairs: [{ tncDeviceId: tnc.deviceId, radioDeviceId: radio.deviceId }],
    unpairedTncs: [], unpairedRadios: [], ambiguous: [], caveat: KEYUP_CAVEAT,
  };
}

// Capability-doctor mock (GET/POST /api/v1/ports/{id}/doctor). A believable checklist per port so
// the "Check radio" surface renders with no node — covering all three states: pass (green), fail
// (red + remedy), unknown (grey). The transmitting probes (txdelay/sdm/pairing) are gated: `unknown`
// with a "requires a brief transmit" detail on the safe form, `pass` once the operator runs the full
// (interrupt) check — exactly the live server's safe-vs-interrupt behaviour.
const p = (name: string, status: DoctorProbe["status"], detail: string, remedy: string | null = null): DoctorProbe =>
  ({ name, status, detail, remedy });

const GATED = "requires a brief transmit — rerun with interrupt=true";

export function doctorReport(portId: string, interrupt: boolean): DoctorReport {
  let probes: DoctorProbe[];
  if (portId === "vhf-1") {
    // A NinoTNC + Tait radio, healthy. getrssi is an informational unknown (removed on 3.44 firmware).
    probes = [
      p("tnc-present", "pass", "GETVER answered: firmware 3.44"),
      p("getrssi", "unknown", "no reply in 2 s — removed in firmware 3.44 (was an undocumented 3.41 feature)", "meter deviation by decode-rate / FEC deltas instead"),
      p("dip-software-control", "pass", "DIPs 1111 — software control"),
      p("running-mode", "pass", "mode 6 (1200 AFSK AX.25)"),
      interrupt
        ? p("txdelay-software-control", "pass", "(mode pinned to 6 first) TXDELAY under software control (pot at minimum)")
        : p("txdelay-software-control", "unknown", GATED),
      p("radio-present", "pass", "Tait TM8110 s/n 19925328 (CCDI 1.10.0)"),
      p("progress-messages", "pass", "enabled for this session (FUNCTION 0/4/1 accepted)"),
      interrupt
        ? p("sdm", "pass", "wildcard SDM accepted (one short over-air transmission)")
        : p("sdm", "unknown", "SDM-enabled check " + GATED),
      interrupt
        ? p("tnc-radio-pairing", "pass", "radio reported PTT within 2 s of the TNC keying a frame")
        : p("tnc-radio-pairing", "unknown", GATED),
    ];
  } else if (portId === "uhf-2") {
    // A NinoTNC with the DIPs left in switch-pinned mode, and no radio attached.
    probes = [
      p("tnc-present", "pass", "GETVER answered: firmware 3.41"),
      p("getrssi", "pass", "available (firmware 3.41-era) — deviation meter fast path active (idle -0.0 dB)"),
      p("dip-software-control", "fail", "DIPs 0110 — mode pinned by switches", "set all four DIP switches up (1111) so KISS SETHW controls the mode"),
      p("running-mode", "pass", "mode 6 (1200 AFSK AX.25)"),
      interrupt
        ? p("txdelay-software-control", "pass", "(mode pinned to 6 first) TXDELAY under software control (pot at minimum)")
        : p("txdelay-software-control", "unknown", GATED),
      p("radio-attached", "unknown", "no radio attached to this port"),
    ];
  } else {
    // A serial-KISS (non-NinoTNC) modem with no radio — the degraded checklist.
    const notNino = "not a NinoTNC — this modem exposes no NinoTNC diagnostics";
    probes = [
      p("tnc-present", "unknown", notNino),
      p("getrssi", "unknown", notNino),
      p("dip-software-control", "unknown", notNino),
      p("running-mode", "unknown", notNino),
      p("txdelay-software-control", "unknown", notNino),
      p("radio-attached", "unknown", "no radio attached to this port"),
    ];
  }
  return { portId, probes, ranAt: new Date().toISOString() };
}

// ---- guided deviation tuning (mock backend) ----
// A scripted, converging tuned-session the /tools/tuner screen renders with no node: armed →
// peer-connected → a sequence of rounds whose decode-rate climbs and advice walks sweep → up → ok as
// the (imaginary) operator turns the pot. Each round is gated on the operator's "next" so the "Next
// round" button behaves like the real one.
const TUNE_ROUNDS: { decoded: number; total: number; advice: TuningAdvice; levelDb: number }[] = [
  { decoded: 0, total: 5, advice: "sweep", levelDb: -18.0 },
  { decoded: 2, total: 5, advice: "up", levelDb: -41.5 },
  { decoded: 4, total: 5, advice: "up", levelDb: -55.0 },
  { decoded: 5, total: 5, advice: "ok", levelDb: -62.5 },
  { decoded: 5, total: 5, advice: "ok", levelDb: -62.7 },
];

const ADVICE_NOTE: Record<TuningAdvice, string> = {
  up: "turn the deviation up",
  down: "turn the deviation down",
  ok: "leave the pot alone",
  sweep: "no decode — sweep the pot",
};

const tuneDrivers = new Map<string, () => void>();

export function tuneSession(portId: string, body: TuningStartRequest): TuningSessionInfo {
  return {
    sessionId: "mock-" + portId,
    portId,
    role: body.role,
    peerSdmId: body.peerSdmId,
    state: "armed",
    burstFrames: body.burstFrames ?? 5,
    startedAt: new Date().toISOString(),
  };
}

// Called by the mock api.tuneNext — advances the scripted stream to its next round.
export function tuneAdvance(portId: string): void {
  tuneDrivers.get(portId)?.();
}

// Drive a scripted tuning feed for a port. Returns an unsubscribe. onError is unused (the mock
// session never self-ends — the screen ends it via Stop, which unsubscribes).
export function driveTuneStream(
  portId: string,
  onEvent: (e: TuningEvent) => void,
  _onError?: () => void,
): () => void {
  let stopped = false;
  let round = 0;
  const timers: ReturnType<typeof setTimeout>[] = [];
  const now = () => new Date().toISOString();
  const emit = (e: TuningEvent) => { if (!stopped) onEvent(e); };
  const after = (ms: number, fn: () => void) => { timers.push(setTimeout(() => { if (!stopped) fn(); }, ms)); };

  const runRound = () => {
    const r = TUNE_ROUNDS[Math.min(round, TUNE_ROUNDS.length - 1)];
    round++;
    emit({
      kind: "round", at: now(), state: "peer-connected", burstIndex: round,
      decoded: r.decoded, total: r.total, levelDb: r.levelDb, rssiDbm: -90.3,
      advice: r.advice, note: ADVICE_NOTE[r.advice],
    });
    after(400, () => emit({ kind: "awaiting-adjustment", at: now(), state: "awaiting-adjustment" }));
  };

  emit({ kind: "armed", at: now(), state: "armed" });
  after(500, () => emit({ kind: "peer-connected", at: now(), state: "peer-connected" }));
  after(1200, runRound);
  tuneDrivers.set(portId, runRound);

  return () => {
    stopped = true;
    tuneDrivers.delete(portId);
    for (const t of timers) clearTimeout(t);
  };
}

// Heard stations (GET /api/v1/mheard) with last-heard RSSI where a radio measured it. Fixture data for
// a future MHeard view — lastRssiDbm is null for stations heard on a port with no radio attached.
export const HEARD_STATIONS: HeardStation[] = [
  { callsign: "M0LTE", portId: "vhf-1", firstHeard: "2:14:08", lastHeard: "0:00:12", count: 412, ports: 1, lastRssiDbm: -79 },
  { callsign: "2E0XYZ", portId: "vhf-1", firstHeard: "5:41:22", lastHeard: "0:01:47", count: 88, ports: 1, lastRssiDbm: -101 },
  { callsign: "G4APL-1", portId: "uhf-2", firstHeard: "9:02:51", lastHeard: "0:00:33", count: 1904, ports: 1, lastRssiDbm: null },
  { callsign: "G8PZT-7", portId: "link-dn", firstHeard: "1d 3:11:00", lastHeard: "0:00:03", count: 20441, ports: 1, lastRssiDbm: null },
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
// Rig-dial frequency grouping: 14_074_000 Hz → "14.074.000" (MHz.kHz.Hz, how transceivers
// render the dial). Callers add the unit suffix.
export function fmtRigFrequency(hz: number): string {
  const mhz = Math.floor(hz / 1_000_000);
  const khz = Math.floor(hz / 1_000) % 1_000;
  const rem = hz % 1_000;
  return `${mhz}.${String(khz).padStart(3, "0")}.${String(rem).padStart(3, "0")}`;
}

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
export const KIND_LABEL: Record<string, string> = { "kiss-tcp": "kiss-tcp", "serial-kiss": "serial-kiss", "nino-tnc": "ninotnc", "axudp": "axudp", "axudp-multipoint": "axudp-mp" };
export const KIND_USES_KISS: Record<string, boolean> = { "kiss-tcp": true, "serial-kiss": true, "nino-tnc": true, "axudp": false, "axudp-multipoint": false };

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
  netRomMinQuality: { label: "NET/ROM min quality", unit: "", help: "The worst route quality (0–255) a route learned on this port may have and still be kept (BPQ MINQUAL). Leave blank to inherit the node-wide minimum. Set a high floor on a busy or poor port (e.g. 100 on RF) so only good routes survive there." },
  nodesPaclen: { label: "NODES PACLEN", unit: "bytes", help: "Cap on the size of each NET/ROM NODES-broadcast frame (~28–256, BPQ NODESPACLEN). A large routing table fragments into several smaller frames so the broadcast stays robust on a slow or shared channel. Leave blank for no cap. Distinct from the connected-mode PACLEN (N1) above." },
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
  compress: { label: "Compress circuit data", desc: "Offer LinBPQ-style payload compression on NET/ROM circuits (BPQ L4Compress). It's negotiated per link, so a peer that doesn't support it transparently gets uncompressed data. Off by default — turn on only for links to compression-capable BPQ neighbours." },
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
