// ============================================================
// pdn — Sessions (§5): active circuit table + session console drawer +
// sysop interactive connect-out. Ported from the handoff screens-net.jsx.
// ============================================================
import { useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Page, PageHeader } from "@/components/layout/shell";
import {
  Button, Badge, Card, StatusDot, Input, Label, Field, Select,
  Th, Td, Sheet, Modal, EmptyState, Icon, type BadgeVariant,
} from "@/components/ui";
import { cn } from "@/lib/utils";
import { api, useQuery } from "@/lib/api";
import { PORTS_LIST, fmtUptime, fmtBytes } from "@/lib/mock";
import type { SessionInfo, SessionRole } from "@/lib/types";

const ROLE_BADGE: Record<SessionRole, BadgeVariant> = {
  console: "default",
  interlink: "secondary",
  bridge: "muted",
};
function stateBadge(st: string): BadgeVariant {
  return st === "Connected" ? "success" : st === "TimerRecovery" ? "warning" : "muted";
}
function stateDot(st: string): "up" | "faulted" | "down" {
  return st === "Connected" ? "up" : st === "TimerRecovery" ? "faulted" : "down";
}

export function Sessions() {
  const { data, loading, error } = useQuery(api.sessions);
  const [sessions, setSessions] = useState<SessionInfo[]>([]);
  const [openSession, setOpenSession] = useState<SessionInfo | null>(null);
  const [connectOpen, setConnectOpen] = useState(false);
  const [params, setParams] = useSearchParams();

  // Sync the local working copy when the query resolves (so disconnect/connect
  // mutations stay client-side over the fetched snapshot — minimal v1).
  useEffect(() => {
    if (data) setSessions(data);
  }, [data]);

  // Routes → Sessions hand-off: ?connect=<call>&port=<portId> auto-opens
  // the connect-out modal prefilled, then clears the params.
  const connectCall = params.get("connect");
  const connectPort = params.get("port");
  useEffect(() => {
    if (connectCall) {
      setConnectOpen(true);
      const next = new URLSearchParams(params);
      next.delete("connect");
      next.delete("port");
      setParams(next, { replace: true });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectCall]);

  const drop = (id: string) => setSessions((s) => s.filter((x) => x.id !== id));

  return (
    <Page>
      <PageHeader
        title="Sessions"
        subtitle="Active AX.25 L2 + NET/ROM L4 circuits"
        actions={
          <Button size="sm" onClick={() => setConnectOpen(true)}>
            <Icon name="link" size={14} /> Connect
          </Button>
        }
      />

      <Card className="overflow-hidden p-0">
        <div className="overflow-x-auto">
          <table className="w-full border-collapse">
            <thead>
              <tr className="border-b border-border">
                <Th>Peer</Th>
                <Th>Port</Th>
                <Th>Role</Th>
                <Th>State</Th>
                <Th className="text-right">V(S)/V(R)</Th>
                <Th className="text-right">Win</Th>
                <Th className="hidden text-right md:table-cell">Uptime</Th>
                <Th className="hidden text-right md:table-cell">Bytes ↓/↑</Th>
                <Th className="hidden text-right lg:table-cell">Last</Th>
                <Th className="w-px" />
              </tr>
            </thead>
            <tbody>
              {error && (
                <tr>
                  <td colSpan={10}>
                    <div className="py-10">
                      <EmptyState icon="alert" title="Couldn't load sessions" body={error} />
                    </div>
                  </td>
                </tr>
              )}
              {!error && loading && sessions.length === 0 && (
                <tr>
                  <td colSpan={10}>
                    <div className="py-10 text-center text-sm text-muted-foreground">Loading sessions…</div>
                  </td>
                </tr>
              )}
              {!error && !loading && sessions.length === 0 && (
                <tr>
                  <td colSpan={10}>
                    <div className="py-10">
                      <EmptyState icon="sessions" title="No active sessions" body="Connect out to a station or alias to start one." />
                    </div>
                  </td>
                </tr>
              )}
              {sessions.map((s) => (
                <tr key={s.id} className="border-b border-border/60 hover:bg-accent/40">
                  <Td>
                    <button onClick={() => setOpenSession(s)} className="font-mono font-semibold hover:text-primary">{s.peer}</button>
                  </Td>
                  <Td className="font-mono text-xs text-muted-foreground">{s.portId}</Td>
                  <Td><Badge variant={ROLE_BADGE[s.role]}>{s.role}</Badge></Td>
                  <Td>
                    <span className="flex items-center gap-1.5">
                      <StatusDot state={stateDot(s.state)} />
                      <Badge variant={stateBadge(s.state)}>{s.state}</Badge>
                    </span>
                  </Td>
                  <Td className="tnum text-right font-mono text-xs">{s.vs}/{s.vr}</Td>
                  <Td className="tnum text-right font-mono text-xs text-muted-foreground">{s.window}</Td>
                  <Td className="hidden text-right text-xs text-muted-foreground md:table-cell">{fmtUptime(s.uptimeSeconds)}</Td>
                  <Td className="hidden text-right font-mono text-xs text-muted-foreground md:table-cell">
                    <span className="inline-flex items-center justify-end gap-1">
                      <Icon name="arrowDown" size={11} className="text-muted-foreground/60" />{fmtBytes(s.bytesIn)}
                      <Icon name="arrowUp" size={11} className="ml-1 text-muted-foreground/60" />{fmtBytes(s.bytesOut)}
                    </span>
                  </Td>
                  <Td className="hidden text-right font-mono text-xs text-muted-foreground lg:table-cell">{s.lastActivity}</Td>
                  <Td>
                    <div className="flex items-center justify-end gap-1">
                      <Button variant="ghost" size="iconSm" title="Open" onClick={() => setOpenSession(s)}>
                        <Icon name="external" size={14} />
                      </Button>
                      <Button variant="ghost" size="iconSm" title="Disconnect" onClick={() => drop(s.id)}>
                        <Icon name="power" size={14} className="text-danger" />
                      </Button>
                    </div>
                  </Td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      <SessionConsole
        session={openSession}
        onClose={() => setOpenSession(null)}
        onDrop={(id) => { drop(id); setOpenSession(null); }}
      />
      <ConnectOut
        open={connectOpen}
        initialCall={connectCall ?? ""}
        // TODO: derive default via-port from live config; the ConnectOut <Select> options already do.
        initialPort={connectPort ?? PORTS_LIST[0]}
        onClose={() => setConnectOpen(false)}
        onConnect={(call, port) => {
          // Sysop interactive connect — (mock) create the session and drop
          // straight into its terminal.
          const sess: SessionInfo = {
            id: "s" + Date.now(), portId: port, peer: call, role: "console",
            state: "Connected", vs: 0, vr: 0, window: 4,
            uptimeSeconds: 0, bytesIn: 0, bytesOut: 0, lastActivity: "0:00:00",
          };
          setSessions((s) => [...s, sess]);
          setConnectOpen(false);
          setOpenSession(sess);
        }}
      />
    </Page>
  );
}

// ---- Session detail drawer (with a minimal send-into-session affordance) ----
interface StreamLine { dir: "in" | "out"; text: string }

function SessionConsole({ session, onClose, onDrop }: {
  session: SessionInfo | null;
  onClose: () => void;
  onDrop: (id: string) => void;
}) {
  const [lines, setLines] = useState<StreamLine[]>([]);
  const [draft, setDraft] = useState("");
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (session) {
      setLines([
        { dir: "in", text: `${session.peer} connected to GB7RDG` },
        { dir: "in", text: "Welcome to GB7RDG — Reading & District packet gateway" },
        { dir: "in", text: "Type ? for help" },
      ]);
      setDraft("");
    }
  }, [session]);

  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [lines]);

  const send = () => {
    if (!draft.trim()) return;
    const echo = draft;
    setLines((l) => [...l, { dir: "out", text: echo }]);
    setDraft("");
    setTimeout(() => setLines((l) => [...l, { dir: "in", text: `ack: ${echo.slice(0, 40)}` }]), 600);
  };

  return (
    <Sheet
      open={!!session}
      onClose={onClose}
      title={session ? `Session — ${session.peer}` : ""}
      subtitle={session ? `${session.portId} · ${session.role} · ${session.state}` : undefined}
      width="max-w-2xl"
      footer={session && (
        <>
          <Button variant="destructive" size="sm" onClick={() => onDrop(session.id)}>
            <Icon name="x" size={14} /> Disconnect
          </Button>
          <Button variant="outline" size="sm" onClick={onClose}>Close</Button>
        </>
      )}
    >
      {session && (
        <div className="space-y-4">
          <div className="grid grid-cols-3 gap-2">
            <Stat k="V(S)/V(R)" v={`${session.vs}/${session.vr}`} />
            <Stat k="Window" v={String(session.window)} />
            <Stat k="Uptime" v={fmtUptime(session.uptimeSeconds)} />
            <Stat k="Bytes in" v={fmtBytes(session.bytesIn)} />
            <Stat k="Bytes out" v={fmtBytes(session.bytesOut)} />
            <Stat k="Last activity" v={session.lastActivity} />
          </div>

          <div>
            <Label>Session stream</Label>
            <div ref={scrollRef} className="mt-1.5 h-64 overflow-y-auto rounded-md border border-border bg-background/60 p-3 font-mono text-xs">
              {lines.map((l, i) => (
                <div key={i} className={cn("flex gap-2 py-0.5", l.dir === "out" && "text-primary")}>
                  <span className="shrink-0 text-muted-foreground/60">{l.dir === "out" ? "»" : "«"}</span>
                  <span className="whitespace-pre-wrap break-all">{l.text}</span>
                </div>
              ))}
            </div>
            <div className="mt-2 flex items-center gap-2">
              <Input
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => { if (e.key === "Enter") send(); }}
                placeholder="send a line into the session…"
                className="font-mono text-xs"
              />
              <Button size="sm" onClick={send}><Icon name="send" size={14} /> Send</Button>
            </div>
            <p className="mt-1.5 text-[11px] text-muted-foreground">Minimal v1 affordance — pushes one line of text into the connected-mode session.</p>
          </div>
        </div>
      )}
    </Sheet>
  );
}

function Stat({ k, v }: { k: string; v: string }) {
  return (
    <div className="rounded-md border border-border bg-muted/30 p-2.5">
      <p className="text-[11px] text-muted-foreground">{k}</p>
      <p className="mt-0.5 font-mono text-sm font-semibold">{v}</p>
    </div>
  );
}

// ---- Connect-out modal (alias autocomplete from the routes list) ----
function ConnectOut({ open, onClose, onConnect, initialCall, initialPort }: {
  open: boolean;
  onClose: () => void;
  onConnect: (call: string, port: string) => void;
  initialCall: string;
  initialPort: string;
}) {
  const { data: routes } = useQuery(api.routes);
  const { data: config } = useQuery(api.config);
  // Via-port options come from the live config; fall back to the mock list.
  const portIds = config?.ports.map((p) => p.id) ?? PORTS_LIST;
  const [target, setTarget] = useState("");
  const [port, setPort] = useState(initialPort);

  useEffect(() => {
    if (open) {
      setTarget(initialCall);
      setPort(initialPort);
    }
  }, [open, initialCall, initialPort]);

  // Suggest matching alias/callsign from both destinations and neighbours.
  const candidates = useMemo(() => {
    const m = new Map<string, string>();
    if (routes) {
      for (const d of routes.destinations) if (!m.has(d.destination)) m.set(d.destination, d.alias);
      for (const n of routes.neighbours) if (!m.has(n.neighbour)) m.set(n.neighbour, n.alias);
    }
    return [...m].map(([call, alias]) => ({ call, alias }));
  }, [routes]);

  const q = target.trim().toUpperCase();
  const suggestions = q
    ? candidates.filter((c) => c.call.includes(q) || c.alias.toUpperCase().includes(q)).slice(0, 6)
    : [];
  const exactMatch = suggestions.length === 1 && suggestions[0].call === q;

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Connect out"
      footer={
        <>
          <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
          <Button size="sm" disabled={!target.trim()} onClick={() => onConnect(target.trim().toUpperCase(), port)}>
            <Icon name="link" size={14} /> Connect
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Field
          label="Callsign or NET/ROM alias"
          hint="You're opening an interactive session from this node to the station — the same as a sysop Connect."
        >
          <div className="relative">
            <Input
              value={target}
              onChange={(e) => setTarget(e.target.value)}
              placeholder="e.g. GB7CIP or CIPGW"
              className="font-mono"
              autoFocus
            />
            {suggestions.length > 0 && !exactMatch && (
              <div className="absolute z-10 mt-1 w-full overflow-hidden rounded-md border border-border bg-popover shadow-lg">
                {suggestions.map((s) => (
                  <button
                    key={s.call}
                    onClick={() => setTarget(s.call)}
                    className="flex w-full items-center justify-between px-3 py-2 text-left text-sm hover:bg-accent"
                  >
                    <span className="font-mono font-semibold">{s.call}</span>
                    <span className="font-mono text-xs text-muted-foreground">{s.alias}</span>
                  </button>
                ))}
              </div>
            )}
          </div>
        </Field>
        <Field label="Via port">
          <Select value={port} onChange={(e) => setPort(e.target.value)}>
            {portIds.map((p) => <option key={p} value={p}>{p}</option>)}
          </Select>
        </Field>
      </div>
    </Modal>
  );
}
