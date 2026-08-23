// ============================================================
// pdn - the FAKE NODE: fixture data + the behaviour behind VITE_API_MODE=mock.
// Typed port of the design handoff's pdn/data.jsx; field names match the real
// records (see types.ts / docs/node-ui-design.md §6).
//
// NOTHING outside api.ts's mock branch and the tests may import this file. It
// describes a node that does not exist (GB7RDG, ports vhf-1/uhf-2/link-dn), and
// every screen that reached in here for a default or a loading-state fallback was
// showing the operator that invented node instead of their own (#691 C021/C022).
// An eslint `no-restricted-imports` rule enforces it; the operator-facing copy,
// presets, help tables and unit helpers the screens DO need live in catalogue.ts.
// ============================================================
import type {
  NodeConfig, NetRomRoutingSnapshot, NodeStatus, PortStatus, SessionInfo,
  LinkStats, PeerCapability, MonitorEvent, FrameType,
  User, LogLine, NodeApp, AppPackage, AvailableApp,
  TailscaleStatus, SystemInfo,
  RadioStatus, RadioScanResult, HeardStation, HeadEndScan, HeadEndKeyupResult,
  DoctorReport, DoctorProbe,
  TuningStartRequest, TuningSessionInfo, TuningEvent, TuningAdvice,
  TaitProgramRequest, TaitProgramInfo, TaitProgramEvent, TaitProgramState,
  RigStatus, RigScan, RigModelCatalogue, SoundModemQualitySnapshot,
  NinoModeCatalogue,
} from "./types";
import { FRAME_TYPES, PIDS, NINO_MODES } from "./catalogue";

// 6.1 NodeConfig tree ----------------------------------------
export const NODE_CONFIG: NodeConfig = {
  // NodeConfig.CurrentSchemaVersion on the server. The mock claimed 3, a version the node has
  // never written (src/test/contract.test.ts now pins the whole document to the server's shape).
  schemaVersion: 2,
  identity: { callsign: "GB7RDG", alias: "RDGGW", grid: "IO91nl" },
  ports: [
    { id: "vhf-1", enabled: true, transport: { kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 }, profile: null, ax25: { t1Ms: 3000, t2Ms: 300, t3Ms: 180000, n2: 8, windowSize: 4, maxCachedPeers: 64 }, kiss: { txDelay: 30, persistence: 63, slotTime: 10, txTail: 5 }, beacon: { enabled: true, intervalMinutes: null, text: null }, link: null, radio: { kind: "tait-ccdi", serial: "19925328", baud: 28800 }, rig: { kind: "flrig", host: "127.0.0.1", port: 12345 } },
    { id: "uhf-2", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 }, profile: "slow-afsk1200", ax25: { t1Ms: 4000, t2Ms: 500, t3Ms: 180000, n2: 10, windowSize: 4, maxCachedPeers: 64 }, kiss: { txDelay: 40, persistence: 63, slotTime: 10, txTail: 8 }, beacon: { enabled: true, intervalMinutes: 15, text: "{node}:{call} UHF 9k6 data gateway QRV" }, link: null },
    { id: "link-dn", enabled: true, transport: { kind: "axudp", host: "44.131.91.2", port: 10093, localPort: 10093 }, profile: null, ax25: { t1Ms: 2000, t2Ms: 200, t3Ms: 180000, n2: 8, windowSize: 7, maxCachedPeers: 32 }, kiss: null, beacon: { enabled: false, intervalMinutes: null, text: null }, link: null },
    { id: "mp-net", enabled: true, transport: { kind: "axudp-multipoint", localPort: 10093, peers: [{ call: "N0CALL-1", host: "44.131.10.1", port: 10093, broadcast: true }, { call: "N0CALL-7", host: "44.131.10.2", port: 10094, broadcast: false }] }, profile: null, ax25: { t1Ms: 2000, t2Ms: 200, t3Ms: 180000, n2: 8, windowSize: 7, maxCachedPeers: 32 }, kiss: null, beacon: null, link: null, netRomMinQuality: 100, nodesPaclen: 160 },
    // The one port with a non-default LINK policy, so the Ports editor's Link setup controls are
    // exercised in mock mode: an HF port facing a LinBPQ neighbour, so it dials plain AX.25 2.0
    // (BPQ ignores a SABME rather than refusing it) and always leads with the XID that gets SREJ
    // at mod-8. Every other mock port carries link: null - the server's Auto + Auto.
    { id: "hf-300", enabled: false, transport: { kind: "serial-kiss", device: "/dev/ttyUSB1", baud: 38400 }, profile: "slow-afsk1200", ax25: { t1Ms: 8000, t2Ms: 1500, t3Ms: 300000, n2: 12, windowSize: 2, maxCachedPeers: 16 }, kiss: { txDelay: 25, persistence: 32, slotTime: 10, txTail: 10 }, beacon: null, link: { dial: "V20", preConnectXid: "On" }, radio: { kind: "tait-ccdi", port: "/dev/ttyUSB2", baud: 28800 }, rig: { kind: "hamlib", host: "127.0.0.1", port: 4532 } },
  ],
  services: { banner: "{node}:{call} — Reading & District packet gateway", prompt: "{node}:{call}}" },
  management: {
    telnet: { enabled: true, bind: "127.0.0.1", port: 8011 },
    http: { bind: "0.0.0.0", port: 8080 },
    https: { enabled: false, bind: "0.0.0.0", port: 8443, certificatePath: null, certificatePassword: null, generateSelfSignedOnMissing: true },
    auth: { enabled: false, accessTokenMinutes: null, refreshTokenMinutes: null, sysopElevationMinutes: null, webAuthn: { relyingPartyId: "localhost", relyingPartyName: "pdn node", allowedOrigins: [] } },
    mdns: { enabled: true, instanceName: null },
    console: { idleTimeoutMinutes: 30 },
  },
  netRom: {
    enabled: true, broadcast: true, routing: "Transit", forwardMode: "PerFlow",
    // The legacy pair `routing` replaced. A node that never carried them sends null for both.
    connect: null, forward: null,
    defaultNeighbourQuality: 192, minQuality: 40,
    obsoleteInitial: 6, obsoleteMinimum: 4, sweepIntervalSeconds: 300,
    window: 4, transportTimeoutSeconds: 60, transportRetries: 3, timeToLive: 25,
    compress: false,
    // The four timing knobs are seconds on the wire; these are the server defaults, as are
    // the six the Config screen does not surface but every GET carries.
    inp3: {
      enabled: true, preferInp3Routes: true, l3RttInterval: 60, l3RttResetWindow: 180,
      rifInterval: 300, positiveDebounce: 5,
      snttGainShift: 3, probeUnknownCapability: true, advertiseIpAccept: null,
      capabilityTextWidth: 8, hopLimit: 30, worsenThresholdMs: 1000,
    },
  },
  beacon: { enabled: true, intervalMinutes: 30, text: "{node}:{call} pdn node — Reading & District ARS" },
  rhp: { enabled: false, bind: "127.0.0.1", port: 9000, requireAuth: false, maxConnections: 64, maxHandlesPerClient: 256, inFrameTimeoutSeconds: 30 },
  mcp: { enabled: false, sse: { enabled: false, path: "/mcp" }, tokenLifetimeDays: 90, oauth: { enabled: false, accessTokenLifetimeMinutes: 60 } },
  applications: [],
  // The owner's overrides for the discovered packages in APP_PACKAGES - what the Apps screen writes.
  apps: [
    { id: "wall", enabled: true, command: "WALL", callsign: "M0ABC-1", netrom: null, environment: {} },
    { id: "bbs-bridge", enabled: true, command: "BBS", callsign: "M0ABC-3", netrom: { alias: "RDGBBS", quality: 255 }, environment: {} },
  ],
  appPackageRoots: null,
  traffic: { enabled: true, path: null, retentionDays: 14, maxMb: 512 },
  tailscale: { enabled: false, authKey: null, authKeyFile: null, hostname: "pdn", tags: [], stateDir: "/var/lib/packetnet/tsnet", target: "127.0.0.1:8080", funnel: false },
  oarc: {
    enabled: false, baseUrl: "https://node-api.packet.oarc.uk/",
    reportNodeStatus: true, reportLinks: true, reportCircuits: true,
    reportTraces: false, tracesRfOnly: true, publishExactPosition: false,
    statusIntervalSecs: 300, sessionStatusIntervalSecs: 60,
  },
  mqtt: {
    enabled: false, brokerHost: "", brokerPort: 1883, useTls: false,
    username: null, password: null, topicPrefix: "", nodeName: null, base64: false, qos: 2, rfOnly: false,
  },
  // Node-level soundmodem services, both off by default (server defaults). Enabling either opens a
  // dedicated audio device + a TCP listener — see the Services tab's ARDOP / POCSAG forms.
  paging: { enabled: false, device: "default", captureRate: 48000, bind: "127.0.0.1", port: 8106, baud: 1200, invertPolarity: false, ptt: "" },
  ardop: { enabled: false, device: "default", captureRate: 48000, bind: "127.0.0.1", port: 8515, ptt: "" },
  headEnds: [{ id: "shack-north", address: "192.168.1.44:8080" }],
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


// 6.2 NET/ROM routing snapshot -------------------------------
export const NETROM: NetRomRoutingSnapshot = {
  generatedAt: "2026-06-08T14:21:07Z",
  // GB7BNS is dual-homed: audible on vhf-1 AND uhf-2, so it is two adjacencies with their own
  // path qualities - a neighbour is keyed (port, callsign).
  neighbours: [
    { neighbour: "GB7BNS", alias: "BNSGW", portId: "vhf-1", pathQuality: 203, lastHeard: "0:00:14" },
    { neighbour: "GB7BNS", alias: "BNSGW", portId: "uhf-2", pathQuality: 168, lastHeard: "0:00:31" },
    { neighbour: "MB7UWS", alias: "UWSNOD", portId: "vhf-1", pathQuality: 168, lastHeard: "0:01:52" },
    { neighbour: "GB7CIP", alias: "CIPGW", portId: "uhf-2", pathQuality: 188, lastHeard: "0:00:41" },
    { neighbour: "G8PZT-7", alias: "KIDDER", portId: "link-dn", pathQuality: 222, lastHeard: "0:00:03" },
  ],
  destinations: [
    { destination: "GB7BNS", alias: "BNSGW", bestRoute: 0, routes: [{ neighbour: "GB7BNS", portId: "vhf-1", quality: 203, obsolescence: 6, inp3: { targetTimeMs: 240, hopCount: 1 } }, { neighbour: "GB7BNS", portId: "uhf-2", quality: 168, obsolescence: 6, inp3: null }] },
    { destination: "GB7CIP", alias: "CIPGW", bestRoute: 0, routes: [{ neighbour: "GB7CIP", portId: "uhf-2", quality: 188, obsolescence: 6, inp3: { targetTimeMs: 410, hopCount: 1 } }, { neighbour: "GB7BNS", portId: "vhf-1", quality: 142, obsolescence: 4, inp3: { targetTimeMs: 980, hopCount: 2 } }] },
    { destination: "G1MNW-2", alias: "READNG", bestRoute: 0, routes: [{ neighbour: "G8PZT-7", portId: "link-dn", quality: 199, obsolescence: 6, inp3: { targetTimeMs: 180, hopCount: 1 } }] },
    { destination: "GB7MAX", alias: "MAXGW", bestRoute: 0, routes: [{ neighbour: "G8PZT-7", portId: "link-dn", quality: 176, obsolescence: 5, inp3: { targetTimeMs: 620, hopCount: 3 } }, { neighbour: "GB7CIP", portId: "uhf-2", quality: 151, obsolescence: 4, inp3: null }] },
    { destination: "MB7UWS", alias: "UWSNOD", bestRoute: 0, routes: [{ neighbour: "MB7UWS", portId: "vhf-1", quality: 168, obsolescence: 6, inp3: null }] },
    { destination: "GB7SAN", alias: "SANGW", bestRoute: 0, routes: [{ neighbour: "GB7BNS", portId: "vhf-1", quality: 133, obsolescence: 3, inp3: { targetTimeMs: 1340, hopCount: 4 } }] },
  ],
};

// 6.4 node status -------------------------------------------
export const NODE_STATUS: NodeStatus = {
  callsign: "GB7RDG", alias: "RDGGW", grid: "IO91nl",
  version: "0.7.0-rc2 (b57f327)",
  uptimeSeconds: 1987260,
  portsUp: 3, portsTotal: 4, sessionCount: 4,
  netrom: { neighbours: 5, destinations: 6, inp3Enabled: true },
  traffic: { enabled: true, dropped: 0 },
};

// 6.4 port status -------------------------------------------
// Shapes the SERVER can actually produce (the contract fixtures pin them): a disabled port is
// `disabled`, never `faulted`, and a faulted one is enabled-but-not-serving with a reason.
const SINCE = "2026-08-17T11:52:00+00:00";
export const PORT_STATUS: Record<string, PortStatus> = {
  "vhf-1": { id: "vhf-1", enabled: true, state: "up", sessionCount: 2, lastError: null, framesIn: 184213, framesOut: 95120, degraded: [], since: SINCE, channelBusy: false },
  "uhf-2": { id: "uhf-2", enabled: true, state: "up", sessionCount: 1, lastError: null, framesIn: 52109, framesOut: 30877, degraded: [], since: SINCE, channelBusy: true },
  // Serving with its radio missing - the state the API could not express before #722.
  "link-dn": { id: "link-dn", enabled: true, state: "degraded", sessionCount: 1, lastError: "radio (tait-ccdi on /dev/ttyUSB2): open failed", framesIn: 421882, framesOut: 410337, degraded: ["radio"], since: SINCE, channelBusy: null },
  "hf-300": { id: "hf-300", enabled: true, state: "retrying", sessionCount: 0, lastError: "serial: /dev/ttyUSB1 not present", framesIn: 0, framesOut: 0, degraded: [], since: SINCE, channelBusy: null },
};

// 6.4 sessions ----------------------------------------------
export const SESSIONS: SessionInfo[] = [
  // The ids are the node's real convention (packet.net#723): `port:peer` for a circuit to the
  // node's own callsign, `port:peer>local` for one answered as an application callsign - which
  // is what the third row here is, and why the table can show the identity a caller reached.
  { id: "vhf-1:M0LTE", portId: "vhf-1", peer: "M0LTE", local: "GB7RDG", role: "console", state: "Connected", vs: 12, vr: 11, window: 4, uptimeSeconds: 842, bytesIn: 4821, bytesOut: 19233, lastActivity: "0:00:02" },
  { id: "vhf-1:2E0XYZ", portId: "vhf-1", peer: "2E0XYZ", local: "GB7RDG", role: "console", state: "TimerRecovery", vs: 3, vr: 7, window: 4, uptimeSeconds: 121, bytesIn: 980, bytesOut: 1422, lastActivity: "0:00:09" },
  { id: "uhf-2:G4APL-1>M0ABC-3", portId: "uhf-2", peer: "G4APL-1", local: "M0ABC-3", role: "bridge", state: "Connected", vs: 88, vr: 90, window: 4, uptimeSeconds: 5403, bytesIn: 71204, bytesOut: 60891, lastActivity: "0:00:01" },
  { id: "link-dn:G8PZT-7", portId: "link-dn", peer: "G8PZT-7", local: "GB7RDG", role: "interlink", state: "Connected", vs: 401, vr: 398, window: 7, uptimeSeconds: 91244, bytesIn: 2104882, bytesOut: 1988401, lastActivity: "0:00:00" },
];

// 6.3 monitor frame generation ------------------------------
export const CALLS = ["M0LTE", "2E0XYZ", "G4APL-1", "G8PZT-7", "GB7BNS", "GB7CIP", "MB7UWS", "G1MNW-2", "M7ABC", "GB7RDG", "2E1FOX", "G0HWC"];
export const PORTS_LIST = ["vhf-1", "uhf-2", "link-dn"];

export function randItem<T>(a: T[]): T { return a[Math.floor(Math.random() * a.length)]; }

// What the fake stream emits. The filter's FRAME_TYPES also carries the decoder's bare "U"
// fallback, which is what the server calls a U control octet it cannot name - not a frame
// anything would deliberately send, so the generator leaves it out.
const GENERATED_FRAME_TYPES: FrameType[] = FRAME_TYPES.filter((t) => t !== "U");

let _frameSeq = 9000;
// The mock stands in for one node process, so every frame it makes carries one boot id (the
// live node stamps a real one per process - see MonitorEvent.bootId).
const _bootId = "mock-boot";
export function makeFrame(now: Date): MonitorEvent {
  const type = randItem(GENERATED_FRAME_TYPES);
  const dir: "in" | "out" = Math.random() > 0.5 ? "in" : "out";
  const port = randItem(PORTS_LIST);
  let source = randItem(CALLS);
  let dest = randItem(CALLS);
  if (dir === "out") source = "GB7RDG";
  if (source === dest) dest = randItem(CALLS);
  const isI = type === "I";
  const isU = ["UI", "SABM", "SABME", "UA", "DISC", "DM", "XID", "FRMR", "TEST"].includes(type);
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
  else if (type === "TEST") summary = "TEST (loopback echo)";
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
    // The decoded first control octet + the info-field length the server also puts on the wire.
    // The generator does not synthesise a real control octet, so it reports the one value that
    // is always honest for a fixture: 0. infoLength IS derivable from the frame it made up.
    control: 0,
    infoLength: isI ? Math.max(0, length - 17) : type === "UI" ? Math.max(0, length - 15) : 0,
    path: Math.random() > 0.7 ? [randItem(["GB7BNS", "GB7CIP", "MB7UWS"])] : [],
    rssiDbm, snrDb, noiseFloorDbm, bootId: _bootId,
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
  { serial: "19925328", model: "Tait TM8110", ccdiVersion: "1.10.0", baud: 28800, devicePath: "/dev/ttyUSB0", byIdPath: "/dev/serial/by-id/usb-Silicon_Labs_CP2102_USB_to_UART_Bridge_Controller-if00-port0", bandCode: "B1", amateurBand: "2m" },
  { serial: "1G000123", model: "Tait TM8110", ccdiVersion: "1.10.0", baud: 28800, devicePath: "/dev/ttyUSB2", byIdPath: null, bandCode: null, amateurBand: null },
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

// Rig discovery scan (GET /api/v1/rigs/scan) — what "Scan for rigs" surfaces in the PortEditor's
// rig (CAT) section. Three rows covering every state the picker must render:
//   - an IC-7300 whose by-id descriptor matched the curated table AND the local hamlib catalogue
//     (a full suggestion: pick the row and the model fills itself);
//   - a device already claimed by a configured port (hf-300's serial-kiss modem on /dev/ttyUSB1)
//     — not pickable, the row says what claims it;
//   - a bare FTDI CAT cable with a by-id path but no suggestion (generic bridge chip — the
//     descriptor identifies the cable, not the rig behind it) — picking it requires the model picker.
export const RIG_SCAN: RigScan = {
  devices: [
    {
      devicePath: "/dev/ttyUSB3",
      byIdPath: "/dev/serial/by-id/usb-Icom_Inc._IC-7300_IC-7300_02012345-if00-port0",
      descriptor: "usb-Icom_Inc._IC-7300_IC-7300_02012345-if00-port0",
      claimedBy: null,
      suggestion: { manufacturer: "Icom", model: "IC-7300", modelNumber: 3073, source: "by-id" },
    },
    {
      devicePath: "/dev/ttyUSB1",
      byIdPath: null,
      descriptor: null,
      claimedBy: "port 'hf-300' transport (serial-kiss)",
      suggestion: null,
    },
    {
      devicePath: "/dev/ttyUSB4",
      byIdPath: "/dev/serial/by-id/usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0",
      descriptor: "usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0",
      claimedBy: null,
      suggestion: null,
    },
  ],
  catalogueAvailable: true,
};

// The node's hamlib model catalogue (GET /api/v1/rigs/models) — the editor's model picker source.
// A small believable slice of `rigctl -l`: Dummy (#1, hamlib's loopback test rig) and the IC-7300
// (#3073, matching the RIG_SCAN suggestion) plus a spread of manufacturers so filter-as-you-type
// has something to narrow.
export const RIG_MODELS: RigModelCatalogue = {
  available: true,
  models: [
    { number: 1, manufacturer: "Hamlib", model: "Dummy", status: "Stable" },
    { number: 1035, manufacturer: "Yaesu", model: "FT-857", status: "Stable" },
    { number: 2031, manufacturer: "Kenwood", model: "TS-590S", status: "Stable" },
    { number: 2311, manufacturer: "Elecraft", model: "K3", status: "Stable" },
    { number: 3073, manufacturer: "Icom", model: "IC-7300", status: "Stable" },
    { number: 3081, manufacturer: "Icom", model: "IC-9700", status: "Stable" },
  ],
};

// The node's NinoTNC mode table (GET /api/v1/modems/nino-tnc/modes). The catalogue is a fixed
// 16-row constant, not sample data, so the mock IS the real table - NINO_MODES, the same
// fallback the editor renders offline. Nothing to invent here, and inventing it is exactly the
// mistake this endpoint exists to undo.
export const NINO_MODE_CATALOGUE: NinoModeCatalogue = { modes: NINO_MODES };

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
/** Synthetic waterfall for VITE_API_MODE=mock: noise floor, a slowly drifting carrier,
 *  and periodic packet-shaped bursts around 1700 Hz. ~3 lines/s, 2048 bins at 2.93 Hz. */
export function driveSpectrumStream(
  _id: string,
  onLine: (bins: Uint8Array, binHz: number) => void,
): () => void {
  const bins = 2048;
  const binHz = 12000 / 4096;
  let t = 0;
  const timer = window.setInterval(() => {
    t += 1;
    const line = new Uint8Array(bins);
    for (let i = 0; i < bins; i++) line[i] = 30 + Math.floor(Math.random() * 25);
    // Drifting carrier at ~2.2 kHz.
    const carrier = Math.floor((2200 + 150 * Math.sin(t / 20)) / binHz);
    for (let d = -2; d <= 2; d++) {
      const k = carrier + d;
      if (k >= 0 && k < bins) line[k] = Math.max(line[k], 220 - 40 * Math.abs(d));
    }
    // A packet burst around 1200–2200 Hz every few seconds.
    if (t % 12 < 4) {
      const lo = Math.floor(1000 / binHz);
      const hi = Math.floor(2400 / binHz);
      for (let k = lo; k < hi; k++) line[k] = Math.max(line[k], 150 + Math.floor(Math.random() * 60));
    }
    onLine(line, binHz);
  }, 330);
  return () => window.clearInterval(timer);
}

// Rolling soundmodem receive-quality snapshot (GET /api/v1/ports/{id}/quality) — the waterfall's
// FrameQuality readout demos with no node. A believable IL2P link quietly spending a little of its
// FEC budget: most frames clean, a few Reed-Solomon-corrected. `recent` is newest-first; the winning
// branch's small +Δf / emphasis on a clean signal is first-past-the-post, NOT the peer's error. The
// oldest seeded frame is a plain-HDLC afsk1200 frame → correctedBytes/crcValid null (kept distinct
// from 0, a clean IL2P frame) so the null-vs-0 render is exercised too.
export const SOUNDMODEM_QUALITY: SoundModemQualitySnapshot = {
  frames: 1842,
  cumulativeCorrectedBytes: 271,
  framesWithCorrections: 63,
  lastFrameCorrectedBytes: 2,
  recent: [
    { receivedAt: new Date(Date.now() - 1200).toISOString(), mode: "qpsk2400-il2pc", frameBytes: 128, correctedBytes: 2, crcValid: true, frequencyOffsetHz: 12, emphasisDb: 3 },
    { receivedAt: new Date(Date.now() - 4800).toISOString(), mode: "qpsk2400-il2pc", frameBytes: 96, correctedBytes: 0, crcValid: true, frequencyOffsetHz: -6, emphasisDb: 0 },
    { receivedAt: new Date(Date.now() - 9100).toISOString(), mode: "afsk1200", frameBytes: 47, correctedBytes: null, crcValid: null, frequencyOffsetHz: null, emphasisDb: null },
  ],
};

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

// ---- Tait codeplug programming (#779) ----------------------
// A scripted run for VITE_API_MODE=mock: the real thing spends most of its time waiting for a human
// to power-cycle a radio, so the demo walks the same states on a short timer. Cancelling ends it
// where a real cancel would.
const programRuns = new Map<string, TaitProgramInfo>();
const programCancels = new Map<string, () => void>();

// What the scripted radio is "currently" set to: reported by a read run, and by a write run as the
// record of what it replaced.
const MOCK_CURRENT = {
  rxFrequencyHz: 144_850_000,
  txFrequencyHz: 144_850_000,
  bandwidth: "narrow" as const,
  power: "medium" as const,
  profile: "none" as const,
  channelCount: 6,
  databaseVersion: "0095",
  rxTone: "none",
  txTone: "none",
};

export function startRadioProgram(portId: string, body: TaitProgramRequest): TaitProgramInfo {
  const run: TaitProgramInfo = {
    portId,
    mode: "program",
    state: "starting",
    startedAt: new Date().toISOString(),
    finishedAt: null,
    devicePath: "/dev/ttyUSB1",
    plan: {
      rxFrequencyHz: body.rxFrequencyHz,
      txFrequencyHz: body.txFrequencyHz ?? body.rxFrequencyHz,
      bandwidth: body.bandwidth,
      power: body.power,
      profile: body.profile,
      replaceChannelTable: body.replaceChannelTable ?? true,
    },
    current: null,
    radioModel: null,
    radioSerial: null,
    backupPath: null,
    error: null,
    failedState: null,
    log: [],
  };
  programRuns.set(portId, run);
  return run;
}

export function readRadioProgram(portId: string): TaitProgramInfo {
  const run: TaitProgramInfo = {
    portId,
    mode: "read",
    state: "starting",
    startedAt: new Date().toISOString(),
    finishedAt: null,
    devicePath: "/dev/ttyUSB1",
    plan: null,
    current: null,
    radioModel: null,
    radioSerial: null,
    backupPath: null,
    error: null,
    failedState: null,
    log: [],
  };
  programRuns.set(portId, run);
  return run;
}

export function radioProgram(portId: string): TaitProgramInfo | null {
  return programRuns.get(portId) ?? null;
}

export function cancelRadioProgram(portId: string): void {
  programCancels.get(portId)?.();
}

export function driveRadioProgramStream(
  portId: string,
  onEvent: (e: TaitProgramEvent) => void,
  onError?: () => void,
): () => void {
  let stopped = false;
  const timers: ReturnType<typeof setTimeout>[] = [];
  const now = () => new Date().toISOString();
  const settle = (state: TaitProgramState, error?: string) => {
    const run = programRuns.get(portId);
    if (run) {
      programRuns.set(portId, {
        ...run, state, error: error ?? null, finishedAt: new Date().toISOString(),
        radioModel: "TMAB12-B100_0201", radioSerial: "19925328", current: MOCK_CURRENT,
        backupPath: "/var/lib/packetnet/codeplug-backups/tait-19925328-20260823-101500.m8p",
      });
    }
  };
  const emit = (e: TaitProgramEvent) => {
    if (stopped) return;
    const run = programRuns.get(portId);
    if (run && !run.finishedAt) {
      programRuns.set(portId, {
        ...run, state: e.state,
        log: e.message ? [...(run.log ?? []), e.message] : (run.log ?? []),
      });
    }
    onEvent(e);
  };
  const after = (ms: number, fn: () => void) => { timers.push(setTimeout(() => { if (!stopped) fn(); }, ms)); };
  const readOnly = programRuns.get(portId)?.mode === "read";
  const end = (state: TaitProgramState, message: string, error?: string) => {
    settle(state, error);
    emit({ kind: "state", at: now(), state, message, fraction: null, error: error ?? null, failedState: null });
    onError?.();
  };

  emit({ kind: "state", at: now(), state: "starting", message: readOnly ? `reading the radio on ${portId}` : `programming ${portId}`, fraction: null, error: null, failedState: null });
  after(600, () => emit({ kind: "state", at: now(), state: "power-cycle", message: "power-cycle the radio now", fraction: null, error: null, failedState: null }));
  after(3000, () => emit({ kind: "state", at: now(), state: "reading", message: "radio TMAB12-B100_0201 s/n 19925328", fraction: null, error: null, failedState: null }));
  for (let i = 1; i <= 4; i++) {
    after(3000 + i * 400, () => emit({ kind: "progress", at: now(), state: "reading", message: `section ${i * 11}`, fraction: i / 5, error: null, failedState: null }));
  }
  if (!readOnly) {
    after(5200, () => emit({ kind: "state", at: now(), state: "writing", message: "writing the codeplug back", fraction: null, error: null, failedState: null }));
    for (let i = 1; i <= 4; i++) {
      after(5200 + i * 400, () => emit({ kind: "progress", at: now(), state: "writing", message: `record ${i * 250} of 1000`, fraction: i / 5, error: null, failedState: null }));
    }
  }
  after(7000, () => emit({ kind: "state", at: now(), state: "restoring", message: readOnly ? "codeplug read; bringing the port back into service" : "1000 records written; bringing the port back into service", fraction: null, error: null, failedState: null }));
  after(8000, () => end("done", readOnly
    ? "done - the codeplug was read and the port is back in service"
    : "done - the radio is programmed and the port is back in service"));

  programCancels.set(portId, () => end("cancelled", "cancelled - the port is back in service"));

  return () => {
    stopped = true;
    programCancels.delete(portId);
    for (const t of timers) clearTimeout(t);
  };
}

// Heard stations (GET /api/v1/mheard) with last-heard RSSI where a radio measured it. Fixture data for
// a future MHeard view — lastRssiDbm is null for stations heard on a port with no radio attached.
export const HEARD_STATIONS: HeardStation[] = [
  { callsign: "M0LTE", portId: "vhf-1", firstHeard: "2:14:08", lastHeard: "0:00:12", count: 412, ports: 1, lastRssiDbm: -79, lastSnrDb: 24.5, medianPreDataCarrierMs: 212, preDataCarrierSamples: 18, txDelayAdvisory: "300 ms of TXDELAY, ~90 ms more than this station needs" },
  { callsign: "2E0XYZ", portId: "vhf-1", firstHeard: "5:41:22", lastHeard: "0:01:47", count: 88, ports: 1, lastRssiDbm: -101, lastSnrDb: 8.0, medianPreDataCarrierMs: null, preDataCarrierSamples: 0, txDelayAdvisory: null },
  { callsign: "G4APL-1", portId: "uhf-2", firstHeard: "9:02:51", lastHeard: "0:00:33", count: 1904, ports: 1, lastRssiDbm: null, lastSnrDb: null, medianPreDataCarrierMs: null, preDataCarrierSamples: 0, txDelayAdvisory: null },
  { callsign: "G8PZT-7", portId: "link-dn", firstHeard: "1d 3:11:00", lastHeard: "0:00:03", count: 20441, ports: 1, lastRssiDbm: null, lastSnrDb: null, medianPreDataCarrierMs: null, preDataCarrierSamples: 0, txDelayAdvisory: null },
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
// bbs-bridge is the PINNED-callsign fixture (pinnedCallsign === callsign); wall/quiz carry a
// node auto-assigned callsign, so their pinnedCallsign is null.
// The api.ts mock mutation path updates these in place so a refetch shows the result.
export const APP_PACKAGES: AppPackage[] = [
  { id: "wall", name: "WALL", version: "1.2.0", description: "Shared message wall — leave a note for the next station", icon: "message-square", capabilities: ["session", "web"], enabled: true, source: "package", installed: true, error: null, service: "managed", state: "Running", pid: 4711, detail: null, forwards: [], command: "WALL", callsign: "M0ABC-1", pinnedCallsign: null, netromAlias: null, netromQuality: null },
  { id: "lobby", name: "LOBBY", version: "0.9.1", description: "Multi-user chat lobby", icon: "users", capabilities: ["session"], enabled: false, source: "package", installed: true, error: null, service: "managed", state: "Stopped", pid: null, detail: null, forwards: [], command: "LOBBY", callsign: null, pinnedCallsign: null, netromAlias: null, netromQuality: null },
  { id: "quiz", name: "QUIZ", version: "2.0.0", description: "Trivia over packet", icon: null, capabilities: ["session"], enabled: true, source: "package", installed: true, error: null, service: "managed", state: "Faulted", pid: null, detail: "exited 5 times in 30s (exit code 1) — giving up until restarted", forwards: [], command: "QUIZ", callsign: "M0ABC-2", pinnedCallsign: null, netromAlias: null, netromQuality: null },
  { id: "bbs-bridge", name: "BBS bridge", version: "0.3.0", description: "Bridges sessions to an externally-run BBS process", icon: null, capabilities: ["session"], enabled: true, source: "package", installed: true, error: null, service: "external", state: "External", pid: null, detail: null, forwards: [], command: "BBS", callsign: "M0ABC-3", pinnedCallsign: "M0ABC-3", netromAlias: "RDGBBS", netromQuality: 255 },
  { id: "wx", name: "wx", version: null, description: null, icon: null, capabilities: [], enabled: false, source: "package", installed: true, error: "pdn-app.yaml: missing required field 'command'", service: "none", state: null, pid: null, detail: null, forwards: [], command: null, callsign: null, pinnedCallsign: null, netromAlias: null, netromQuality: null },
  { id: "motd", name: "MOTD", version: null, description: null, icon: null, capabilities: ["session"], enabled: true, source: "inline", installed: true, error: null, service: "none", state: null, pid: null, detail: null, forwards: [], command: "MOTD", callsign: null, pinnedCallsign: null, netromAlias: null, netromQuality: null },
  { id: "notes", name: "Notes", version: "1.0.0", description: "Static node notice board — no service process", icon: "sticky-note", capabilities: [], enabled: false, source: "package", installed: true, error: null, service: "none", state: null, pid: null, detail: null, forwards: [], command: null, callsign: null, pinnedCallsign: null, netromAlias: null, netromQuality: null },
  // A BBS-style app that asks pdn to expose mail ports on the tailnet (a capability the
  // owner sees in the enable confirm — docs/network-access.md § App-declared port forwarding).
  { id: "mail", name: "Mail", version: "1.0.0", description: "IMAP/SMTP mailbox over the tailnet", icon: "inbox", capabilities: ["network"], enabled: false, source: "package", installed: true, error: null, service: "managed", state: "Stopped", pid: null, detail: null, forwards: [{ listen: 993, target: "127.0.0.1:1430", tls: "terminate" }, { listen: 465, target: "127.0.0.1:1465", tls: "terminate" }], command: null, callsign: null, pinnedCallsign: null, netromAlias: null, netromQuality: null },
  // chat: configured and ENABLED in `apps:`, but no root holds its package: the app the node
  // knows about and cannot run (packet.net#738 item 2). No manifest, so no name/version/
  // capabilities: the row renders from `installed: false` alone.
  { id: "chat", name: "chat", version: null, description: null, icon: null, capabilities: [], enabled: true, source: "package", installed: false, error: null, service: "none", state: null, pid: null, detail: null, forwards: [], command: null, callsign: null, pinnedCallsign: null, netromAlias: null, netromQuality: null },
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



