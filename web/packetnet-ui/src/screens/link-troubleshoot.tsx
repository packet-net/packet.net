// ============================================================
// Link troubleshoot (#467) — the per-link timing/recovery view a sysop
// reaches for when a connected-mode link is misbehaving. Consumes the
// monitor-v2 telemetry already exposed by the node:
//   - GET /api/v1/links → LinkStats: live SmoothedRttMs (SRTT) + Retries
//     (the live RC) + REJ/SREJ tallies + frame counts, per (port, peer).
//   - GET /api/v1/config → each port's configured AX.25 T1 / T3 timers
//     (ax25.t1Ms / ax25.t3Ms) — what SRTT/retries are tuned against.
// The view polls /links on a short interval, keeps a small per-link history
// ring, and draws SRTT + retry trend sparklines (inline SVG — no charting
// dep). Links coming/going are handled by keying on `port:peer`: a link that
// drops out of a poll is marked idle (kept briefly so a flapping link stays
// visible), and reappears live when telemetry resumes.
// ============================================================
import { useEffect, useRef, useState } from "react";
import { Page, PageHeader } from "@/components/layout/shell";
import { Badge, Card, Th, Td, EmptyState, Icon, InfoHint } from "@/components/ui";
import { cn } from "@/lib/utils";
import { api, useQuery } from "@/lib/api";
import type { LinkStats, NodeConfig } from "@/lib/types";

// How often we re-read /links, and how many samples each link's trend keeps.
const POLL_MS = 2000;
const HISTORY = 30;
// A link absent from this many consecutive polls is dropped from the view (a
// few cycles of grace so a momentarily-quiet link doesn't flicker away).
const STALE_AFTER_POLLS = 5;

interface LinkSample { rttMs: number; retries: number }
interface TrackedLink {
  key: string;          // `${portId}:${peer}` — stable identity across polls
  stat: LinkStats;      // the most recent telemetry row
  history: LinkSample[];// oldest → newest, capped at HISTORY
  missedPolls: number;  // consecutive polls this link was absent (0 = live)
}

export function LinkTroubleshoot() {
  // Config gives us each port's configured T1/T3 (the targets SRTT/retries are
  // judged against). One read on mount — config is effectively static here.
  const { data: config } = useQuery<NodeConfig>(api.config, []);

  // The tracked links, accumulated across polls. A ref mirrors state so the
  // interval callback always merges against the latest map without re-arming.
  const [links, setLinks] = useState<TrackedLink[]>([]);
  const linksRef = useRef<TrackedLink[]>([]);
  linksRef.current = links;
  const [paused, setPaused] = useState(false);
  const pausedRef = useRef(paused);
  pausedRef.current = paused;
  const [lastError, setLastError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    const merge = (rows: LinkStats[]) => {
      setLinks((prev) => {
        const byKey = new Map(prev.map((l) => [l.key, l]));
        const seen = new Set<string>();
        for (const stat of rows) {
          const key = `${stat.portId}:${stat.peer}`;
          seen.add(key);
          const existing = byKey.get(key);
          const sample: LinkSample = { rttMs: stat.smoothedRttMs, retries: stat.retries };
          byKey.set(key, {
            key,
            stat,
            history: [...(existing?.history ?? []), sample].slice(-HISTORY),
            missedPolls: 0,
          });
        }
        // Age out links absent this poll; drop them once past the grace window.
        for (const [key, l] of byKey) {
          if (seen.has(key)) continue;
          const missed = l.missedPolls + 1;
          if (missed > STALE_AFTER_POLLS) byKey.delete(key);
          else byKey.set(key, { ...l, missedPolls: missed });
        }
        // Stable order: live links first, then by port then peer.
        return [...byKey.values()].sort((a, b) =>
          a.missedPolls - b.missedPolls ||
          a.stat.portId.localeCompare(b.stat.portId) ||
          a.stat.peer.localeCompare(b.stat.peer));
      });
    };

    const poll = async () => {
      if (pausedRef.current) return;
      try {
        const rows = await api.linkStats();
        if (!alive) return;
        merge(rows);
        setLastError(null);
      } catch (e) {
        if (alive) setLastError(String((e as Error)?.message ?? e));
      }
    };

    void poll();
    const id = setInterval(() => { void poll(); }, POLL_MS);
    return () => { alive = false; clearInterval(id); };
  }, []);

  // Configured T1/T3 (ms) for a port, defaulting to the engine defaults the node
  // applies when the config leaves them null (T1 3000ms, T3 180000ms here mirror
  // the node's Ax25 defaults so the view never shows a misleading blank).
  const timers = (portId: string): { t1: number | null; t3: number | null } => {
    const ax25 = config?.ports.find((p) => p.id === portId)?.ax25;
    return { t1: ax25?.t1Ms ?? null, t3: ax25?.t3Ms ?? null };
  };

  const liveCount = links.filter((l) => l.missedPolls === 0).length;

  return (
    <Page>
      <PageHeader
        title="Link troubleshoot"
        subtitle="Per-link AX.25 timing and recovery — T1 / T3 / SRTT / retries"
        actions={
          <div className="flex items-center gap-2 text-xs text-muted-foreground">
            {paused
              ? <Badge variant="warning">paused</Badge>
              : <span className="flex items-center gap-1.5"><span className="h-1.5 w-1.5 rounded-full bg-success live-dot" />polling</span>}
            <button
              onClick={() => setPaused((p) => !p)}
              className="inline-flex items-center gap-1.5 rounded-md border border-input bg-transparent px-2.5 py-1 text-xs font-medium hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <Icon name={paused ? "play" : "pause"} size={13} />
              {paused ? "Resume" : "Pause"}
            </button>
          </div>
        }
      />

      {lastError && links.length === 0 && (
        <Card className="mb-4 border-danger/40 bg-danger/5 p-3 text-sm text-danger">
          Couldn't read link telemetry: {lastError}
        </Card>
      )}

      {links.length === 0 ? (
        <EmptyState
          icon="gauge"
          title="No active links"
          body="Per-link timing appears here for each connected-mode AX.25 session. Open or wait for a link and its T1/T3/SRTT/retries will stream in."
        />
      ) : (
        <>
          <p className="mb-3 text-xs text-muted-foreground">
            {liveCount} active {liveCount === 1 ? "link" : "links"}
            {links.length > liveCount && <span> · {links.length - liveCount} idle</span>}
          </p>

          {/* per-link cards — the troubleshoot detail with trend sparklines */}
          <div className="mb-6 grid grid-cols-1 gap-3 lg:grid-cols-2">
            {links.map((l) => (
              <LinkCard key={l.key} link={l} timers={timers(l.stat.portId)} />
            ))}
          </div>

          {/* compact comparison table across every link */}
          <Card className="overflow-hidden p-0">
            <div className="overflow-x-auto">
              <table className="w-full border-collapse">
                <thead className="border-b border-border">
                  <tr>
                    <Th>Peer</Th>
                    <Th className="w-20">Port</Th>
                    <Th className="w-20 text-right">
                      <span className="inline-flex items-center justify-end gap-1">T1 <InfoHint text="Configured T1 (ms) — the outstanding-ack timer. On expiry the node retransmits and bumps the retry count; T1 backs off as SRTT grows." /></span>
                    </Th>
                    <Th className="w-20 text-right">
                      <span className="inline-flex items-center justify-end gap-1">T3 <InfoHint text="Configured T3 (ms) — the idle-link keepalive timer. When the link is quiet for T3 the node polls the peer to confirm the link is still up." /></span>
                    </Th>
                    <Th className="w-24 text-right">
                      <span className="inline-flex items-center justify-end gap-1">SRTT <InfoHint text="Smoothed round-trip time (ms) the AX.25 engine measures live for this link — the basis for T1. Climbing SRTT means a slow or congested path." /></span>
                    </Th>
                    <Th className="w-20 text-right">
                      <span className="inline-flex items-center justify-end gap-1">Retries <InfoHint text="The live retry count (RC) for this link — how many times the current frame has been retransmitted after a T1 expiry. Non-zero means acks aren't getting back." /></span>
                    </Th>
                    <Th className="w-24 text-right">REJ/SREJ</Th>
                    <Th className="hidden w-28 text-right md:table-cell">Frames in/out</Th>
                  </tr>
                </thead>
                <tbody>
                  {links.map((l) => {
                    const t = timers(l.stat.portId);
                    return (
                      <tr key={l.key} className={cn("border-b border-border/60 font-mono text-xs", l.missedPolls > 0 && "opacity-50")}>
                        <Td className="font-semibold">
                          {l.stat.peer}
                          {l.missedPolls > 0 && <span className="ml-2 font-sans text-[10px] font-normal text-muted-foreground">idle</span>}
                        </Td>
                        <Td className="text-muted-foreground">{l.stat.portId}</Td>
                        <Td className="tnum text-right text-muted-foreground">{fmtMs(t.t1)}</Td>
                        <Td className="tnum text-right text-muted-foreground">{fmtMs(t.t3)}</Td>
                        <Td className={cn("tnum text-right", rttClass(l.stat.smoothedRttMs, t.t1))}>{l.stat.smoothedRttMs}ms</Td>
                        <Td className={cn("tnum text-right", l.stat.retries > 0 ? "text-warning" : "text-foreground")}>{l.stat.retries}</Td>
                        <Td className={cn("tnum text-right", l.stat.rejCount + l.stat.srejCount > 0 ? "text-danger" : "text-muted-foreground")}>{l.stat.rejCount}/{l.stat.srejCount}</Td>
                        <Td className="tnum hidden text-right text-muted-foreground md:table-cell">{l.stat.framesIn}/{l.stat.framesOut}</Td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </Card>
        </>
      )}
    </Page>
  );
}

// One link's detail card: the timer/recovery stats plus SRTT + retry trend
// sparklines so a sysop can spot a deteriorating link at a glance.
function LinkCard({ link, timers }: { link: TrackedLink; timers: { t1: number | null; t3: number | null } }) {
  const { stat, history, missedPolls } = link;
  const idle = missedPolls > 0;
  const rejTotal = stat.rejCount + stat.srejCount;
  return (
    <Card className={cn("p-4", idle && "opacity-60")}>
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <span className="font-mono text-sm font-semibold">{stat.peer}</span>
          <Badge variant="muted">{stat.portId}</Badge>
        </div>
        {idle
          ? <Badge variant="warning">idle</Badge>
          : <span className="flex items-center gap-1.5 text-[11px] text-muted-foreground"><span className="h-1.5 w-1.5 rounded-full bg-success live-dot" />live</span>}
      </div>

      <div className="mt-3 grid grid-cols-2 gap-x-6 gap-y-2 sm:grid-cols-4">
        <Stat label="T1" value={fmtMs(timers.t1)} muted />
        <Stat label="T3" value={fmtMs(timers.t3)} muted />
        <Stat label="SRTT" value={`${stat.smoothedRttMs}ms`} className={rttClass(stat.smoothedRttMs, timers.t1)} />
        <Stat label="Retries" value={String(stat.retries)} className={stat.retries > 0 ? "text-warning" : undefined} />
      </div>

      <div className="mt-3 grid grid-cols-2 gap-3">
        <Sparkline
          label="SRTT"
          values={history.map((h) => h.rttMs)}
          format={(v) => `${Math.round(v)}ms`}
          colorClass={rttClass(stat.smoothedRttMs, timers.t1)}
        />
        <Sparkline
          label="Retries"
          values={history.map((h) => h.retries)}
          format={(v) => String(Math.round(v))}
          colorClass={stat.retries > 0 ? "text-warning" : "text-muted-foreground"}
        />
      </div>

      <div className="mt-3 flex items-center justify-between border-t border-border/60 pt-2 text-[11px] text-muted-foreground">
        <span className={cn("font-mono", rejTotal > 0 && "text-danger")}>REJ {stat.rejCount} · SREJ {stat.srejCount}</span>
        <span className="font-mono">{stat.framesIn} in / {stat.framesOut} out</span>
      </div>
    </Card>
  );
}

function Stat({ label, value, muted, className }: { label: string; value: string; muted?: boolean; className?: string }) {
  return (
    <div>
      <p className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">{label}</p>
      <p className={cn("tnum mt-0.5 font-mono text-sm", muted ? "text-muted-foreground" : "text-foreground", className)}>{value}</p>
    </div>
  );
}

// A dependency-free inline-SVG sparkline of a metric's recent trend. Flat when
// there's <2 samples (a fresh link); auto-scales to the run's own min/max.
function Sparkline({ label, values, format, colorClass }: {
  label: string; values: number[]; format: (v: number) => string; colorClass?: string;
}) {
  const W = 100, H = 28;
  const latest = values.length ? values[values.length - 1] : 0;
  let path = "";
  if (values.length >= 2) {
    const min = Math.min(...values);
    const max = Math.max(...values);
    const span = max - min || 1;
    const stepX = W / (values.length - 1);
    path = values
      .map((v, i) => {
        const x = i * stepX;
        const y = H - 2 - ((v - min) / span) * (H - 4);
        return `${i === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`;
      })
      .join(" ");
  }
  return (
    <div className="rounded-md border border-border/60 bg-background/40 p-2">
      <div className="flex items-center justify-between">
        <span className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">{label}</span>
        <span className={cn("tnum font-mono text-[11px]", colorClass ?? "text-foreground")}>{format(latest)}</span>
      </div>
      <svg viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" className="mt-1 h-7 w-full" role="img" aria-label={`${label} trend`}>
        {path
          ? <path d={path} fill="none" stroke="currentColor" strokeWidth={1.5} className={colorClass ?? "text-foreground"} vectorEffect="non-scaling-stroke" />
          : <line x1={0} y1={H / 2} x2={W} y2={H / 2} stroke="currentColor" strokeWidth={1} className="text-muted-foreground/40" vectorEffect="non-scaling-stroke" />}
      </svg>
    </div>
  );
}

// Colour SRTT relative to the configured T1 (its target): green well under T1,
// amber as it approaches, red once it meets/exceeds T1 (T1 should be ≥ ~2·SRTT,
// so SRTT near T1 means the timer is barely keeping ahead of the path).
function rttClass(rttMs: number, t1: number | null): string {
  if (!t1) return rttMs > 1500 ? "text-warning" : "text-foreground";
  if (rttMs >= t1) return "text-danger";
  if (rttMs >= t1 / 2) return "text-warning";
  return "text-foreground";
}

function fmtMs(ms: number | null): string {
  if (ms == null) return "—";
  if (ms >= 1000) return `${(ms / 1000).toFixed(ms % 1000 === 0 ? 0 : 1)}s`;
  return `${ms}ms`;
}
