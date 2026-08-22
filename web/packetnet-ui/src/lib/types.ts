// ============================================================
// pdn domain types - mirror the real records (docs/node-ui-design.md §6):
//   Packet.Node.Core.Configuration (NodeConfig tree),
//   Packet.NetRom.Routing (routing model),
//   the new Slice-3 read models (NodeStatus/PortStatus/SessionInfo/LinkStats),
//   the MonitorEvent derived from Ax25Listener.FrameTraced.
// Plus the operator-facing helper models introduced by the design (profiles,
// nino modes, beacons, help copy) - these are UI-layer concepts.
// ============================================================

// ---- 6.1 NodeConfig tree -----------------------------------
// Every `kind:` the server's TransportKinds declares (server:
// Packet.Node.Core.Configuration.TransportKinds - all EIGHT arms). The two kinds the
// Ports editor does not author (nino-tnc-tcp, tait-transparent) are still typed here:
// they are created elsewhere (head-end adopt / the YAML file), and a port carrying one
// must round-trip through the editor untouched rather than be dropped on save.
export type TransportKind =
  | "kiss-tcp" | "serial-kiss" | "nino-tnc" | "nino-tnc-tcp"
  | "axudp" | "axudp-multipoint" | "tait-transparent" | "soundmodem";

// The kinds a UI form can AUTHOR. nino-tnc-tcp is created by the head-end adopt flow (it binds a
// device from a head-end's inventory) and tait-transparent by the config file, so no form offers
// them - but a port carrying one is still edited, and saved, by the Ports screen.
export type AuthorableTransportKind = Exclude<TransportKind, "nino-tnc-tcp" | "tait-transparent">;

// Fields the SERVER computes and echoes on every GET, on top of what the operator authored.
// System.Text.Json serialises a public get-only property, so these ride out with the config and
// (because the Ports editor saves the port it loaded, verbatim) ride back in on the PUT, where
// the server ignores them. They are typed so a save is not "an unmodelled field slipping
// through" but a stated part of the contract - see the contract fixtures in test/contract.
export interface TransportComputed {
  /** The exclusive resource this transport claims - the key the duplicate-endpoint rule
   *  dedupes ports on (server: TransportConfig.EndpointKey). Server-computed, read-only. */
  readonly endpointKey?: string;
}

export interface KissTcpTransport extends TransportComputed { kind: "kiss-tcp"; host: string; port: number }
export interface SerialKissTransport extends TransportComputed { kind: "serial-kiss"; device: string; baud: number }
export interface NinoTncTransport extends TransportComputed { kind: "nino-tnc"; device: string; baud: number; mode: number }
// A full-control NinoTNC reached over a split-station head-end's raw TCP pipe (server:
// NinoTncTcpTransport). Created by the head-end adopt flow (POST /headends/{id}/adopt),
// not by the Ports editor: the modem is picked from the head-end's device inventory.
export interface NinoTncTcpTransport extends TransportComputed { kind: "nino-tnc-tcp"; headEndId: string; deviceId: string; mode: number }
// A Tait TM8100/TM8200 radio in Transparent mode used AS the modem (server:
// TaitTransparentTransportConfig). Three mutually-exclusive bindings - local device path,
// CCDI serial, or a head-end device - plus the baud/airtime model. Config-file authored;
// the editor carries it through untouched.
export interface TaitTransparentTransport extends TransportComputed {
  kind: "tait-transparent";
  device?: string; serial?: string; headEndId?: string; deviceId?: string;
  baud?: number; transparentBaud?: number; ffskBaud?: number; leadInMs?: number;
  /** Server-computed: whether the binding is the head-end pair rather than a local device. */
  readonly isHeadEndBound?: boolean;
}
export interface AxudpTransport extends TransportComputed { kind: "axudp"; host: string; port: number; localPort: number }
// One multipoint-AXUDP partner - a BPQ `MAP <call> <ip> UDP <port> [B]` line
// (server: Packet.Node.Core.Configuration.AxudpPeerConfig). `call` is the routing
// key (an outbound frame whose AX.25 destination is this callsign goes to this peer);
// `broadcast` is the BPQ `B` suffix - fan NODES/ID/BEACON broadcasts to this peer.
export interface AxudpPeer { call: string; host: string; port: number; broadcast: boolean }
// Multipoint AXUDP - the BPQ BPQAXIP analog: ONE UDP socket bound to `localPort`
// reaches MANY partners, each addressed by callsign (server:
// Packet.Node.Core.Configuration.AxudpMultipointTransport). Replaces the point-to-point
// `axudp` host/port with a `peers[]` partner table.
export interface AxudpMultipointTransport extends TransportComputed { kind: "axudp-multipoint"; localPort: number; peers: AxudpPeer[] }
// FlexRadio slice tuning for a `flex:` soundmodem device (headless slice control;
// server: Packet.Node.Core.Configuration.SoundModemFlexConfig). Used only when
// `device` is a `flex:` device - ignored for ALSA devices.
export interface SoundModemFlex {
  frequency?: string; antenna?: string; mode?: string; daxChannel?: string;
}
// In-process soundcard modem (the pdn-soundmodem engine) - native DCD, sample-accurate
// TX-complete (server: Packet.Node.Core.Configuration.SoundModemTransportConfig).
//   offsetPairs/offsetStepHz - the bpsk300*/qpsk* differential frequency-diversity bank
//     knobs (2·offsetPairs+1 stepped decoder branches; ignored by non-bank modes).
//   pskDetector - "coherent" | "differential" for the bpsk*/qpsk* modes (null = the
//     catalogue default, differential for every PSK mode).
//   flex - FlexRadio slice tuning, only for a `flex:` device.
export interface SoundModemTransport extends TransportComputed {
  kind: "soundmodem"; device: string; captureRate: number; mode: string;
  frequency?: number; ptt?: string;
  offsetPairs?: number | null; offsetStepHz?: number | null;
  pskDetector?: "coherent" | "differential" | null;
  flex?: SoundModemFlex | null;
}
export type TransportConfig =
  | KissTcpTransport
  | SerialKissTransport
  | NinoTncTransport
  | NinoTncTcpTransport
  | AxudpTransport
  | AxudpMultipointTransport
  | TaitTransparentTransport
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
  // txDelay / slotTime / txTail are BYTES in units of 10 ms (the KISS protocol's own units,
  // `byte?` on the server) - 30 means 300 ms. persistence is a 0-255 byte shown as a percentage.
  // Hold wire values here; the editor converts for display (lib/mock.ts tenMsToMs / msToTenMs).
  txDelay?: number; persistence?: number; slotTime?: number; txTail?: number;
  // Pace outbound TX over the G8BPQ ACKMODE extension (kiss-tcp ports only). Default
  // false. Unlike the other knobs this is construction-time - toggling it restarts the
  // port. See Packet.Node.Core.Configuration.KissParams.AckMode.
  ackMode?: boolean;
  // Start T1 from the modem's TX-COMPLETE rather than from handing the frame to the modem,
  // where the transport can tell us (ACKMODE / the in-process soundmodem). Default false.
  // See Packet.Node.Core.Configuration.KissParams.T1FromTxComplete.
  t1FromTxComplete?: boolean;
}
// Per-port AX.25 compatibility profile (Packet.Node.Core.Configuration.PortCompatConfig).
// preset picks the Ax25ParseOptions preset (null = lenient, the historical default);
// the nullable booleans override individual flags on top of the preset; quirks selects
// the SDL session-quirks set (null = default, the spec-correct one). Absent/null compat
// = lenient + default - no behavioural change.
export type CompatPreset = "strict" | "lenient" | "bpq" | "xrouter" | "direwolf";
export interface PortCompatConfig {
  preset?: CompatPreset | null;
  allowEmptyCallsignBase?: boolean | null;
  allowInfoOnSupervisoryFrames?: boolean | null;
  allowCommandFrameAsResponse?: boolean | null;
  quirks?: "default" | "strictly-faithful" | null;
}
// Per-port AX.25 LINK SETUP - how this port dials OUT (server:
// Packet.Node.Core.Configuration.PortLinkConfig). dial picks which AX.25 version an outbound
// connect offers first (Auto = offer v2.2/SABME and learn per peer, V22 = always v2.2, V20 =
// always plain v2.0/SABM - what a BPQ / LinBPQ or older-TNC facing port wants); preConnectXid
// decides whether a v2.0 dial leads with an XID exchange to negotiate SREJ. Both are plain
// enums with defaults on the server, so both are always present when `link` is; the config
// dialect serialises them as their member names. Absent/null link = Auto + Auto, i.e. exactly
// today's dial behaviour. Inbound connects are never affected - the node answers whatever
// version the caller offers.
export type LinkDialPreference = "Auto" | "V22" | "V20";
export type LinkPreConnectXid = "Auto" | "On" | "Off";
export interface PortLinkConfig { dial: LinkDialPreference; preConnectXid: LinkPreConnectXid }
// Optional per-port radio-control attachment (server: Packet.Node.Core.Configuration.PortRadioConfig).
// The serial control channel to the radio behind this port's modem - a SEPARATE serial device from
// the modem's. When present, every inbound frame carries per-frame RSSI/SNR sampled from the radio's
// control channel (the signal data KISS can't provide). Pin which radio EITHER by CCDI serial
// (preferred/stable - survives /dev/ttyUSB* renumbering and shared-USB-serial ambiguity) OR by device
// path OR by a head-end device (headEndId + deviceId, the split-station binding the head-end adopt
// flow writes): exactly one binding mode. A local (serial/port) radio is valid only on the
// serial-modem transport kinds (serial-kiss, nino-tnc); a head-end-bound one pairs with the
// co-located nino-tnc-tcp modem; a `rig`-kind radio re-presents the port's rig: daemon and pairs with
// any transport (server: PortConfigValidator's pairing rules). Null/absent = no radio attached.
export interface RadioConfig {
  kind: "tait-ccdi" | "rig";
  serial?: string;
  port?: string;
  // Split-station binding: the head-end instance + the stable device id of the radio's serial
  // port on it. Written by the head-end adopt flow; the editor never authors it but must carry
  // it through untouched (dropping it silently detaches the radio from an adopted port).
  headEndId?: string;
  deviceId?: string;
  baud?: number;
  // How often (seconds) the health monitor samples the radio; null/absent = driver default (10 s).
  // Not surfaced by the editor - carried through untouched so a YAML-set value survives a save.
  healthIntervalSeconds?: number | null;
  // Answer a peer's SDM "hail" on this radio (the split-station identify handshake), and the peer
  // callsign to answer. Config-file authored; the editor carries them through untouched.
  hailResponder?: boolean;
  hailResponderPeer?: string;
  /** Server-computed: whether the binding is headEndId+deviceId rather than a local serial/port. */
  readonly isHeadEndBound?: boolean;
}
// Optional per-port rig-control (CAT) attachment (server: Packet.Node.Core.Configuration.PortRigConfig).
// The station-control sibling of the radio: block - the CAT channel to the transceiver behind this
// port's modem (dial frequency / mode / PTT / TX meters); it never touches the packet path, so unlike
// radio: it is valid on EVERY transport kind. Two binding shapes, selected by `device` being set
// (mirrors the server's PortRigConfig.IsNodeManaged authority):
//   node-managed - { kind: "hamlib", device, model, serialSpeed? }: the node spawns + supervises
//     rigctld -m <model> -r <device> on a loopback port it allocates itself (host/port stay unset;
//     hamlib only - flrig is a GUI app the node can't spawn). Prefer the /dev/serial/by-id device
//     path: it survives /dev/ttyUSB* renumbering across replug/reboot.
//   BYO daemon  - { kind: "hamlib"|"flrig", host, port? }: dial a rigctld/flrig the operator already
//     runs (model/serialSpeed stay unset; a null port means the kind default - 4532 / 12345).
// Every field but `kind`/`host` is nullable on the server (int? / string?), and the two shapes
// are mutually exclusive: a node-managed rig sends port null, a BYO daemon sends
// device/model/serialSpeed null. The editor omits what it does not set; the server sends the
// nulls. Both spellings must type.
export interface RigConfig {
  kind: "hamlib" | "flrig";
  host?: string;
  port?: number | null;
  device?: string | null;
  model?: number | null;
  serialSpeed?: number | null;
  // Poll cadences (idle / keyed), seconds. Not surfaced by the editor - carried through untouched
  // so a YAML-set value survives a Forms save (mirrors RadioConfig.healthIntervalSeconds).
  pollIntervalSeconds?: number | null;
  meterIntervalSeconds?: number | null;
  /** Server-computed: `device` is set, so the node spawns and supervises rigctld itself
   *  (server: PortRigConfig.IsNodeManaged - the authority on which of the two shapes this is). */
  readonly isNodeManaged?: boolean;
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
  // Per-port outbound-dial link policy (which AX.25 version this port offers first, and whether
  // a v2.0 dial leads with an XID to negotiate SREJ). Null/absent = Auto + Auto, today's
  // behaviour exactly. See Packet.Node.Core.Configuration.PortConfig.Link.
  link?: PortLinkConfig | null;
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
  // Label feeding the {instance} segment of this port's kissproxy MQTT topics; null = the
  // port id. Written by the head-end adopt flow (the radio's amateur band) and by hand in
  // the config file; no screen edits it, so it must ride through every port save untouched
  // (server: Packet.Node.Core.Configuration.PortConfig.MqttInstance).
  mqttInstance?: string | null;
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
// mDNS service advertisement for the control panel (server: MdnsConfig). `instanceName` null =
// the node derives one from the callsign. No screen edits it yet; it must round-trip.
export interface MdnsConfig { enabled: boolean; instanceName: string | null }
// The browser sysop console's idle reaper (server: SysopConsoleConfig).
export interface SysopConsoleConfig { idleTimeoutMinutes: number }
export interface ManagementConfig {
  telnet: TelnetConfig;
  http: HttpConfig;
  https: HttpsConfig;
  auth: AuthConfig;
  mdns: MdnsConfig;
  console: SysopConsoleConfig;
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
// The INP3 time-routing overlay (server: Packet.NetRom.Wire.NetRomInp3Options). The four
// timing knobs are TimeSpan on the server and cross the config wire as whole SECONDS
// (SecondsTimeSpanJsonConverter) - never as "hh:mm:ss" duration strings.
export interface Inp3Config {
  enabled: boolean; preferInp3Routes: boolean;
  l3RttInterval: number; l3RttResetWindow: number;
  rifInterval: number; positiveDebounce: number;
  // The rest of NetRomInp3Options. No screen edits them (the Config screen surfaces the four
  // timers + the two toggles), but they are on every GET and must ride back through a save.
  //   snttGainShift          - the smoothed-RTT gain shift (RFC-793-style 1/2^n).
  //   probeUnknownCapability - probe a neighbour whose INP3 capability is not yet known.
  //   advertiseIpAccept      - the IP-accept bits advertised in the RIF, or null for none.
  //   capabilityTextWidth    - width of the capability text field in an INP3 RIF.
  //   hopLimit               - the RIF hop cap.
  //   worsenThresholdMs      - how much a target time must worsen before it is re-advertised.
  snttGainShift: number;
  probeUnknownCapability: boolean;
  advertiseIpAccept: number | null;
  capabilityTextWidth: number;
  hopLimit: number;
  worsenThresholdMs: number;
}
// OARC network-map reporting (server: Packet.Node.Core.Configuration.OarcConfig).
// Default-off: nothing is reported until `enabled`. All outbound - the node POSTs its
// own telemetry to the OARC collector; the collector never reaches in. Each category is
// independently toggleable; traces is the high-volume per-frame firehose (opt-in).
//   baseUrl                   - collector URL (an absolute http(s) URL).
//   reportNodeStatus          - node up/status/down heartbeats.
//   reportLinks               - L2 link events + stats.
//   reportCircuits            - L4 NET/ROM circuit events + stats.
//   reportTraces              - per-frame L2 trace firehose (high volume, most revealing).
//   tracesRfOnly              - when traces on, only over-air (RF) frames.
//   publishExactPosition      - publish exact lat/lon vs locator-only.
//   statusIntervalSecs        - node-status heartbeat cadence.
//   sessionStatusIntervalSecs - link/circuit status refresh cadence.
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
// How a transit node picks among several kept routes to one destination
// (server: Packet.NetRom.NetRomForwardMode):
//   BestRoute - always the single best route; every datagram takes the same path.
//   PerFlow   - quality-weighted spread; one circuit always hashes to one route,
//               distinct circuits spread across the kept routes. The default.
export type NetRomForwardMode = "BestRoute" | "PerFlow";
// The node's NET/ROM routing role - the single 3-state successor to the old
// connect+forward bools (server: Packet.Node.Core.Configuration.NetRomRouting):
//   None     - passive: hear + maintain the table only (no interlinks, no transit).
//   Endpoint - open interlinks for our own connect <alias>, but don't relay transit.
//   Transit  - full router: interlinks + relay third-party transit.
// The legacy connect/forward keys are still accepted by the server for back-compat,
// but the UI edits only this knob.
export type NetRomRouting = "None" | "Endpoint" | "Transit";
export interface NetRomConfig {
  enabled: boolean; broadcast: boolean;
  routing: NetRomRouting | null; forwardMode: NetRomForwardMode;
  // The legacy connect/forward pair the 3-state `routing` replaced. The server still ACCEPTS
  // them (and resolves them into routing when routing is null), so it also emits them; the UI
  // never authors them, but a save must carry them back unchanged.
  connect: boolean | null;
  forward: boolean | null;
  /** Server-computed: `routing` with the legacy connect/forward fallback already resolved -
   *  what the node will actually do (server: NetRomConfig.EffectiveRouting). Read-only. */
  readonly effectiveRouting?: NetRomRouting;
  // The node's NET/ROM alias is unified with identity.alias (the single node-name concept).
  defaultNeighbourQuality?: number; minQuality?: number;
  obsoleteInitial?: number; obsoleteMinimum?: number; sweepIntervalSeconds?: number;
  window?: number; transportTimeoutSeconds?: number; transportRetries?: number; timeToLive?: number;
  // Offer LinBPQ-style negotiated NET/ROM L4 payload compression on circuits (BPQ L4Compress).
  // Default false (decline) - the interop-safe path. Turn on only for links to compression-capable
  // BPQ neighbours. See Packet.Node.Core.Configuration.NetRomConfig.Compress.
  compress: boolean;
  inp3: Inp3Config;
}
// ARDOP virtual-TNC service (server: Packet.Node.Core.Configuration.ArdopConfig). A NODE-LEVEL
// soundmodem SERVICE, not a port transport: an ardopcf-compatible TCP host interface (command
// socket + a data socket on port+1) backed by its own dedicated audio device, so external ARDOP
// hosts (BPQ DRIVER=ARDOP, Pat, Winlink Express) can drive this node's soundcard/FlexRadio as an
// ARDOP modem. Off by default. `captureRate` is ALSA-only (a flex: device clocks off DAX); `ptt`
// must be empty for a flex: device (the radio keys itself); `flex` is used only for a flex: device.
export interface ArdopConfig {
  enabled: boolean;
  device: string;
  captureRate: number;
  bind: string;
  port: number;
  ptt: string;
  flex?: SoundModemFlex | null;
}
// POCSAG paging service (server: Packet.Node.Core.Configuration.PagingConfig). A NODE-LEVEL
// soundmodem SERVICE: a TCP line server (PAGE/HEARD) that transmits + receives POCSAG pages over
// its own dedicated audio device. Off by default. Like ArdopConfig plus a POCSAG `baud` (512, 1200
// (DAPNET) or 2400) and a baseband polarity `invertPolarity`. Same flex: caveats as ArdopConfig.
export interface PagingConfig {
  enabled: boolean;
  device: string;
  captureRate: number;
  bind: string;
  port: number;
  baud: number;
  invertPolarity: boolean;
  ptt: string;
  flex?: SoundModemFlex | null;
}
// ---- the config blocks no screen edits (yet) ---------------------------------------
// Every one of these is on GET /config and must survive a Forms save: the config PUT
// replaces the document wholesale, so a block the client did not model would be dropped
// the first time an operator changed a callsign. They were untyped pass-through until the
// contract fixtures made the whole wire surface visible (review item C018).

// The Remote Host Protocol server (server: RhpConfig) - the binary control socket axcall,
// packet-term and the app gateway speak. Config-file authored.
export interface RhpConfig {
  enabled: boolean; bind: string; port: number; requireAuth: boolean;
  maxConnections: number; maxHandlesPerClient: number; inFrameTimeoutSeconds: number;
}
export interface McpSseConfig { enabled: boolean; path: string }
export interface McpOauthConfig { enabled: boolean; accessTokenLifetimeMinutes: number }
// The Model Context Protocol server (server: McpConfig).
export interface McpConfig {
  enabled: boolean; sse: McpSseConfig; tokenLifetimeDays: number; oauth: McpOauthConfig;
}
// How an inline (config-authored) app is launched (server: ApplicationKind).
export type ApplicationKind = "Process" | "Socket" | "Inline";
// One inline, config-authored application (server: ApplicationConfig) - the pre-package way to
// attach a session app. Discovered .pdnapp packages are NOT here; they surface via /apps/packages.
export interface AppUiConfig { upstream: string; name?: string | null; icon?: string | null; mode: AppUiMode }
export interface AppNetromConfig { alias: string | null; quality: number | null }
export interface ApplicationConfig {
  id: string; command: string; enabled: boolean; kind: ApplicationKind;
  executable: string | null; socketPath: string | null; args: string[];
  workingDirectory: string | null; capabilities: string[];
  ui: AppUiConfig | null; callsign: string | null; netrom: AppNetromConfig | null;
}
// The operator's per-package overrides for a DISCOVERED app package (server: AppOverrideConfig).
// This is what the Apps screen's enable/disable + identity actions write.
export interface AppOverrideConfig {
  id: string; enabled: boolean;
  command: string | null; callsign: string | null;
  netrom: AppNetromConfig | null;
  environment: Record<string, string>;
}
// The persistent per-frame traffic log (server: TrafficConfig). `path` null = the default under
// the state dir.
export interface TrafficConfig { enabled: boolean; path: string | null; retentionDays: number; maxMb: number }
// kissproxy-style MQTT frame publishing (server: MqttConfig). Default-off, all outbound.
export interface MqttConfig {
  enabled: boolean; brokerHost: string; brokerPort: number; useTls: boolean;
  username: string | null; password: string | null;
  topicPrefix: string; nodeName: string | null; base64: boolean; qos: number; rfOnly: boolean;
}
// A config-pinned split-station head-end (server: HeadEndConfig). mDNS-discovered instances are
// not listed here; the adopt flow appends one when the instance was not already declared.
export interface HeadEndConfig { id: string; address: string }

export interface NodeConfig {
  schemaVersion: number;
  identity: IdentityConfig;
  ports: PortConfig[];
  services: ServicesConfig;
  management: ManagementConfig;
  netRom: NetRomConfig;
  beacon: BeaconConfig;
  rhp: RhpConfig;
  mcp: McpConfig;
  applications: ApplicationConfig[];
  apps: AppOverrideConfig[];
  appPackageRoots: string[] | null;
  traffic: TrafficConfig;
  tailscale: TailscaleConfig;
  oarc: OarcConfig;
  mqtt: MqttConfig;
  // Node-level soundmodem services (each runs over its own dedicated audio device; off by default).
  paging: PagingConfig;
  ardop: ArdopConfig;
  headEnds: HeadEndConfig[];
}

// ---- system info / self-update (server: PdnSystemApi.SystemInfoResponse) ----
// GET /api/v1/system/info - the node's running version, install channel, what an
// update would do, and whether a newer version is known (the shared API contract the
// control panel's About panel + "update available" banner consume).
//   channel: "apt" | "github" | "unknown" - "unknown" disables Apply (nothing on the host
//     owns this install, e.g. an unpacked release archive; the operator replaces it by hand).
//   updateMechanism: what POST /system/update does here ("apt" | "github" | "none").
//   updateAvailable + latestVersion: the per-channel available-version check; when true,
//     latestVersion is the version the banner offers (else null).
export type InstallChannelName = "apt" | "github" | "unknown";
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
/** One route to a destination. The next hop is the (portId, neighbour) pair - the ADJACENCY -
 *  so a destination can hold two routes via the same callsign on different ports, each with its
 *  own quality. Key React rows on both halves. Server: NetRomRoute. */
export interface NetRomRoute {
  neighbour: string; portId: string; quality: number; obsolescence: number; inp3: Inp3RouteMetric | null;
}
export interface NetRomDestination {
  destination: string; alias: string; bestRoute: number; routes: NetRomRoute[];
}
/** A directly-heard neighbour ADJACENCY: one row per (portId, neighbour), so a station audible
 *  on two ports appears twice with each port's own pathQuality. Server: NetRomNeighbour. */
export interface NetRomNeighbour {
  neighbour: string; alias: string; portId: string; pathQuality: number; lastHeard: string;
}
export interface NetRomRoutingSnapshot {
  generatedAt: string;
  neighbours: NetRomNeighbour[];
  destinations: NetRomDestination[];
}

// ---- 6.4 read models ---------------------------------------
/** The persistent traffic log's health: whether it is running this boot, and how many frames
 *  the WRITER dropped (never the radio path's loss). Server: TrafficLogStatus. */
export interface TrafficLogStatus { enabled: boolean; dropped: number }
export interface NodeStatus {
  callsign: string;
  // alias + grid are OPTIONAL station identity on the server (Identity.Alias / .Grid are
  // string?), so a node that set neither sends null for both. They were typed non-null here,
  // which is how the dashboard came to print an empty alias chip rather than omitting it.
  alias: string | null;
  grid: string | null;
  version: string; uptimeSeconds: number;
  portsUp: number; portsTotal: number; sessionCount: number;
  netrom: { neighbours: number; destinations: number; inp3Enabled: boolean };
  traffic: TrafficLogStatus;
}
/** The server's port lifecycle states (Packet.Node.Core.Hosting.PortStates). `up` and
 *  `degraded` are the SERVING states - a degraded port is on the air with a piece missing
 *  (see PortStatus.degraded). Everything else is not carrying traffic. */
export type PortState =
  | "configured" | "disabled" | "starting" | "up" | "degraded" | "faulted" | "retrying" | "stopping";
export interface PortStatus {
  id: string; enabled: boolean; state: PortState;
  sessionCount: number;
  /** Why the port last failed to come up or died; null if it never has. Retained across a
   *  recovery, so a port that is up again still shows what happened. */
  lastError: string | null;
  framesIn: number; framesOut: number;
  /** Components a `degraded` port is running without: radio | rig | rigctld | transport. */
  degraded: string[];
  /** When the port entered `state` (ISO-8601 UTC). */
  since: string;
  /** Port-level carrier sense (radio DCD or a channel-sensing transport such as the
   *  in-process soundmodem): true=busy, false=clear, null=no source / not running. */
  channelBusy: boolean | null;
}

/** The two states in which a port is actually carrying traffic. */
export const PORT_SERVING_STATES: PortState[] = ["up", "degraded"];

/** One waterfall line from a soundmodem port's spectrum SSE feed. */
export interface SpectrumEvent {
  seq: number;
  /** Hz per bin (bins run 0 Hz .. bins.length*binHz). */
  binHz: number;
  /** Base64 of dB-scaled bytes, one per bin. */
  bins: string;
}

// ---- soundmodem receive-quality snapshot (server: Packet.Node.Core.Transports.SoundModemFrameQuality) ----
// GET /api/v1/ports/{id}/quality → SoundModemQualitySnapshot (404 for a non-soundmodem or
// not-running port). A `kind: soundmodem` port's rolling per-frame FEC/CRC receive diagnostics
// (#635) - the Reed-Solomon correction counts the deframers always computed. camelCase on the wire.
// Deliberately NOT a bit-error rate (BER is unobservable at a receiver): `correctedBytes` is an
// honest byte-error floor, and its null (an unprotected HDLC framing carries no FEC count) is kept
// distinct from 0 (a clean IL2P frame). cumulativeCorrectedBytes climbing is the early-warning that
// the link is spending its FEC budget before frames start dropping.
export interface SoundModemFrameQuality {
  receivedAt: string;
  /** Mode (and, for a multi-decoder bank, the winning branch) that decoded the frame. */
  mode: string;
  frameBytes: number;
  /** Bytes FEC repaired (IL2P/FX.25); null for an unprotected (HDLC) framing - distinct from 0. */
  correctedBytes: number | null;
  /** IL2P trailing-CRC state; null where the framing carries no trailer (plain IL2P, HDLC, FX.25). */
  crcValid: boolean | null;
  /** The decoding branch's frequency offset for a multi-decoder bank; null for single decoders. */
  frequencyOffsetHz: number | null;
  /** The winning branch's input pre-emphasis (dB/octave) for a multi-decoder bank; null otherwise. */
  emphasisDb: number | null;
}
export interface SoundModemQualitySnapshot {
  frames: number;
  cumulativeCorrectedBytes: number;
  framesWithCorrections: number;
  /** The most recent frame's FEC count; null for an unprotected (HDLC) last frame / none yet. */
  lastFrameCorrectedBytes: number | null;
  /** The most recent frames' diagnostics, newest first, capped at a small bound. */
  recent: SoundModemFrameQuality[];
}
export type SessionRole = "console" | "interlink" | "bridge";
/** One LIVE circuit: the node lists every session it holds in a state other than Disconnected,
 *  so an establishing (AwaitingConnection / AwaitingV22Connection) or releasing (AwaitingRelease)
 *  link appears too and can be disconnected - read `state` to tell them apart (packet.net#727).
 *  Note the last four fields are per-LINK, not per-circuit: uptimeSeconds, bytesIn, bytesOut and
 *  lastActivity come from the node's (port, peer) telemetry link, so they span every circuit with
 *  that callsign on that port since the port attached and count all traffic to/from it (UI frames
 *  and beacons included). A reconnecting peer therefore shows running totals, not a fresh zero.
 *  The per-circuit truth is state / vs / vr / window. See packet.net#694 (C063). */
export interface SessionInfo {
  /** OPAQUE. `port:peer` for a circuit to the node's own callsign, `port:peer>local` for one to
   *  an application callsign or alias - the engine keys a session on (local, remote), so one
   *  station can hold both at once and each must be separately addressable by DELETE / send /
   *  stream. Pass it back verbatim; never parse it (packet.net#723). Render `local` instead. */
  id: string; portId: string; peer: string;
  /** The LOCAL callsign this circuit is answered as: the node's own, or an application callsign
   *  the node binds on that port. */
  local: string;
  role: SessionRole; state: string;
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
// polling (true) from one configured but not currently attached (false - port down, or the radio
// failed to open and the port degraded to running without it).
export type RadioConnectionState = "healthy" | "faulted" | "unknown";
// A radio's self-reported identity (CCDI MODEL / versions queries).
export interface RadioIdentity { model: string; ccdiVersion: string }
// The latest radio-health sample, projected for the operator surface. RSSI is read only while the
// receiver is un-muted (not transmitting); the forward/reverse figures land only on transmit samples.
// NOTE the forward/reverse detector figures are an antenna-health TREND, NEVER VSWR - the detectors
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
// that has a `rig:` block - the station-control sibling of RadioStatus (a port can carry both).
// `capabilities` is the render contract: show exactly the slice the rig advertises.
export type RigConnectionState = "healthy" | "faulted" | "unknown";
// Latest TX-side meter sample - sampled only while the transmitter is keyed (idle rigs read ~0).
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
  connectionState: RigConnectionState; // "faulted" self-heals - the backends re-dial per poll
  frequencyHz: number | null;      // current-VFO Hz
  mode: string | null;             // "USB" | "PKTUSB" | rig-native ("DATA-U", …)
  passbandHz: number | null;       // hamlib reports it; flrig can't
  transmitting: boolean | null;    // last observed PTT
  meters: RigMeters | null;
  sampledAt: string | null;        // last successful poll tick
}

// GET /api/v1/radios/scan → RadioScanResult[]. One row per radio a bus scan found. `serial` (the CCDI
// serial number) is the STABLE primary key: device paths renumber across replug/reboot and the CP2102
// CCDI dongles share a USB serial, so `byIdPath` may be null (ambiguous) - bind a port by `serial`.
export interface RadioScanResult {
  serial: string;
  model: string;
  ccdiVersion: string;
  baud: number;
  devicePath: string;
  byIdPath: string | null;
  /** The Tait band designator ("B1", "H5") and the UK amateur band its split covers ("2m",
   *  "70cm", "4m"); both null for a radio whose band could not be determined. */
  bandCode: string | null;
  amateurBand: string | null;
}

// ---- rig (CAT) discovery scan (GET /api/v1/rigs/scan) ----
// One row per candidate serial device the node can see. `byIdPath` is the stable
// /dev/serial/by-id symlink (preferred bind key - survives /dev/ttyUSB* renumbering); null when
// the device exposes none. `descriptor` is the by-id name the suggestion was matched against.
// `claimedBy` non-null = the device is already in use (a port transport / radio / rig references
// it) - the row is not pickable, and the string says what claims it. `suggestion` is the curated
// descriptor-table match; its `modelNumber` may be null when the name matched the curated table
// but not the local hamlib catalogue - the operator then picks the model explicitly.
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
// node-managed rig shape can't run) - the picker disables with a note.
export interface RigModel {
  number: number;
  manufacturer: string;
  model: string;
  /** hamlib's backend maturity ("Stable", "Beta", …); null when `rigctl -l` did not name one. */
  status: string | null;
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
// port) is NOT probed (single-client-per-pipe) - its `kind` then comes from the binding and `free`
// is false. `serial`/`model` are Tait-only; `version` is the NinoTNC GETVER firmware or the Tait CCDI
// version; `baud` is the rate the device answered at (the sweep-locked rate for a Tait).
// `bandCode`/`amateurBand` (identify/pair/name v2, #568): the Tait band designator (e.g. "B1") and the
// UK amateur band its split covers ("2m"/"70cm"/"4m") - null for a NinoTNC / unknown-band Tait; adopt
// defaults the port id + MQTT {instance} label to the amateur band when known, so pass it in the adopt
// body. `idSource`/`idStable` (id stability, #570/#575): which link the head-end derived `deviceId`
// from ("by-path" stable | "dev" the unstable kernel-name last resort) and whether it survives a
// reboot/replug - `idStable === false` warrants a warning (the binding may not survive a replug);
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
// A suggested pairing within one instance (the co-location invariant - a TNC pairs only with a radio
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
  /** Liveness as of THIS scan (`reachable` may be a cached answer), and when the instance was
   *  last seen. Both null/absent on a scan that did not track it. */
  reachableNow?: boolean | null;
  lastSeen?: string | null;
}
// An instance id advertised at more than one address with no config address to disambiguate - mDNS
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
// KEYING each free NinoTNC's transmitter (RF is emitted - admin-scoped, same bar as hail/tuning/
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
  /** lastRssiDbm minus the tracked noise floor (dB), or null when either is unavailable. */
  lastSnrDb: number | null;
  /** Median pre-data carrier time (ms) measured for this station - how long its transmitter
   *  keyed before data started, i.e. the TXDELAY it is actually using. Null until sampled. */
  medianPreDataCarrierMs: number | null;
  /** How many pre-data-carrier samples that median is over. */
  preDataCarrierSamples: number;
  /** The node's plain-language TXDELAY advice for this station, or null when it has none. */
  txDelayAdvisory: string | null;
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

// ---- connectionless TEST ping (docs/node-api.md PingResult) ----
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
// pass = working, fail = broken (see `remedy`), unknown = not determined - e.g. a transmitting
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
// channel: this port is one end (tuned - the operator turns the TX-DEV pot here; or meter - measures
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
  // The TXDELAY-minimisation arm of the same feed (a round measures the peer's pre-data carrier
  // and recommends a shorter TXDELAY). Null on a deviation round.
  txDelayMs?: number | null;
  preDataCarrierMs?: number | null;
  recommendedTxDelayMs?: number | null;
}

// ---- 6.3 monitor event (derived from FrameTraced) ----------
// Every value the server's decoder puts in MonitorEvent.type. Derived, not transcribed: the
// contract fixture sweeps all 256 control octets through MonitorEventFactory.Classify, so this
// union is checked against the classifier's real output (closed-sets.json). TEST is a real
// U-frame - it is what POST /ping transmits - and the bare "U" is the decoder's fallback for a
// U control octet it does not name. There is deliberately NO bare "S": the S-frame subtype is
// two bits with all four values named (RR/RNR/REJ/SREJ), so that fallback is unreachable. "S"
// is a frame CLASS, not a frame type (#691 C050 added it here by symmetry with "U").
export type FrameType =
  | "UI" | "SABM" | "SABME" | "I" | "RR" | "RNR" | "REJ" | "SREJ"
  | "FRMR" | "UA" | "DISC" | "DM" | "XID" | "TEST" | "U";
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
  /** The first control octet as decoded, modulo-independent (server: MonitorEvent.Control). */
  control: number;
  /** Length of the information field in bytes; 0 for a frame without one. */
  infoLength: number;
  // ---- per-frame radio metadata (additive; null when no radio attributed the frame) ----
  // Received signal strength (dBm) of this frame, from the port's radio control channel - null on TX
  // frames, ports with no radio attached, or inbound frames no sample could be attributed to.
  rssiDbm?: number | null;
  // rssiDbm minus the tracked channel-idle noise floor (dB), or null when either is unavailable.
  snrDb?: number | null;
  // The channel-idle noise floor (dBm) the radio was tracking when this frame arrived, or null.
  noiseFloorDbm?: number | null;
  // Identifies the node PROCESS that numbered this frame. `seq` restarts at 1 on every node
  // boot, so (bootId, seq) is the stable identity a client dedupes on - see mergeLiveFrame.
  bootId?: string;
}

// ---- operator-facing helper models (UI layer) --------------
// ---- NinoTNC mode catalogue (GET /api/v1/modems/nino-tnc/modes) ----
// The node's NinoTNC DIP-switch table (server: PdnModemsApi.NinoTncModeRow, itself a projection
// of NinoTncCatalog.ByMode) - the source for the Ports editor's "Modem mode" picker. Static:
// 16 rows, the same whether or not a TNC is attached. `name` is Nino's own mode name
// ("3600 QPSK IL2P+CRC"); `bitRateHz` is 0 for mode 15 ("Set from KISS"), which is variable.
export interface NinoMode {
  mode: number;
  name: string;
  bitRateHz: number;
  /** The mode's published occupied bandwidth needs a wide (25 kHz) channel. */
  requiresWideChannel: boolean;
}
export interface NinoModeCatalogue { modes: NinoMode[] }
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
// (NinoTest went with the Ports screen's NinoTNC test-frame banner in #691. It typed a
// fixture, not a wire record - no endpoint or SSE event ever carried one - and the banner
// it fed had been disabled behind `false &&` since it would otherwise have announced the
// mock's test frame on a node with no NinoTNC. Reinstate it from the server's shape if a
// real test-frame event is ever added.)
// A per-port beacon override (Packet.Node.Core.Configuration.PortBeaconConfig).
// enabled is authoritative for the port; null intervalMinutes / text inherit the
// system default (BeaconConfig).
export interface PortBeacon { enabled: boolean; intervalMinutes: number | null; text: string | null }
export interface User {
  name: string; role: string; scopes: string[]; passkeys: number; lastLogin: string;
}

// ---- auth wire shapes (mirror Packet.Node.Api.PdnAuthApi DTOs) ----
// A user projected for the API - no password hash. Matches UserSummary (server).
export interface UserSummary {
  username: string;
  /** The single granted scope: "read" | "operate" | "admin". */
  scope: string;
  createdUtc: string;
  lastLoginUtc: string | null;
  /** Whether this user has an over-RF sysop code (TOTP) enrolled, and the callsign it is bound
   *  to (null when not enrolled). Both are on the wire; the Users screen used to re-fetch
   *  /auth/totp/enroll to learn what the list already told it, and could only learn it for the
   *  signed-in user, so every other row showed "self-service" whether or not it was enrolled. */
  hasTotp: boolean;
  callsign: string | null;
}
// POST /auth/login + POST /auth/refresh success body. `scopes` is the single granted
// scope string; `refreshToken` is the opaque one-time-use token the client stores and
// presents to /auth/refresh (may be null only if the node couldn't persist one - the
// access token still works until it expires).
// `username` is the authenticated account as the server resolved it - authoritative, so
// a passwordless passkey sign-in (no typed username) shows the right identity, not a guess.
export interface LoginResult { token: string; expiresAt: string; scopes: string; refreshToken: string | null; username: string }
// GET /setup/state body.
export interface SetupState { needsSetup: boolean }
// POST /setup request body (identity + first admin + optional first port).
export interface SetupIdentityInput { callsign: string; alias?: string | null; grid?: string | null }
export interface SetupAdminInput { username: string; password: string }
export interface SetupRequest { identity: SetupIdentityInput; admin: SetupAdminInput; firstPort?: PortConfig | null }
// POST /setup success body (the created admin - no token).
export interface SetupResult { username: string; scope: string }

// GET /setup/devices body - the first-run wizard's modem picker (mirrors
// Packet.Node.Core.Api.ModemScan). One row per local serial device that could carry a KISS TNC.
// `kind` is "nino-tnc" when the device answered the NinoTNC firmware query, "serial" otherwise -
// unidentified is NOT "not a TNC", because generic KISS has no identify handshake to answer.
// `devicePath` is the stable value to bind a port to (a /dev/serial/by-id link when udev gave an
// unambiguous one, else the kernel path); `kernelPath` is what it is called right now.
// `claimedBy` non-null = something in the config already uses it. `probeError` is a short reason
// the identify did not land ("permission denied", "no reply"). `permissionDenied` on the scan is
// the install-level version of that: the node cannot open serial devices at all.
export interface ModemScanDevice {
  devicePath: string;
  kernelPath: string;
  descriptor: string | null;
  kind: string;
  firmwareVersion: string | null;
  claimedBy: string | null;
  probeError: string | null;
}
export interface ModemScan {
  devices: ModemScanDevice[];
  permissionDenied: boolean;
}

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
// falls back to a generic app glyph. `url` is always "/apps/{id}/" - an absolute path
// on the same origin that pdn reverse-proxies, NOT a client-side SPA route.
// `state` is the live supervisor state (the same AppPackageState set the packages
// inventory uses), or null when the app has no pdn-managed service to watch (an
// inline app, or a package with no service: block). The left-nav uses it to warn
// when an ENABLED app isn't running (Stopped/Backoff/Faulted) - sourced from this one
// fetch so the nav needn't also hit /apps/packages. Every app the feed returns is
// enabled (the feed lists only enabled web apps), so a non-healthy state IS the warning.
// How the panel opens an app from its left-nav entry (the manifest `ui.mode` contract -
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
  /** How the nav opens this app - standalone (full nav) vs embedded/slot (in-panel iframe). */
  uiMode: AppUiMode;
  state?: AppPackageState | null;
}
// ---- app packages (GET /api/v1/apps/packages) ---------------
// One discovered app package - or inline config-authored app - with its manifest
// summary + supervisor state (mirrors PdnAppPackagesApi.AppPackageEntry, camelCase
// on the wire) - or a configured app whose package is not installed at all
// (`installed: false`, no manifest behind it). `error` non-null = a broken package
// (unreadable/invalid manifest);
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
// and a null state means there is no service to run at all. ONLY meaningful for an enabled app -
// a disabled app is expected to be Stopped, so callers must gate on `enabled` first.
export function isAppNotRunning(state: AppPackageState | null | undefined): boolean {
  return state === "Stopped" || state === "Backoff" || state === "Faulted";
}

// Display-normalise a declared capability string for the trust prompt: the `network` capability
// is shown as `packet` (transport-accurate for packet-radio network access - RF/KISS-TCP/AXUDP/
// sim - vs the TCP/IP/LAN reading of "network"). `network` stays a back-compat alias accepted on
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
  /** Declared capabilities - shown to the owner at enable time (the trust prompt). */
  capabilities: string[];
  enabled: boolean;
  source: AppPackageSource;
  /** False for a configured `apps:` entry whose package no root holds - the app the node knows
   *  about but cannot run. There is no manifest behind it, so name/version/capabilities/service
   *  carry nothing: render the row from this flag (packet.net#738 item 2). */
  installed: boolean;
  error: string | null;
  service: AppPackageService;
  state: AppPackageState | null;
  pid: number | null;
  detail: string | null;
  /** Declared tailnet port forwards - a capability shown in the enable confirm. */
  forwards: AppForward[];
  // ---- packet identity (docs/app-packages.md § Application packet identity) ----
  /** The effective node-prompt command verb (owner override ?? manifest / inline). Null = the
   *  app declares no verb (reachable only by callsign / NET/ROM alias). */
  command?: string | null;
  /** The node-resolved on-air callsign this app binds - a pin or an auto-assigned
   *  `<node-base>-N`. Null when the app binds no callsign (a pure session app with no pin).
   *  Display only: it is the RESOLVED value, so it must never be sent back as the pin. */
  callsign?: string | null;
  /** The owner's literal `callsign` pin, verbatim (including the bare `-N` SSID form), or null
   *  when the resolved callsign above was auto-assigned. This is the field the identity editor
   *  seeds from and sends back: PUT /identity is a full replace, so a form that always sent
   *  null cleared an existing pin on any verb/alias edit (#702 C043). */
  pinnedCallsign?: string | null;
  /** The opt-in NET/ROM alias the node advertises → this app's callsign. Null = not advertised. */
  netromAlias?: string | null;
  /** The quality the NET/ROM alias is advertised at (only meaningful with `netromAlias`). */
  netromQuality?: number | null;
}
// The PUT /apps/packages/{id}/identity body - the owner's packet-identity overrides for a
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
// a returned { ok:false } - this shape mirrors the wire body either way.
export interface InstallOutcome {
  ok: boolean;
  id: string;
  version?: string | null;
  error?: string | null;
  /** True when the install replaced the binary of an already-enabled, pdn-managed package and
   *  the supervisor bounced it onto the new bits. Absent on a failed attempt. */
  restarted?: boolean;
}
export interface LogLine { t: string; lvl: "info" | "warn" | "error"; msg: string }
export interface ToggleHelp { label: string; desc: string }
export interface FieldHelp { label: string; unit: string; help: string }
