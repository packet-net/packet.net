// ============================================================
// Guided deviation tuning workspace (/tools/tuner) — the operator surface for
// Packet.Node's SDM-coordinated deviation-tuning session. Opened from a port's
// "Tune link". This port is one end of a two-ended procedure: the operator turns
// the TX-DEV pot here (role "tuned") while a peer radio measures decode rate +
// RX-audio level and returns advice, or this port meters a remote peer's pot
// (role "meter"). The session TRANSMITS and pauses the port's normal traffic, so
// it is admin-initiated and the port is restored when the session ends.
//
// The payoff is "watch the numbers as you turn the pot": start a session, watch
// the live trend table fill in per round, and hit "Next round" after each pot
// adjustment. Reads ?port=<id> from the URL.
// ============================================================
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Page } from "@/components/layout/shell";
import { Button, Card, Field, Input, Select, Icon } from "@/components/ui";
import { cn } from "@/lib/utils";
import { api, useQuery, subscribeTune } from "@/lib/api";
import type { TuningRole, TuningState, TuningEvent, TuningSessionInfo } from "@/lib/types";

function clamp(n: number, a: number, b: number): number { return Math.max(a, Math.min(b, n)); }

const STATE_LABEL: Record<TuningState, string> = {
  "armed": "armed — waiting for the peer",
  "peer-connected": "peer connected — measuring",
  "awaiting-adjustment": "adjust the pot, then Next round",
  "ended": "session ended",
  "error": "session error",
  "stopped": "session stopped",
};

const ADVICE_STYLE: Record<string, { chip: string; icon: "arrowUp" | "arrowDown" | "check" | "search"; label: string }> = {
  up: { chip: "bg-warning/15 text-warning", icon: "arrowUp", label: "turn UP" },
  down: { chip: "bg-warning/15 text-warning", icon: "arrowDown", label: "turn DOWN" },
  ok: { chip: "bg-success/15 text-success", icon: "check", label: "OK — leave it" },
  sweep: { chip: "bg-danger/15 text-danger", icon: "search", label: "SWEEP" },
};

export function LinkTuner() {
  const [searchParams] = useSearchParams();
  const { data: config } = useQuery(api.config, []);
  const ports = useMemo(() => config?.ports ?? [], [config]);
  const urlPort = searchParams.get("port");

  const [portId, setPortId] = useState<string>("");
  const [role, setRole] = useState<TuningRole>("tuned");
  const [peerSdmId, setPeerSdmId] = useState("");
  const [burstFrames, setBurstFrames] = useState(5);

  const [session, setSession] = useState<TuningSessionInfo | null>(null);
  const [state, setState] = useState<TuningState | null>(null);
  const [rounds, setRounds] = useState<TuningEvent[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const unsub = useRef<(() => void) | null>(null);

  // Default the port from ?port= once the config loads.
  useEffect(() => {
    if (portId) return;
    const first = ports[0]?.id ?? "";
    setPortId(urlPort && ports.some((p) => p.id === urlPort) ? urlPort : first);
  }, [ports, urlPort, portId]);

  // Tear the SSE down on unmount (leaves the server session running — the operator
  // ends it explicitly with Stop, which is when the node restores the port).
  useEffect(() => () => { unsub.current?.(); }, []);

  const attach = useCallback((port: string) => {
    unsub.current?.();
    unsub.current = subscribeTune(
      port,
      (e) => {
        setState(e.state);
        if (e.kind === "round") setRounds((rs) => [...rs, e]);
        if (e.kind === "error") setError(e.error ?? "the tuning link failed");
      },
      () => setState((s) => (s === "error" ? s : "ended")),
    );
  }, []);

  const start = useCallback(async () => {
    setBusy(true);
    setError(null);
    setRounds([]);
    setState(null);
    try {
      const info = await api.startTune(portId, { role, peerSdmId, burstFrames });
      setSession(info);
      setState(info.state);
      attach(portId);
    } catch (e) {
      const msg = String((e as Error)?.message ?? e);
      if (/already active/i.test(msg)) {
        // Re-attach to the session already running on this port (e.g. after a reload).
        setSession({ sessionId: "", portId, role, peerSdmId, state: "peer-connected", burstFrames, startedAt: new Date().toISOString() });
        setState("peer-connected");
        attach(portId);
      } else {
        setError(msg);
      }
    } finally {
      setBusy(false);
    }
  }, [portId, role, peerSdmId, burstFrames, attach]);

  const next = useCallback(async () => {
    if (!session) return;
    try { await api.tuneNext(session.portId); }
    catch (e) { setError(String((e as Error)?.message ?? e)); }
  }, [session]);

  const stop = useCallback(async () => {
    if (!session) return;
    setBusy(true);
    try { await api.tuneStop(session.portId); }
    catch (e) { setError(String((e as Error)?.message ?? e)); }
    finally {
      unsub.current?.();
      unsub.current = null;
      setSession(null);
      setState((s) => (s === "error" ? s : "stopped"));
      setBusy(false);
    }
  }, [session]);

  const active = session !== null;
  const awaiting = active && role === "tuned" && state === "awaiting-adjustment";
  const canStart = portId.length > 0 && peerSdmId.length === 8 && !busy;

  return (
    <Page>
      <div className="mx-auto w-full max-w-3xl space-y-5">
        {/* header */}
        <div className="flex items-center gap-3">
          <span className="grid h-8 w-8 place-items-center rounded-md bg-primary/15 text-primary"><Icon name="gauge" size={17} /></span>
          <div>
            <h1 className="text-xl font-semibold leading-tight tracking-tight">Deviation tuning</h1>
            <p className="text-xs text-muted-foreground">
              SDM-coordinated · watch the numbers as you turn the TX-DEV pot
            </p>
          </div>
        </div>

        {/* transmitting / paused banner */}
        {active && (
          <div className="flex items-start gap-2.5 rounded-md border border-warning/40 bg-warning/10 px-3.5 py-2.5 text-sm text-warning">
            <Icon name="alert" size={16} className="mt-0.5 shrink-0" />
            <div>
              <span className="font-semibold">Port <span className="font-mono">{session!.portId}</span> is paused for tuning and transmitting bursts.</span>{" "}
              Normal traffic is suspended for the session; it is restored when you Stop.
            </div>
          </div>
        )}

        {error && (
          <div className="flex items-start gap-2.5 rounded-md border border-danger/40 bg-danger/10 px-3.5 py-2.5 text-sm text-danger">
            <Icon name="alert" size={16} className="mt-0.5 shrink-0" />
            <div>{error}</div>
          </div>
        )}

        {/* setup / controls */}
        <Card className="p-4">
          {!active ? (
            <div className="space-y-4">
              <div className="flex flex-wrap items-end gap-3">
                <Field label="Port" className="w-40">
                  <Select value={portId} onChange={(e) => setPortId(e.target.value)}>
                    {ports.map((p) => <option key={p.id} value={p.id}>{p.id}</option>)}
                  </Select>
                </Field>
                <Field label="This end is" className="w-40">
                  <Select value={role} onChange={(e) => setRole(e.target.value as TuningRole)}>
                    <option value="tuned">tuned (I turn the pot)</option>
                    <option value="meter">meter (I measure)</option>
                  </Select>
                </Field>
                <Field label="Peer SDM id" className="w-40">
                  <Input
                    value={peerSdmId}
                    onChange={(e) => setPeerSdmId(e.target.value.slice(0, 8))}
                    placeholder="8 chars"
                    className="font-mono"
                    maxLength={8}
                  />
                </Field>
                <Field label="Burst frames" className="w-28">
                  <Input
                    type="number" value={burstFrames}
                    onChange={(e) => setBurstFrames(clamp(+e.target.value, 1, 50))}
                    className="font-mono"
                  />
                </Field>
                <Button onClick={start} disabled={!canStart}>
                  <Icon name="play" size={14} />{busy ? "Starting…" : "Start tuning"}
                </Button>
              </div>
              <p className="text-xs text-muted-foreground">
                Needs a NinoTNC + a Tait radio with SDM enabled on the port. Starting <b>keys the radio</b> and
                pauses the port's normal traffic for the session.
              </p>
            </div>
          ) : (
            <div className="flex flex-wrap items-center gap-3">
              <span className={cn("flex items-center gap-1.5 rounded px-2 py-1 text-xs font-medium",
                state === "error" ? "bg-danger/15 text-danger" : state === "awaiting-adjustment" ? "bg-warning/15 text-warning" : "bg-primary/10 text-primary")}>
                <span className={cn("h-1.5 w-1.5 rounded-full", state === "error" ? "bg-danger" : "bg-primary live-dot")} />
                {state ? STATE_LABEL[state] : "starting…"}
              </span>
              <span className="text-xs text-muted-foreground">
                role <span className="font-mono">{session!.role}</span> · peer <span className="font-mono">{session!.peerSdmId}</span>
              </span>
              <div className="ml-auto flex items-center gap-2">
                <Button onClick={next} disabled={!awaiting} title={role === "meter" ? "Rounds are driven by the remote tuned end" : "Run the next measurement round"}>
                  <Icon name="arrowUp" size={14} />Next round — I've adjusted the pot
                </Button>
                <Button variant="outline" onClick={stop} disabled={busy}>
                  <Icon name="power" size={14} />Stop
                </Button>
              </div>
            </div>
          )}
        </Card>

        {/* live trend table */}
        <Card className="p-4">
          <div className="mb-3 flex items-center justify-between">
            <span className="text-sm font-semibold">Trend</span>
            {rounds.length > 0 && <span className="text-xs text-muted-foreground">{rounds.length} round{rounds.length === 1 ? "" : "s"}</span>}
          </div>
          {rounds.length === 0 ? (
            <div className="py-8 text-center text-xs text-muted-foreground">
              {active ? "Waiting for the first measurement round…" : "Start a session to watch decode rate, level and advice per round."}
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-border text-left text-xs text-muted-foreground">
                    <th className="pb-2 pr-3 font-medium">Round</th>
                    <th className="pb-2 pr-3 font-medium">Decoded</th>
                    <th className="pb-2 pr-3 font-medium">Level</th>
                    <th className="pb-2 pr-3 font-medium">Advice</th>
                    <th className="pb-2 font-medium">Note</th>
                  </tr>
                </thead>
                <tbody>
                  {rounds.map((r, i) => {
                    const total = r.total ?? 0;
                    const decoded = r.decoded ?? 0;
                    const good = total > 0 && decoded / total >= 0.9;
                    const adv = r.advice ? ADVICE_STYLE[r.advice] : undefined;
                    return (
                      <tr key={i} className="border-b border-border/50 last:border-0">
                        <td className="py-1.5 pr-3 font-mono text-muted-foreground">{r.burstIndex}</td>
                        <td className={cn("py-1.5 pr-3 font-mono", good ? "text-success" : decoded === 0 ? "text-danger" : "text-warning")}>
                          {decoded}/{total}
                        </td>
                        <td className="py-1.5 pr-3 font-mono text-muted-foreground">{r.levelDb != null ? `${r.levelDb.toFixed(1)} dB` : "—"}</td>
                        <td className="py-1.5 pr-3">
                          {adv ? (
                            <span className={cn("inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-xs font-semibold", adv.chip)}>
                              <Icon name={adv.icon} size={12} />{adv.label}
                            </span>
                          ) : <span className="text-xs text-muted-foreground">—</span>}
                        </td>
                        <td className="py-1.5 text-xs text-muted-foreground">{r.note}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </Card>
      </div>
    </Page>
  );
}
