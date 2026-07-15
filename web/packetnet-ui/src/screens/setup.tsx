// ============================================================
// First-run setup wizard (README §2). A 3-step stepper:
//   Station identity → Create admin → First port.
// Submits POST /setup { identity, admin, firstPort? } — one-shot: creates the
// first admin + applies the station identity. The endpoint returns the created
// admin (no token), so on success we send the operator to /login to sign in.
// Full-screen, centred (not wrapped in <Page>).
// ============================================================
import { Fragment, useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, Field, Input, Select, Switch, Icon } from "@/components/ui";
import { Logo, ThemeToggle } from "@/components/layout/shell";
import { cn } from "@/lib/utils";
import { api, ConfigRejected } from "@/lib/api";
import { passkeysAvailable } from "@/lib/secureContext";
import type { PortConfig, SetupRequest, TransportConfig, TransportKind } from "@/lib/types";

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
  addPort: boolean; portId: string; portKind: TransportKind; device: string; baud: number;
}

const STEPS = ["Station identity", "Create admin", "First port"];
const MIN_PW = 8;

// Build a PortConfig from the wizard's first-port fields. The wizard collects a
// transport kind + device + baud; map those to the right transport union member
// (host/port kinds reuse the two fields as host:port). Defaults keep the candidate
// valid — the operator can tune the rest later in Config.
function buildPort(d: SetupData): PortConfig {
  let transport: TransportConfig;
  switch (d.portKind) {
    case "nino-tnc": transport = { kind: "nino-tnc", device: d.device, baud: d.baud, mode: 4 }; break;
    case "serial-kiss": transport = { kind: "serial-kiss", device: d.device, baud: d.baud }; break;
    case "kiss-tcp": transport = { kind: "kiss-tcp", host: d.device || "127.0.0.1", port: d.baud || 8001 }; break;
    case "axudp": transport = { kind: "axudp", host: d.device || "127.0.0.1", port: d.baud || 10093, localPort: d.baud || 10093 }; break;
    // The wizard's port-kind picker doesn't offer multipoint (its partner table doesn't
    // fit the simple first-port form), but the switch stays exhaustive over TransportKind:
    // seed an empty peers table the operator fills in later from the Ports editor.
    case "axudp-multipoint": transport = { kind: "axudp-multipoint", localPort: d.baud || 10093, peers: [] }; break;
    // Same exhaustiveness note: the wizard doesn't offer the soundmodem (audio device,
    // mode and PTT choices belong in the Ports editor / config), but seed a sane default.
    case "soundmodem": transport = { kind: "soundmodem", device: d.device || "default", captureRate: 48000, mode: "afsk1200" }; break;
  }
  return { id: d.portId, enabled: true, transport, profile: null, ax25: null, kiss: null, beacon: null };
}

export function Setup() {
  const navigate = useNavigate();
  const [step, setStep] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [data, setData] = useState<SetupData>({
    callsign: "", alias: "", grid: "",
    username: "admin", password: "", confirm: "",
    addPort: true, portId: "vhf-1", portKind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600,
  });
  const set = <K extends keyof SetupData>(k: K, v: SetupData[K]) => setData((d) => ({ ...d, [k]: v }));

  const pwOk = data.password.length >= MIN_PW && data.password === data.confirm;
  const canNext = step === 0 ? !!data.callsign.trim() : step === 1 ? pwOk : true;

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
      // The endpoint returns no token (it creates the admin) — send the operator to
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
              <Field label="Callsign (required)" hint="Your station's licensed callsign.">
                <Input value={data.callsign} onChange={(e) => set("callsign", e.target.value.toUpperCase())} placeholder="GB7RDG" className="font-mono" autoFocus />
              </Field>
              <div className="grid grid-cols-2 gap-3">
                <Field label="Alias"><Input value={data.alias} maxLength={6} onChange={(e) => set("alias", e.target.value.toUpperCase())} placeholder="RDGGW" className="font-mono" /></Field>
                <Field label="Locator"><Input value={data.grid} onChange={(e) => set("grid", e.target.value)} placeholder="IO91nl" className="font-mono" /></Field>
              </div>
            </div>
          )}

          {step === 1 && (
            <div className="space-y-4">
              <Field label="Admin username"><Input value={data.username} onChange={(e) => set("username", e.target.value)} className="font-mono" autoComplete="username" /></Field>
              <Field label="Password" hint={`Min ${MIN_PW} chars · hashed with Argon2id.`}>
                <Input type="password" value={data.password} onChange={(e) => set("password", e.target.value)} placeholder="••••••••" autoComplete="new-password" />
              </Field>
              <Field label="Confirm password" hint={data.confirm && data.password !== data.confirm ? "Passwords don't match." : undefined}>
                <Input type="password" value={data.confirm} onChange={(e) => set("confirm", e.target.value)} placeholder="••••••••" autoComplete="new-password" />
              </Field>
              {/* Only mention passkeys when this origin is a secure context — on a
                  plain-HTTP LAN node the ceremony can't run, so password + over-RF
                  TOTP are the auth methods (see network-access.md). */}
              <p className="text-[11px] text-muted-foreground">
                {passkeysAvailable()
                  ? "Passkeys (WebAuthn) can be enrolled later — coming soon."
                  : "Passkeys need HTTPS — reach this node over Tailscale or localhost to enrol them. Password login works here."}
              </p>
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
                      <Select value={data.portKind} onChange={(e) => set("portKind", e.target.value as TransportKind)}>
                        <option value="nino-tnc">nino-tnc</option>
                        <option value="kiss-tcp">kiss-tcp</option>
                        <option value="serial-kiss">serial-kiss</option>
                        <option value="axudp">axudp</option>
                      </Select>
                    </Field>
                  </div>
                  <div className="grid grid-cols-2 gap-3">
                    <Field label={data.portKind === "kiss-tcp" || data.portKind === "axudp" ? "Host" : "Device"}>
                      <Input value={data.device} onChange={(e) => set("device", e.target.value)} className="font-mono" />
                    </Field>
                    <Field label={data.portKind === "kiss-tcp" || data.portKind === "axudp" ? "Port" : "Baud"}>
                      <Input type="number" value={data.baud} onChange={(e) => set("baud", +e.target.value)} className="font-mono" />
                    </Field>
                  </div>
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
              : <Button size="sm" disabled={busy} onClick={finish}><Icon name="check" size={14} /> {busy ? "Setting up…" : "Finish setup"}</Button>}
          </div>
        </div>
      </Card>
    </AuthFrame>
  );
}
