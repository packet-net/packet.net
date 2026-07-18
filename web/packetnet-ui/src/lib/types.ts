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
export type TransportKind = "kiss-tcp" | "serial-kiss" | "nino-tnc" | "axudp" | "axudp-multipoint" | "soundmodem";

export interface KissTcpTransport { kind: "kiss-tcp"; host: string; port: number }
export interface SerialKissTransport { kind: "serial-kiss"; device: string; baud: number }
export interface NinoTncTransport { kind: "nino-tnc"; device: string; baud: number; mode: number }
export interface AxudpTransport { kind: "axudp"; host: string; port: number; localPort: number }
// One multipoint-AXUDP partner — a BPQ `MAP <call> <ip> UDP <port> [B]` line
// (server: Packet.Node.Core.Configuration.AxudpPeerConfig). `call` is the routing
// key (an outbound frame whose AX.25 destination is this callsign goes to this peer);
// `broadcast` is the BPQ `B` suffix — fan NODES/ID/BEACON broadcasts to this peer.
export interface AxudpPeer { call: string; host: string; port: number; broadcast: boolean }
// Multipoint AXUDP — the BPQ BPQAXIP analog: ONE UDP socket bound to `localPort`
// reaches MANY partners, each addressed by callsign (server:
// Packet.Node.Core.Configuration.AxudpMultipointTransport). Replaces the point-to-point
// `axudp` host/port with a `peers[]` partner table.
export interface AxudpMultipointTransport { kind: "axudp-multipoint"; localPort: number; peers: AxudpPeer[] }
// FlexRadio slice tuning for a `flex:` soundmodem device (headless slice control;
// server: Packet.Node.Core.Configuration.SoundModemFlexConfig). Used only when
// `device` is a `flex:` device — ignored for ALSA devices.
export interface SoundModemFlex {
  frequency?: string; antenna?: string; mode?: string; daxChannel?: string;
}
// In-process soundcard modem (the pdn-soundmodem engine) — native DCD, sample-accurate
// TX-complete (server: Packet.Node.Core.Configuration.SoundModemTransportConfig).
//   offsetPairs/offsetStepHz — the bpsk300 differential frequency-diversity bank knobs
//     (2·offsetPairs+1 stepped decoder branches; ignored by non-bank modes).
//   pskDetector — "coherent" | "differential" for the bpsk*/qpsk* modes (null = the
//     per-family default: BPSK differential, QPSK coherent).
//   flex — FlexRadio slice tuning, only for a `flex:` device.
export interface SoundModemTransport {
  kind: "soundmodem"; device: string; captureRate: number; mode: string;
  frequency?: number; ptt?: string;
  offsetPairs?: number; offsetStepHz?: number;
  pskDetector?: "coherent" | "differential";
  flex?: SoundModemFlex;
}
export type TransportConfig =
  | KissTcpTransport
  | SerialKissTransport
  | NinoTncTransport
  | AxudpTransport
  | AxudpMultipointTransport
  | SoundModemTransport;

export interface Ax25PortParams {
  t1Ms?: number; t2Ms?: number; t3Ms?: number;
  n2?: number; windowSize?: number; maxCachedPeers?: number;
  // N1 / PACLEN: max information-field length (octets). Null = engine default (256).
  // Lower it (~80) on a slow/lossy medium (HF) to keep frames short. XID can only
  // negotiate it down. See Packet.Node.Core.Configuration.Ax25PortParams.N1.
  n1?: number;
}
export interface KissParams {
  txDelay?: number; persistence?: number; slotTime?: number; txTail?: number;
  // Pace outbound TX over the G8BPQ ACKMODE extension (kiss-tcp ports only). Default
  // false. Unlike the other knobs this is construction-time — toggling it restarts the
  // port. See Packet.Node.Core.Configuration.KissParams.AckMode.
  ackMode?: boolean;
}
// Per-port AX.25 compatibility profile (Packet.Node.Core.Configuration.PortCompatConfig).
// preset picks the Ax25ParseOptions preset (null = lenient, the historical default);
// the nullable booleans override individual flags on top of the preset; quirks selects
// the SDL session-quirks set (null = default, the spec-correct one). Absent/null compat
// = lenient + default — no behavioural change.
export type CompatPreset = "strict" | "lenient" | "bpq" | "xrouter" | "direwolf";
export interface PortCompatConfig {
  preset?: CompatPreset | null;
  allowEmptyCallsignBase?: boolean | null;
  allowInfoOnSupervisoryFrames?: boolean | null;
  allowCommandFrameAsResponse?: boolean | null;
  quirks?: "default" | "strictly-faithful" | null;
}
// Optional per-port radio-control attachment (server: Packet.Node.Core.Configuration.PortRadioConfig).
// The serial control channel to the radio behind this port's modem — a SEPARATE serial device from
// the modem's. When present, every inbound frame carries per-frame RSSI/SNR sampled from the radio's
// control channel (the signal data KISS can't provide). Pin which radio EITHER by CCDI serial
// (preferred/stable — survives /dev/ttyUSB* renumbering and shared-USB-serial ambiguity) OR by device
// path: exactly one of `serial`/`port`. Only valid on the serial-modem transport kinds (serial-kiss,
// nino-tnc). Null/absent = no radio attached.
export interface RadioConfig {
  kind: "tait-ccdi";
  serial?: string;
  port?: string;
  baud?: number;
  // How often (seconds) the health monitor samples the radio; null/absent = driver default (10 s).
  // Not surfaced by the editor — carried through untouched so a YAML-set value survives a save.
  healthIntervalSeconds?: number | null;
}
// Optional per-port rig-control (CAT) attachment (server: Packet.Node.Core.Configuration.PortRigConfig).
// The station-control sibling of the radio: block — the CAT channel to the transceiver behind this
// port's modem (dial frequency / mode / PTT / TX meters); it never touches the packet path, so unlike
// radio: it is valid on EVERY transport kind. Two binding shapes, selected by `device` being set
// (mirrors the server's PortRigConfig.IsNodeManaged authority):
//   node-managed — { kind: "hamlib", device, model, serialSpeed? }: the node spawns + supervises
//     rigctld -m <model> -r <device> on a loopback port it allocates itself (host/port stay unset;
//     hamlib only — flrig is a GUI app the node can't spawn). Prefer the /dev/serial/by-id device
//     path: it survives /dev/ttyUSB* renumbering across replug/reboot.
//   BYO daemon  — { kind: "hamlib"|"flrig", host, port? }: dial a rigctld/flrig the operator already
//     runs (model/serialSpeed stay unset; a null port means the kind default — 4532 / 12345).
export interface RigConfig {
  kind: "hamlib" | "flrig";
  host?: string;
  port?: number;
  device?: string;
  model?: number;
  serialSpeed?: number;
  // Poll cadences (idle / keyed), seconds. Not surfaced by the editor — carried through untouched
  // so a YAML-set value survives a Forms save (mirrors RadioConfig.healthIntervalSeconds).
  pollIntervalSeconds?: number | null;
  meterIntervalSeconds?: number | null;
}
export interface PortConfig {
  id: string;
  enabled: boolean;
  transport: TransportConfig;
  profile: string | null;
  ax25: Ax25PortParams | null;
  kiss: KissParams | null;
  beacon: PortBeacon | null;
  compat?: PortCompatConfig | null;
  // Per-port radio-control attachment (RSSI/health). Null/absent = no radio.
  radio?: RadioConfig | null;
  // Per-port rig-control (CAT) attachment. Null/absent = no rig.
  rig?: RigConfig | null;
  // Per-port NET/ROM route quality (BPQ per-port QUALITY), 0..255. Null = inherit the
  // node-wide netRom.defaultNeighbourQuality. See Packet.Node.Core.Configuration.PortConfig.NetRomQuality.
  netRomQuality?: number | null;
  // Per-port NET/ROM minimum quality (BPQ per-port MINQUAL), 0..255. The worst quality a
  // route learned on this port may have and still be kept. Null = inherit the node-wide
  // netRom.minQuality. See Packet.Node.Core.Configuration.PortConfig.NetRomMinQuality.
  netRomMinQuality?: number | null;
  // Per-port cap (octets) on a NET/ROM NODES-broadcast UI frame (BPQ per-port NODESPACLEN),
  // ~28..256. Large NODES tables fragment into frames no larger than this. Null = no cap
  // (the structural 11-entries limit). See Packet.Node.Core.Configuration.PortConfig.NodesPaclen.
  nodesPaclen?: number | null;
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
// WebAuthn / passkey relying-party config (server: Packet.Node.Core.Configuration.WebAuthnConfig).
// The RP id + allowed origins the "Use <fqdn> for passkeys" action writes.
export interface WebAuthnConfig {
  relyingPartyId: string;
  relyingPartyName: string;
  allowedOrigins: string[];
}
// Web control-API auth (server: AuthConfig). Only the fields the UI touches are typed; the
// rest round-trip untouched through the structured PUT.
export interface AuthConfig {
  enabled: boolean;
  accessTokenMinutes?: number | null;
  refreshTokenMinutes?: number | null;
  webAuthn: WebAuthnConfig;
  sysopElevationMinutes?: number | null;
}
export interface ManagementConfig {
  telnet: TelnetConfig;
  http: HttpConfig;
  https: HttpsConfig;
  auth: AuthConfig;
}
// The embedded Tailscale tsnet sidecar config (server: TailscaleConfig). Default-off.
export interface TailscaleConfig {
  enabled: boolean;
  authKey?: string | null;
  authKeyFile?: string | null;
  hostname: string;
  tags: string[];
  stateDir: string;
  target: string;
  funnel: boolean;
}
export interface Inp3Config {
  enabled: boolean; preferInp3Routes: boolean;
  l3RttInterval: number; l3RttResetWindow: number;
  rifInterval: number; positiveDebounce: number;
}
// OARC network-map reporting (server: Packet.Node.Core.Configuration.OarcConfig).
// Default-off: nothing is reported until `enabled`. All outbound — the node POSTs its
// own telemetry to the OARC collector; the collector never reaches in. Each category is
// independently toggleable; traces is the high-volume per-frame firehose (opt-in).
//   baseUrl                   — collector URL (an absolute http(s) URL).
//   reportNodeStatus          — node up/status/down heartbeats.
//   reportLinks               — L2 link events + stats.
//   reportCircuits            — L4 NET/ROM circuit events + stats.
//   reportTraces              — per-frame L2 trace firehose (high volume, most revealing).
//   tracesRfOnly              — when traces on, only over-air (RF) frames.
//   publishExactPosition      — publish exact lat/lon vs locator-only.
//   statusIntervalSecs        — node-status heartbeat cadence.
//   sessionStatusIntervalSecs — link/circuit status refresh cadence.
export interface OarcConfig {
  enabled: boolean;
  baseUrl: string;
  reportNodeStatus: boolean;
  reportLinks: boolean;
  reportCircuits: boolean;
  reportTraces: boolean;
  tracesRfOnly: boolean;
  publishExactPosition: boolean;
  statusIntervalSecs: number;
  sessionStatusIntervalSecs: number;
}
export type NetRomForwardMode = "PerFlow" | "Single";
// The node's NET/ROM routing role — the single 3-state successor to the old
// connect+forward bools (server: Packet.Node.Core.Configuration.NetRomRouting):
//   None     — passive: hear + maintain the table only (no interlinks, no transit).
//   Endpoint — open interlinks for our own connect <alias>, but don't relay transit.
//   Transit  — full router: interlinks + relay third-party transit.
// The legacy connect/forward keys are still accepted by the server for back-compat,
// but the UI edits only this knob.
export type NetRomRouting = "None" | "Endpoint" | "Transit";
export interface NetRomConfig {
  enabled: boolean; broadcast: boolean;
  routing: NetRomRouting; forwardMode: NetRomForwardMode;
  // The node's NET/ROM alias is unified with identity.alias (the single node-name concept).
  defaultNeighbourQuality?: number; minQuality?: number;
  obsoleteInitial?: number; obsoleteMinimum?: number; sweepIntervalSeconds?: number;
  window?: number; transportTimeoutSeconds?: number; transportRetries?: number; timeToLive?: number;
  // Offer LinBPQ-style negotiated NET/ROM L4 payload compression on circuits (BPQ L4Compress).
  // Default false (decline) — the interop-safe path. Turn on only for links to compression-capable
  // BPQ neighbours. See Packet.Node.Core.Configuration.NetRomConfig.Compress.
  compress: boolean;
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
  tailscale: TailscaleConfig;
  oarc: OarcConfig;
}

// ---- system info / self-update (server: PdnSystemApi.SystemInfoResponse) ----
// GET /api/v1/system/info — the node's running version, install channel, what an
// update would do, and whether a newer version is known (the shared API contract the
// control panel's About panel + "update available" banner consume).
//   channel: "apt" | "github" | "selfcontained" | "unknown" — "unknown" disables Apply
//     (the node won't self-update; update via the package manager / reinstall).
//   updateMechanism: what POST /system/update does here ("apt" | "github" | "selfcontained" | "none").
//   updateAvailable + latestVersion: the per-channel available-version check; when true,
//     latestVersion is the version the banner offers (else null).
export type InstallChannelName = "apt" | "github" | "selfcontained" | "unknown";
export interface SystemInfo {
  version: string;
  channel: InstallChannelName;
  updateMechanism: string;
  updateAvailable: boolean;
  latestVersion: string | null;
}

// ---- Tailscale sidecar status (server: PdnSystemApi.TailscaleStatusResponse) ----
// GET /api/v1/system/tailscale. state: disabled | starting | needs-login | running | error.
export type TailscaleState = "disabled" | "starting" | "needs-login" | "running" | "error";
export interface TailscaleStatus {
  enabled: boolean;
  state: TailscaleState;
  fqdn: string | null;
  authUrl: string | null;
  funnel: boolean;
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
  /** Port-level carrier sense (radio DCD or a channel-sensing transport such as the
   *  in-process soundmodem): true=busy, false=clear, null=no source / not running. */
  channelBusy: boolean | null;
}

/** One waterfall line from a soundmodem port's spectrum SSE feed. */
export interface SpectrumEvent {
  seq: number;
  /** Hz per bin (bins run 0 Hz .. bins.length*binHz). */
  binHz: number;
  /** Base64 of dB-scaled bytes, one per bin. */
  bins: string;
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

// ---- radio-control status + health (server: Packet.Node.Core.Api.RadioStatus) ----
// GET /api/v1/radios → RadioStatus[]; GET /api/v1/ports/{id}/radio → RadioStatus (404 unknown port).
// One per port that has a `radio:` block. `attached` distinguishes a radio the node has open and is
// polling (true) from one configured but not currently attached (false — port down, or the radio
// failed to open and the port degraded to running without it).
export type RadioConnectionState = "healthy" | "faulted" | "unknown";
// A radio's self-reported identity (CCDI MODEL / versions queries).
export interface RadioIdentity { model: string; ccdiVersion: string }
// The latest radio-health sample, projected for the operator surface. RSSI is read only while the
// receiver is un-muted (not transmitting); the forward/reverse figures land only on transmit samples.
// NOTE the forward/reverse detector figures are an antenna-health TREND, NEVER VSWR — the detectors
// are uncalibrated and √P-scaled; alert on a station's *change*, not the absolute value.
export interface RadioHealth {
  rssiDbm: number | null;
  averagedRssiDbm: number | null;
  paTemperatureC: number | null;
  forwardTrendMillivolts: number | null;
  reverseTrendMillivolts: number | null;
  reverseForwardRatio: number | null;
  sampleAt: string | null;
}
export interface RadioStatus {
  portId: string;
  attached: boolean;
  kind: string;
  controlPort: string | null;
  serial: string | null;
  identity: RadioIdentity | null;
  connectionState: RadioConnectionState;
  channelBusy: boolean | null;
  health: RadioHealth | null;
}
// ---- rig-control (CAT) status (server: Packet.Node.Core.Api.RigStatus) ----
// GET /api/v1/rigs → RigStatus[]; GET /api/v1/ports/{id}/rig → RigStatus (404 unknown port);
// GET /api/v1/rigs/events → SSE, one `event: rig` per poll tick per attached rig. One per port
// that has a `rig:` block — the station-control sibling of RadioStatus (a port can carry both).
// `capabilities` is the render contract: show exactly the slice the rig advertises.
export type RigConnectionState = "healthy" | "faulted" | "unknown";
// Latest TX-side meter sample — sampled only while the transmitter is keyed (idle rigs read ~0).
export interface RigMeters {
  swr: number | null;              // dimensionless ratio, 1.0 = perfect match
  rfPowerWatts: number | null;     // calibrated backends
  rfPowerRelative: number | null;  // 0..1 fraction of full scale
  sampleAt: string;
}
export interface RigStatus {
  portId: string;
  attached: boolean;
  kind: string;                    // "hamlib" | "flrig" ("" for a port with no rig block)
  endpoint: string;                // host:port, config defaults resolved
  backend: string | null;          // e.g. "Hamlib rigctld", "flrig"
  manufacturer: string | null;
  model: string | null;            // e.g. "IC-7300"
  capabilities: string[];          // camelCased RigCapabilities flag names
  connectionState: RigConnectionState; // "faulted" self-heals — the backends re-dial per poll
  frequencyHz: number | null;      // current-VFO Hz
  mode: string | null;             // "USB" | "PKTUSB" | rig-native ("DATA-U", …)
  passbandHz: number | null;       // hamlib reports it; flrig can't
  transmitting: boolean | null;    // last observed PTT
  meters: RigMeters | null;
  sampledAt: string | null;        // last successful poll tick
}

// GET /api/v1/radios/scan → RadioScanResult[]. One row per radio a bus scan found. `serial` (the CCDI
// serial number) is the STABLE primary key: device paths renumber across replug/reboot and the CP2102
// CCDI dongles share a USB serial, so `byIdPath` may be null (ambiguous) — bind a port by `serial`.
export interface RadioScanResult {
  serial: string;
  model: string;
  ccdiVersion: string;
  baud: number;
  devicePath: string;
  byIdPath: string | null;
}

// ---- rig (CAT) discovery scan (GET /api/v1/rigs/scan) ----
// One row per candidate serial device the node can see. `byIdPath` is the stable
// /dev/serial/by-id symlink (preferred bind key — survives /dev/ttyUSB* renumbering); null when
// the device exposes none. `descriptor` is the by-id name the suggestion was matched against.
// `claimedBy` non-null = the device is already in use (a port transport / radio / rig references
// it) — the row is not pickable, and the string says what claims it. `suggestion` is the curated
// descriptor-table match; its `modelNumber` may be null when the name matched the curated table
// but not the local hamlib catalogue — the operator then picks the model explicitly.
export interface RigSuggestion {
  manufacturer: string;
  model: string;
  modelNumber: number | null;
  source: string;
}
export interface RigScanDevice {
  devicePath: string;
  byIdPath: string | null;
  descriptor: string | null;
  claimedBy: string | null;
  suggestion: RigSuggestion | null;
}
export interface RigScan {
  devices: RigScanDevice[];
  catalogueAvailable: boolean;
}

// ---- hamlib model catalogue (GET /api/v1/rigs/models) ----
// The node's local hamlib rig list (`rigctl -l`), for the editor's model picker.
// available:false = hamlib is not installed on the node (models is then empty and the
// node-managed rig shape can't run) — the picker disables with a note.
export interface RigModel {
  number: number;
  manufacturer: string;
  model: string;
  status: string;
}
export interface RigModelCatalogue {
  available: boolean;
  models: RigModel[];
}

// ---- split-station RF head-end fleet scan + adopt (server: Packet.Node.Core.Api.HeadEndScan) ----
// GET /api/v1/radios/headends → HeadEndScan: every head-end instance PDN found (config-pinned ∪
// mDNS-discovered), the devices each bridges (reach-through identified), the auto-suggested TNC↔radio
// pairs, and any duplicate-instance-id conflicts. This is the "plug into any port and go" preview the
// operator confirms before an adopt. System.Text.Json web defaults camel-case every member on the wire.
// See docs/research/split-station-rf-headend.md § Discovery & adoption flow.

// How a head-end's address was resolved: a pinned config address, or an mDNS browse hit.
export type HeadEndSource = "config" | "mdns";
// The reach-through device classification (server: HeadEndDeviceKind). "unknown" = neither probe
// (NinoTNC GETVER / Tait MODEL) classified it, or it was unreachable.
export type HeadEndDeviceKind = "nino-tnc" | "tait-ccdi" | "unknown";
// One device on a head-end as the scan saw it. A bound device (already referenced by a configured
// port) is NOT probed (single-client-per-pipe) — its `kind` then comes from the binding and `free`
// is false. `serial`/`model` are Tait-only; `version` is the NinoTNC GETVER firmware or the Tait CCDI
// version; `baud` is the rate the device answered at (the sweep-locked rate for a Tait).
// `bandCode`/`amateurBand` (identify/pair/name v2, #568): the Tait band designator (e.g. "B1") and the
// UK amateur band its split covers ("2m"/"70cm"/"4m") — null for a NinoTNC / unknown-band Tait; adopt
// defaults the port id + MQTT {instance} label to the amateur band when known, so pass it in the adopt
// body. `idSource`/`idStable` (id stability, #570/#575): which link the head-end derived `deviceId`
// from ("by-path" stable | "dev" the unstable kernel-name last resort) and whether it survives a
// reboot/replug — `idStable === false` warrants a warning (the binding may not survive a replug);
// null/absent = the head-end predates the fields (unknown, deliberately NOT assumed stable).
export interface HeadEndDeviceScan {
  deviceId: string;
  kind: HeadEndDeviceKind;
  model: string | null;
  version: string | null;
  serial: string | null;
  baud: number;
  free: boolean;
  bandCode?: string | null;
  amateurBand?: string | null;
  idSource?: string | null;
  idStable?: boolean | null;
}
// A suggested pairing within one instance (the co-location invariant — a TNC pairs only with a radio
// on the SAME instance). `auto` is true only when the instance has exactly one free TNC and one free
// radio (an unambiguous one-click suggestion); otherwise it is one of several candidate combinations.
export interface HeadEndPairProposal { tncDeviceId: string; radioDeviceId: string; auto: boolean }
// One head-end instance: its stable id + resolved address, how the address was found, whether its
// inventory could be fetched (`reachable`; `error` set when false), the devices it bridges, and the
// proposed pairings. `pairingAmbiguous` = more than one free TNC or radio, so the operator chooses.
export interface HeadEndInstanceScan {
  instanceId: string;
  host: string;
  httpPort: number;
  source: HeadEndSource;
  reachable: boolean;
  error: string | null;
  devices: HeadEndDeviceScan[];
  proposedPairs: HeadEndPairProposal[];
  pairingAmbiguous: boolean;
}
// An instance id advertised at more than one address with no config address to disambiguate — mDNS
// does not police its TXT payloads, so PDN surfaces the clash loudly rather than binding one blindly.
export interface HeadEndConflict { instanceId: string; addresses: string[] }
export interface HeadEndScan {
  instances: HeadEndInstanceScan[];
  conflicts: HeadEndConflict[];
}
// POST /api/v1/radios/headends/{instanceId}/adopt body (server: HeadEndAdoptRequest). The operator's
// chosen pairing → one matched port (a nino-tnc-tcp transport + a head-end-bound tait-ccdi radio).
// `portId` defaults to the amateur band when known, else the instance id (uniquified); `mode` to 0;
// `enabled` to true; `address` (optional manual host:port) is stored on the head-end config only when
// the instance isn't already declared. `amateurBand` (from the selected radio's scan row) band-names
// the port + its MQTT {instance} label; `mqttInstance` explicitly overrides that label when set.
export interface HeadEndAdoptRequest {
  tncDeviceId: string;
  radioDeviceId: string;
  portId?: string | null;
  mode?: number | null;
  enabled?: boolean | null;
  address?: string | null;
  amateurBand?: string | null;
  mqttInstance?: string | null;
}
// POST /api/v1/radios/headends/{instanceId}/pair-by-keyup → HeadEndKeyupResult (server:
// Packet.Node.Core.Api.HeadEndKeyupResult). The PHYSICAL modem↔radio map discovered by briefly
// KEYING each free NinoTNC's transmitter (RF is emitted — admin-scoped, same bar as hail/tuning/
// doctor) and watching which co-located Tait reports its PTT asserting. Ground truth: replaces the
// scan's co-location guess for the ambiguous case, verifies the unambiguous one. `caveat` is the
// server's RF warning text, always present. Reachable:false leaves the lists empty and sets `error`.
export interface HeadEndKeyupPair { tncDeviceId: string; radioDeviceId: string }
export interface HeadEndKeyupAmbiguity { tncDeviceId: string; radioDeviceIds: string[] }
export interface HeadEndKeyupResult {
  instanceId: string;
  reachable: boolean;
  error: string | null;
  pairs: HeadEndKeyupPair[];
  unpairedTncs: string[];
  unpairedRadios: string[];
  ambiguous: HeadEndKeyupAmbiguity[];
  caveat: string;
}
// One heard station for the MHeard surface (server: Packet.Node.Core.Api.HeardStation). For the
// node-wide view portId is null and ports is the count of distinct ports the station was heard on;
// for the per-port view portId is the port id and ports is 1. lastRssiDbm is the RSSI (dBm) of the
// most recent frame heard from this station when a radio control channel measured it, else null.
export interface HeardStation {
  callsign: string;
  portId: string | null;
  firstHeard: string;
  lastHeard: string;
  count: number;
  ports: number;
  lastRssiDbm: number | null;
}
// ---- per-peer AX.25 capability cache (server: PdnReadApi.PeerCapability) ----
// One learned (port, peer) record: whether the neighbour speaks v2.2/SABME
// (supportsExtended) and whether it answers a pre-connect XID with SREJ enabled
// (supportsSrejViaXid). Each bool is three-state: true/false = learned, null = never
// probed (the screen shows a "v2.2?" / "SREJ?" unknown badge). lastProbed/lastRefused are
// relative-ago "h:mm:ss" strings (the NetRom row style); lastRefused is null when the peer
// never refused/degraded an extended dial. The id the Forget action addresses is `port:peer`.
export interface PeerCapability {
  portId: string;
  peer: string;
  supportsExtended: boolean | null;
  supportsSrejViaXid: boolean | null;
  lastProbed: string;
  lastRefused: string | null;
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

// ---- capability doctor (server: Packet.Node.Core.Api.PortDoctorReport) ----
// GET /api/v1/ports/{id}/doctor → PortDoctorReport (safe, non-transmitting; read scope).
// POST /api/v1/ports/{id}/doctor?interrupt=true → the same shape, but also runs the transmitting
// probes (admin scope, audited; briefly keys the transmitter). One `status` per capability:
// pass = working, fail = broken (see `remedy`), unknown = not determined — e.g. a transmitting
// probe skipped on the safe form ("requires a brief transmit"), or "not a NinoTNC".
export type DoctorStatus = "pass" | "fail" | "unknown";
export interface DoctorProbe {
  name: string;
  status: DoctorStatus;
  detail: string;
  remedy: string | null;
}
export interface DoctorReport {
  portId: string;
  probes: DoctorProbe[];
  ranAt: string;
}

// ---- guided deviation tuning (server: Packet.Node.Core.Api.TuningSessionInfo / TuningEvent) ----
// A two-ended, operator-initiated, transmitting procedure coordinated over the radios' SDM side
// channel: this port is one end (tuned — the operator turns the TX-DEV pot here; or meter — measures
// a remote peer) and a peer radio is the other. POST .../tuning/session arms it (admin, audited);
// GET .../tuning/events streams rounds + lifecycle (SSE `tuning` events, read scope).
export type TuningRole = "tuned" | "meter";
export type TuningState =
  | "armed" | "peer-connected" | "awaiting-adjustment" | "ended" | "error" | "stopped";
export type TuningAdvice = "up" | "down" | "ok" | "sweep";
export type TuningEventKind =
  | "armed" | "peer-connected" | "round" | "awaiting-adjustment" | "ended" | "error";

export interface TuningStartRequest {
  role: TuningRole;
  peerSdmId: string;
  burstFrames?: number;
}
export interface TuningSessionInfo {
  sessionId: string;
  portId: string;
  role: TuningRole;
  peerSdmId: string;
  state: TuningState;
  burstFrames: number;
  startedAt: string;
}
export interface TuningEvent {
  kind: TuningEventKind;
  at: string;
  state: TuningState;
  burstIndex?: number | null;
  decoded?: number | null;
  total?: number | null;
  levelDb?: number | null;
  rssiDbm?: number | null;
  advice?: TuningAdvice | null;
  note?: string | null;
  error?: string | null;
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
  // ---- per-frame radio metadata (additive; null when no radio attributed the frame) ----
  // Received signal strength (dBm) of this frame, from the port's radio control channel — null on TX
  // frames, ports with no radio attached, or inbound frames no sample could be attributed to.
  rssiDbm?: number | null;
  // rssiDbm minus the tracked channel-idle noise floor (dB), or null when either is unavailable.
  snrDb?: number | null;
  // The channel-idle noise floor (dBm) the radio was tracking when this frame arrived, or null.
  noiseFloorDbm?: number | null;
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
// `username` is the authenticated account as the server resolved it — authoritative, so
// a passwordless passkey sign-in (no typed username) shows the right identity, not a guess.
export interface LoginResult { token: string; expiresAt: string; scopes: string; refreshToken: string | null; username: string }
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
// ---- node app platform (GET /api/v1/apps) ------------------
// One registered app that exposes a web UI. `icon` is an optional lucide-react
// icon name (kebab-case, e.g. "message-square"); may be null/absent → the launcher
// falls back to a generic app glyph. `url` is always "/apps/{id}/" — an absolute path
// on the same origin that pdn reverse-proxies, NOT a client-side SPA route.
// `state` is the live supervisor state (the same AppPackageState set the packages
// inventory uses), or null when the app has no pdn-managed service to watch (an
// inline app, or a package with no service: block). The left-nav uses it to warn
// when an ENABLED app isn't running (Stopped/Backoff/Faulted) — sourced from this one
// fetch so the nav needn't also hit /apps/packages. Every app the feed returns is
// enabled (the feed lists only enabled web apps), so a non-healthy state IS the warning.
// How the panel opens an app from its left-nav entry (the manifest `ui.mode` contract —
// docs/app-packages.md § UI surface modes). `standalone` (the default) is a full browser
// navigation to the app's own page at `url`; `embedded`/`slot` render the app in an in-panel
// iframe inside the panel shell, `slot` additionally appending `?pdn_embed=1` to the iframe src
// so the app renders chrome-less and blends into the single PDN chrome. Unknown/missing →
// standalone (the server already normalises, so the wire value is always one of these three).
export type AppUiMode = "standalone" | "embedded" | "slot";
export interface NodeApp {
  id: string;
  name: string;
  icon?: string | null;
  url: string;
  /** How the nav opens this app — standalone (full nav) vs embedded/slot (in-panel iframe). */
  uiMode: AppUiMode;
  state?: AppPackageState | null;
}
// ---- app packages (GET /api/v1/apps/packages) ---------------
// One discovered app package — or inline config-authored app — with its manifest
// summary + supervisor state (mirrors PdnAppPackagesApi.AppPackageEntry, camelCase
// on the wire). `error` non-null = a broken package (unreadable/invalid manifest);
// it can never be enabled. `state` is null exactly when service === "none" (there
// is nothing to run); `pid` is set only while a managed process is alive; `detail`
// carries supervisor context (e.g. the crash-loop detail behind a Faulted state).
export type AppPackageSource = "package" | "inline";
export type AppPackageService = "none" | "managed" | "external";
export type AppPackageState = "Stopped" | "Starting" | "Running" | "Backoff" | "Faulted" | "External";

// Whether an ENABLED app's supervisor state is a not-running warning: the supervisor should
// have driven it to Running but it is Stopped/Backoff/Faulted instead (crashed/stopped). Used
// by the left-nav badge and the management-row warning. Starting is transient (not a warning),
// Running is healthy, External is owner-managed (pdn never tracks its health → never a warning),
// and a null state means there is no service to run at all. ONLY meaningful for an enabled app —
// a disabled app is expected to be Stopped, so callers must gate on `enabled` first.
export function isAppNotRunning(state: AppPackageState | null | undefined): boolean {
  return state === "Stopped" || state === "Backoff" || state === "Faulted";
}

// Display-normalise a declared capability string for the trust prompt: the `network` capability
// is shown as `packet` (transport-accurate for packet-radio network access — RF/KISS-TCP/AXUDP/
// sim — vs the TCP/IP/LAN reading of "network"). `network` stays a back-compat alias accepted on
// the wire; only the surfaced label changes. Mirrors the C# AppCapabilities.Normalize so an app
// whose manifest still declares the old spelling shows the new one even before the server projects
// it (and so the mock fixtures, which still say "network", render "packet").
export function displayCapability(capability: string): string {
  return capability.toLowerCase() === "network" ? "packet" : capability;
}
// One declared tailnet port forward (mirrors PdnAppPackagesApi.AppForwardEntry). `listen` is
// the tailnet-facing port the node's tsnet node exposes; `target` is the app's loopback
// listener (host:port); `tls` is how the sidecar handles TLS. A capability the owner sees in
// the enable confirm. See docs/network-access.md § App-declared port forwarding.
export type AppForwardTls = "terminate" | "raw";
export interface AppForward { listen: number; target: string; tls: AppForwardTls }
export interface AppPackage {
  id: string;
  name: string;
  version: string | null;
  description: string | null;
  /** Optional lucide icon name (kebab-case), like NodeApp.icon. */
  icon: string | null;
  /** Declared capabilities — shown to the owner at enable time (the trust prompt). */
  capabilities: string[];
  enabled: boolean;
  source: AppPackageSource;
  error: string | null;
  service: AppPackageService;
  state: AppPackageState | null;
  pid: number | null;
  detail: string | null;
  /** Declared tailnet port forwards — a capability shown in the enable confirm. */
  forwards: AppForward[];
  // ---- packet identity (docs/app-packages.md § Application packet identity) ----
  /** The effective node-prompt command verb (owner override ?? manifest / inline). Null = the
   *  app declares no verb (reachable only by callsign / NET/ROM alias). */
  command?: string | null;
  /** The node-resolved on-air callsign this app binds — a pin or an auto-assigned
   *  `<node-base>-N`. Null when the app binds no callsign (a pure session app with no pin). */
  callsign?: string | null;
  /** The opt-in NET/ROM alias the node advertises → this app's callsign. Null = not advertised. */
  netromAlias?: string | null;
  /** The quality the NET/ROM alias is advertised at (only meaningful with `netromAlias`). */
  netromQuality?: number | null;
}
// The PUT /apps/packages/{id}/identity body — the owner's packet-identity overrides for a
// discovered package (mirrors PdnAppPackagesApi.AppIdentityRequest, camelCase on the wire).
// Every field is optional: an absent/blank value CLEARS that override (a blank callsign falls
// back to node auto-assignment; a blank alias turns the advert off).
export interface AppIdentityRequest {
  command?: string | null;
  callsign?: string | null;
  netromAlias?: string | null;
  netromQuality?: number | null;
}
// ---- app catalog: available apps (GET /api/v1/apps/available) ----
// One catalog entry projected with this node's view (mirrors
// PdnAvailableAppsApi.AvailableApp, camelCase on the wire). `installed` is whether a
// package with this id is already present; `installedVersion` + `updateAvailable`
// compare it against the catalog `version`. `installable` is false when the catalog has
// no artifact for this node's architecture (the Install button shows disabled, hinted).
// `kind` is the artifact shape ("assets" per-RID binary · "deb" extracted · "pdnapp"
// tarball). `capabilities` are the manifest's declared grants, shown at install time.
export interface AvailableApp {
  id: string;
  name: string;
  version: string;
  description?: string | null;
  icon?: string | null;
  capabilities: string[];
  homepage?: string | null;
  kind: "assets" | "deb" | "pdnapp";
  installed: boolean;
  installedVersion?: string | null;
  updateAvailable: boolean;
  installable: boolean;
}
// The result of an install / update / upload. `ok` true ⇒ the package was staged
// (and now appears as discovered-but-disabled); `error` carries the server's message
// on a failed attempt. The live client surfaces a failure as a thrown Error rather than
// a returned { ok:false } — this shape mirrors the wire body either way.
export interface InstallOutcome { ok: boolean; id: string; version?: string | null; error?: string | null }
export interface LogLine { t: string; lvl: "info" | "warn" | "error"; msg: string }
export interface ToggleHelp { label: string; desc: string }
export interface FieldHelp { label: string; unit: string; help: string }
