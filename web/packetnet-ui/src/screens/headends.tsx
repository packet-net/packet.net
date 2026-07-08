// ============================================================
// pdn — Head-ends: the split-station "plug into any port and go" surface. Discover the
// head-end fleet (config-pinned ∪ mDNS), preview each instance's bridged devices +
// the reach-through identity, and adopt a matched TNC↔radio pair into one port.
//   GET  /api/v1/radios/headends                          — the fleet scan/preview (read scope)
//   POST /api/v1/radios/headends/{instanceId}/adopt       — create the matched port (operate scope)
//   POST /api/v1/radios/headends/{id}/pair-by-keyup       — physical pairing resolve (ADMIN scope —
//     it TRANSMITS: each free modem is briefly keyed; gated behind an RF-warning confirm)
// See docs/research/split-station-rf-headend.md § Discovery & adoption flow + § Identify/pair/name
// v2. This screen is the Stage 4a UI leg over the Stage 3b backend, caught up to v2 (#579): band
// badges (+ band-named adopt), unstable-id warnings, the keyup resolver, and the MQTT label option.
// ============================================================
import { useState, type ReactNode } from "react";
import {
  Button, Badge, Card, StatusDot, Select, Field, Input, EmptyState, Icon, Tooltip, Modal,
} from "@/components/ui";
import { Page, PageHeader } from "@/components/layout/shell";
import { cn } from "@/lib/utils";
import type {
  HeadEndInstanceScan, HeadEndDeviceScan, HeadEndConflict, HeadEndAdoptRequest,
  HeadEndKeyupPair, HeadEndKeyupResult,
} from "@/lib/types";
import { api, useQuery, ConfigRejected } from "@/lib/api";
import { useAuth } from "@/app/auth";

// A device's operator-facing kind label + which side of a pair it can fill.
const KIND_LABEL: Record<string, string> = {
  "nino-tnc": "NinoTNC",
  "tait-ccdi": "Tait CCDI radio",
  unknown: "Unknown",
};
const isTnc = (d: HeadEndDeviceScan) => d.kind === "nino-tnc";
const isRadio = (d: HeadEndDeviceScan) => d.kind === "tait-ccdi";

// A one-line device descriptor for a select option / summary line (model + version + id).
function deviceLabel(d: HeadEndDeviceScan): string {
  const bits = [d.model ?? KIND_LABEL[d.kind] ?? d.kind];
  if (d.version) bits.push(`v${d.version}`);
  if (d.serial) bits.push(`s/n ${d.serial}`);
  bits.push(d.deviceId);
  return bits.join(" · ");
}

type Notice = { tone: "success" | "danger" | "warning"; text: string };

export function HeadEnds() {
  const { has } = useAuth();
  const canOperate = has("operate"); // adopt is operate-scoped (it writes config)
  const canAdmin = has("admin"); // keyup pairing is admin-scoped (it transmits — same bar as hail/tuning/doctor)
  const { data: scan, loading, error, reload } = useQuery(api.getHeadEnds, []);
  const [notice, setNotice] = useState<Notice | null>(null);
  // The instance currently being adopted (its Adopt button spins + disables), or null.
  const [adoptingId, setAdoptingId] = useState<string | null>(null);

  // Turn a caught error into a human banner — a 422 carries the config validator's per-field
  // problems (declared-reference / co-location pairing rule); anything else is its message.
  const showError = (e: unknown, fallback: string) => {
    if (e instanceof ConfigRejected) {
      setNotice({ tone: "danger", text: e.problem.errors.map((x) => `${x.path}: ${x.message}`).join("; ") || fallback });
    } else {
      setNotice({ tone: "danger", text: String((e as Error)?.message ?? e) || fallback });
    }
  };

  // Adopt a chosen pairing → one matched port, then refresh the scan so the adopted devices
  // move to "in use" and the pairing is consumed.
  const adopt = async (instance: HeadEndInstanceScan, req: HeadEndAdoptRequest) => {
    setAdoptingId(instance.instanceId);
    setNotice(null);
    try {
      const result = await api.adoptHeadEnd(instance.instanceId, req);
      const summary = [...result.live, ...result.portRestart, ...result.nodeReset]
        .map((c) => c.summary).join(" ");
      setNotice({
        tone: "success",
        text: summary || `Adopted a matched port on ${instance.instanceId}.`,
      });
      reload();
    } catch (e) {
      showError(e, `Could not adopt a pair on ${instance.instanceId}.`);
    } finally {
      setAdoptingId(null);
    }
  };

  const instances = scan?.instances ?? [];
  const conflicts = scan?.conflicts ?? [];
  const nothing = !loading && instances.length === 0 && conflicts.length === 0;

  return (
    <Page>
      <PageHeader
        title="Head-ends"
        subtitle="Plug a modem + radio into any head-end on the LAN and adopt the matched pair"
        actions={
          <Button size="sm" variant="outline" onClick={reload} disabled={loading} title="Re-scan the head-end fleet">
            <Icon name={loading ? "restart" : "search"} size={14} className={cn(loading && "animate-spin")} />
            {loading ? "Scanning…" : "Rescan"}
          </Button>
        }
      />

      {notice && (
        <div className={cn(
          "mb-4 flex items-start gap-2 rounded-md border px-3 py-2 text-sm",
          notice.tone === "danger" ? "border-danger/30 bg-danger/5 text-danger"
            : notice.tone === "warning" ? "border-warning/30 bg-warning/5 text-warning"
            : "border-success/30 bg-success/5 text-success",
        )}>
          <Icon name={notice.tone === "success" ? "check" : "alert"} size={15} className="mt-0.5 shrink-0" />
          <span className="flex-1">{notice.text}</span>
          <button onClick={() => setNotice(null)} className="shrink-0 opacity-70 hover:opacity-100" aria-label="Dismiss"><Icon name="x" size={14} /></button>
        </div>
      )}

      {/* A failed scan (node down / read denied) — distinct from an empty fleet. */}
      {error && !scan && (
        <div className="mb-4 flex items-start gap-2 rounded-md border border-danger/30 bg-danger/5 px-3 py-2 text-sm text-danger">
          <Icon name="alert" size={15} className="mt-0.5 shrink-0" />
          <span>Could not scan the head-end fleet: {error}</span>
        </div>
      )}

      {/* Conflicts render FIRST + loud: a duplicate instance id is a mis-config PDN refuses to
          guess through, so it must be seen before any adopt. */}
      {conflicts.length > 0 && (
        <div className="mb-4 space-y-2" data-testid="headend-conflicts">
          {conflicts.map((c) => <ConflictCard key={c.instanceId} conflict={c} />)}
        </div>
      )}

      {nothing ? (
        <EmptyState
          icon="radio"
          title="No head-ends found"
          body="Nothing is advertising _pdnhead._tcp on this LAN, and no head-end address is pinned in config. Plug a head-end in (or add its host:port to config) and rescan."
        />
      ) : (
        <div className="space-y-3">
          {instances.map((inst) => (
            <InstanceCard
              key={inst.instanceId}
              instance={inst}
              canOperate={canOperate}
              canAdmin={canAdmin}
              adopting={adoptingId === inst.instanceId}
              onAdopt={adopt}
              onRescan={reload}
            />
          ))}
        </div>
      )}
    </Page>
  );
}

// ---- a duplicate-instance-id conflict, with the remediation hint ----
function ConflictCard({ conflict }: { conflict: HeadEndConflict }) {
  return (
    <Card className="border-danger/40 p-4" data-testid={`headend-conflict-${conflict.instanceId}`}>
      <div className="flex items-start gap-2.5">
        <Icon name="alert" size={16} className="mt-0.5 shrink-0 text-danger" />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-sm font-semibold text-danger">Duplicate head-end id</span>
            <Badge variant="danger">{conflict.instanceId}</Badge>
          </div>
          <p className="mt-1 text-xs text-muted-foreground">
            Two head-ends advertise id <span className="font-mono text-foreground/80">{conflict.instanceId}</span> at{" "}
            {conflict.addresses.map((a, i) => (
              <span key={a}>
                {i > 0 && ", "}
                <span className="font-mono text-foreground/80">{a}</span>
              </span>
            ))}
            . PDN won't guess which one to bind, so neither is adopted. Pin distinct <span className="font-mono">instanceId</span>s
            on the two boxes (<span className="font-mono">--instance</span> / env / config), or set an explicit{" "}
            <span className="font-mono">address</span> in this node's head-end config to disambiguate.
          </p>
        </div>
      </div>
    </Card>
  );
}

// ---- one head-end instance: identity + reachability + devices + the adopt affordance ----
function InstanceCard({ instance, canOperate, canAdmin, adopting, onAdopt, onRescan }: {
  instance: HeadEndInstanceScan;
  canOperate: boolean;
  canAdmin: boolean;
  adopting: boolean;
  onAdopt: (instance: HeadEndInstanceScan, req: HeadEndAdoptRequest) => void;
  onRescan: () => void;
}) {
  const freeTncs = instance.devices.filter((d) => d.free && isTnc(d));
  const freeRadios = instance.devices.filter((d) => d.free && isRadio(d));
  // The unambiguous auto suggestion (exactly one free TNC + one free radio), if any.
  const autoPair = !instance.pairingAmbiguous ? instance.proposedPairs.find((p) => p.auto) ?? null : null;

  // Operator-chosen ids (ambiguous case). Seed from the auto pair when present so the summary
  // renders a concrete pairing even before any interaction.
  const [tncId, setTncId] = useState<string>(autoPair?.tncDeviceId ?? "");
  const [radioId, setRadioId] = useState<string>(autoPair?.radioDeviceId ?? "");
  const [portId, setPortId] = useState<string>("");
  const [mode, setMode] = useState<string>("");
  const [mqtt, setMqtt] = useState<string>("");
  const [showOptions, setShowOptions] = useState(false);

  const accent = !instance.reachable ? "border-danger/40" : "border-border";

  const submit = (tnc: string, radio: string) => {
    const req: HeadEndAdoptRequest = { tncDeviceId: tnc, radioDeviceId: radio };
    const pid = portId.trim();
    if (pid) req.portId = pid;
    if (mode.trim() !== "") req.mode = Number(mode);
    // The selected radio's band (from its scan row) rides the adopt body so a UI adopt gets the
    // same band-named port + MQTT {instance} label an API adopt does (#579).
    const band = instance.devices.find((d) => d.deviceId === radio)?.amateurBand;
    if (band) req.amateurBand = band;
    const label = mqtt.trim();
    if (label) req.mqttInstance = label;
    onAdopt(instance, req);
  };

  // A keyup-resolved physical pair pre-selects the adopt pickers (the ambiguous case's manual
  // choice becomes ground truth; in the auto case it simply confirms the suggestion).
  const applyResolvedPair = (pair: HeadEndKeyupPair) => {
    setTncId(pair.tncDeviceId);
    setRadioId(pair.radioDeviceId);
  };

  // The RF-emitting physical resolver is offered wherever a keyup could resolve/verify something:
  // at least one free TNC AND one free radio (most valuable when pairingAmbiguous).
  const keyup = freeTncs.length > 0 && freeRadios.length > 0 ? (
    <KeyupPairing
      instance={instance}
      canAdmin={canAdmin}
      onResolved={applyResolvedPair}
      onRescan={onRescan}
    />
  ) : null;

  return (
    <Card className={cn("p-4", accent)} data-testid={`headend-${instance.instanceId}`}>
      {/* header: id + reachability + source + address */}
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-center gap-2.5">
          <StatusDot state={instance.reachable ? "up" : "error"} live={instance.reachable} />
          <div>
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-mono text-sm font-semibold">{instance.instanceId}</span>
              <Badge variant={instance.source === "config" ? "secondary" : "muted"}>
                {instance.source === "config" ? "config" : "mDNS"}
              </Badge>
              {instance.reachable
                ? <Badge variant="success">reachable</Badge>
                : <Badge variant="danger">unreachable</Badge>}
            </div>
            <p className="mt-0.5 font-mono text-xs text-muted-foreground">{instance.host}:{instance.httpPort}</p>
          </div>
        </div>
      </div>

      {/* unreachable: surface the error + nothing else (no devices came back) */}
      {!instance.reachable ? (
        <div className="mt-3 flex items-start gap-2 rounded-md bg-danger/10 px-2.5 py-2 text-xs text-danger">
          <Icon name="alert" size={13} className="mt-px shrink-0" />
          <span>{instance.error ?? "The head-end could not be reached."}</span>
        </div>
      ) : (
        <>
          {/* devices this head-end bridges */}
          <div className="mt-3 border-t border-border pt-3">
            <p className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
              Devices ({instance.devices.length})
            </p>
            {instance.devices.length === 0 ? (
              <p className="text-xs text-muted-foreground">The head-end bridges no serial devices right now.</p>
            ) : (
              <div className="space-y-1.5">
                {instance.devices.map((d) => <DeviceRow key={d.deviceId} device={d} />)}
              </div>
            )}
          </div>

          {/* pairing / adopt */}
          <div className="mt-3 rounded-lg border border-border bg-muted/30 p-3">
            {autoPair ? (
              <AutoPairPanel
                instance={instance}
                autoTnc={instance.devices.find((d) => d.deviceId === autoPair.tncDeviceId) ?? null}
                autoRadio={instance.devices.find((d) => d.deviceId === autoPair.radioDeviceId) ?? null}
                canOperate={canOperate}
                adopting={adopting}
                showOptions={showOptions}
                setShowOptions={setShowOptions}
                portId={portId} setPortId={setPortId}
                mode={mode} setMode={setMode}
                mqtt={mqtt} setMqtt={setMqtt}
                keyup={keyup}
                onAdopt={() => submit(autoPair.tncDeviceId, autoPair.radioDeviceId)}
              />
            ) : freeTncs.length > 0 && freeRadios.length > 0 ? (
              <AmbiguousPanel
                instance={instance}
                freeTncs={freeTncs}
                freeRadios={freeRadios}
                tncId={tncId} setTncId={setTncId}
                radioId={radioId} setRadioId={setRadioId}
                canOperate={canOperate}
                adopting={adopting}
                showOptions={showOptions}
                setShowOptions={setShowOptions}
                portId={portId} setPortId={setPortId}
                mode={mode} setMode={setMode}
                mqtt={mqtt} setMqtt={setMqtt}
                keyup={keyup}
                onAdopt={() => submit(tncId, radioId)}
              />
            ) : (
              <p className="text-xs text-muted-foreground">
                No adoptable pair here — a matched port needs one free NinoTNC <em>and</em> one free Tait radio on this
                head-end{freeTncs.length === 0 && freeRadios.length === 0 ? " (both are missing or already in use)." : freeTncs.length === 0 ? " (no free TNC)." : " (no free radio)."}
              </p>
            )}
          </div>
        </>
      )}
    </Card>
  );
}

// ---- one device row: kind + identity + band + id stability + free/in-use ----
function DeviceRow({ device }: { device: HeadEndDeviceScan }) {
  // The band the radio's split covers (CCDI-readable off the product code): prefer the amateur band
  // ("2m"), fall back to the raw Tait designator ("B1") for a split with no amateur allocation.
  const band = device.amateurBand ?? device.bandCode ?? null;
  return (
    <div className="flex items-center justify-between gap-2 rounded-md border border-border bg-background/40 px-2.5 py-1.5">
      <div className="flex min-w-0 items-center gap-2">
        <Icon name={isRadio(device) ? "radio" : "signal"} size={14} className="shrink-0 text-muted-foreground" />
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="font-mono text-xs font-medium">{device.deviceId}</span>
            <Badge variant="outline">{KIND_LABEL[device.kind] ?? device.kind}</Badge>
            {band && (
              <Tooltip text={device.amateurBand
                ? `The radio's band split covers the ${device.amateurBand} amateur band${device.bandCode ? ` (Tait designator ${device.bandCode})` : ""} — adopting it names the port + MQTT label by band.`
                : `Tait band designator ${band} — a split with no UK amateur allocation.`}>
                <Badge variant="secondary">{band}</Badge>
              </Tooltip>
            )}
            {device.idStable === false && (
              <Tooltip text="This device has no /dev/serial/by-path (or by-id) link on the head-end, so its id is the raw kernel name — it can renumber, and a port binding to it may not survive a replug. Prefer a USB socket that yields a by-path id.">
                <Badge variant="warning">unstable id</Badge>
              </Tooltip>
            )}
          </div>
          <p className="truncate text-[11px] text-muted-foreground">
            {device.model ?? "—"}
            {device.version ? ` · v${device.version}` : ""}
            {device.serial ? ` · s/n ${device.serial}` : ""}
            {` · ${device.baud} baud`}
          </p>
        </div>
      </div>
      {device.free
        ? <Badge variant="success">free</Badge>
        : <Tooltip text="Already bound to a configured port — the head-end is single-client-per-pipe, so this device isn't re-probed or adoptable."><Badge variant="muted">in use</Badge></Tooltip>}
    </div>
  );
}

// ---- optional adopt details (port id + modem mode + MQTT label), shared by both panels ----
function AdoptOptions({ show, setShow, portId, setPortId, mode, setMode, mqtt, setMqtt, instanceId }: {
  show: boolean; setShow: (v: boolean) => void;
  portId: string; setPortId: (v: string) => void;
  mode: string; setMode: (v: string) => void;
  mqtt: string; setMqtt: (v: string) => void;
  instanceId: string;
}) {
  return (
    <div className="mt-2">
      <button
        type="button"
        onClick={() => setShow(!show)}
        className="flex items-center gap-1 text-[11px] text-muted-foreground hover:text-foreground"
      >
        <Icon name="chevRight" size={12} className={cn("transition-transform", show && "rotate-90")} />
        Options
      </button>
      {show && (
        <div className="mt-2 grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Field label="Port id" info="The id for the new port. Defaults to the radio's amateur band when known (e.g. 2m), else the head-end instance id.">
            <Input value={portId} onChange={(e) => setPortId(e.target.value)} placeholder={instanceId} className="font-mono" />
          </Field>
          <Field label="Modem mode" info="The NinoTNC modem mode (0–15) the new port comes up in. Defaults to 0.">
            <Input type="number" min={0} max={15} value={mode} onChange={(e) => setMode(e.target.value)} placeholder="0" className="font-mono" />
          </Field>
          <Field label="MQTT instance label" info="The {instance} segment in the port's MQTT topics (kissproxy-migration collector continuity). Defaults to the radio's amateur band when known.">
            <Input value={mqtt} onChange={(e) => setMqtt(e.target.value)} placeholder="e.g. 2m" className="font-mono" aria-label="MQTT instance label" />
          </Field>
        </div>
      )}
    </div>
  );
}

// The Adopt button, gated on operate scope + a valid pairing (+ a spinner while in flight).
function AdoptButton({ canOperate, disabled, adopting, onClick }: {
  canOperate: boolean; disabled: boolean; adopting: boolean; onClick: () => void;
}) {
  return (
    <Button
      size="sm"
      disabled={!canOperate || disabled || adopting}
      title={canOperate ? undefined : "Adopting a head-end pair requires the operate scope"}
      onClick={onClick}
    >
      {adopting
        ? <><Icon name="restart" size={14} className="animate-spin" /> Adopting…</>
        : <><Icon name="check" size={14} /> Adopt</>}
    </Button>
  );
}

// ---- auto pairing: one free TNC + one free radio → one-click adopt ----
function AutoPairPanel({ instance, autoTnc, autoRadio, canOperate, adopting, showOptions, setShowOptions, portId, setPortId, mode, setMode, mqtt, setMqtt, keyup, onAdopt }: {
  instance: HeadEndInstanceScan;
  autoTnc: HeadEndDeviceScan | null;
  autoRadio: HeadEndDeviceScan | null;
  canOperate: boolean;
  adopting: boolean;
  showOptions: boolean; setShowOptions: (v: boolean) => void;
  portId: string; setPortId: (v: string) => void;
  mode: string; setMode: (v: string) => void;
  mqtt: string; setMqtt: (v: string) => void;
  keyup: ReactNode;
  onAdopt: () => void;
}) {
  return (
    <div>
      <div className="mb-2 flex items-center gap-2">
        <Badge variant="success">suggested pairing</Badge>
        <span className="text-xs text-muted-foreground">one free modem + one free radio — adopt as one port</span>
      </div>
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <span className="rounded-md bg-background px-2 py-1 font-mono">{autoTnc ? deviceLabel(autoTnc) : "?"}</span>
        <Icon name="link" size={13} className="text-muted-foreground" />
        <span className="rounded-md bg-background px-2 py-1 font-mono">{autoRadio ? deviceLabel(autoRadio) : "?"}</span>
      </div>
      <AdoptOptions
        show={showOptions} setShow={setShowOptions}
        portId={portId} setPortId={setPortId}
        mode={mode} setMode={setMode}
        mqtt={mqtt} setMqtt={setMqtt}
        instanceId={instance.instanceId}
      />
      <div className="mt-3 flex items-center justify-between gap-2">
        <div>{keyup}</div>
        <AdoptButton canOperate={canOperate} disabled={!autoTnc || !autoRadio} adopting={adopting} onClick={onAdopt} />
      </div>
    </div>
  );
}

// ---- ambiguous pairing: >1 free TNC or radio → the operator picks before adopting ----
function AmbiguousPanel({ instance, freeTncs, freeRadios, tncId, setTncId, radioId, setRadioId, canOperate, adopting, showOptions, setShowOptions, portId, setPortId, mode, setMode, mqtt, setMqtt, keyup, onAdopt }: {
  instance: HeadEndInstanceScan;
  freeTncs: HeadEndDeviceScan[];
  freeRadios: HeadEndDeviceScan[];
  tncId: string; setTncId: (v: string) => void;
  radioId: string; setRadioId: (v: string) => void;
  canOperate: boolean;
  adopting: boolean;
  showOptions: boolean; setShowOptions: (v: boolean) => void;
  portId: string; setPortId: (v: string) => void;
  mode: string; setMode: (v: string) => void;
  mqtt: string; setMqtt: (v: string) => void;
  keyup: ReactNode;
  onAdopt: () => void;
}) {
  // Only a selection that is still among the free devices is adoptable (a prior adopt may have
  // consumed a device the local state still points at, after a refresh).
  const tncValid = freeTncs.some((d) => d.deviceId === tncId);
  const radioValid = freeRadios.some((d) => d.deviceId === radioId);
  return (
    <div>
      <div className="mb-2 flex items-center gap-2">
        <Badge variant="warning">choose a pairing</Badge>
        <span className="text-xs text-muted-foreground">more than one free modem or radio — pick which two to pair, or resolve physically</span>
      </div>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <Field label="Modem (NinoTNC)" info="The NinoTNC to bind as this port's transport (a nino-tnc-tcp pipe to the head-end).">
          <Select value={tncId} onChange={(e) => setTncId(e.target.value)} aria-label="Modem (NinoTNC)">
            <option value="">Select a modem…</option>
            {freeTncs.map((d) => <option key={d.deviceId} value={d.deviceId}>{deviceLabel(d)}</option>)}
          </Select>
        </Field>
        <Field label="Radio (Tait CCDI)" info="The Tait radio to bind as this port's radio-control channel (RSSI / DCD / tuning).">
          <Select value={radioId} onChange={(e) => setRadioId(e.target.value)} aria-label="Radio (Tait CCDI)">
            <option value="">Select a radio…</option>
            {freeRadios.map((d) => <option key={d.deviceId} value={d.deviceId}>{deviceLabel(d)}</option>)}
          </Select>
        </Field>
      </div>
      <AdoptOptions
        show={showOptions} setShow={setShowOptions}
        portId={portId} setPortId={setPortId}
        mode={mode} setMode={setMode}
        mqtt={mqtt} setMqtt={setMqtt}
        instanceId={instance.instanceId}
      />
      <div className="mt-3 flex items-center justify-between gap-2">
        <div>{keyup}</div>
        <div className="flex items-center gap-2">
          {(!tncValid || !radioValid) && (
            <span className="text-[11px] text-muted-foreground">Pick a modem and a radio to adopt.</span>
          )}
          <AdoptButton canOperate={canOperate} disabled={!tncValid || !radioValid} adopting={adopting} onClick={onAdopt} />
        </div>
      </div>
    </div>
  );
}

// ---- keyup pairing: the RF-emitting physical modem↔radio resolver (admin scope) ----
// The trigger button (disabled + explained without the admin scope) + a confirm-first modal: the
// operator must read the RF warning before anything keys. The result phase lists the resolved
// pairs / unpaired devices / ambiguities and quotes the server's caveat; a resolved pair has already
// been applied to the adopt pickers and the scan refreshed by the time it renders.
function KeyupPairing({ instance, canAdmin, onResolved, onRescan }: {
  instance: HeadEndInstanceScan;
  canAdmin: boolean;
  onResolved: (pair: HeadEndKeyupPair) => void;
  onRescan: () => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button
        size="sm"
        variant="outline"
        disabled={!canAdmin}
        title={canAdmin
          ? "Discover the physical modem↔radio cabling by briefly keying each free modem's transmitter"
          : "Keyup pairing transmits on-air — it requires the admin scope"}
        onClick={() => setOpen(true)}
      >
        <Icon name="signal" size={14} /> Resolve physically (keys each modem briefly)
      </Button>
      {open && (
        <KeyupDialog
          instance={instance}
          onClose={() => setOpen(false)}
          onResolved={onResolved}
          onRescan={onRescan}
        />
      )}
    </>
  );
}

// A device id → its scan-row label, for readable keyup result lines.
function keyupDeviceLabel(instance: HeadEndInstanceScan, deviceId: string): string {
  const d = instance.devices.find((x) => x.deviceId === deviceId);
  return d ? deviceLabel(d) : deviceId;
}

function KeyupDialog({ instance, onClose, onResolved, onRescan }: {
  instance: HeadEndInstanceScan;
  onClose: () => void;
  onResolved: (pair: HeadEndKeyupPair) => void;
  onRescan: () => void;
}) {
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<HeadEndKeyupResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  const run = async () => {
    setRunning(true);
    setError(null);
    try {
      const r = await api.pairHeadEndByKeyup(instance.instanceId);
      setResult(r);
      // Ground truth in hand: pre-select the adopt pickers with the first resolved pair and
      // refresh the scan (a keyup may also have verified/changed what the passive scan showed).
      if (r.reachable && r.pairs.length > 0) onResolved(r.pairs[0]);
      onRescan();
    } catch (e) {
      setError(String((e as Error)?.message ?? e));
    } finally {
      setRunning(false);
    }
  };

  return (
    <Modal
      open
      onClose={onClose}
      width="max-w-lg"
      title={`Resolve pairing physically — ${instance.instanceId}`}
      footer={<>
        <Button variant="outline" size="sm" onClick={onClose}>{result ? "Done" : "Cancel"}</Button>
        {!result && (
          <Button size="sm" variant="destructive" onClick={run} disabled={running}>
            {running
              ? <><Icon name="restart" size={14} className="animate-spin" /> Keying…</>
              : <><Icon name="signal" size={14} /> Key up and resolve</>}
          </Button>
        )}
      </>}
    >
      <div className="space-y-3">
        {!result && (
          <div className="flex items-start gap-2 rounded-md border border-warning/40 bg-warning/10 px-2.5 py-2 text-xs text-warning" data-testid="keyup-rf-warning">
            <Icon name="alert" size={14} className="mt-0.5 shrink-0" />
            <span>
              <span className="font-semibold">RF warning — this transmits.</span> Each free NinoTNC on{" "}
              <span className="font-mono">{instance.instanceId}</span> is briefly keyed (transmitted through) to
              discover its physically-cabled radio by the PTT it asserts. It emits on-air and must only be run by an
              operator on frequencies they are licensed and clear to key. It is never part of the passive scan.
            </span>
          </div>
        )}

        {error && (
          <div className="flex items-start gap-2 rounded-md border border-danger/40 bg-danger/10 px-2.5 py-2 text-xs text-danger">
            <Icon name="alert" size={13} className="mt-px shrink-0" />
            <span>{error}</span>
          </div>
        )}

        {result && !result.reachable && (
          <div className="flex items-start gap-2 rounded-md border border-danger/40 bg-danger/10 px-2.5 py-2 text-xs text-danger">
            <Icon name="alert" size={13} className="mt-px shrink-0" />
            <span>{result.error ?? "The head-end could not be reached."}</span>
          </div>
        )}

        {result?.reachable && (
          <div className="space-y-2" data-testid="keyup-result">
            {result.pairs.length > 0 ? (
              <>
                <p className="text-xs font-semibold text-success">
                  {result.pairs.length === 1 ? "1 physical pair resolved" : `${result.pairs.length} physical pairs resolved`}
                  {" — the first is pre-selected in the adopt pickers."}
                </p>
                <div className="space-y-1.5">
                  {result.pairs.map((p) => (
                    <div key={p.tncDeviceId} className="flex flex-wrap items-center gap-2 rounded-md border border-border bg-background/60 px-2.5 py-1.5 text-xs">
                      <span className="font-mono">{keyupDeviceLabel(instance, p.tncDeviceId)}</span>
                      <Icon name="link" size={13} className="text-muted-foreground" />
                      <span className="font-mono">{keyupDeviceLabel(instance, p.radioDeviceId)}</span>
                    </div>
                  ))}
                </div>
              </>
            ) : (
              <p className="text-xs text-muted-foreground">No pair resolved — no keyup fired a radio's PTT.</p>
            )}

            {result.unpairedTncs.length > 0 && (
              <p className="text-xs text-muted-foreground">
                <span className="font-semibold text-warning">Unpaired modems</span> (keyup fired no radio's PTT — radio
                off, PROGRESS disabled, or not cabled):{" "}
                <span className="font-mono">{result.unpairedTncs.join(", ")}</span>
              </p>
            )}
            {result.unpairedRadios.length > 0 && (
              <p className="text-xs text-muted-foreground">
                <span className="font-semibold text-warning">Unpaired radios</span> (no modem's keyup fired them):{" "}
                <span className="font-mono">{result.unpairedRadios.join(", ")}</span>
              </p>
            )}
            {result.ambiguous.length > 0 && (
              <div className="text-xs text-danger">
                <span className="font-semibold">Ambiguous</span> (one keyup fired more than one PTT — not physically
                expected; check the cabling):
                {result.ambiguous.map((a) => (
                  <p key={a.tncDeviceId} className="mt-0.5 font-mono">
                    {a.tncDeviceId} → {a.radioDeviceIds.join(", ")}
                  </p>
                ))}
              </div>
            )}

            <p className="rounded-md bg-muted/40 px-2.5 py-2 text-[11px] text-muted-foreground">{result.caveat}</p>
          </div>
        )}
      </div>
    </Modal>
  );
}
