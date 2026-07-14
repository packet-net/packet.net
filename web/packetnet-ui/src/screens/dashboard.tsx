// ============================================================
// Dashboard — health at a glance (README §3).
// Metric strip (4 cards) → detail screens, three info cards
// (Station / Ports / NET/ROM), and a journald-style log tail.
// ============================================================
import { useEffect, useState, type ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";
import { Page, PageHeader } from "@/components/layout/shell";
import {
  Button, Badge, Card, CardHeader, CardTitle, CardContent, StatusDot, Tooltip, Icon,
  Modal, Field, Input, Select,
} from "@/components/ui";
import { cn } from "@/lib/utils";
import { api, useQuery, subscribeFrames, subscribeRigs } from "@/lib/api";
import { useAuth } from "@/app/auth";
import { portHealth } from "@/lib/health";
import { KIND_LABEL, fmtUptime, fmtRigFrequency } from "@/lib/mock";
import type { RadioStatus, RigStatus } from "@/lib/types";

export function Dashboard() {
  const navigate = useNavigate();
  const { data: status } = useQuery(api.status, []);
  const { data: config } = useQuery(api.config, []);
  const { data: portStatus } = useQuery(api.ports, []);
  const { data: links } = useQuery(api.linkStats, []);
  const { data: log } = useQuery(api.log, []);
  const { data: sessions } = useQuery(api.sessions, []);

  // Live role breakdown for the Active-sessions card (replaces a hardcoded string).
  const sessionSub = (() => {
    const sess = sessions ?? [];
    if (sess.length === 0) return "none";
    const n = { console: 0, bridge: 0, interlink: 0 } as Record<string, number>;
    for (const x of sess) n[x.role] = (n[x.role] ?? 0) + 1;
    return (["console", "bridge", "interlink"] as const)
      .filter((r) => n[r] > 0)
      .map((r) => `${n[r]} ${r}`)
      .join(" · ");
  })();

  // Frames/sec: a rolling rate computed from the live frame stream (the same SSE
  // feed the monitor consumes; mock mode supplies a timer-driven stream). We keep
  // a 3-second window of arrival times and recompute the rate each second.
  const [fps, setFps] = useState(0);
  useEffect(() => {
    const arrivals: number[] = [];
    const unsub = subscribeFrames(() => arrivals.push(Date.now()));
    const t = setInterval(() => {
      const cutoff = Date.now() - 3000;
      while (arrivals.length > 0 && arrivals[0] < cutoff) arrivals.shift();
      setFps(+(arrivals.length / 3).toFixed(1));
    }, 1000);
    return () => { unsub(); clearInterval(t); };
  }, []);

  const s = status;
  const ports = portStatus ?? [];
  const faulted = ports.filter((p) => p.state === "faulted").length;

  return (
    <Page>
      <PageHeader
        title="Dashboard"
        subtitle="Health at a glance"
        actions={
          <Button variant="outline" size="sm" onClick={() => navigate("/config")}>
            <Icon name="config" size={14} /> Configure
          </Button>
        }
      />

      {/* metric strip */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Metric
          label="Status"
          value={<span className="flex items-center gap-2"><StatusDot state="up" live /> Operational</span>}
          sub={s ? `up ${fmtUptime(s.uptimeSeconds)}` : "—"}
        />
        <Metric
          label="Ports up"
          value={s ? <span className="tnum">{s.portsUp}<span className="text-muted-foreground">/{s.portsTotal}</span></span> : <span className="tnum">—</span>}
          sub={faulted > 0 ? `${faulted} faulted` : "all healthy"}
          subVariant={faulted > 0 ? "warning" : undefined}
          to="/ports"
        />
        <Metric
          label="Active sessions"
          value={<span className="tnum">{s ? s.sessionCount : "—"}</span>}
          sub={sessionSub}
          to="/sessions"
        />
        <Metric
          label="Frames/sec"
          value={<span className="tnum">{fps}</span>}
          sub={<span className="flex items-center gap-1"><span className="h-1.5 w-1.5 rounded-full bg-success live-dot" /> live</span>}
          to="/monitor"
        />
      </div>

      <div className="mt-5 grid grid-cols-1 gap-4 lg:grid-cols-3">
        {/* station identity */}
        <Card>
          <CardHeader><CardTitle className="flex items-center gap-2"><Icon name="radio" size={15} className="text-muted-foreground" /> Station</CardTitle></CardHeader>
          <CardContent className="space-y-2.5">
            <Row k="Callsign" v={<span className="font-mono font-semibold">{s?.callsign ?? "—"}</span>} />
            <Row k="Alias" v={<span className="font-mono">{s?.alias ?? "—"}</span>} />
            <Row k="Locator" v={<span className="font-mono">{s?.grid ?? "—"}</span>} />
            <Row k="Version" v={<span className="font-mono text-xs">{s?.version ?? "—"}</span>} />
            <Row k="Uptime" v={s ? fmtUptime(s.uptimeSeconds) : "—"} />
          </CardContent>
        </Card>

        {/* ports */}
        <Card>
          <div className="flex items-center justify-between p-4 pb-2">
            <CardTitle className="flex items-center gap-2"><Icon name="ports" size={15} className="text-muted-foreground" /> Ports</CardTitle>
            <Link to="/ports"><Button variant="ghost" size="xs">All <Icon name="chevRight" size={13} /></Button></Link>
          </div>
          <CardContent className="space-y-1">
            {ports.map((p) => {
              const cfg = config?.ports.find((x) => x.id === p.id);
              const h = portHealth(p, links ?? []);
              return (
                <button
                  key={p.id}
                  onClick={() => navigate("/ports")}
                  className="flex w-full items-center justify-between rounded-md px-2 py-1.5 text-sm hover:bg-accent"
                >
                  <span className="flex items-center gap-2">
                    <StatusDot state={h.level === "degraded" ? "faulted" : p.state} live={p.state === "up" && h.level === "good"} />
                    <span className="font-mono">{p.id}</span>
                  </span>
                  <span className="flex items-center gap-2 text-xs text-muted-foreground">
                    {h.level === "degraded" && <Tooltip text={h.reason}><Badge variant="warning">attention</Badge></Tooltip>}
                    {h.level === "faulted" && <Badge variant="danger">faulted</Badge>}
                    {cfg && <Badge variant="muted">{KIND_LABEL[cfg.transport.kind]}</Badge>}
                  </span>
                </button>
              );
            })}
          </CardContent>
        </Card>

        {/* netrom */}
        <Card>
          <div className="flex items-center justify-between p-4 pb-2">
            <CardTitle className="flex items-center gap-2"><Icon name="routes" size={15} className="text-muted-foreground" /> NET/ROM</CardTitle>
            <Link to="/routes"><Button variant="ghost" size="xs">Routes <Icon name="chevRight" size={13} /></Button></Link>
          </div>
          <CardContent className="space-y-2.5">
            <Row k="Neighbours" v={<span className="tnum font-semibold">{s ? s.netrom.neighbours : "—"}</span>} />
            <Row k="Destinations" v={<span className="tnum font-semibold">{s ? s.netrom.destinations : "—"}</span>} />
            <Row k="Forwarding" v={<Badge variant="success">PerFlow</Badge>} />
            <Row k="INP3 overlay" v={s?.netrom.inp3Enabled ? <Badge variant="success">on</Badge> : <Badge variant="muted">off</Badge>} />
          </CardContent>
        </Card>
      </div>

      {/* radios — link quality + health for every radio-attached port */}
      <RadiosPanel />

      {/* rigs — the station-control (CAT) view for every rig-attached port */}
      <RigsPanel />

      {/* recent activity — journald-style log tail */}
      <Card className="mt-4">
        <div className="flex items-center justify-between p-4 pb-2">
          <CardTitle className="flex items-center gap-2"><Icon name="signal" size={15} className="text-muted-foreground" /> Recent activity</CardTitle>
          <Badge variant="muted">journald tail</Badge>
        </div>
        <CardContent>
          <div className="space-y-0.5 font-mono text-xs">
            {(log ?? []).length === 0 ? (
              <p className="px-1.5 py-1 text-muted-foreground">No recent activity.</p>
            ) : (
              (log ?? []).map((l, i) => (
                <div key={i} className="flex gap-3 rounded px-1.5 py-1 hover:bg-accent/60">
                  <span className="shrink-0 text-muted-foreground">{l.t}</span>
                  <span className={cn("w-10 shrink-0 font-semibold uppercase", l.lvl === "error" ? "text-danger" : l.lvl === "warn" ? "text-warning" : "text-muted-foreground")}>{l.lvl}</span>
                  <span className="text-foreground/90">{l.msg}</span>
                </div>
              ))
            )}
          </div>
        </CardContent>
      </Card>
    </Page>
  );
}

function Metric({ label, value, sub, subVariant, to }: {
  label: string;
  value: ReactNode;
  sub: ReactNode;
  subVariant?: "warning";
  to?: string;
}) {
  const inner = (
    <Card className={cn("h-full p-4", to && "cursor-pointer transition-colors hover:border-primary/40")}>
      <p className="text-xs font-medium text-muted-foreground">{label}</p>
      <div className="mt-1.5 text-2xl font-semibold tracking-tight tnum">{value}</div>
      <p className={cn("mt-1 text-xs", subVariant === "warning" ? "text-warning" : "text-muted-foreground")}>{sub}</p>
    </Card>
  );
  return to ? <Link to={to} className="block">{inner}</Link> : inner;
}

function Row({ k, v }: { k: ReactNode; v: ReactNode }) {
  return (
    <div className="flex items-center justify-between text-sm">
      <span className="text-muted-foreground">{k}</span>
      <span>{v}</span>
    </div>
  );
}

// ---- radio link-quality + health panel (GET /api/v1/radios) -----------------
// The operator payoff: attach a radio to a port, then SEE the link quality. One card per
// radio-attached port — identity, a connection-state badge, a live-ish channel-busy pill, and the
// health readout (RSSI + averaged, PA temperature, and the forward/reverse detector TREND, which is
// explicitly labelled "not VSWR" per the backend's uncalibrated-detector caveat). The section is
// absent on a node with no radios (empty /radios → nothing to show).
function RadiosPanel() {
  const { data: radios } = useQuery(api.getRadios, []);
  if (!radios || radios.length === 0) return null;
  return (
    <section className="mt-5">
      <div className="mb-3 flex items-center gap-2">
        <Icon name="radio" size={15} className="text-muted-foreground" />
        <h2 className="text-sm font-semibold tracking-tight">Radios</h2>
        <Badge variant="muted">{radios.length}</Badge>
      </div>
      <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
        {radios.map((r) => <RadioCard key={r.portId} r={r} />)}
      </div>
    </section>
  );
}

// dBm → a glance colour: strong reads green, workable amber, weak/near-noise red.
function signalTone(dbm: number | null | undefined): string {
  if (dbm == null) return "text-muted-foreground";
  if (dbm >= -85) return "text-success";
  if (dbm >= -100) return "text-warning";
  return "text-danger";
}

const CONNECTION_BADGE: Record<RadioStatus["connectionState"], { variant: "success" | "danger" | "muted"; label: string }> = {
  healthy: { variant: "success", label: "healthy" },
  faulted: { variant: "danger", label: "faulted" },
  unknown: { variant: "muted", label: "unknown" },
};

function RadioCard({ r }: { r: RadioStatus }) {
  const conn = CONNECTION_BADGE[r.connectionState];
  const h = r.health;
  const rssi = h?.rssiDbm ?? null;
  return (
    <Card className={cn("p-4", r.connectionState === "faulted" && "border-danger/40")}>
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="font-mono text-sm font-semibold">{r.portId}</span>
            <Badge variant={conn.variant}>{conn.label}</Badge>
            {!r.attached && <Badge variant="muted">not attached</Badge>}
          </div>
          <p className="mt-0.5 truncate text-xs text-muted-foreground">
            {r.identity
              ? <>{r.identity.model} · CCDI {r.identity.ccdiVersion}{r.serial && <> · <span className="font-mono">s/n {r.serial}</span></>}</>
              : <span className="font-mono">{r.serial ? `s/n ${r.serial}` : r.kind}</span>}
          </p>
        </div>
        {/* channel-busy (DCD) — live-ish carrier sense */}
        <div className="shrink-0 text-right">
          {r.channelBusy == null ? (
            <span className="text-[11px] text-muted-foreground/60">—</span>
          ) : r.channelBusy ? (
            <span className="inline-flex items-center gap-1.5 text-[11px] font-medium text-warning">
              <span className="h-1.5 w-1.5 rounded-full bg-warning live-dot" /> channel busy
            </span>
          ) : (
            <span className="inline-flex items-center gap-1.5 text-[11px] text-muted-foreground">
              <span className="h-1.5 w-1.5 rounded-full bg-success" /> idle
            </span>
          )}
        </div>
      </div>

      {/* RSSI hero — the glance value */}
      <div className="mt-3 flex items-end justify-between gap-3">
        <div>
          <p className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">RSSI</p>
          <p className={cn("tnum text-2xl font-semibold leading-none", signalTone(rssi))}>
            {rssi == null ? "—" : <>{rssi}<span className="ml-1 text-sm font-normal text-muted-foreground">dBm</span></>}
          </p>
        </div>
        <div className="text-right text-xs text-muted-foreground">
          <p>avg <span className="tnum font-mono text-foreground/80">{h?.averagedRssiDbm != null ? `${h.averagedRssiDbm} dBm` : "—"}</span></p>
          <p className="mt-0.5">PA temp <span className="tnum font-mono text-foreground/80">{h?.paTemperatureC != null ? `${h.paTemperatureC} °C` : "—"}</span></p>
        </div>
      </div>

      {/* antenna-health trend — explicitly NOT VSWR */}
      <div className="mt-3 border-t border-border pt-3">
        <Tooltip text="An uncalibrated, √P-scaled forward/reverse detector reading from the radio — a per-station TREND to watch for change, NOT a calibrated VSWR / power measurement. Alert on a shift over time, never on the absolute value.">
          <p className="mb-1.5 inline-flex items-center gap-1 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
            Antenna-health trend (not VSWR) <Icon name="info" size={11} />
          </p>
        </Tooltip>
        <div className="grid grid-cols-3 gap-2 text-xs">
          <div><p className="text-muted-foreground">Forward</p><p className="tnum mt-0.5 font-mono">{h?.forwardTrendMillivolts != null ? `${h.forwardTrendMillivolts} mV` : "—"}</p></div>
          <div><p className="text-muted-foreground">Reverse</p><p className="tnum mt-0.5 font-mono">{h?.reverseTrendMillivolts != null ? `${h.reverseTrendMillivolts} mV` : "—"}</p></div>
          <div><p className="text-muted-foreground">Rev/Fwd</p><p className="tnum mt-0.5 font-mono">{h?.reverseForwardRatio != null ? h.reverseForwardRatio.toFixed(3) : "—"}</p></div>
        </div>
      </div>
    </Card>
  );
}


// ---- rig-control (CAT) panel (GET /api/v1/rigs + the `event: rig` SSE feed) -----------------
// The station-control sibling of RadiosPanel: one card per rig-attached port — the dial
// (frequency + mode), a PTT pill, and SWR/power meters that come alive during a transmission.
// Capability-driven by contract: the card renders exactly the slice the rig advertises in
// `capabilities` (an IC-7300 over rigctld shows everything; a Tait adapter would show PTT and a
// power bar and nothing else). Seeded from REST, then live: each SSE tick REPLACES that port's
// status (the stream is keyed, not append-only like frames). Absent on a node with no rigs.
function RigsPanel() {
  const { data: seeded } = useQuery(api.getRigs, []);
  const [byPort, setByPort] = useState<Record<string, RigStatus>>({});
  useEffect(() => {
    if (!seeded) return;
    setByPort((prev) => {
      const next = { ...prev };
      for (const r of seeded) next[r.portId] ??= r;
      return next;
    });
  }, [seeded]);
  useEffect(() => subscribeRigs((r) => setByPort((prev) => ({ ...prev, [r.portId]: r }))), []);

  const rigs = Object.values(byPort).sort((a, b) => a.portId.localeCompare(b.portId));
  if (rigs.length === 0) return null;
  return (
    <section className="mt-5">
      <div className="mb-3 flex items-center gap-2">
        <Icon name="gauge" size={15} className="text-muted-foreground" />
        <h2 className="text-sm font-semibold tracking-tight">Rigs</h2>
        <Badge variant="muted">{rigs.length}</Badge>
      </div>
      <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
        {rigs.map((r) => <RigCard key={r.portId} r={r} />)}
      </div>
    </section>
  );
}

// SWR ratio → a glance colour: a good match reads green, worth-a-look amber, get-off-the-key red.
function swrTone(swr: number): string {
  if (swr <= 1.5) return "text-success";
  if (swr <= 2.5) return "text-warning";
  return "text-danger";
}

function swrBarClass(swr: number): string {
  if (swr <= 1.5) return "bg-success";
  if (swr <= 2.5) return "bg-warning";
  return "bg-danger";
}

function RigCard({ r }: { r: RigStatus }) {
  const conn = CONNECTION_BADGE[r.connectionState];
  const can = (cap: string) => r.capabilities.includes(cap);
  const meters = r.meters;
  const showMeters = can("swrMeter") || can("rfPowerMeter") || can("rfPowerMeterWatts");
  return (
    <Card className={cn("p-4", r.connectionState === "faulted" && "border-danger/40")}>
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="font-mono text-sm font-semibold">{r.portId}</span>
            <Badge variant={conn.variant}>{conn.label}</Badge>
            {!r.attached && <Badge variant="muted">not attached</Badge>}
          </div>
          <p className="mt-0.5 truncate text-xs text-muted-foreground">
            {r.model
              ? <>{r.manufacturer ? `${r.manufacturer} ` : ""}{r.model} · {r.backend ?? r.kind}</>
              : <>{r.kind} · <span className="font-mono">{r.endpoint}</span></>}
          </p>
        </div>
        {/* PTT — the transmitter state, when the rig can report it — plus the TUNE
            affordance when the rig advertises a settable dial (frequencySet / modeSet). */}
        <div className="flex shrink-0 flex-col items-end gap-1 text-right">
          {!can("pttGet") || r.transmitting == null ? (
            <span className="text-[11px] text-muted-foreground/60">—</span>
          ) : r.transmitting ? (
            <span className="inline-flex items-center gap-1.5 text-[11px] font-medium text-danger">
              <span className="h-1.5 w-1.5 rounded-full bg-danger live-dot" /> transmitting
            </span>
          ) : (
            <span className="inline-flex items-center gap-1.5 text-[11px] text-muted-foreground">
              <span className="h-1.5 w-1.5 rounded-full bg-success" /> receive
            </span>
          )}
          {r.attached && (can("frequencySet") || can("modeSet")) && <RigTuneButton r={r} />}
        </div>
      </div>

      {/* the dial — frequency hero + mode badge */}
      <div className="mt-3 flex items-end justify-between gap-3">
        <div>
          <p className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Frequency</p>
          <p className="tnum font-mono text-2xl font-semibold leading-none">
            {can("frequencyGet") && r.frequencyHz != null
              ? <>{fmtRigFrequency(r.frequencyHz)}<span className="ml-1 text-sm font-normal text-muted-foreground">MHz</span></>
              : <span className="text-muted-foreground/50">—</span>}
          </p>
        </div>
        <div className="text-right">
          {can("modeGet") && r.mode
            ? <>
                <Badge variant="secondary" className="font-mono">{r.mode}</Badge>
                {r.passbandHz != null && r.passbandHz > 0 && (
                  <p className="tnum mt-1 text-[11px] text-muted-foreground">{(r.passbandHz / 1000).toFixed(1)} kHz</p>
                )}
              </>
            : <span className="text-[11px] text-muted-foreground/60">—</span>}
        </div>
      </div>

      {/* TX meters — sampled only while transmitting; the last TX sample stays on display */}
      {showMeters && (
        <div className="mt-3 border-t border-border pt-3">
          <Tooltip text="SWR and power are sampled while the transmitter is keyed (an idle rig reads ~0, so meters aren't polled between transmissions). The values shown are from the most recent transmission.">
            <p className="mb-1.5 inline-flex items-center gap-1 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
              TX meters <Icon name="info" size={11} />
            </p>
          </Tooltip>
          <div className="grid grid-cols-2 gap-2 text-xs">
            {can("swrMeter") && (
              <div>
                <p className="text-muted-foreground">SWR</p>
                {meters?.swr != null ? (
                  <div className="mt-0.5 flex items-center gap-2">
                    <span className={cn("tnum font-mono font-semibold", swrTone(meters.swr))}>{meters.swr.toFixed(1)}</span>
                    <span className="h-1.5 w-16 overflow-hidden rounded-full bg-muted">
                      <span
                        className={cn("block h-full rounded-full", swrBarClass(meters.swr))}
                        style={{ width: `${Math.min(100, Math.max(4, ((meters.swr - 1) / 3) * 100))}%` }}
                      />
                    </span>
                  </div>
                ) : <p className="tnum mt-0.5 font-mono text-muted-foreground/50">—</p>}
              </div>
            )}
            {(can("rfPowerMeterWatts") || can("rfPowerMeter")) && (
              <div>
                <p className="text-muted-foreground">Power</p>
                <p className="tnum mt-0.5 font-mono">
                  {meters?.rfPowerWatts != null
                    ? <>{Math.round(meters.rfPowerWatts)}<span className="ml-0.5 text-muted-foreground">W</span></>
                    : meters?.rfPowerRelative != null
                      ? <>{Math.round(meters.rfPowerRelative * 100)}<span className="ml-0.5 text-muted-foreground">%</span></>
                      : <span className="text-muted-foreground/50">—</span>}
                </p>
              </div>
            )}
          </div>
        </div>
      )}
    </Card>
  );
}

// ---- TUNE — the rig card's one write affordance (operate scope) ---------------------
// The trigger follows the DoctorButton shape (a small ghost button opening a self-contained
// modal) and the scope convention: DISABLED with an explanatory title without the operate
// scope, never hidden. Rendered only when the rig is attached and advertises a settable
// dial (frequencySet / modeSet) — see the call site in RigCard.
function RigTuneButton({ r }: { r: RigStatus }) {
  const { has } = useAuth();
  const canOperate = has("operate");
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button
        size="xs"
        variant="ghost"
        disabled={!canOperate}
        title={canOperate
          ? "Retune the transceiver's current VFO"
          : "Retuning a transmitter requires the operate scope"}
        onClick={() => setOpen(true)}
      >
        <Icon name="gauge" size={13} /> Tune
      </Button>
      {open && <RigTuneModal r={r} onClose={() => setOpen(false)} />}
    </>
  );
}

// The mode picker's common tokens; "Other…" reveals a free-text input for rig-native
// tokens ("DATA-U", …) the shortlist can't know about.
const COMMON_RIG_MODES = ["USB", "LSB", "CW", "PKTUSB", "PKTFM", "FM", "AM", "RTTY"];

// Dial-entry parsing: a value under 1000 reads as MHz-decimal ("14.074" → 14 074 000 Hz),
// anything else as raw Hz ("14074000"). The ranges can't collide — no dial we would retune
// sits below 1 kHz. Returns null for anything non-numeric or non-positive.
function parseDialInput(raw: string): number | null {
  const v = Number(raw.trim());
  if (!Number.isFinite(v) || v <= 0) return null;
  return Math.round(v < 1000 ? v * 1e6 : v);
}

// The TUNE modal — renders exactly the settable slice (frequency input iff frequencySet,
// mode picker iff modeSet), applies ONLY the fields the operator filled in (sequentially:
// frequency, then mode; empty = leave unchanged), and closes on success. No re-fetch on
// close: the server wakes the rig poller after a set, so the next `event: rig` SSE tick
// replaces the card's status. Errors surface INLINE (the doctor.tsx pattern), not as a
// toast. Passband is deliberately not offered — the rig's default for the mode applies.
function RigTuneModal({ r, onClose }: { r: RigStatus; onClose: () => void }) {
  const can = (cap: string) => r.capabilities.includes(cap);
  const [freq, setFreq] = useState("");
  const [modeSel, setModeSel] = useState("");     // "" = leave unchanged; "other" = free text
  const [modeCustom, setModeCustom] = useState("");
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const parsedHz = freq.trim() === "" ? null : parseDialInput(freq);
  const badFreq = freq.trim() !== "" && parsedHz == null;
  const newMode = modeSel === "other" ? modeCustom.trim() : modeSel;
  const dirty = (can("frequencySet") && freq.trim() !== "") || (can("modeSet") && newMode !== "");

  const apply = async () => {
    setRunning(true);
    setError(null);
    try {
      if (can("frequencySet") && freq.trim() !== "") {
        if (parsedHz == null) throw new Error("Enter a frequency in MHz (e.g. 14.074) or in Hz.");
        await api.setRigFrequency(r.portId, parsedHz);
      }
      if (can("modeSet") && newMode !== "") await api.setRigMode(r.portId, newMode);
      onClose();   // success — the next rig SSE tick shows the new dial on the card
    } catch (e) {
      setError(String((e as Error)?.message ?? e));
    } finally {
      setRunning(false);
    }
  };

  return (
    <Modal open onClose={onClose} title={`Tune — ${r.portId}`} footer={<>
      <Button variant="outline" size="sm" onClick={onClose} disabled={running}>Cancel</Button>
      <Button
        size="sm"
        onClick={apply}
        disabled={running || !dirty || badFreq}
        title={dirty ? undefined : "Fill in a frequency and/or a mode — empty fields are left unchanged"}
      >
        <Icon name="gauge" size={14} /> {running ? "Applying…" : "Apply"}
      </Button>
    </>}>
      <div className="space-y-4">
        <div className="flex items-start gap-2 rounded-md bg-muted/40 px-2.5 py-2 text-[11px] text-muted-foreground">
          <Icon name="info" size={13} className="mt-px shrink-0" />
          <span>Retunes the transceiver's current VFO. No RF is emitted by a retune.</span>
        </div>

        {error && (
          <div className="flex items-start gap-2 rounded-md border border-warning/40 bg-warning/10 px-2.5 py-2 text-[11px] text-warning">
            <Icon name="alert" size={13} className="mt-px shrink-0" />
            <span>{error}</span>
          </div>
        )}

        {can("frequencySet") && (
          <Field
            label="Frequency"
            hint={parsedHz != null
              ? <>→ <span className="font-mono">{fmtRigFrequency(parsedHz)}</span> MHz</>
              : badFreq
                ? <span className="text-warning">Not a number — enter MHz-decimal (14.074) or raw Hz (14074000).</span>
                : <>Now <span className="font-mono">{r.frequencyHz != null ? fmtRigFrequency(r.frequencyHz) : "—"}</span> MHz. Leave empty to keep it.</>}
          >
            <Input
              value={freq}
              onChange={(e) => setFreq(e.target.value)}
              placeholder="14.074 (MHz) or 14074000 (Hz)"
              inputMode="decimal"
            />
          </Field>
        )}

        {can("modeSet") && (
          <Field label="Mode" hint={<>Now <span className="font-mono">{r.mode ?? "—"}</span>. Leave unchanged to keep it.</>}>
            <div className="space-y-1.5">
              <Select value={modeSel} onChange={(e) => setModeSel(e.target.value)}>
                <option value="">(leave unchanged)</option>
                {COMMON_RIG_MODES.map((m) => <option key={m} value={m}>{m}</option>)}
                <option value="other">Other (rig-native token)…</option>
              </Select>
              {modeSel === "other" && (
                <Input
                  value={modeCustom}
                  onChange={(e) => setModeCustom(e.target.value)}
                  placeholder='Rig-native mode token, e.g. "DATA-U"'
                />
              )}
            </div>
          </Field>
        )}
      </div>
    </Modal>
  );
}
