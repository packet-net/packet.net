// ============================================================
// First-run setup wizard (README §2). A 3-step stepper:
//   Station identity -> Create admin -> First port.
// Submits POST /setup { identity, admin, firstPort? }: one-shot, creating the
// first admin + applying the station identity. The endpoint returns the created
// admin (no token), so on success we send the operator to /login to sign in.
// Full-screen, centred (not wrapped in <Page>).
//
// The first-port step does NOT ask the operator to type a device path. It calls
// GET /setup/devices, which enumerates the node's serial devices and asks the plausible
// NinoTNCs among them for their firmware version, and offers the result as a picker, so
// a NinoTNC is chosen from a list that has already proved the node is talking to the
// modem. Typing a path by hand stays available for anything discovery cannot see.
// ============================================================
import { Fragment, useCallback, useEffect, useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, Field, Input, Select, Switch, Icon } from "@/components/ui";
import { Logo, ThemeToggle } from "@/components/layout/shell";
import { cn } from "@/lib/utils";
import { api, ConfigRejected } from "@/lib/api";
import type { PortConfig, SetupRequest, TransportConfig, AuthorableTransportKind, ModemScan, ModemScanDevice } from "@/lib/types";

function AuthFrame({ children }: { children: ReactNode }) {
  return (
    <div className="relative flex min-h-screen items-center justify-center overflow-hidden bg-background p-4">
      <div className="pointer-events-none absolute inset-0 opacity-[0.18]" style={{
        backgroundImage: "linear-gradient(hsl(var(--border)) 1px, transparent 1px), linear-gradient(90deg, hsl(var(--border)) 1px, transparent 1px)",
        backgroundSize: "44px 44px",
        maskImage: "radial-gradient(ellipse at center, black, transparent 72%)",
        WebkitMaskImage: "radial-gradient(ellipse at center, black, transparent 72%)",
      }} />
      <div className="absolute right-4 top-4"><ThemeToggle /></div>
      <div className="relative w-full max-w-sm">
        <div className="mb-6 flex flex-col items-center text-center">
          <Logo size={40} />
          <p className="mt-3 text-xs text-muted-foreground">amateur packet radio node</p>
        </div>
        {children}
      </div>
    </div>
  );
}

interface SetupData {
  callsign: string; alias: string; grid: string;
  username: string; password: string; confirm: string;
  addPort: boolean; portId: string; portKind: AuthorableTransportKind; device: string; baud: number;
}

const STEPS = ["Station identity", "Create admin", "First port"];
const MIN_PW = 8;

// A NinoTNC's USB-serial wire speed is fixed in its firmware. The operator never picks it, and a
// different number here would simply stop the modem answering, so the field is shown, not asked.
const NINO_WIRE_BAUD = 57600;

// The transport kinds the wizard offers, named the way an operator names them rather than the way
// the config file spells them. `serial-kiss` really is "any KISS TNC on a serial port"; the
// NinoTNC has its own entry because pdn drives it natively (modes, GETVER, TX completion). The
// other authorable kinds (axudp-multipoint, soundmodem) belong in the Ports editor: their setup
// does not fit two fields, and neither is a plausible first port on a fresh node.
const WIZARD_KINDS: { kind: AuthorableTransportKind; label: string }[] = [
  { kind: "nino-tnc", label: "NinoTNC" },
  { kind: "serial-kiss", label: "Generic KISS" },
  { kind: "kiss-tcp", label: "KISS over TCP" },
  { kind: "axudp", label: "AXUDP" },
];

// Serial-shaped kinds take a device path; the rest take host + port.
const isSerialKind = (k: AuthorableTransportKind) => k === "nino-tnc" || k === "serial-kiss";

// Build a PortConfig from the wizard's first-port fields. The wizard collects a
// transport kind + device + baud; map those to the right transport union member
// (host/port kinds reuse the two fields as host:port). Defaults keep the candidate
// valid: the operator can tune the rest later in Config.
function buildPort(d: SetupData): PortConfig {
  let transport: TransportConfig;
  switch (d.portKind) {
    case "nino-tnc": transport = { kind: "nino-tnc", device: d.device, baud: NINO_WIRE_BAUD, mode: 4 }; break;
    case "serial-kiss": transport = { kind: "serial-kiss", device: d.device, baud: d.baud }; break;
    case "kiss-tcp": transport = { kind: "kiss-tcp", host: d.device || "127.0.0.1", port: d.baud || 8001 }; break;
    case "axudp": transport = { kind: "axudp", host: d.device || "127.0.0.1", port: d.baud || 10093, localPort: d.baud || 10093 }; break;
    // The wizard's port-kind picker doesn't offer multipoint (its partner table doesn't
    // fit the simple first-port form), but the switch stays exhaustive over the kinds a form
    // can author (AuthorableTransportKind):
    // seed an empty peers table the operator fills in later from the Ports editor.
    case "axudp-multipoint": transport = { kind: "axudp-multipoint", localPort: d.baud || 10093, peers: [] }; break;
    // Same exhaustiveness note: the wizard doesn't offer the soundmodem (audio device,
    // mode and PTT choices belong in the Ports editor / config), but seed a sane default.
    case "soundmodem": transport = { kind: "soundmodem", device: d.device || "default", captureRate: 48000, mode: "afsk1200" }; break;
  }
  return { id: d.portId, enabled: true, transport, profile: null, ax25: null, kiss: null, beacon: null };
}

// One picker row's label: what the device is, then where it is. The identified NinoTNCs carry
// their firmware version, because that IS the proof the node just talked to the modem. Kept
// SHORT (the kernel name without /dev/) because the card is narrow and a clipped label is worse
// than a terse one; the full stable path the port will be bound to is shown under the picker.
function deviceLabel(d: ModemScanDevice): string {
  const where = d.kernelPath.replace(/^\/dev\//, "");
  if (d.kind === "nino-tnc") {
    return `NinoTNC${d.firmwareVersion ? ` ${d.firmwareVersion}` : ""} - ${where}`;
  }
  if (d.claimedBy) return `${where} - in use (${d.claimedBy})`;
  if (d.descriptor) return `${where} - ${d.descriptor}`;
  return where;
}

// The sentinel option that swaps the picker for a free-text device path. Discovery cannot see
// everything (a pty, an unusual /dev name, a device the node cannot open yet), and a first-run
// wizard must never be a dead end.
const MANUAL = " manual";

export function Setup() {
  const navigate = useNavigate();
  const [step, setStep] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [data, setData] = useState<SetupData>({
    callsign: "", alias: "", grid: "",
    username: "admin", password: "", confirm: "",
    addPort: true, portId: "vhf-1", portKind: "nino-tnc", device: "", baud: NINO_WIRE_BAUD,
  });
  const set = <K extends keyof SetupData>(k: K, v: SetupData[K]) => setData((d) => ({ ...d, [k]: v }));

  // The modem scan behind the device picker. null = not fetched yet.
  const [scan, setScan] = useState<ModemScan | null>(null);
  const [scanning, setScanning] = useState(false);
  const [manualDevice, setManualDevice] = useState(false);

  const runScan = useCallback(async () => {
    setScanning(true);
    const result = await api.setupDevices();
    setScan(result);
    setScanning(false);
    // Pre-select the most useful device so the common case needs no clicks at all: an identified
    // NinoTNC, else the first free device. Never overwrite a choice the operator already made.
    setData((d) => {
      if (d.device || !isSerialKind(d.portKind)) return d;
      const free = result.devices.filter((x) => !x.claimedBy);
      const pick = free.find((x) => x.kind === "nino-tnc") ?? free[0];
      return pick ? { ...d, device: pick.devicePath } : d;
    });
  }, []);

  // Scan once, when the operator first reaches the port step. Earlier would open serial ports for
  // a wizard they may never finish; later would mean showing them an empty picker first.
  useEffect(() => {
    if (step === 2 && scan === null && !scanning) void runScan();
  }, [step, scan, scanning, runScan]);

  const pwOk = data.password.length >= MIN_PW && data.password === data.confirm;
  const portOk = !data.addPort || !isSerialKind(data.portKind) || !!data.device.trim();
  const canNext = step === 0 ? !!data.callsign.trim() : step === 1 ? pwOk : true;

  // The row the operator has selected, if discovery found it. Drives the confirmation line.
  const selected = scan?.devices.find((d) => d.devicePath === data.device) ?? null;

  const finish = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    const payload: SetupRequest = {
      identity: {
        callsign: data.callsign.trim(),
        alias: data.alias.trim() || null,
        grid: data.grid.trim() || null,
      },
      admin: { username: data.username.trim(), password: data.password },
      firstPort: data.addPort ? buildPort(data) : null,
    };
    try {
      await api.setup(payload);
      // The endpoint returns no token (it creates the admin), so send the operator to
      // sign in with the credentials they just chose.
      navigate("/login", { replace: true });
    } catch (e) {
      setError(e instanceof ConfigRejected
        ? e.message
        : e instanceof Error ? e.message : "Setup failed.");
      setBusy(false);
    }
  };

  return (
    <AuthFrame>
      <Card className="overflow-hidden p-0">
        {/* stepper */}
        <div className="flex items-center gap-2 border-b border-border bg-muted/30 px-5 py-3">
          {STEPS.map((s, i) => (
            <Fragment key={s}>
              <div className="flex items-center gap-2">
                <span className={cn("grid h-6 w-6 place-items-center rounded-full text-xs font-semibold", i < step ? "bg-success text-success-foreground" : i === step ? "bg-primary text-primary-foreground" : "bg-muted text-muted-foreground")}>
                  {i < step ? <Icon name="check" size={13} /> : i + 1}
                </span>
                <span className={cn("hidden text-xs font-medium sm:inline", i === step ? "text-foreground" : "text-muted-foreground")}>{s}</span>
              </div>
              {i < STEPS.length - 1 && <div className="h-px flex-1 bg-border" />}
            </Fragment>
          ))}
        </div>

        <div className="p-6">
          <p className="mb-4 text-xs text-muted-foreground">First-run setup · creates the first administrator and applies your station identity.</p>

          {step === 0 && (
            <div className="space-y-4">
              <Field label="Callsign (required)" hint="SSID optional.">
                <Input value={data.callsign} onChange={(e) => set("callsign", e.target.value.toUpperCase())} placeholder="GB7AAA" className="font-mono" autoFocus />
              </Field>
              <div className="grid grid-cols-2 gap-3">
                <Field label="Alias"><Input value={data.alias} maxLength={6} onChange={(e) => set("alias", e.target.value.toUpperCase())} placeholder="MYNODE" className="font-mono" /></Field>
                <Field label="Locator"><Input value={data.grid} onChange={(e) => set("grid", e.target.value)} placeholder="AA00aa" className="font-mono" /></Field>
              </div>
            </div>
          )}

          {step === 1 && (
            <div className="space-y-4">
              <Field label="Admin username"><Input value={data.username} onChange={(e) => set("username", e.target.value)} className="font-mono" autoComplete="username" /></Field>
              <Field label="Password" hint={`Min ${MIN_PW} chars`}>
                <Input type="password" value={data.password} onChange={(e) => set("password", e.target.value)} placeholder="••••••••" autoComplete="new-password" />
              </Field>
              {/* The mismatch hint is ALWAYS rendered, just invisible when there is nothing to
                  say. A hint that appears and disappears changes the card's height, and a
                  vertically-centred card then jumps under the operator's cursor mid-typing.
                  The placeholder is a NON-BREAKING space on purpose: an ordinary space collapses
                  away, the paragraph gets no line box, and the height reservation silently does
                  nothing (which is exactly what it did on the first attempt at this fix). */}
              <Field
                label="Confirm password"
                hint={
                  <span className={cn(data.confirm && data.password !== data.confirm ? "text-danger" : "invisible")}>
                    {data.confirm && data.password !== data.confirm ? "Passwords don't match." : "\u00A0"}
                  </span>
                }
              >
                <Input type="password" value={data.confirm} onChange={(e) => set("confirm", e.target.value)} placeholder="••••••••" autoComplete="new-password" />
              </Field>
            </div>
          )}

          {step === 2 && (
            <div className="space-y-4">
              <button type="button" onClick={() => set("addPort", !data.addPort)} className="flex w-full items-center justify-between rounded-lg border border-border p-3">
                <div className="text-left"><p className="text-sm font-medium">Add a first port now</p><p className="text-xs text-muted-foreground">You can add more later.</p></div>
                <Switch checked={data.addPort} onChange={(v) => set("addPort", v)} />
              </button>
              {data.addPort && (
                <div className="space-y-3 rounded-lg border border-border p-3">
                  <div className="grid grid-cols-2 gap-3">
                    <Field label="Port id"><Input value={data.portId} onChange={(e) => set("portId", e.target.value)} className="font-mono" /></Field>
                    <Field label="Transport">
                      <Select
                        aria-label="Transport"
                        value={data.portKind}
                        onChange={(e) => {
                          const kind = e.target.value as AuthorableTransportKind;
                          setManualDevice(false);
                          setData((d) => ({
                            ...d,
                            portKind: kind,
                            // The two field pairs mean different things per kind, so reset them to
                            // that kind's own default rather than carrying a device path into a
                            // host box (or 57600 into a TCP port number).
                            device: isSerialKind(kind)
                              ? (scan?.devices.find((x) => !x.claimedBy && (kind !== "nino-tnc" || x.kind === "nino-tnc"))?.devicePath ?? "")
                              : "127.0.0.1",
                            baud: kind === "nino-tnc" ? NINO_WIRE_BAUD
                              : kind === "serial-kiss"
                                ? (scan?.devices.some((x) => !x.claimedBy && x.kind === "nino-tnc") ? NINO_WIRE_BAUD : 9600)
                                : kind === "kiss-tcp" ? 8001
                                  : 10093,
                          }));
                        }}
                      >
                        {WIZARD_KINDS.map((k) => <option key={k.kind} value={k.kind}>{k.label}</option>)}
                      </Select>
                    </Field>
                  </div>

                  {isSerialKind(data.portKind) ? (
                    <div className="space-y-3">
                      <div className="grid grid-cols-2 gap-3">
                        {/* Device spans the row: a modem's name plus where it is does not fit in
                            half a narrow card, and a clipped device name is exactly the thing an
                            operator needs to read. */}
                        <Field label="Device" className="col-span-2">
                          {manualDevice ? (
                            <Input aria-label="Device path" value={data.device} onChange={(e) => set("device", e.target.value)} placeholder="/dev/ttyACM0" className="font-mono" />
                          ) : (
                            <Select
                              aria-label="Device"
                              value={data.device}
                              onChange={(e) => {
                                if (e.target.value === MANUAL) { setManualDevice(true); set("device", ""); return; }
                                const chosen = e.target.value;
                                // A NinoTNC driven through the Generic KISS transport is still a
                                // NinoTNC on the wire: 57600 or it says nothing at all. Leaving
                                // the generic 9600 default there would build a port whose only
                                // symptom is silence.
                                const nino = scan?.devices.some((x) => x.devicePath === chosen && x.kind === "nino-tnc");
                                setData((d) => ({ ...d, device: chosen, baud: nino ? NINO_WIRE_BAUD : d.baud }));
                              }}
                            >
                              {/* Nothing chosen yet (still scanning, nothing found, or nothing
                                  free): an empty option keeps the control honest instead of
                                  showing the first row as though it had been picked. */}
                              {!data.device && <option value="">{scanning ? "Scanning..." : "Select a device..."}</option>}
                              {(scan?.devices ?? []).map((d) => (
                                <option key={d.devicePath} value={d.devicePath} disabled={!!d.claimedBy}>{deviceLabel(d)}</option>
                              ))}
                              <option value={MANUAL}>Other (type a path)...</option>
                            </Select>
                          )}
                        </Field>
                        <Field label={data.portKind === "nino-tnc" ? "USB wire speed" : "Baud"}>
                          {data.portKind === "nino-tnc" ? (
                            <div
                              data-testid="nino-baud-fixed"
                              className="flex h-9 items-center rounded-md border border-input bg-muted/40 px-3 font-mono text-sm text-muted-foreground"
                            >
                              {NINO_WIRE_BAUD} <span className="ml-1.5 text-[11px]">fixed</span>
                            </div>
                          ) : (
                            <Input aria-label="Baud" type="number" value={data.baud} onChange={(e) => set("baud", +e.target.value)} className="font-mono" />
                          )}
                        </Field>
                        <div className="flex items-end justify-end">
                          <Button variant="ghost" size="sm" type="button" disabled={scanning} onClick={() => { setManualDevice(false); void runScan(); }}>Rescan</Button>
                        </div>
                      </div>

                      <div className="space-y-1">
                        <p className="text-[11px] leading-4">
                          {scanning
                            ? <span className="text-muted-foreground">Looking for modems...</span>
                            : scan?.permissionDenied
                              ? <span className="text-danger">The node cannot open this machine's serial devices. Put its user in the dialout group with <span className="font-mono">sudo usermod -aG dialout packetnet</span>, restart pdn, then rescan.</span>
                              : selected?.kind === "nino-tnc" && data.portKind === "serial-kiss"
                                ? <span className="text-muted-foreground">This is a NinoTNC (firmware {selected.firmwareVersion ?? "unknown"}). Generic KISS will work at 57600, but the NinoTNC transport drives it properly: modem modes, TX completion, diagnostics.</span>
                                : selected?.kind === "nino-tnc"
                                  ? <span className="text-success">NinoTNC answered{selected.firmwareVersion ? `, firmware ${selected.firmwareVersion}` : ""}.</span>
                                  : data.portKind === "nino-tnc" && selected
                                    ? <span className="text-warning">This device did not answer as a NinoTNC{selected.probeError ? ` (${selected.probeError})` : ""}. You can still use it, but check the cable first.</span>
                                    : selected
                                      ? <span className="text-muted-foreground">Not identified. A generic KISS TNC has no way to announce itself.</span>
                                      : <span className="text-muted-foreground">No modem selected. Rescan to look again.</span>}
                        </p>
                        {/* The path the port is actually bound to. It is a by-id name whenever
                            udev gave an unambiguous one, and saying so here is the difference
                            between a port that survives a replug and one that silently moves to
                            the wrong modem. */}
                        {data.device && (
                          <p className="truncate font-mono text-[11px] text-muted-foreground" title={data.device}>{data.device}</p>
                        )}
                      </div>
                    </div>
                  ) : (
                    <div className="grid grid-cols-2 gap-3">
                      <Field label="Host">
                        <Input aria-label="Host" value={data.device} onChange={(e) => set("device", e.target.value)} className="font-mono" />
                      </Field>
                      <Field label="Port">
                        <Input aria-label="Port" type="number" value={data.baud} onChange={(e) => set("baud", +e.target.value)} className="font-mono" />
                      </Field>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}

          {error && (
            <div className="mt-4 flex items-start gap-2 rounded-md bg-danger/10 px-3 py-2 text-xs text-danger">
              <Icon name="info" size={14} className="mt-0.5 shrink-0" /> {error}
            </div>
          )}

          <div className="mt-6 flex items-center justify-between">
            <Button variant="ghost" size="sm" disabled={busy || step === 0} onClick={() => setStep(step - 1)}>Back</Button>
            {step < 2
              ? <Button size="sm" disabled={!canNext} onClick={() => setStep(step + 1)}>Continue <Icon name="chevRight" size={14} /></Button>
              : <Button size="sm" disabled={busy || !portOk} onClick={finish}><Icon name="check" size={14} /> {busy ? "Setting up..." : "Finish setup"}</Button>}
          </div>
        </div>
      </Card>
    </AuthFrame>
  );
}
