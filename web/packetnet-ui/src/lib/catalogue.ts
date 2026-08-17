// ============================================================
// pdn - the UI CATALOGUE: operator-facing copy, labels, presets, help tables and
// the unit helpers the screens need in EVERY mode.
//
// This is the half of the old mock.ts that was never fixture data. The other half
// - the fake node (NODE_CONFIG, PORT_STATUS, SESSIONS, the frame generator, ...) -
// stays in mock.ts and is imported ONLY by api.ts's mock branch and by tests. An
// eslint `no-restricted-imports` rule (eslint.config.js) now stops anything under
// src/screens or src/components reaching for lib/mock again: a fixture rendered in
// live mode is a lie about somebody's real node (#691 C021/C022).
//
// Nothing here is node state. If a value describes THIS node it comes off the wire
// (api.ts + types.ts); if it is copy, a preset or a conversion, it belongs here.
// ============================================================
import type {
  ApplyImpact, ChannelMode, FieldHelp, FrameType, LinkDifficulty, NetRomRouting,
  NinoMode, ParamHelp, RadioProfile, ToggleHelp,
} from "./types";

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
  // Node-level soundmodem services: like the audio-device-owning soundmodem port transport
  // (port.transport) and the auxiliary telnet listener, editing these opens/closes an audio device
  // + a TCP listener — a bounded restart of that service, not a hot apply.
  "ardop": "port-restart",
  "paging": "port-restart",
};

// ---- monitor frame vocabulary ------------------------------
// The frame-type filter's options: every value the server's decoder can put in
// MonitorEvent.type (Packet.Node.Core/Api/MonitorEvent.cs) - the named U/S frames, TEST
// (which /ping itself transmits), and the bare "U" the decoder falls back to for a U
// control octet it does not name. Without those the operator could see the frames in the
// table but never isolate them (#691 C050). There is no bare "S" here: the S-frame subtype
// is two bits with all four values named, so that fallback is unreachable - the contract
// fixtures now derive the set from the classifier itself (#692 C018).
export const FRAME_TYPES: FrameType[] = [
  "UI", "SABM", "SABME", "I", "RR", "RNR", "REJ", "SREJ", "FRMR", "UA", "DISC", "DM", "XID",
  "TEST", "U",
];
// PID octet -> the name the decoder gives it. Mirrors MonitorEvent.cs PidName, which
// returns null for anything else - so a decode row shows the hex alone rather than the
// word "null" (#691 C050).
export const PIDS: Record<string, string> = { "0xF0": "No layer 3", "0xCF": "NET/ROM", "0xCC": "ARPA IP", "0x08": "Segmentation" };

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
// One label per server transport kind (TransportKinds - all eight). nino-tnc-tcp and
// tait-transparent are authored elsewhere (head-end adopt / the config file) but reach the
// Ports screen like any other port, so they need a badge and a descriptor of their own.
export const KIND_LABEL: Record<string, string> = {
  "kiss-tcp": "kiss-tcp", "serial-kiss": "serial-kiss", "nino-tnc": "ninotnc", "nino-tnc-tcp": "ninotnc-tcp",
  "axudp": "axudp", "axudp-multipoint": "axudp-mp", "tait-transparent": "tait-transparent", "soundmodem": "soundmodem",
};
// soundmodem carries native AX.25 frames over a shared CSMA channel — the KISS TXDELAY/PERSIST/
// SLOTTIME knobs drive the modem's own p-persistent channel access (server: ICsmaChannelParams),
// so it uses the KISS param block like the other RF transports (true), unlike the UDP tunnels.
// nino-tnc-tcp is a NinoTNC like any other (NinoTncSerialPort implements ICsmaChannelParams over
// the head-end pipe); tait-transparent is NOT - TaitTransparentTransport exposes no CSMA params,
// so the modem-keying knobs would be silently inert there.
export const KIND_USES_KISS: Record<string, boolean> = {
  "kiss-tcp": true, "serial-kiss": true, "nino-tnc": true, "nino-tnc-tcp": true,
  "axudp": false, "axudp-multipoint": false, "tait-transparent": false, "soundmodem": true,
};

// The in-process soundmodem's accepted modem modes — mirrors the server's SoundModemValidator.
// KnownModes (ModemCatalog.KnownModes minus bpsk1200-multi). The bpsk*/qpsk* modes expose the
// diversity-bank + PSK-detector knobs; bpsk300 is the differential frequency-diversity bank,
// bpsk1200 stays the legacy single-carrier modem.
export const SOUNDMODEM_MODES: string[] = [
  "afsk1200", "afsk1200-fx25", "afsk1200-fx25rx", "afsk1200-multi", "afsk1200-il2p", "afsk1200-il2p-nocrc",
  "afsk300", "afsk300-il2p", "afsk300-il2pc",
  "bpsk300", "bpsk300-multi", "bpsk300-nocrc", "bpsk1200",
  "qpsk600", "qpsk2400", "qpsk3600",
  "fsk9600", "fsk9600-il2p", "fsk4800-il2p",
  "c4fsk9600", "c4fsk19200",
  "freedv-datac0", "freedv-datac1", "freedv-datac3", "freedv-datac4", "freedv-datac13", "freedv-datac14",
  "ms110d-wn0", "ms110d-wn1", "ms110d-wn2", "ms110d-wn3", "ms110d-wn4", "ms110d-wn5", "ms110d-wn6", "ms110d-wn13",
];

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

// The radio-preset catalogue is a UI-ONLY starting-point table: picking one writes its
// baseline into the port's ax25:/kiss: blocks client-side. These ids are NOT server channel
// profiles (server: ChannelProfiles.Names) and never travel to the API as `profile` - sending
// one 422s the port (#690 C002). The server profile is carried verbatim on PortDraft.profile.
export const RADIO_PROFILES: RadioProfile[] = [
  { id: "vhf-fm-1200", name: "VHF FM · 1200 AFSK", ninoMode: 1, baseline: { t1Ms: 3000, t2Ms: 300, t3Ms: 180000, n2: 8, windowSize: 4, txDelay: 30, slotTime: 10, txTail: 5, persistence: 63 } },
  { id: "vhf-fm-9600", name: "VHF FM · 9600 G3RUH", ninoMode: 5, baseline: { t1Ms: 2500, t2Ms: 200, t3Ms: 180000, n2: 8, windowSize: 4, txDelay: 15, slotTime: 10, txTail: 3, persistence: 63 } },
  { id: "uhf-data-9600", name: "UHF data · 9600 GFSK IL2P", ninoMode: 4, baseline: { t1Ms: 2500, t2Ms: 200, t3Ms: 180000, n2: 8, windowSize: 4, txDelay: 15, slotTime: 10, txTail: 3, persistence: 63 } },
  { id: "hf-robust-300", name: "HF robust · 300 AFSK", ninoMode: 0, baseline: { t1Ms: 8000, t2Ms: 1500, t3Ms: 300000, n2: 12, windowSize: 2, txDelay: 25, slotTime: 10, txTail: 10, persistence: 32 } },
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
// (PORT_SETUP removed with #690 C002: it was a demo table of preset choices keyed by port id,
// and the editor indexed it by LIVE port id - so a real port whose id happened to match a
// fixture id was opened with the fixture's preset and saved with the fixture's profile. The
// editor now derives the summary + the preset state from the port's own fields.)
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
// (AX25_DEFAULTS / KISS_DEFAULTS removed with #690 C005. They were UI invention - they did not
// match the ENGINE's null-block defaults (T1 6000 / T2 3000 / T3 30000 / N2 10 / txTail 0 /
// modem-own CSMA) - and the port editor spread them over a port whose ax25:/kiss: were null, so
// ANY save silently persisted and applied a different set of timings than the port had been
// running. The editor now keeps null blocks null and shows the engine defaults as placeholders:
// see ENGINE_AX25_DEFAULTS / ENGINE_KISS_DEFAULTS in screens/ports.tsx.)
//
// KISS TXDELAY/SLOTTIME/TXTAIL are single BYTES in units of 10 ms on the wire (that is the
// KISS protocol, and the server types them `byte?`) - 30 means 300 ms. They are STORED in wire
// units in the editor draft and converted for display by the ms<->units pair below, exactly as
// persistence is stored as a 0-255 byte and shown as a percentage. Writing milliseconds into
// these fields is what made every panel-created port POST `txDelay: 300` into a byte.

export function persistPct(v: number): number { return Math.round((v / 255) * 100); }
export function pctToPersist(p: number): number { return Math.round((p / 100) * 255); }

/** A KISS 10 ms-unit byte as milliseconds, for display. */
export function tenMsToMs(units: number): number { return units * 10; }
/** Milliseconds back to a KISS 10 ms-unit byte, clamped to the 0..255 the wire allows
 *  (255 = 2.55 s, the longest TXDELAY/TXTAIL/SLOTTIME KISS can express). */
export function msToTenMs(ms: number): number { return Math.min(255, Math.max(0, Math.round(ms / 10))); }

// NET/ROM + INP3 operator copy -------------------------------
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
  l3RttResetWindow: { label: "Probe reset window", unit: "seconds", help: "How long a neighbour may go without answering a time-probe before its measured time is treated as unknown again. Must be longer than the probe interval." },
  rifInterval: { label: "Share-timing interval", unit: "seconds", help: "How often your node passes its measured route timings on to neighbours, so the whole network's time map stays current." },
  positiveDebounce: { label: "Switch-route patience", unit: "seconds", help: "How long good news ('this route got faster') is batched up before the node passes it on, so a burst of improvements becomes one update instead of several, which stops it flapping on momentary blips. Bad news is always passed on straight away." },
};
