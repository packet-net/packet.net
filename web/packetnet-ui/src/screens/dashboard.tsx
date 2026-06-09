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
} from "@/components/ui";
import { cn } from "@/lib/utils";
import { api, useQuery } from "@/lib/api";
import { portHealth } from "@/lib/health";
import { KIND_LABEL, fmtUptime } from "@/lib/mock";

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

  // Frames/sec ticks via a timer (mock live feed) — README §3.
  const [fps, setFps] = useState(7.4);
  useEffect(() => {
    const t = setInterval(() => setFps(+(4 + Math.random() * 9).toFixed(1)), 1400);
    return () => clearInterval(t);
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
