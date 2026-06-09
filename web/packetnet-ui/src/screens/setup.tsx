// ============================================================
// First-run setup wizard (README §2) — reached via a one-time
// /setup?token=… link printed to the node log. A 3-step stepper:
//   Station identity → Create admin → First port.
// Full-screen, centred (not wrapped in <Page>).
// ============================================================
import { Fragment, useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, Field, Input, Select, Switch, Icon } from "@/components/ui";
import { Logo, ThemeToggle } from "@/components/layout/shell";
import { cn } from "@/lib/utils";

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
  username: string; password: string; passkey: boolean;
  addPort: boolean; portId: string; portKind: string; device: string; baud: number;
}

const STEPS = ["Station identity", "Create admin", "First port"];

export function Setup() {
  const navigate = useNavigate();
  const [step, setStep] = useState(0);
  const [data, setData] = useState<SetupData>({
    callsign: "", alias: "", grid: "",
    username: "admin", password: "", passkey: false,
    addPort: true, portId: "vhf-1", portKind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600,
  });
  const set = <K extends keyof SetupData>(k: K, v: SetupData[K]) => setData((d) => ({ ...d, [k]: v }));

  const canNext = step === 0 ? !!data.callsign : step === 1 ? (data.password.length >= 8 || data.passkey) : true;
  const finish = () => navigate("/login");

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
          <p className="mb-4 text-xs text-muted-foreground">First-run setup · reached via a one-time link printed to the node log.</p>

          {step === 0 && (
            <div className="space-y-4">
              <Field label="Callsign (required)" hint="Your station's licensed callsign.">
                <Input value={data.callsign} onChange={(e) => set("callsign", e.target.value.toUpperCase())} placeholder="GB7RDG" className="font-mono" autoFocus />
              </Field>
              <div className="grid grid-cols-2 gap-3">
                <Field label="Alias"><Input value={data.alias} onChange={(e) => set("alias", e.target.value.toUpperCase())} placeholder="RDGGW" className="font-mono" /></Field>
                <Field label="Locator"><Input value={data.grid} onChange={(e) => set("grid", e.target.value)} placeholder="IO91nl" className="font-mono" /></Field>
              </div>
            </div>
          )}

          {step === 1 && (
            <div className="space-y-4">
              <Field label="Admin username"><Input value={data.username} onChange={(e) => set("username", e.target.value)} className="font-mono" /></Field>
              <Field label="Password" hint="Min 8 chars · hashed with Argon2id."><Input type="password" value={data.password} onChange={(e) => set("password", e.target.value)} placeholder="••••••••" /></Field>
              <button type="button" onClick={() => set("passkey", !data.passkey)} className={cn("flex w-full items-center gap-3 rounded-lg border p-3 text-left transition-colors", data.passkey ? "border-primary bg-primary/5" : "border-border hover:bg-accent")}>
                <Icon name="fingerprint" size={18} className={data.passkey ? "text-primary" : "text-muted-foreground"} />
                <div className="flex-1"><p className="text-sm font-medium">Enrol a passkey</p><p className="text-xs text-muted-foreground">WebAuthn · optional, recommended</p></div>
                <Switch checked={data.passkey} onChange={(v) => set("passkey", v)} />
              </button>
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
                      <Select value={data.portKind} onChange={(e) => set("portKind", e.target.value)}>
                        <option value="nino-tnc">nino-tnc</option>
                        <option value="kiss-tcp">kiss-tcp</option>
                        <option value="serial-kiss">serial-kiss</option>
                        <option value="axudp">axudp</option>
                      </Select>
                    </Field>
                  </div>
                  <div className="grid grid-cols-2 gap-3">
                    <Field label="Device"><Input value={data.device} onChange={(e) => set("device", e.target.value)} className="font-mono" /></Field>
                    <Field label="Baud"><Input type="number" value={data.baud} onChange={(e) => set("baud", +e.target.value)} className="font-mono" /></Field>
                  </div>
                </div>
              )}
            </div>
          )}

          <div className="mt-6 flex items-center justify-between">
            <Button variant="ghost" size="sm" onClick={() => (step === 0 ? finish() : setStep(step - 1))}>{step === 0 ? "Skip" : "Back"}</Button>
            {step < 2
              ? <Button size="sm" disabled={!canNext} onClick={() => setStep(step + 1)}>Continue <Icon name="chevRight" size={14} /></Button>
              : <Button size="sm" onClick={finish}><Icon name="check" size={14} /> Finish setup</Button>}
          </div>
        </div>
      </Card>
    </AuthFrame>
  );
}
