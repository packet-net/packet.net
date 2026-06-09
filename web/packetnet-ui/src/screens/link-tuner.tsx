// ============================================================
// Link tuning workspace (README "Link tuning workspace") — a focused
// full-page troubleshooting tool opened from a port's "Tune link".
// Send numbered frame bursts to a partner, watch a delivery grid resolve
// ack/lost, tune TX delay / persistence / ack-timeout live, compare runs,
// auto-tune TX delay, and coordinate over a chat panel.
//
// Reads ?port=<id> from the URL. Delivery is simulated from the params +
// path difficulty; in production this drives real frame bursts.
// ============================================================
import { useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Page } from "@/components/layout/shell";
import { Button, Card, Field, Input, Select, Label, Slider, Icon } from "@/components/ui";
import { cn } from "@/lib/utils";
import {
  NODE_CONFIG, PORT_SETUP, PARAM_HELP, KIND_LABEL, CALLS,
  persistPct, pctToPersist,
} from "@/lib/mock";

function clamp(n: number, a: number, b: number): number { return Math.max(a, Math.min(b, n)); }

interface TuneParams { txDelay: number; persistence: number; ackTimeout: number }
interface FrameCell { n: number; state: "pending" | "ack" | "lost" }
interface Run { id: number; params: TuneParams; delivered: number; total: number; pct: number; partner: string }
interface ChatMsg { who: "me" | "them"; call: string; text: string }

// probability a numbered frame is delivered given the params + path difficulty
function deliveryProb(p: TuneParams, difficulty: string): number {
  const floor = ({ easy: 0.86, moderate: 0.7, hard: 0.5 } as Record<string, number>)[difficulty] ?? 0.7;
  let prob = floor;
  prob += (1 - Math.abs(p.txDelay - 300) / 320) * 0.18; // txdelay sweet spot ~300ms
  prob += (1 - p.persistence / 255) * 0.08;             // politer = fewer collisions
  prob += p.ackTimeout >= 2500 && p.ackTimeout <= 4500 ? 0.05 : -0.03;
  return clamp(prob, 0.25, 0.985);
}

export function LinkTuner() {
  const [searchParams] = useSearchParams();
  const port = useMemo(() => {
    const id = searchParams.get("port");
    return NODE_CONFIG.ports.find((p) => p.id === id) ?? NODE_CONFIG.ports[0];
  }, [searchParams]);
  const setup = PORT_SETUP[port.id] ?? { radio: null, channel: "shared", difficulty: "moderate", custom: false };

  const [partner, setPartner] = useState(CALLS[0]);
  const [count, setCount] = useState(20);
  const [params, setParams] = useState<TuneParams>({
    txDelay: port.kiss?.txDelay ?? 300,
    persistence: port.kiss?.persistence ?? 63,
    ackTimeout: port.ax25?.t1Ms ?? 3000,
  });
  const [frames, setFrames] = useState<FrameCell[]>([]);
  const [running, setRunning] = useState(false);
  const [runs, setRuns] = useState<Run[]>([]);
  const [autoTuning, setAutoTuning] = useState(false);
  const [chat, setChat] = useState<ChatMsg[]>([
    { who: "them", call: "G8PZT", text: "ok ready when you are, watching here" },
    { who: "me", call: "M0LTE", text: "sending a burst of 20 now" },
  ]);
  const [draft, setDraft] = useState("");
  const timer = useRef<ReturnType<typeof setInterval> | null>(null);

  useEffect(() => () => { if (timer.current) clearInterval(timer.current); }, []);

  const runBurst = (overrideParams?: TuneParams): Promise<Run> => new Promise((resolve) => {
    const p = overrideParams ?? params;
    const prob = deliveryProb(p, setup.difficulty);
    const total = count;
    setFrames(Array.from({ length: total }, (_, i) => ({ n: i + 1, state: "pending" })));
    setRunning(true);
    let i = 0;
    if (timer.current) clearInterval(timer.current);
    timer.current = setInterval(() => {
      setFrames((prev) => prev.map((f, idx) => (idx === i ? { ...f, state: Math.random() < prob ? "ack" : "lost" } : f)));
      i++;
      if (i >= total) {
        if (timer.current) clearInterval(timer.current);
        setTimeout(() => {
          setFrames((prev) => {
            const delivered = prev.filter((f) => f.state === "ack").length;
            const run: Run = { id: Date.now(), params: { ...p }, delivered, total, pct: Math.round((delivered / total) * 100), partner };
            setRuns((r) => [run, ...r].slice(0, 6));
            resolve(run);
            return prev;
          });
          setRunning(false);
        }, 150);
      }
    }, 90);
  });

  const autoTune = async () => {
    setAutoTuning(true);
    const sweep = [120, 200, 300, 420, 550];
    let best: { txDelay: number; pct: number } | null = null;
    for (const txDelay of sweep) {
      const trial = { ...params, txDelay };
      setParams(trial);
      // eslint-disable-next-line no-await-in-loop
      const run = await runBurst(trial);
      if (!best || run.pct > best.pct) best = { txDelay, pct: run.pct };
    }
    if (best) {
      const winner = best;
      setParams((p) => ({ ...p, txDelay: winner.txDelay }));
      setChat((c) => [...c, { who: "me", call: "pdn", text: `auto-sweep done — best TX delay ${winner.txDelay}ms (${winner.pct}% delivered)` }]);
    }
    setAutoTuning(false);
  };

  const delivered = frames.filter((f) => f.state === "ack").length;
  const done = frames.filter((f) => f.state !== "pending").length;
  const pct = done ? Math.round((delivered / done) * 100) : 0;
  const sendChat = () => { if (!draft.trim()) return; setChat((c) => [...c, { who: "me", call: "M0LTE", text: draft }]); setDraft(""); };

  const pctClass = (v: number) => (v >= 90 ? "text-success" : v >= 70 ? "text-warning" : "text-danger");
  const pctChip = (v: number) => (v >= 90 ? "bg-success/15 text-success" : v >= 70 ? "bg-warning/15 text-warning" : "bg-danger/15 text-danger");

  return (
    <Page>
      <div className="grid min-h-0 grid-cols-1 gap-5 lg:grid-cols-[1fr_340px]">
        {/* workspace */}
        <div className="min-w-0 space-y-5">
          {/* header */}
          <div className="flex items-center gap-3">
            <span className="grid h-8 w-8 place-items-center rounded-md bg-primary/15 text-primary"><Icon name="signal" size={17} /></span>
            <div>
              <h1 className="text-xl font-semibold leading-tight tracking-tight">Link tuning</h1>
              <p className="text-xs text-muted-foreground">port <span className="font-mono">{port.id}</span> · {KIND_LABEL[port.transport.kind]} · {setup.difficulty} path</p>
            </div>
          </div>

          {/* partner + burst controls */}
          <div className="flex flex-wrap items-end gap-3">
            <Field label="Partner station" className="w-44">
              <Select value={partner} onChange={(e) => setPartner(e.target.value)}>
                {CALLS.map((c) => <option key={c} value={c}>{c}</option>)}
              </Select>
            </Field>
            <Field label="Frames per burst" className="w-32">
              <Input type="number" value={count} onChange={(e) => setCount(clamp(+e.target.value, 5, 50))} className="font-mono" />
            </Field>
            <Button onClick={() => runBurst()} disabled={running || autoTuning}>
              <Icon name={running ? "pause" : "play"} size={14} />{running ? "Sending…" : "Send burst"}
            </Button>
            <Button variant="outline" onClick={autoTune} disabled={running || autoTuning}>
              <Icon name="restart" size={14} />{autoTuning ? "Auto-tuning…" : "Auto-tune TX delay"}
            </Button>
          </div>

          {/* delivery grid */}
          <Card className="p-4">
            <div className="mb-3 flex items-center justify-between">
              <span className="text-sm font-semibold">Delivery</span>
              {done > 0 && (
                <span className="flex items-center gap-3 text-xs">
                  <span className="text-muted-foreground">delivered <span className={cn("font-mono font-semibold", pctClass(pct))}>{delivered}/{done}</span></span>
                  <span className={cn("rounded px-1.5 py-0.5 font-mono font-semibold", pctChip(pct))}>{pct}%</span>
                </span>
              )}
            </div>
            {frames.length === 0 ? (
              <div className="py-8 text-center text-xs text-muted-foreground">Send a burst to see how many numbered frames reach <span className="font-mono">{partner}</span>.</div>
            ) : (
              <div className="grid grid-cols-10 gap-1.5">
                {frames.map((f) => (
                  <div key={f.n} title={`#${f.n} ${f.state}`} className={cn("flex aspect-square items-center justify-center rounded font-mono text-[10px] transition-colors",
                    f.state === "pending" ? "bg-muted text-muted-foreground/50" :
                      f.state === "ack" ? "bg-success/20 text-success" : "bg-danger/20 text-danger line-through")}>{f.n}</div>
                ))}
              </div>
            )}
          </Card>

          {/* live parameter tweaks */}
          <Card className="p-4">
            <p className="mb-3 text-sm font-semibold">Tune while you test <span className="font-normal text-muted-foreground">— applies live to the port</span></p>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
              <div>
                <div className="mb-1.5 flex items-center justify-between"><Label>{PARAM_HELP.txDelay.label}</Label><span className="font-mono text-xs text-muted-foreground">{params.txDelay} ms</span></div>
                <Slider value={params.txDelay} min={50} max={600} step={10} onChange={(v) => setParams((p) => ({ ...p, txDelay: v }))} />
              </div>
              <div>
                <div className="mb-1.5 flex items-center justify-between"><Label>{PARAM_HELP.persistence.label}</Label><span className="font-mono text-xs text-muted-foreground">{persistPct(params.persistence)}%</span></div>
                <Slider value={persistPct(params.persistence)} min={0} max={100} onChange={(v) => setParams((p) => ({ ...p, persistence: pctToPersist(v) }))} />
              </div>
              <div>
                <div className="mb-1.5 flex items-center justify-between"><Label>{PARAM_HELP.t1Ms.label}</Label><span className="font-mono text-xs text-muted-foreground">{params.ackTimeout} ms</span></div>
                <Slider value={params.ackTimeout} min={1000} max={8000} step={250} onChange={(v) => setParams((p) => ({ ...p, ackTimeout: v }))} />
              </div>
            </div>
          </Card>

          {/* run history (the compare loop) */}
          {runs.length > 0 && (
            <Card className="p-4">
              <p className="mb-3 text-sm font-semibold">Runs</p>
              <div className="space-y-1.5">
                {runs.map((r) => (
                  <div key={r.id} className="flex items-center gap-3 rounded-md bg-muted/40 px-3 py-2 text-xs">
                    <span className={cn("w-12 shrink-0 rounded px-1.5 py-0.5 text-center font-mono font-semibold", pctChip(r.pct))}>{r.pct}%</span>
                    <span className="font-mono text-muted-foreground">{r.delivered}/{r.total}</span>
                    <span className="ml-auto truncate font-mono text-muted-foreground">TXd {r.params.txDelay}ms · pers {persistPct(r.params.persistence)}% · T1 {r.params.ackTimeout}ms</span>
                  </div>
                ))}
              </div>
            </Card>
          )}
        </div>

        {/* coordination chat — stacks under the workspace on narrow screens */}
        <Card className="flex min-h-[20rem] flex-col overflow-hidden p-0 lg:min-h-0">
          <div className="flex items-center gap-2 border-b border-border px-4 py-3">
            <Icon name="sessions" size={15} className="text-muted-foreground" />
            <span className="text-sm font-medium">Coordinate</span>
            <span className="ml-auto flex items-center gap-1.5 text-xs text-muted-foreground"><span className="h-1.5 w-1.5 rounded-full bg-success live-dot" />{partner} op</span>
          </div>
          <div className="min-h-0 flex-1 space-y-2 overflow-y-auto p-3">
            {chat.map((m, i) => (
              <div key={i} className={cn("flex flex-col", m.who === "me" ? "items-end" : "items-start")}>
                <span className="mb-0.5 text-[10px] text-muted-foreground">{m.call}</span>
                <span className={cn("max-w-[85%] rounded-lg px-2.5 py-1.5 text-xs", m.who === "me" ? "bg-primary text-primary-foreground" : "bg-muted text-foreground")}>{m.text}</span>
              </div>
            ))}
          </div>
          <div className="flex items-center gap-2 border-t border-border p-3">
            <Input value={draft} onChange={(e) => setDraft(e.target.value)} onKeyDown={(e) => e.key === "Enter" && sendChat()} placeholder="message the other op…" className="text-xs" />
            <Button size="iconSm" onClick={sendChat}><Icon name="send" size={14} /></Button>
          </div>
        </Card>
      </div>
    </Page>
  );
}
