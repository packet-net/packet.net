// ============================================================
// Capability doctor — an operator's "Check radio setup" for one port.
// DoctorButton is the drop-in trigger used on the Ports screen's per-port
// action row. It opens a self-contained RadioDoctor modal that renders the
// probe checklist (pass=green / fail=red+remedy / unknown=grey).
//
// The modal auto-runs the SAFE check on open (api.runDoctor(id, false) — a
// read-scoped GET that never transmits). A secondary "Run full check (briefly
// transmits)" action runs the interrupt form (api.runDoctor(id, true) — the
// admin/audited POST that keys the transmitter for the TXDELAY / SDM / pairing
// probes). Both mock and live mode go through api.runDoctor.
// ============================================================
import { useCallback, useEffect, useState } from "react";
import { Button, Modal, Badge, Icon, type ButtonVariant, type ButtonSize, type BadgeVariant } from "@/components/ui";
import { api } from "@/lib/api";
import type { DoctorReport, DoctorStatus } from "@/lib/types";

// pass=green / fail=red / unknown=grey — the three-state badge + icon + text tone.
const STATUS: Record<DoctorStatus, { badge: BadgeVariant; icon: "check" | "alert" | "info"; tone: string }> = {
  pass: { badge: "success", icon: "check", tone: "text-success" },
  fail: { badge: "danger", icon: "alert", tone: "text-danger" },
  unknown: { badge: "muted", icon: "info", tone: "text-muted-foreground" },
};

function RadioDoctor({ portId, onClose }: { portId: string; onClose: () => void }) {
  const [report, setReport] = useState<DoctorReport | null>(null);
  const [running, setRunning] = useState(false);
  const [ranFull, setRanFull] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = useCallback(async (interrupt: boolean) => {
    setRunning(true);
    setError(null);
    try {
      setReport(await api.runDoctor(portId, interrupt));
      setRanFull(interrupt);
    } catch (e) {
      setError(String((e as Error)?.message ?? e));
    } finally {
      setRunning(false);
    }
  }, [portId]);

  // Auto-run the SAFE (non-transmitting) check as soon as the modal opens.
  useEffect(() => { void run(false); }, [run]);

  const failed = report?.probes.filter((probe) => probe.status === "fail").length ?? 0;

  return (
    <Modal open onClose={onClose} width="max-w-xl" title={`Check radio setup — ${portId}`} footer={<>
      <Button variant="outline" size="sm" onClick={onClose}>Close</Button>
      <Button variant="outline" size="sm" onClick={() => run(false)} disabled={running} title="Re-run the safe check (no transmit)">
        <Icon name="restart" size={14} className={running && !ranFull ? "animate-spin" : undefined} /> Re-check
      </Button>
      <Button size="sm" onClick={() => run(true)} disabled={running} title="Also runs the transmitting probes — briefly keys the transmitter and perturbs TXDELAY">
        <Icon name="signal" size={14} className={running && ranFull ? "animate-spin" : undefined} /> Run full check (briefly transmits)
      </Button>
    </>}>
      <div className="space-y-4">
        <div className="flex items-start gap-2 rounded-md bg-muted/40 px-2.5 py-2 text-[11px] text-muted-foreground">
          <Icon name="info" size={13} className="mt-px shrink-0" />
          <span>Probes this port's TNC and radio for a healthy tuning setup. The safe check never transmits; the transmitting probes (TXDELAY, SDM, TNC↔radio pairing) show <span className="font-semibold">unknown</span> until you run the full check, which <span className="text-warning">briefly keys the transmitter</span>.</span>
        </div>

        {error && (
          <div className="flex items-start gap-2 rounded-md border border-warning/40 bg-warning/10 px-2.5 py-2 text-[11px] text-warning">
            <Icon name="alert" size={13} className="mt-px shrink-0" />
            <span>{error}</span>
          </div>
        )}

        {!report && running && !error && (
          <div className="py-6 text-center text-xs text-muted-foreground">Running checks…</div>
        )}

        {report && (
          <div className="space-y-1.5">
            {report.probes.map((probe) => {
              const s = STATUS[probe.status];
              return (
                <div key={probe.name} className="rounded-md border border-border bg-background/60 px-3 py-2">
                  <div className="flex items-center gap-2">
                    <Icon name={s.icon} size={14} className={s.tone} />
                    <span className="font-mono text-xs font-semibold">{probe.name}</span>
                    <Badge variant={s.badge} className="ml-auto">{probe.status}</Badge>
                  </div>
                  <p className="mt-1 pl-6 text-[11px] text-muted-foreground">{probe.detail}</p>
                  {probe.remedy && (
                    <p className="mt-1 pl-6 text-[11px] text-warning">→ {probe.remedy}</p>
                  )}
                </div>
              );
            })}
            <div className="pt-1 text-[11px] text-muted-foreground">
              {failed === 0
                ? <span className="text-success">No failed checks.</span>
                : <span className="text-danger">{failed} check{failed === 1 ? "" : "s"} failed — see the remedies above.</span>}
              {ranFull && <span> · full check ran (transmitted).</span>}
            </div>
          </div>
        )}
      </div>
    </Modal>
  );
}

// Drop-in trigger: a button that opens the doctor modal for one port.
export function DoctorButton({ portId, label, size, variant }: {
  portId: string;
  label?: string;
  size?: ButtonSize;
  variant?: ButtonVariant;
}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button size={size ?? "sm"} variant={variant ?? "ghost"} onClick={() => setOpen(true)} title="Check this port's radio setup">
        <Icon name="radio" size={14} />{label ?? "Check radio"}
      </Button>
      {open && <RadioDoctor portId={portId} onClose={() => setOpen(false)} />}
    </>
  );
}
