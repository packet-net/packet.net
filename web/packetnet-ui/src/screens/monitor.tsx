// ============================================================
// Live monitor (README §4) — the marquee screen.
// Per-link stat strip, filters, a streaming frame table with a
// custom easeOutCubic smooth-prepend, and a Wireshark-style
// FrameDecode (decoded fields + hex/ASCII octet dump).
// ============================================================
import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Page, PageHeader } from "@/components/layout/shell";
import {
  Button, Badge, FrameBadge, Card, Input, Select, Sheet, Th, Td, EmptyState, Icon,
} from "@/components/ui";
import { cn } from "@/lib/utils";
import { api, useFrameStream, useQuery } from "@/lib/api";
import { FRAME_TYPES, hex } from "@/lib/catalogue";
import type { LinkStats, MonitorEvent } from "@/lib/types";

export function Monitor() {
  const { frames, paused, setPaused, clear, connected } = useFrameStream(500);
  const { data: links } = useQuery(api.linkStats, []);
  const { data: config } = useQuery(api.config, []);
  // Port-filter options are THIS node's ports. Empty until /config resolves - the
  // "All ports" option still works, and offering a port the node does not have would
  // only filter the table down to nothing (#691 C022).
  const portIds = config?.ports.map((p) => p.id) ?? [];

  const [fPort, setFPort] = useState("all");
  const [fType, setFType] = useState("all");
  const [fCall, setFCall] = useState("");
  const [selected, setSelected] = useState<MonitorEvent | null>(null);

  // ---- smooth-prepend (README §4) -------------------------------------------
  // Newest rows insert at the top. In follow mode we hold the visual frame then
  // glide the container up via a custom easeOutCubic rAF tween (~520ms) — not
  // CSS scroll-behavior, which is too steppy at this cadence. When the operator
  // has scrolled down to read (scrollTop > 140) we disengage follow and preserve
  // their position by offsetting scrollTop by the height that went in above them.
  //
  // That offset is measured from an ANCHOR ROW (the row that was on top last time)
  // rather than from a scrollHeight delta, and the effect is keyed on the newest row's
  // identity rather than on the row COUNT. Both had to change together: the frame ring
  // holds 500 and is filled to the cap by the bootstrap fetch on a busy node, after
  // which every arriving frame evicts one at the tail, so the count never changes again
  // (the effect stopped running) and the height delta is ~0 anyway (#702 C049).
  const scrollRef = useRef<HTMLDivElement>(null);
  const anchorRef = useRef<{ key: string; offsetTop: number } | null>(null);
  const followRef = useRef(true);
  const firstRef = useRef(true);
  const tweenRef = useRef(0);

  const onScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    if (el.scrollTop < 8) followRef.current = true;
    else if (el.scrollTop > 140) followRef.current = false;
  };

  const glideToTop = (el: HTMLDivElement) => {
    cancelAnimationFrame(tweenRef.current);
    const start = el.scrollTop;
    const startT = performance.now();
    const dur = 520;
    const ease = (t: number) => 1 - Math.pow(1 - t, 3); // easeOutCubic
    const step = (now: number) => {
      const t = Math.min(1, (now - startT) / dur);
      el.scrollTop = Math.round(start * (1 - ease(t)));
      if (t < 1) tweenRef.current = requestAnimationFrame(step);
    };
    tweenRef.current = requestAnimationFrame(step);
  };

  const filtered = frames.filter(
    (f) =>
      (fPort === "all" || f.portId === fPort) &&
      (fType === "all" || f.type === fType) &&
      (!fCall ||
        f.source.includes(fCall.toUpperCase()) ||
        f.dest.includes(fCall.toUpperCase())),
  );

  // The identity of the topmost VISIBLE row: what the prepend effect keys on.
  const newestKey = filtered.length > 0 ? frameKey(filtered[0]) : "";

  // Re-baseline the anchor on a filter change so the next arriving frame doesn't lurch: the
  // list is rebuilt wholesale there, which is not a prepend. Declared BEFORE the prepend
  // effect so it wins the commit they share (layout effects run in declaration order).
  useLayoutEffect(() => {
    anchorRef.current = topRow(scrollRef.current);
  }, [fPort, fType, fCall]);

  // glide on new rows; preserve position when scrolled away to read
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    if (firstRef.current) {
      firstRef.current = false;
      anchorRef.current = topRow(el);
      return;
    }
    // How far the anchor row moved down IS the height that went in above it. A row evicted
    // at the tail can't move it, so this keeps working at the ring cap; if the anchor itself
    // was evicted (a burst bigger than the viewport backlog) we simply don't compensate.
    const anchor = anchorRef.current;
    const now = anchor ? rowOffsetTop(el, anchor.key) : null;
    const added = anchor && now !== null ? now - anchor.offsetTop : 0;
    if (added > 0) {
      el.scrollTop = el.scrollTop + added; // hold the visual frame / keep the read row still…
      if (followRef.current) glideToTop(el); // …then, in follow mode, ease back to the top
    }
    anchorRef.current = topRow(el);
  }, [newestKey, filtered.length]);

  // tear the tween down on unmount
  useLayoutEffect(() => () => cancelAnimationFrame(tweenRef.current), []);

  const newestSeq = frames[0]?.seq;

  return (
    <Page>
      <PageHeader
        title="Live monitor"
        subtitle="Frames on the air — every port, pre-address-filter"
        actions={
          <div className="flex items-center gap-2">
            <Button variant={paused ? "default" : "outline"} size="sm" onClick={() => setPaused(!paused)}>
              <Icon name={paused ? "play" : "pause"} size={14} />
              {paused ? "Resume" : "Pause"}
            </Button>
            <Button variant="outline" size="sm" onClick={clear}>
              <Icon name="trash" size={14} /> Clear
            </Button>
          </div>
        }
      />

      {/* per-link stat strip */}
      <div className="mb-4 grid grid-cols-2 gap-2 sm:grid-cols-4">
        {(links ?? []).map((l: LinkStats, i: number) => (
          <div key={i} className="rounded-lg border border-border bg-card p-3">
            <div className="flex items-center justify-between">
              <span className="font-mono text-xs font-semibold">{l.peer}</span>
              <Badge variant="muted">{l.portId}</Badge>
            </div>
            <div className="mt-2 grid grid-cols-2 gap-y-1 text-[11px] text-muted-foreground">
              <span>RTT</span>
              <span className={cn("tnum text-right font-mono", l.smoothedRttMs > 1500 ? "text-warning" : "text-foreground")}>{l.smoothedRttMs}ms</span>
              <span>Retries</span>
              <span className={cn("tnum text-right font-mono", l.retries > 0 ? "text-warning" : "text-foreground")}>{l.retries}</span>
              <span>REJ/SREJ</span>
              <span className={cn("tnum text-right font-mono", l.rejCount + l.srejCount > 0 ? "text-danger" : "text-foreground")}>{l.rejCount}/{l.srejCount}</span>
            </div>
          </div>
        ))}
      </div>

      {/* filters */}
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <div className="relative">
          <Icon name="search" size={14} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
          <Input value={fCall} onChange={(e) => setFCall(e.target.value)} placeholder="callsign…" className="h-8 w-36 pl-8 font-mono text-xs" />
        </div>
        <Select value={fPort} onChange={(e) => setFPort(e.target.value)} className="h-8 w-32 text-xs">
          <option value="all">All ports</option>
          {portIds.map((p) => <option key={p} value={p}>{p}</option>)}
        </Select>
        <Select value={fType} onChange={(e) => setFType(e.target.value)} className="h-8 w-32 text-xs">
          <option value="all">All types</option>
          {FRAME_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
        </Select>
        <div className="ml-auto flex items-center gap-2 text-xs text-muted-foreground">
          {/* The dot tells the truth about the FEED, not just the pause button: a dropped
              stream (expired token, node restart) now reads "reconnecting" while the client
              backs off and re-subscribes, instead of claiming to stream into a dead socket. */}
          {paused
            ? <Badge variant="warning">paused</Badge>
            : connected
              ? <span className="flex items-center gap-1.5"><span className="h-1.5 w-1.5 rounded-full bg-success live-dot" />streaming</span>
              : <span className="flex items-center gap-1.5"><span className="h-1.5 w-1.5 rounded-full bg-warning" />reconnecting</span>}
          <span className="tnum">{filtered.length} frames</span>
        </div>
      </div>

      {/* streaming frame table */}
      <Card className="overflow-hidden p-0">
        <div ref={scrollRef} onScroll={onScroll} className="max-h-[calc(100vh-22rem)] overflow-y-auto">
          <table className="w-full border-collapse">
            <thead className="sticky top-0 z-10 bg-card/95 backdrop-blur">
              <tr className="border-b border-border">
                <Th className="w-24">Time</Th>
                <Th className="w-16">Port</Th>
                <Th className="w-8"></Th>
                <Th>Source → Dest</Th>
                <Th className="w-16">Type</Th>
                <Th className="hidden w-20 md:table-cell">PID</Th>
                <Th className="w-14 text-right">Len</Th>
                <Th className="w-24 text-right">RSSI</Th>
                <Th className="hidden lg:table-cell">Summary</Th>
              </tr>
            </thead>
            <tbody>
              {filtered.length === 0 && (
                <tr>
                  <td colSpan={9}>
                    <div className="py-10">
                      <EmptyState icon="filter" title="No frames match" body="Adjust the filters, or resume the stream." />
                    </div>
                  </td>
                </tr>
              )}
              {filtered.map((f) => (
                <tr
                  key={f.seq}
                  data-frame={frameKey(f)}
                  onClick={() => setSelected(f)}
                  className={cn(
                    "cursor-pointer border-b border-border/60 font-mono text-xs hover:bg-accent/50",
                    selected?.seq === f.seq && "bg-accent/60",
                    f.seq === newestSeq && !paused && "row-flash",
                  )}
                >
                  <Td className="text-muted-foreground">{fmtTime(f.timestamp)}</Td>
                  <Td><span className="text-muted-foreground">{f.portId}</span></Td>
                  <Td>
                    {f.direction === "in"
                      ? <Icon name="arrowDown" size={13} className="text-emerald-500" />
                      : <Icon name="arrowUp" size={13} className="text-sky-500" />}
                  </Td>
                  <Td>
                    <span className="font-semibold">{f.source}</span>
                    <span className="text-muted-foreground"> → {f.dest}</span>
                    {f.path.length > 0 && <span className="text-muted-foreground/60"> v {f.path.join(",")}</span>}
                  </Td>
                  <Td><FrameBadge type={f.type} classKind={f.classKind} /></Td>
                  <Td className="hidden text-muted-foreground md:table-cell">{f.pid || "—"}</Td>
                  <Td className="tnum text-right text-muted-foreground">{f.length}</Td>
                  <Td className="text-right">
                    {f.rssiDbm == null ? (
                      <span className="text-muted-foreground/50">—</span>
                    ) : (
                      <span className="tnum inline-flex flex-col items-end leading-tight">
                        <span className="text-foreground/90">{f.rssiDbm} dBm</span>
                        {f.snrDb != null && <span className="text-[10px] text-muted-foreground">{f.snrDb} dB SNR</span>}
                      </span>
                    )}
                  </Td>
                  <Td className="hidden text-muted-foreground lg:table-cell">{f.summary}</Td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      {/* full Wireshark-style decode */}
      <Sheet
        open={selected !== null}
        onClose={() => setSelected(null)}
        title="Frame decode"
        subtitle={selected ? `${selected.source} → ${selected.dest} · ${selected.type} · ${selected.portId}` : undefined}
        width="max-w-3xl"
      >
        {selected && <FrameDecode f={selected} />}
      </Sheet>
    </Page>
  );
}

// A frame's stable identity: `seq` restarts at 1 on every node boot, so it is (bootId, seq),
// the same pair api.ts's mergeLiveFrame dedupes on. Stamped on each row as `data-frame` so the
// smooth-prepend effect can find its anchor row again after a prepend.
const frameKey = (f: MonitorEvent): string => `${f.bootId ?? ""}:${f.seq}`;

/** The top row's identity + where it currently sits, or null when the table is empty. */
function topRow(el: HTMLDivElement | null): { key: string; offsetTop: number } | null {
  const row = el?.querySelector<HTMLElement>("tbody tr[data-frame]");
  return row ? { key: row.dataset.frame ?? "", offsetTop: row.offsetTop } : null;
}

/** Where the row with `key` sits now, or null when it has been filtered out / evicted. */
function rowOffsetTop(el: HTMLDivElement, key: string): number | null {
  const row = el.querySelector<HTMLElement>(`tbody tr[data-frame="${key}"]`);
  return row ? row.offsetTop : null;
}

// A muted em-dash for absent radio metadata (never render a bare 0 for a null reading).
const emdash = <span className="text-muted-foreground/50">—</span>;

function fmtTime(ts: string | Date): string {
  const d = ts instanceof Date ? ts : new Date(ts);
  return `${d.toLocaleTimeString("en-GB", { hour12: false })}.${String(d.getMilliseconds()).padStart(3, "0")}`;
}

// Full Wireshark-style frame decode: AX.25 fields + hex/ASCII octet dump.
function FrameDecode({ f }: { f: MonitorEvent }) {
  const copyHex = () => {
    const text = f.raw.map((b) => hex(b)).join(" ");
    void navigator.clipboard?.writeText(text);
  };
  return (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
      {/* decoded fields */}
      <div>
        <p className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">AX.25 frame</p>
        <div className="space-y-px rounded-md border border-border font-mono text-xs">
          <DecRow k="Direction" v={f.direction === "in" ? "RX (received)" : "TX (transmitted)"} />
          <DecRow k="Port" v={f.portId} />
          <DecRow k="Source SSID" v={f.source} />
          <DecRow k="Destination SSID" v={f.dest} />
          {f.path.length > 0 && <DecRow k="Digipeater path" v={f.path.join(", ")} />}
          <DecRow k="Frame type" v={<FrameBadge type={f.type} classKind={f.classKind} />} />
          <DecRow k="Class" v={`${f.classKind}-frame`} />
          <DecRow k="Command/Response" v={f.command ? "Command" : "Response"} />
          {f.ns != null && <DecRow k="N(S) send seq" v={f.ns} />}
          {f.nr != null && <DecRow k="N(R) recv seq" v={f.nr} />}
          <DecRow k="Poll/Final" v={f.pf ? "1" : "0"} />
          {/* The server names only four PIDs (MonitorEvent.cs PidName) and returns null for
              the rest, so an unnamed PID shows its octet alone - never "0xE0 - null" (#691 C050). */}
          {f.pid && <DecRow k="PID" v={f.pidName ? `${f.pid} - ${f.pidName}` : f.pid} />}
          <DecRow k="Length" v={`${f.length} bytes`} />
          {/* radio metadata (null on TX frames / ports with no radio → em-dash) */}
          <DecRow k="RSSI" v={f.rssiDbm == null ? emdash : `${f.rssiDbm} dBm`} />
          <DecRow k="SNR" v={f.snrDb == null ? emdash : `${f.snrDb} dB`} />
          <DecRow k="Noise floor" v={f.noiseFloorDbm == null ? emdash : `${f.noiseFloorDbm} dBm`} />
        </div>
      </div>

      {/* hex dump */}
      <div>
        <p className="mb-2 flex items-center justify-between text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
          <span>Raw octets</span>
          <button onClick={copyHex} className="flex items-center gap-1 normal-case text-muted-foreground hover:text-foreground">
            <Icon name="copy" size={12} /> copy hex
          </button>
        </p>
        <div className="rounded-md border border-border bg-background/60 p-3 font-mono text-xs leading-relaxed">
          {Array.from({ length: Math.ceil(f.raw.length / 16) }).map((_, row) => {
            const slice = f.raw.slice(row * 16, row * 16 + 16);
            return (
              <div key={row} className="flex gap-4">
                <span className="text-muted-foreground/60">{hex(row * 16, 4)}</span>
                <span className="flex-1 text-foreground/90">{slice.map((b) => hex(b)).join(" ")}</span>
                <span className="text-muted-foreground">{slice.map((b) => (b >= 32 && b < 127 ? String.fromCharCode(b) : "·")).join("")}</span>
              </div>
            );
          })}
        </div>
        <p className="mt-2 text-[11px] text-muted-foreground">Decoded summary: <span className="font-mono text-foreground/80">{f.summary}</span></p>
      </div>
    </div>
  );
}

function DecRow({ k, v }: { k: string; v: ReactNode }) {
  return (
    <div className="flex items-center justify-between px-3 py-1.5 odd:bg-muted/30">
      <span className="text-muted-foreground">{k}</span>
      <span className="text-right text-foreground/90">{v}</span>
    </div>
  );
}
