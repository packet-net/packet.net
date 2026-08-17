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
import { api, useQuery, subscribeSessionOutput } from "@/lib/api";
import { useAuth } from "@/app/auth";
import { fmtUptime, fmtBytes } from "@/lib/catalogue";
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
  const { has } = useAuth();
  const canOperate = has("operate"); // connect/disconnect is operate-scoped
  const { data, loading, error, reload } = useQuery(api.sessions);
  const [sessions, setSessions] = useState<SessionInfo[]>([]);
  const [openSession, setOpenSession] = useState<SessionInfo | null>(null);
  const [connectOpen, setConnectOpen] = useState(false);
  // Bumped on every open; it keys ConnectOut, so each open remounts the modal against the
  // hand-off (if any) it was opened with.
  const [connectSeq, setConnectSeq] = useState(0);
  const [params, setParams] = useSearchParams();
  // A banner-style notice for a failed action (mirrors the Ports/Config screens — there is
  // no toast primitive). Cleared on the next successful action.
  const [notice, setNotice] = useState<string | null>(null);

  // Sync the local working copy when the query resolves. Connect/disconnect call the live
  // API and then reload(), which refetches /sessions; the local copy keeps the table
  // responsive between the action and the refetch.
  useEffect(() => {
    if (data) setSessions(data);
  }, [data]);

  // Routes → Sessions hand-off: ?connect=<call>&port=<portId> auto-opens the connect-out
  // modal prefilled, then clears the params. The hand-off is CAPTURED INTO STATE first:
  // clearing the params re-renders with connect=null, and the modal used to be re-seeded
  // from those nulls on the very next render, so the operator saw an empty form (#691 C042).
  const [handoff, setHandoff] = useState<{ call: string; port: string | null } | null>(null);
  const connectCall = params.get("connect");
  const connectPort = params.get("port");

  // The one way the modal opens: with the hand-off it is opening for (null = a bare
  // header Connect), and a sequence bump so it remounts against it.
  const openConnect = (h: { call: string; port: string | null } | null) => {
    setHandoff(h);
    setConnectSeq((n) => n + 1);
    setConnectOpen(true);
  };

  useEffect(() => {
    if (!connectCall) return;
    openConnect({ call: connectCall, port: connectPort });
    const next = new URLSearchParams(params);
    next.delete("connect");
    next.delete("port");
    setParams(next, { replace: true });
    // `params`/`setParams` are recreated per render; keying the effect on the hand-off
    // itself is what makes it run once.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectCall, connectPort]);

  // Disconnect: call the live API, drop the row optimistically, then reload to reflect the
  // server's truth. A failure surfaces in the notice banner (the row is left in place).
  const drop = async (id: string) => {
    try {
      await api.disconnectSession(id);
      setNotice(null);
      setSessions((s) => s.filter((x) => x.id !== id));
      reload();
    } catch (e) {
      setNotice(String((e as Error)?.message ?? e) || "Could not disconnect the session.");
    }
  };

  // Connect out: open the session via the API, surface it, drop into its drawer, reload.
  const connect = async (target: string, port: string) => {
    try {
      const sess = await api.connectSession(target, port);
      setNotice(null);
      setSessions((s) => [...s.filter((x) => x.id !== sess.id), sess]);
      setConnectOpen(false);
      setOpenSession(sess);
      reload();
    } catch (e) {
      setNotice(String((e as Error)?.message ?? e) || `Could not connect to ${target}.`);
    }
  };

  return (
    <Page>
      <PageHeader
        title="Sessions"
        subtitle="Active AX.25 L2 + NET/ROM L4 circuits"
        actions={
          <Button size="sm" disabled={!canOperate} title={canOperate ? undefined : "Connecting out requires the operate scope"} onClick={() => openConnect(null)}>
            <Icon name="link" size={14} /> Connect
          </Button>
        }
      />

      {notice && (
        <div className="mb-4 flex items-start gap-2 rounded-md border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">
          <Icon name="alert" size={15} className="mt-px shrink-0" />
          <span className="flex-1">{notice}</span>
          <button onClick={() => setNotice(null)} className="shrink-0 text-danger/70 hover:text-danger" title="Dismiss">
            <Icon name="x" size={14} />
          </button>
        </div>
      )}

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
        onDrop={async (id) => { await drop(id); setOpenSession(null); }}
        onNotice={setNotice}
      />
      {/* keyed on the open sequence: every open REMOUNTS the modal against the hand-off it
          was opened with, so a later prop change (the query params being cleared) cannot
          reset the form the operator is looking at (#691 C042, same pattern as #690 C036) */}
      <ConnectOut
        key={connectSeq}
        open={connectOpen}
        initialCall={handoff?.call ?? ""}
        initialPort={handoff?.port ?? null}
        onClose={() => setConnectOpen(false)}
        // Sysop interactive connect-out — opens the session on the node and drops into
        // its drawer (no console-bridge / received-data stream in v1 — the monitor shows
        // the frames; the drawer's send is a one-line affordance).
        onConnect={connect}
      />
    </Page>
  );
}

// ---- Session detail drawer (live interactive console) ----
// The drawer is a working terminal pane: when it opens for a session it subscribes to the
// session's output stream (SSE `output` events, replayed-backlog-then-live) and accumulates
// the decoded text chunks into a single buffer rendered monospace + scroll-to-bottom.
// Typed lines are sent via api.sendSessionLine and echoed optimistically into the buffer so
// the operator sees what they sent. Closing the drawer only unsubscribes — it does NOT
// disconnect the session (that's the separate Disconnect action).
function SessionConsole({ session, onClose, onDrop, onNotice }: {
  session: SessionInfo | null;
  onClose: () => void;
  onDrop: (id: string) => void;
  onNotice: (msg: string | null) => void;
}) {
  const [buffer, setBuffer] = useState("");
  const [draft, setDraft] = useState("");
  const scrollRef = useRef<HTMLDivElement>(null);

  // Subscribe to the session's output stream while the drawer is open; reset the buffer for
  // each new session and tear the subscription down on close/unmount (the cleanup runs when
  // session changes or the component unmounts).
  useEffect(() => {
    if (!session) return;
    setBuffer("");
    setDraft("");
    // Normalise line endings for display: packet stations terminate lines with a
    // bare CR (BPQ does), and some send CRLF — but the pre-wrap pane only breaks on
    // LF, so without this every CR-terminated line collapses onto one row. Fold both
    // CRLF and lone CR to LF; the wire stream itself stays untouched.
    //
    // The node replays the session's whole backlog on EVERY subscription (including the
    // stream helper's silent reconnect after a token rotation or a node restart), as a
    // `backlog` event. onReset fires just before it is written: empty the buffer so the
    // replay REPLACES the history rather than appending a second copy (C045, #689).
    const unsubscribe = subscribeSessionOutput(
      session.id,
      (chunk) => setBuffer((b) => b + chunk.replace(/\r\n/g, "\n").replace(/\r/g, "\n")),
      { onReset: () => setBuffer("") },
    );
    return unsubscribe;
  }, [session]);

  // Keep the pane pinned to the bottom as output (and echoes) arrive.
  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [buffer]);

  // Send one line into the session via the API (CR-terminated server-side). Echo it
  // optimistically into the buffer (prefixed) so the operator sees what they sent; a
  // failure surfaces in the screen's notice banner.
  const send = async () => {
    if (!session || !draft.trim()) return;
    const echo = draft;
    setDraft("");
    setBuffer((b) => `${b}» ${echo}\n`);
    try {
      await api.sendSessionLine(session.id, echo);
      onNotice(null);
    } catch (e) {
      onNotice(String((e as Error)?.message ?? e) || `Could not send to ${session.peer}.`);
    }
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
            <Label>Console</Label>
            <div
              ref={scrollRef}
              className="mt-1.5 h-72 overflow-y-auto whitespace-pre-wrap break-all rounded-md border border-border bg-background/60 p-3 font-mono text-xs leading-relaxed"
            >
              {buffer || <span className="text-muted-foreground/60">Waiting for output…</span>}
            </div>
            <div className="mt-2 flex items-center gap-2">
              <Input
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => { if (e.key === "Enter") send(); }}
                placeholder="type a command and press Enter…"
                className="font-mono text-xs"
                autoFocus
              />
              <Button size="sm" onClick={send}><Icon name="send" size={14} /> Send</Button>
            </div>
            <p className="mt-1.5 text-[11px] text-muted-foreground">Lines you send go onto the link (CR-terminated by the node). The pane shows the remote's live output, with your sent lines echoed (»).</p>
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
// Mounted fresh per open (the parent keys it on the open sequence), so the initial props
// ARE the form's initial state - there is no re-seeding effect to fight with.
function ConnectOut({ open, onClose, onConnect, initialCall, initialPort }: {
  open: boolean;
  onClose: () => void;
  onConnect: (call: string, port: string) => void;
  initialCall: string;
  /** The port the Routes hand-off named, or null for a bare Connect. */
  initialPort: string | null;
}) {
  const { data: routes } = useQuery(api.routes);
  const { data: config, error: configError } = useQuery(api.config);
  // Via-port options are THIS node's ports, with no fixture fallback. The default used to
  // be the mock's first port ("vhf-1") while the select showed the node's real first port,
  // so the form displayed one port and POSTed another - a 404 from /sessions (#691 C021).
  const portIds = config?.ports.map((p) => p.id) ?? [];
  const [target, setTarget] = useState(initialCall);
  const [port, setPort] = useState(initialPort ?? "");

  // Settle the via-port when /config resolves: keep the hand-off's port if this node has
  // it, else fall back to the node's first port. Until then `port` is "" and Connect is
  // disabled - there is no port worth POSTing yet.
  useEffect(() => {
    const ids = config?.ports.map((p) => p.id) ?? [];
    if (ids.length === 0) return;
    setPort((p) => (ids.includes(p) ? p : ids[0]));
  }, [config]);

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
          <Button
            size="sm"
            disabled={!target.trim() || !port}
            title={port ? undefined : "Waiting for the node's port list"}
            onClick={() => onConnect(target.trim().toUpperCase(), port)}
          >
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
        <Field
          label="Via port"
          hint={configError
            ? `Couldn't load this node's ports: ${configError}`
            : portIds.length === 0 ? "Loading this node's ports…" : undefined}
        >
          <Select value={port} onChange={(e) => setPort(e.target.value)} disabled={portIds.length === 0}>
            {portIds.map((p) => <option key={p} value={p}>{p}</option>)}
          </Select>
        </Field>
      </div>
    </Modal>
  );
}
