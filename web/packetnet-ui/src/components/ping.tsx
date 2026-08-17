// ============================================================
// Shared AX.25 ping — connectionless TEST-frame round-trip check
// (the analogue of the Linux `axping` tool). No session required;
// the far station's link layer echoes the TEST frame straight back.
//
// PingButton is the drop-in trigger imported by Routes (neighbours +
// destinations) and the Ports header. It opens a self-contained
// Ax25Ping modal, optionally pre-targeted.
//
// Both mock and live mode go through api.pingTarget → a single PingResult.
// Live mode POSTs /ping; mock mode synthesises a believable result. A node
// that doesn't implement TEST never answers → every reply times out and
// lossPct is 100 — that renders as a clear "no response" state, not an error.
// ============================================================
import { useEffect, useState } from "react";
import { Button, Modal, Field, Input, Select, Icon, type ButtonVariant, type ButtonSize } from "@/components/ui";
import { api, useQuery, PingUnavailable } from "@/lib/api";
import type { PingResult } from "@/lib/types";

function Ax25Ping({ station, portId, onClose }: { station?: string; portId?: string; onClose: () => void }) {
  // The via-port options are THIS node's ports. They used to come from the mock's
  // PORTS_LIST, so on any real node every ping named a port the node does not have and
  // the server answered 404 "Port 'vhf-1' is not running" (#691 C021).
  const { data: config } = useQuery(api.config, []);
  const portIds = config?.ports.map((p) => p.id) ?? [];
  const [call, setCall] = useState(station ?? "");
  const [via, setVia] = useState(portId ?? "");
  const [count, setCount] = useState(5);
  const [result, setResult] = useState<PingResult | null>(null);
  const [running, setRunning] = useState(false);
  // A node that hasn't implemented TEST ping returns 501 → PingUnavailable; surface that
  // message gracefully. A genuine transport error (404 port, etc.) surfaces here too.
  const [error, setError] = useState<string | null>(null);

  // Settle the via-port once /config resolves: keep the caller's port if the node still
  // has it (Routes hands one in), else fall back to the node's first port. Before that
  // there is no port worth naming, so the select is empty and Send is disabled.
  useEffect(() => {
    const ids = config?.ports.map((p) => p.id) ?? [];
    if (ids.length === 0) return;
    setVia((v) => (ids.includes(v) ? v : ids[0]));
  }, [config]);

  const run = async () => {
    if (!call.trim()) return;
    setResult(null);
    setError(null);
    setRunning(true);
    try {
      setResult(await api.pingTarget(call.trim(), via, count));
    } catch (e) {
      if (e instanceof PingUnavailable) setError(e.message);
      else setError(String((e as Error)?.message ?? e));
    } finally {
      setRunning(false);
    }
  };

  // A peer that doesn't implement TEST simply never answers — every reply timed out.
  // This is a normal result to display ("no response"), not an error.
  const noResponse = result != null && result.lossPct >= 100;

  return (
    <Modal open onClose={onClose} width="max-w-lg" title="AX.25 ping" footer={<>
      <Button variant="outline" size="sm" onClick={onClose}>Close</Button>
      <Button size="sm" onClick={run} disabled={running || !call.trim() || !via}>
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
          <Field label="Via port" className="w-32"><Select value={via} onChange={(e) => setVia(e.target.value)}>{portIds.map((p) => <option key={p} value={p}>{p}</option>)}</Select></Field>
          <Field label="Count" className="w-20"><Input type="number" value={count} onChange={(e) => setCount(Math.max(1, Math.min(20, +e.target.value)))} className="font-mono" /></Field>
        </div>

        {error && (
          <div className="flex items-start gap-2 rounded-md border border-warning/40 bg-warning/10 px-2.5 py-2 text-[11px] text-warning">
            <Icon name="alert" size={13} className="mt-px shrink-0" />
            <span>{error}</span>
          </div>
        )}

        {result && (
          <div className="rounded-md border border-border bg-background/60 p-3 font-mono text-xs">
            {result.replies.map((r) => (
              <div key={r.seq} className="flex items-center gap-3 py-0.5">
                <span className="w-20 text-muted-foreground">TEST seq={r.seq}</span>
                {!r.timeout && r.rttMs != null
                  ? <span className="text-success">reply · {r.rttMs} ms</span>
                  : <span className="text-danger">no response (timeout)</span>}
              </div>
            ))}
            <div className="mt-2 border-t border-border pt-2">
              {noResponse ? (
                <div className="text-danger">
                  No response — station unreachable, or TEST unsupported ({result.lossPct}% loss).
                </div>
              ) : (
                <div className="text-muted-foreground">
                  <div>
                    {result.replies.length} sent · {result.replies.filter((r) => !r.timeout).length} received ·{" "}
                    <span className={result.lossPct > 0 ? "text-warning" : "text-success"}>{result.lossPct}% loss</span>
                  </div>
                  <div>rtt min/avg/max = <span className="text-foreground/80">{result.minMs}/{result.avgMs}/{result.maxMs} ms</span></div>
                </div>
              )}
            </div>
          </div>
        )}
      </div>
    </Modal>
  );
}

// Drop-in trigger: a button that opens the ping modal, optionally pre-targeted.
// Signature is a contract depended on by Routes + Ports.
export function PingButton({ station, portId, label, size, variant }: {
  /** Pre-targeted station, when the caller knows one (Routes does). Blank elsewhere -
   *  a header ping has no target until the operator types one. */
  station?: string;
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
