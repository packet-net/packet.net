// ============================================================
// Shared AX.25 ping — connectionless TEST-frame round-trip check
// (the analogue of the Linux `axping` tool). No session required;
// the far station's link layer echoes the TEST frame straight back.
//
// PingButton is the drop-in trigger imported by Routes (neighbours +
// destinations) and the Ports header. It opens a self-contained
// Ax25Ping modal, optionally pre-targeted.
// ============================================================
import { useEffect, useRef, useState } from "react";
import { Button, Modal, Field, Input, Select, Icon, type ButtonVariant, type ButtonSize } from "@/components/ui";
import { PORTS_LIST } from "@/lib/mock";

// plausible round-trip baseline per transport (mock; live reads real RTTs)
function pingBaseline(portId: string): { rtt: number; jitter: number; loss: number } {
  // link-dn is an AXUDP network link → fast + reliable; serial/HF-ish ports are slow + lossy.
  if (portId === "link-dn") return { rtt: 42, jitter: 14, loss: 0.02 };
  if (portId === "hf-300") return { rtt: 2600, jitter: 900, loss: 0.18 }; // HF-ish
  return { rtt: 720, jitter: 280, loss: 0.06 }; // VHF/UHF
}

interface PingResult { seq: number; rtt: number | null }

function Ax25Ping({ station, portId, onClose }: { station: string; portId?: string; onClose: () => void }) {
  const [call, setCall] = useState(station ?? "");
  const [via, setVia] = useState(portId ?? PORTS_LIST[0]);
  const [count, setCount] = useState(5);
  const [results, setResults] = useState<PingResult[]>([]);
  const [running, setRunning] = useState(false);
  const timer = useRef<ReturnType<typeof setInterval> | null>(null);
  useEffect(() => () => { if (timer.current) clearInterval(timer.current); }, []);

  const run = () => {
    if (!call.trim()) return;
    const base = pingBaseline(via);
    setResults([]);
    setRunning(true);
    let i = 0;
    if (timer.current) clearInterval(timer.current);
    timer.current = setInterval(() => {
      const lost = Math.random() < base.loss;
      const rtt = lost ? null : Math.max(20, Math.round(base.rtt + (Math.random() * 2 - 1) * base.jitter));
      setResults((r) => [...r, { seq: i + 1, rtt }]);
      i++;
      if (i >= count) {
        if (timer.current) clearInterval(timer.current);
        setTimeout(() => setRunning(false), 300);
      }
    }, 700);
  };

  const got = results.filter((r) => r.rtt != null);
  const rtts = got.map((r) => r.rtt as number);
  const loss = results.length ? Math.round(((results.length - got.length) / results.length) * 100) : 0;
  const stat = rtts.length
    ? { min: Math.min(...rtts), avg: Math.round(rtts.reduce((a, b) => a + b, 0) / rtts.length), max: Math.max(...rtts) }
    : null;

  return (
    <Modal open onClose={onClose} width="max-w-lg" title="AX.25 ping" footer={<>
      <Button variant="outline" size="sm" onClick={onClose}>Close</Button>
      <Button size="sm" onClick={run} disabled={running || !call.trim()}>
        <Icon name={running ? "pause" : "signal"} size={14} />{running ? "Pinging…" : "Send TEST frames"}
      </Button>
    </>}>
      <div className="space-y-4">
        <div className="flex items-start gap-2 rounded-md bg-muted/40 px-2.5 py-2 text-[11px] text-muted-foreground">
          <Icon name="info" size={13} className="mt-px shrink-0" />
          <span>Sends connectionless AX.25 <span className="font-mono">TEST</span> frames — the far station's link layer echoes each one back. No connected session needed (the remote station must support TEST).</span>
        </div>

        <div className="grid grid-cols-[1fr_auto_auto] gap-2">
          <Field label="Station"><Input value={call} onChange={(e) => setCall(e.target.value.toUpperCase())} placeholder="GB7CIP" className="font-mono" autoFocus /></Field>
          <Field label="Via port" className="w-32"><Select value={via} onChange={(e) => setVia(e.target.value)}>{PORTS_LIST.map((p) => <option key={p} value={p}>{p}</option>)}</Select></Field>
          <Field label="Count" className="w-20"><Input type="number" value={count} onChange={(e) => setCount(Math.max(1, Math.min(20, +e.target.value)))} className="font-mono" /></Field>
        </div>

        {results.length > 0 && (
          <div className="rounded-md border border-border bg-background/60 p-3 font-mono text-xs">
            {results.map((r) => (
              <div key={r.seq} className="flex items-center gap-3 py-0.5">
                <span className="w-20 text-muted-foreground">TEST seq={r.seq}</span>
                {r.rtt != null
                  ? <span className="text-success">reply · {r.rtt} ms</span>
                  : <span className="text-danger">no response (timeout)</span>}
              </div>
            ))}
            {!running && stat && (
              <div className="mt-2 border-t border-border pt-2 text-muted-foreground">
                <div>{results.length} sent · {got.length} received · <span className={loss > 0 ? "text-warning" : "text-success"}>{loss}% loss</span></div>
                <div>rtt min/avg/max = <span className="text-foreground/80">{stat.min}/{stat.avg}/{stat.max} ms</span></div>
              </div>
            )}
            {!running && !stat && <div className="mt-2 border-t border-border pt-2 text-danger">No replies — station unreachable, or TEST unsupported.</div>}
          </div>
        )}
      </div>
    </Modal>
  );
}

// Drop-in trigger: a button that opens the ping modal, optionally pre-targeted.
// Signature is a contract depended on by Routes + Ports.
export function PingButton({ station, portId, label, size, variant }: {
  station: string;
  portId?: string;
  label?: string;
  size?: ButtonSize;
  variant?: ButtonVariant;
}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button size={size ?? "xs"} variant={variant ?? "outline"} onClick={() => setOpen(true)} title={station ? `AX.25 ping ${station}` : "AX.25 ping a station"}>
        <Icon name="signal" size={14} />{label ?? "Ping"}
      </Button>
      {open && <Ax25Ping station={station} portId={portId} onClose={() => setOpen(false)} />}
    </>
  );
}
