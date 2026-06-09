// ============================================================
// Login (README §1) — passkey-first, centred card on a faint grid
// backdrop. Full-screen (not wrapped in <Page>); theme toggle top-right.
// ============================================================
import { useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, Field, Input, Icon } from "@/components/ui";
import { Logo, ThemeToggle } from "@/components/layout/shell";
import { useAuth } from "@/app/auth";

function AuthFrame({ children, footer }: { children: ReactNode; footer?: ReactNode }) {
  return (
    <div className="relative flex min-h-screen items-center justify-center overflow-hidden bg-background p-4">
      {/* quiet technical backdrop */}
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
        {footer}
      </div>
    </div>
  );
}

export function Login() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const [pw, setPw] = useState("");
  const [busy, setBusy] = useState(false);

  const signIn = () => { login(); navigate("/"); };
  const passkey = () => { setBusy(true); setTimeout(signIn, 700); };
  const password = () => { if (!pw) return; setBusy(true); setTimeout(signIn, 500); };

  return (
    <AuthFrame footer={<p className="mt-6 text-center text-[11px] text-muted-foreground">GB7RDG · 127.0.0.1:8080</p>}>
      <Card className="p-6">
        <h1 className="text-lg font-semibold">Sign in</h1>
        <p className="mt-1 text-sm text-muted-foreground">Authenticate to manage this node.</p>

        <Button className="mt-5 w-full" onClick={passkey} disabled={busy}>
          <Icon name="fingerprint" size={16} /> Continue with passkey
        </Button>

        <div className="my-4 flex items-center gap-3 text-[11px] uppercase tracking-wide text-muted-foreground">
          <div className="h-px flex-1 bg-border" />or password<div className="h-px flex-1 bg-border" />
        </div>

        <div className="space-y-3">
          <Field label="Username"><Input defaultValue="tom" className="font-mono" /></Field>
          <Field label="Password">
            <Input type="password" value={pw} onChange={(e) => setPw(e.target.value)} onKeyDown={(e) => e.key === "Enter" && password()} placeholder="••••••••" />
          </Field>
          <Button variant="outline" className="w-full" onClick={password} disabled={busy}>Sign in</Button>
        </div>
      </Card>
    </AuthFrame>
  );
}
