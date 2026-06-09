// ============================================================
// pdn — Ports (§7): profile-first port editor + NinoTNC test-frame banner +
// save-confirm. Port of the design handoff's screens-manage.jsx Ports/
// PortEditor/NinoTestFlash, wired to the typed API client + mock domain models.
// ============================================================
import { useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import {
  Button, Badge, Card, StatusDot, Input, Field, Label, Tooltip,
  Slider, Select, Switch, Modal, Sheet, Icon,
} from "@/components/ui";
import { Page, PageHeader } from "@/components/layout/shell";
import { cn } from "@/lib/utils";
import { PingButton } from "@/components/ping";
import type {
  PortConfig, PortStatus, TransportConfig, Ax25PortParams, KissParams, PortSetup,
} from "@/lib/types";
import {
  NODE_CONFIG, PORT_STATUS, RADIO_PROFILES, NINO_MODES, CHANNEL_MODES,
  LINK_DIFFICULTY, PORT_SETUP, PARAM_HELP, AX25_DEFAULTS, KISS_DEFAULTS,
  KIND_LABEL, KIND_USES_KISS, persistPct, pctToPersist, NINO_TEST,
} from "@/lib/mock";
import { portHealth } from "@/lib/health";
import { api, useQuery, ConfigRejected, PortLifecycleUnavailable } from "@/lib/api";

// ---- the editor draft: a PortConfig plus the operator-facing setup choices ----
interface PortDraft {
  id: string;
  enabled: boolean;
  transport: TransportConfig;
  ax25: Ax25PortParams;
  kiss: KissParams;
  setup: PortSetup;
  _new?: boolean;
  // The id the draft was opened against (the reconcile key for an edit) — set on edit,
  // unset on add. Lets a rename in the editor edit the original entry rather than 404.
  _origId?: string;
}

// ---- transport descriptor line (e.g. "/dev/ttyACM0 · 9600 baud · GFSK · IL2P") ----
function transportDesc(t: TransportConfig): string {
  switch (t.kind) {
    case "kiss-tcp":
      return `${t.host}:${t.port}`;
    case "serial-kiss":
      return `${t.device} @ ${t.baud}`;
    case "nino-tnc": {
      const m = NINO_MODES.find((x) => x.mode === t.mode);
      return `${t.device} · ${m ? m.label : "mode " + t.mode}`;
    }
    case "axudp":
      return `${t.host}:${t.port}`;
  }
}

// ---- setup summary line (profile · channel · difficulty, or "Custom parameters") ----
function setupSummary(id: string): string {
  const s = PORT_SETUP[id];
  if (!s) return "Custom";
  if (s.custom) return "Custom parameters";
  const r = RADIO_PROFILES.find((x) => x.id === s.radio);
  const ch = CHANNEL_MODES.find((x) => x.id === s.channel);
  const d = LINK_DIFFICULTY.find((x) => x.id === s.difficulty);
  return [r?.name, ch?.name, d?.name].filter(Boolean).join(" · ");
}

export function Ports() {
  const navigate = useNavigate();
  const { data: config, reload: reloadConfig } = useQuery(api.config, []);
  const { data: portStatus, reload: reloadPorts } = useQuery(api.ports, []);
  const { data: links } = useQuery(api.linkStats, []);

  const [edit, setEdit] = useState<PortDraft | null>(null);
  const [testDismissed, setTestDismissed] = useState(false);
  // A banner-style notice for a rejected/failed mutation or a deferred action
  // (mirrors the config screen's `problem` surface — there is no toast primitive).
  const [notice, setNotice] = useState<{ tone: "danger" | "warning"; text: string } | null>(null);

  // Refetch /config + /ports after a successful mutation so the screen reflects the
  // applied state (no local mock list — the server is the source of truth in live mode).
  const reloadAll = () => { reloadConfig(); reloadPorts(); };

  const list = config?.ports ?? NODE_CONFIG.ports;
  const statusById: Record<string, PortStatus> = {};
  for (const st of portStatus ?? Object.values(PORT_STATUS)) statusById[st.id] = st;

  // Turn any caught error into a human banner (a 422 carries per-field problems).
  const showError = (e: unknown, fallback: string) => {
    if (e instanceof ConfigRejected) {
      setNotice({ tone: "danger", text: e.problem.errors.map((x) => `${x.path}: ${x.message}`).join("; ") || fallback });
    } else if (e instanceof PortLifecycleUnavailable) {
      setNotice({ tone: "warning", text: e.message });
    } else {
      setNotice({ tone: "danger", text: String((e as Error)?.message ?? e) || fallback });
    }
  };

  const newPort = (): PortDraft => ({
    id: "",
    enabled: true,
    transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 },
    ax25: { ...AX25_DEFAULTS },
    kiss: { ...KISS_DEFAULTS },
    setup: { radio: RADIO_PROFILES[0].id, channel: "shared", difficulty: "moderate", custom: false },
    _new: true,
  });

  const openEdit = (p: PortConfig) => {
    setEdit({
      id: p.id,
      enabled: p.enabled,
      transport: p.transport,
      ax25: { ...AX25_DEFAULTS, ...(p.ax25 ?? {}) },
      kiss: { ...KISS_DEFAULTS, ...(p.kiss ?? {}) },
      setup: PORT_SETUP[p.id] ?? { radio: RADIO_PROFILES[0].id, channel: "shared", difficulty: "moderate", custom: true },
      _origId: p.id,
    });
  };

  // Add (POST) or edit (PUT) the port through the config-write reconcile path, then
  // reload so the applied state shows. The _new flag decides add vs edit; an edit keys
  // on the *original* id (renaming the id edits the original entry).
  const saveDraft = async (d: PortDraft) => {
    const saved: PortConfig = {
      id: d.id,
      enabled: d.enabled,
      transport: d.transport,
      profile: d.setup.custom ? null : d.setup.radio,
      ax25: d.ax25,
      kiss: KIND_USES_KISS[d.transport.kind] ? d.kiss : null,
    };
    try {
      if (d._new) await api.addPort(saved);
      else await api.editPort(d._origId ?? saved.id, saved);
      setEdit(null);
      reloadAll();
    } catch (e) {
      showError(e, d._new ? "Could not add the port." : "Could not save the port.");
    }
  };

  const removePort = async (id: string) => {
    try { await api.removePort(id); reloadAll(); }
    catch (e) { showError(e, "Could not remove the port."); }
  };

  // up/down flip enabled via the lifecycle endpoint (persisted through the config seam);
  // restart is deferred server-side (501) and surfaces as a warning banner.
  const lifecycle = async (id: string, action: "up" | "down" | "restart") => {
    try { await api.portLifecycle(id, action); reloadAll(); }
    catch (e) { showError(e, `Could not ${action} the port.`); }
  };

  return (
    <Page>
      <PageHeader
        title="Ports"
        subtitle="Each RF or network port pdn talks through"
        actions={
          <div className="flex items-center gap-2">
            <PingButton station={NODE_CONFIG.identity.callsign} label="AX.25 ping" variant="outline" size="sm" />
            <Button size="sm" onClick={() => setEdit(newPort())}><Icon name="plus" size={14} /> Add port</Button>
          </div>
        }
      />

      {notice && (
        <div className={cn(
          "mb-4 flex items-start gap-2 rounded-md border px-3 py-2 text-sm",
          notice.tone === "danger" ? "border-danger/30 bg-danger/5 text-danger" : "border-warning/30 bg-warning/5 text-warning",
        )}>
          <Icon name="alert" size={15} className="mt-0.5 shrink-0" />
          <span className="flex-1">{notice.text}</span>
          <button onClick={() => setNotice(null)} className="shrink-0 opacity-70 hover:opacity-100"><Icon name="x" size={14} /></button>
        </div>
      )}

      {!testDismissed && (
        <NinoTestFlash
          onDismiss={() => setTestDismissed(true)}
          onConfigure={() => {
            const p = list.find((x) => x.id === NINO_TEST.portId);
            if (p) openEdit(p);
          }}
        />
      )}

      <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
        {list.map((p) => {
          const st = statusById[p.id];
          const h = portHealth(st, links ?? []);
          const accent = h.level === "faulted" ? "border-danger/40" : h.level === "degraded" ? "border-warning/40" : "border-border";
          const up = st?.state === "up";
          return (
            <Card key={p.id} className={cn("p-4", accent)}>
              <div className="flex items-start justify-between">
                <div className="flex items-center gap-2.5">
                  <StatusDot state={st?.state ?? "down"} live={up} />
                  <div>
                    <div className="flex items-center gap-2">
                      <span className="font-mono text-sm font-semibold">{p.id}</span>
                      {!p.enabled && <Badge variant="muted">disabled</Badge>}
                      {h.level === "faulted" && <Badge variant="danger">faulted</Badge>}
                      {h.level === "degraded" && (
                        <Tooltip text={h.reason}><Badge variant="warning">needs attention</Badge></Tooltip>
                      )}
                    </div>
                    <p className="mt-0.5 font-mono text-xs text-muted-foreground">{transportDesc(p.transport)}</p>
                  </div>
                </div>
                <Badge variant="secondary">{KIND_LABEL[p.transport.kind]}</Badge>
              </div>

              {h.level === "degraded" && (
                <div className="mt-3 flex items-center gap-2 rounded-md bg-warning/10 px-2.5 py-1.5 text-xs text-warning">
                  <Icon name="alert" size={13} /> {h.reason}
                </div>
              )}
              {st?.lastError && (
                <div className="mt-3 flex items-center gap-2 rounded-md bg-danger/10 px-2.5 py-1.5 text-xs text-danger">
                  <Icon name="alert" size={13} /> {st.lastError}
                </div>
              )}

              <div className="mt-3 rounded-md bg-muted/40 px-2.5 py-2 text-xs">
                <span className="text-muted-foreground">Setup </span>
                <span className="font-medium text-foreground">{setupSummary(p.id)}</span>
              </div>

              <div className="mt-3 grid grid-cols-3 gap-2 border-t border-border pt-3 text-xs">
                <div><p className="text-muted-foreground">Sessions</p><p className="tnum mt-0.5 font-mono font-semibold">{st?.sessionCount ?? 0}</p></div>
                <div><p className="text-muted-foreground">Frames ↓</p><p className="tnum mt-0.5 font-mono font-semibold">{(st?.framesIn ?? 0).toLocaleString()}</p></div>
                <div><p className="text-muted-foreground">Frames ↑</p><p className="tnum mt-0.5 font-mono font-semibold">{(st?.framesOut ?? 0).toLocaleString()}</p></div>
              </div>

              <div className="mt-3 flex flex-wrap items-center gap-2">
                <Button variant="outline" size="sm" onClick={() => openEdit(p)}>Edit</Button>
                <Button variant="ghost" size="sm" title="Tune this link with a partner" onClick={() => navigate("/tools/tuner?port=" + p.id)}>
                  <Icon name="signal" size={14} /> Tune link
                </Button>
                {/* Restart is a deferred backend step (501) — surfaced as a warning
                    banner via lifecycle(); only offered while the port is up. */}
                {up
                  ? <Button variant="ghost" size="sm" title="Restart port (not available yet)" onClick={() => lifecycle(p.id, "restart")}><Icon name="restart" size={14} /> Restart</Button>
                  : <Button variant="ghost" size="sm" title="Bring up" onClick={() => lifecycle(p.id, "up")}><Icon name="power" size={14} /> Bring up</Button>}
                {up && (
                  <Button variant="ghost" size="sm" className="text-muted-foreground" title="Take down" onClick={() => lifecycle(p.id, "down")}>
                    <Icon name="power" size={14} /> Down
                  </Button>
                )}
                <Button variant="ghost" size="sm" className="ml-auto text-muted-foreground hover:text-danger" title="Remove this port" onClick={() => removePort(p.id)}>
                  <Icon name="trash" size={14} /> Remove
                </Button>
              </div>
            </Card>
          );
        })}
      </div>

      <PortEditor draft={edit} onClose={() => setEdit(null)} onSave={saveDraft} statusById={statusById} />
    </Page>
  );
}

// ---- NinoTNC hardware "test button" decode, flashed on the Ports screen ----
function NinoTestFlash({ onDismiss, onConfigure }: { onDismiss: () => void; onConfigure: () => void }) {
  // TODO: live endpoint (beacons / nino-test) — no API endpoint yet, mock-sourced.
  const test = NINO_TEST;
  return (
    <Card className={cn("mb-4 overflow-hidden p-0", test.softwareControl ? "border-success/40" : "border-primary/40")}>
      <div className="flex items-start gap-3 p-4">
        <div className={cn("mt-0.5 grid h-9 w-9 shrink-0 place-items-center rounded-md", test.softwareControl ? "bg-success/15 text-success" : "bg-primary/15 text-primary")}>
          <Icon name="radio" size={18} />
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-sm font-semibold">NinoTNC test frame received</span>
            <Badge variant="muted">{test.portId}</Badge>
            <span className="text-xs text-muted-foreground">{test.receivedAt}</span>
          </div>
          <p className="mt-1 text-xs text-muted-foreground">
            <span className="font-mono text-foreground/80">{test.firmware}</span> · mode {test.mode}{" "}
            <span className="text-foreground/80">{test.modeLabel}</span> · RSSI {test.rssiDbm} dBm · CRC {test.crcOk ? "OK" : "FAIL"}
          </p>
          {!test.softwareControl && (
            <div className="mt-2.5 flex items-start gap-2 rounded-md bg-primary/10 px-2.5 py-2 text-xs text-primary">
              <Icon name="info" size={14} className="mt-px shrink-0" />
              <span>
                This modem is in <strong>hardware control</strong> — TX delay and mode are set by its DIP switches. Switch it
                to <strong>software-control mode</strong> so pdn can set them remotely (the two parameters that matter most for a healthy link).
              </span>
            </div>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-1">
          {!test.softwareControl && <Button variant="outline" size="sm" onClick={onConfigure}>How to enable</Button>}
          <Button variant="ghost" size="iconSm" onClick={onDismiss}><Icon name="x" size={15} /></Button>
        </div>
      </div>
    </Card>
  );
}

// ---- transport defaults per kind (used when switching the Type select) ----
function transportDefaults(kind: TransportConfig["kind"]): TransportConfig {
  switch (kind) {
    case "kiss-tcp": return { kind, host: "127.0.0.1", port: 8001 };
    case "serial-kiss": return { kind, device: "/dev/ttyUSB0", baud: 38400 };
    case "nino-tnc": return { kind, device: "/dev/ttyACM0", baud: 57600, mode: 4 }; // wire baud fixed at 57600
    case "axudp": return { kind, host: "44.0.0.1", port: 10093, localPort: 10093 };
  }
}

interface Disruption { tone: "success" | "warning" | "danger"; text: string }

// ---- transport + parameter editor: profile-first, with a custom escape hatch ----
function PortEditor({ draft, onClose, onSave, statusById }: {
  draft: PortDraft | null;
  onClose: () => void;
  onSave: (d: PortDraft) => void;
  statusById: Record<string, PortStatus>;
}) {
  const [model, setModel] = useState<PortDraft | null>(draft);
  const [srcKey, setSrcKey] = useState<string | undefined>(draft ? draft.id + String(draft._new) : undefined);
  const [confirm, setConfirm] = useState(false);

  // re-seed the local draft when the parent opens a different port
  if (draft) {
    const key = draft.id + String(draft._new);
    if (key !== srcKey) { setSrcKey(key); setModel(draft); }
  }

  if (!draft || !model) return null;

  const t = model.transport;
  const setup = model.setup;
  const usesKiss = KIND_USES_KISS[t.kind];

  const setT = (patch: Partial<TransportConfig>) =>
    setModel((d) => (d ? { ...d, transport: { ...d.transport, ...patch } as TransportConfig } : d));
  const setKind = (kind: TransportConfig["kind"]) =>
    setModel((d) => (d ? { ...d, transport: transportDefaults(kind) } : d));
  const setSetup = (patch: Partial<PortSetup>) =>
    setModel((d) => (d ? { ...d, setup: { ...d.setup, ...patch } } : d));
  const setAx = (k: keyof Ax25PortParams, v: number) =>
    setModel((d) => (d ? { ...d, ax25: { ...d.ax25, [k]: v } } : d));
  const setKiss = (k: keyof KissParams, v: number) =>
    setModel((d) => (d ? { ...d, kiss: { ...d.kiss, [k]: v } } : d));

  const profile = RADIO_PROFILES.find((r) => r.id === setup.radio);
  const baseline: Record<string, number> = profile ? profile.baseline : { ...AX25_DEFAULTS, ...KISS_DEFAULTS };

  const applyProfile = (radioId: string) => {
    const r = RADIO_PROFILES.find((x) => x.id === radioId);
    setModel((d) => {
      if (!d) return d;
      const next: PortDraft = { ...d, setup: { ...d.setup, radio: radioId, custom: false } };
      if (r) {
        next.ax25 = { ...d.ax25, t1Ms: r.baseline.t1Ms, t2Ms: r.baseline.t2Ms, t3Ms: r.baseline.t3Ms, n2: r.baseline.n2, windowSize: r.baseline.windowSize };
        next.kiss = { ...d.kiss, txDelay: r.baseline.txDelay, slotTime: r.baseline.slotTime, txTail: r.baseline.txTail, persistence: r.baseline.persistence };
        if (next.transport.kind === "nino-tnc") next.transport = { ...next.transport, mode: r.ninoMode };
      }
      return next;
    });
  };
  const resetToProfile = () => { if (setup.radio) applyProfile(setup.radio); };

  // disruption summary for the save confirmation (plain language)
  const orig = draft;
  const transportChanged = JSON.stringify(model.transport) !== JSON.stringify(orig.transport);
  const idChanged = !orig._new && model.id !== orig.id;
  const enabledChanged = !orig._new && model.enabled !== orig.enabled;
  const sessions = statusById[orig.id]?.sessionCount ?? 0;
  let disrupt: Disruption;
  if (orig._new) {
    disrupt = { tone: "success", text: `Port ${model.id || "(new)"} will be created and brought up.` };
  } else if (idChanged) {
    disrupt = { tone: "danger", text: `Renaming a port restarts the node — every session on every port drops.` };
  } else if (transportChanged || enabledChanged) {
    disrupt = { tone: "warning", text: `Port ${orig.id} will restart.${sessions > 0 ? ` ${sessions} session${sessions > 1 ? "s" : ""} on this port will drop.` : " No sessions are connected."}` };
  } else {
    disrupt = { tone: "success", text: `Modem parameters apply live to ${orig.id}. No sessions drop.` };
  }

  return (
    <Sheet
      open={!!draft}
      onClose={onClose}
      title={orig._new ? "Add port" : `Edit port — ${orig.id}`}
      subtitle="Pick a profile, or open the parameters to fine-tune"
      footer={
        <>
          <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
          <Button size="sm" onClick={() => setConfirm(true)}><Icon name="check" size={14} /> Save changes</Button>
        </>
      }
    >
      <div className="space-y-5">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Port id" info="A short name you choose for this port (e.g. vhf-1). Used in logs, sessions, and the monitor.">
            <Input value={model.id} onChange={(e) => setModel((d) => (d ? { ...d, id: e.target.value } : d))} className="font-mono" placeholder="vhf-1" />
          </Field>
          <Field label="Enabled" info="Whether pdn brings this port up. Disabling it takes the port down.">
            <div className="flex h-9 items-center"><Switch checked={model.enabled} onChange={(v) => setModel((d) => (d ? { ...d, enabled: v } : d))} /></div>
          </Field>
        </div>

        {/* transport */}
        <div className="rounded-lg border border-border p-3">
          <Label className="text-foreground">Connection</Label>
          <div className="mt-3">
            <Field label="Type" info="How pdn reaches the modem: a KISS TNC over TCP or serial, a NinoTNC, or an AXUDP network link.">
              <Select value={t.kind} onChange={(e) => setKind(e.target.value as TransportConfig["kind"])}>
                <option value="kiss-tcp">{KIND_LABEL["kiss-tcp"]} — KISS TNC over TCP</option>
                <option value="serial-kiss">{KIND_LABEL["serial-kiss"]} — KISS TNC over serial</option>
                <option value="nino-tnc">{KIND_LABEL["nino-tnc"]} — NinoTNC</option>
                <option value="axudp">{KIND_LABEL["axudp"]} — AXUDP network link</option>
              </Select>
            </Field>
          </div>
          <div className="mt-3 grid grid-cols-2 gap-3">
            {t.kind === "kiss-tcp" && (
              <>
                <Field label="Host"><Input value={t.host} onChange={(e) => setT({ host: e.target.value })} className="font-mono" /></Field>
                <Field label="TCP port"><Input type="number" value={t.port} onChange={(e) => setT({ port: +e.target.value })} className="font-mono" /></Field>
              </>
            )}
            {t.kind === "serial-kiss" && (
              <>
                <Field label="Serial device"><Input value={t.device} onChange={(e) => setT({ device: e.target.value })} className="font-mono" /></Field>
                <Field label="Baud"><Input type="number" value={t.baud} onChange={(e) => setT({ baud: +e.target.value })} className="font-mono" /></Field>
              </>
            )}
            {t.kind === "nino-tnc" && (
              <>
                <Field label="Serial device"><Input value={t.device} onChange={(e) => setT({ device: e.target.value })} className="font-mono" /></Field>
                <Field label="USB wire speed" info="A NinoTNC always runs at 57600 baud on the USB-serial wire. The radio-side speed is set by the modem mode below, not here.">
                  <div className="flex h-9 items-center rounded-md border border-input bg-muted/40 px-3 font-mono text-sm text-muted-foreground">
                    57600 <span className="ml-1.5 text-[11px]">fixed</span>
                  </div>
                </Field>
                <Field
                  label="Modem mode"
                  info="The radio-side modulation and speed. Served by the node from the NinoTNC firmware's table. In software-control mode pdn can set this remotely."
                  className="col-span-2"
                >
                  <Select value={t.mode} onChange={(e) => setT({ mode: +e.target.value })}>
                    {NINO_MODES.map((m) => <option key={m.mode} value={m.mode}>mode {m.mode} — {m.label}</option>)}
                  </Select>
                </Field>
              </>
            )}
            {t.kind === "axudp" && (
              <>
                <Field label="Peer host"><Input value={t.host} onChange={(e) => setT({ host: e.target.value })} className="font-mono" /></Field>
                <Field label="Peer port"><Input type="number" value={t.port} onChange={(e) => setT({ port: +e.target.value })} className="font-mono" /></Field>
                <Field label="Local port"><Input type="number" value={t.localPort} onChange={(e) => setT({ localPort: +e.target.value })} className="font-mono" /></Field>
              </>
            )}
          </div>
        </div>

        {/* profile-first setup */}
        <div className="rounded-lg border border-border p-3">
          <div className="mb-3 flex items-center justify-between">
            <Label className="text-foreground">Profile</Label>
            {setup.custom && <Badge variant="warning">customised</Badge>}
          </div>
          <div className="space-y-3">
            <Field label="Radio profile" info="A starting point for this kind of radio and speed. pdn fills in sensible timing and modem parameters; you can fine-tune below.">
              <Select value={setup.radio ?? ""} onChange={(e) => applyProfile(e.target.value)}>
                {RADIO_PROFILES.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
              </Select>
            </Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Channel use" info={CHANNEL_MODES.map((c) => `${c.name}: ${c.help}`).join("  ")}>
                <SegMode options={CHANNEL_MODES} value={setup.channel} onChange={(v) => setSetup({ channel: v })} />
              </Field>
              <Field label="Link difficulty" info={LINK_DIFFICULTY.map((d) => `${d.name}: ${d.help}`).join("  ")}>
                <SegMode options={LINK_DIFFICULTY} value={setup.difficulty} onChange={(v) => setSetup({ difficulty: v })} />
              </Field>
            </div>
          </div>
        </div>

        {/* advanced parameters */}
        <details className="rounded-lg border border-border" open={setup.custom}>
          <summary
            onClick={(e) => { e.preventDefault(); setSetup({ custom: !setup.custom }); }}
            className="flex cursor-pointer list-none items-center justify-between p-3 text-sm font-medium text-foreground"
          >
            <span className="flex items-center gap-2"><Icon name="config" size={14} className="text-muted-foreground" /> Advanced parameters</span>
            <span className="flex items-center gap-2">
              {setup.custom && (
                <button
                  onClick={(e) => { e.preventDefault(); e.stopPropagation(); resetToProfile(); }}
                  className="text-xs font-normal text-muted-foreground hover:text-primary"
                >
                  Reset to profile
                </button>
              )}
              <Icon name="chevDown" size={15} className={cn("text-muted-foreground transition-transform", setup.custom && "rotate-180")} />
            </span>
          </summary>
          {setup.custom && (
            <div className="space-y-4 border-t border-border p-3">
              <div>
                <p className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Link timing</p>
                <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
                  <ParamField k="t1Ms" value={model.ax25.t1Ms} base={baseline.t1Ms} onChange={(v) => setAx("t1Ms", v)} />
                  <ParamField k="t2Ms" value={model.ax25.t2Ms} base={baseline.t2Ms} onChange={(v) => setAx("t2Ms", v)} />
                  <ParamField k="t3Ms" value={model.ax25.t3Ms} base={baseline.t3Ms} onChange={(v) => setAx("t3Ms", v)} />
                  <ParamField k="n2" value={model.ax25.n2} base={baseline.n2} onChange={(v) => setAx("n2", v)} />
                  <ParamField k="windowSize" value={model.ax25.windowSize} base={baseline.windowSize} onChange={(v) => setAx("windowSize", v)} />
                </div>
              </div>
              {usesKiss && (
                <div>
                  <p className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Modem keying</p>
                  <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
                    <ParamField k="txDelay" value={model.kiss.txDelay} base={baseline.txDelay} onChange={(v) => setKiss("txDelay", v)} />
                    <ParamField k="txTail" value={model.kiss.txTail} base={baseline.txTail} onChange={(v) => setKiss("txTail", v)} />
                    <ParamField k="slotTime" value={model.kiss.slotTime} base={baseline.slotTime} onChange={(v) => setKiss("slotTime", v)} />
                  </div>
                  <div className="mt-3">
                    <PersistenceField value={model.kiss.persistence} base={baseline.persistence} onChange={(v) => setKiss("persistence", v)} />
                  </div>
                </div>
              )}
            </div>
          )}
        </details>
      </div>

      <Modal
        open={confirm}
        onClose={() => setConfirm(false)}
        title="Apply changes?"
        width="max-w-md"
        footer={
          <>
            <Button variant="outline" size="sm" onClick={() => setConfirm(false)}>Cancel</Button>
            <Button
              size="sm"
              className={disrupt.tone === "danger" ? "bg-danger hover:bg-danger/90 text-danger-foreground" : disrupt.tone === "warning" ? "bg-warning hover:bg-warning/90 text-warning-foreground" : ""}
              onClick={() => { setConfirm(false); onSave(model); }}
            >
              {disrupt.tone === "success" ? <><Icon name="check" size={14} /> Apply</> : <><Icon name="alert" size={14} /> Apply anyway</>}
            </Button>
          </>
        }
      >
        <div className={cn(
          "flex items-start gap-3 rounded-lg border p-3 text-sm",
          disrupt.tone === "danger" ? "border-danger/30 bg-danger/5 text-danger" : disrupt.tone === "warning" ? "border-warning/30 bg-warning/5 text-warning" : "border-success/30 bg-success/5 text-success",
        )}>
          <Icon name={disrupt.tone === "success" ? "check" : "alert"} size={16} className="mt-0.5 shrink-0" />
          <span>{disrupt.text}</span>
        </div>
      </Modal>
    </Sheet>
  );
}

// ---- segmented control for short option sets, with per-option tooltip ----
function SegMode({ options, value, onChange }: {
  options: { id: string; name: string; help: string }[];
  value: string;
  onChange: (v: string) => void;
}) {
  return (
    <div className="inline-flex w-full rounded-md border border-input p-0.5">
      {options.map((o) => (
        <Tooltip key={o.id} text={o.help} className="flex-1">
          <button
            onClick={() => onChange(o.id)}
            className={cn("w-full rounded px-2 py-1.5 text-xs font-medium transition-colors", value === o.id ? "bg-primary text-primary-foreground" : "text-muted-foreground hover:text-foreground")}
          >
            {o.name}
          </button>
        </Tooltip>
      ))}
    </div>
  );
}

// ---- a single tuneable: friendly label + help + unit + "modified" marker ----
function ParamField({ k, value, base, onChange }: {
  k: string;
  value: number | undefined;
  base: number | undefined;
  onChange: (v: number) => void;
}) {
  const meta = PARAM_HELP[k];
  const modified = base !== undefined && value !== base;
  const badge: ReactNode = modified
    ? <Tooltip text={`Default for this profile: ${base}${meta.unit ? " " + meta.unit : ""}`}><Badge variant="warning">modified</Badge></Tooltip>
    : null;
  return (
    <Field label={meta.label} info={meta.help} badge={badge}>
      <div className="relative">
        <Input
          type="number"
          value={value ?? ""}
          onChange={(e) => onChange(+e.target.value)}
          className={cn("font-mono", meta.unit && "pr-12", modified && "border-warning/60")}
        />
        {meta.unit && <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-[11px] text-muted-foreground">{meta.unit}</span>}
      </div>
    </Field>
  );
}

// ---- persistence as a 0–100% slider (stored as a 0–255 byte) ----
function PersistenceField({ value, base, onChange }: {
  value: number | undefined;
  base: number | undefined;
  onChange: (v: number) => void;
}) {
  const meta = PARAM_HELP.persistence;
  const v = value ?? 0;
  const pct = persistPct(v);
  const modified = base !== undefined && value !== base;
  const badge: ReactNode = modified
    ? <Tooltip text={`Default for this profile: ${persistPct(base)}%`}><Badge variant="warning">modified</Badge></Tooltip>
    : null;
  return (
    <Field label={meta.label} info={meta.help} badge={badge}>
      <div className="flex items-center gap-3">
        <Slider value={pct} min={0} max={100} onChange={(p) => onChange(pctToPersist(p))} />
        <span className="tnum w-20 shrink-0 text-right font-mono text-xs text-muted-foreground">
          {pct}% <span className="text-muted-foreground/50">({v})</span>
        </span>
      </div>
    </Field>
  );
}
