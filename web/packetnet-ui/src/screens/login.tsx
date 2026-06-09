// ============================================================
// Login (README §1) — centred card on a faint grid backdrop. Real submit:
// api.login → auth.login(token, scope, username) → into the app. A 401 shows an
// inline generic error (the server never says which of username/password was
// wrong). The "Continue with passkey" button runs a real WebAuthn passwordless
// assertion (api.passkeyAssert → the SAME token pair a password login mints) when
// the origin is a secure context (HTTPS or localhost); otherwise it stays disabled
// (we never fake a ceremony — in mock mode it's a no-op disabled affordance).
// ============================================================
import { useState, type FormEvent, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, Field, Input, Icon } from "@/components/ui";
import { Logo, ThemeToggle } from "@/components/layout/shell";
import { useAuth } from "@/app/auth";
import { api, Unauthorized } from "@/lib/api";

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
  const auth = useAuth();
  const navigate = useNavigate();
  const [username, setUsername] = useState("");
  const [pw, setPw] = useState("");
  const [busy, setBusy] = useState(false);
  const [passkeyBusy, setPasskeyBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Passkeys only run in a secure context (HTTPS or localhost) with the WebAuthn API
  // present; otherwise the button stays disabled rather than failing on tap. Mock mode
  // reports false too (no ceremony to run — we don't fake one).
  const passkeySupported = api.webauthnSupported();

  const submit = async (e?: FormEvent) => {
    e?.preventDefault();
    if (!username || !pw || busy) return;
    setBusy(true);
    setError(null);
    try {
      const res = await api.login(username, pw);
      auth.login(res.token, res.scopes, username, res.refreshToken);
      navigate("/", { replace: true });
    } catch (err) {
      setError(err instanceof Unauthorized
        ? "Invalid username or password."
        : err instanceof Error ? err.message : "Sign-in failed.");
      setBusy(false);
    }
  };

  // Passwordless WebAuthn assertion. We pass the typed username when present (it scopes
  // the allow-list); when blank, the browser offers any discoverable passkey for the RP.
  const passkey = async () => {
    if (passkeyBusy || !passkeySupported) return;
    setPasskeyBusy(true);
    setError(null);
    try {
      const res = await api.passkeyAssert(username.trim() || undefined);
      auth.login(res.token, res.scopes, username.trim(), res.refreshToken);
      navigate("/", { replace: true });
    } catch (err) {
      // A user-cancelled / aborted ceremony (NotAllowedError) is not an error to shout
      // about — just stop. Anything else surfaces inline.
      const aborted = err instanceof DOMException && (err.name === "NotAllowedError" || err.name === "AbortError");
      if (!aborted) {
        setError(err instanceof Unauthorized
          ? "That passkey was not recognised."
          : err instanceof Error ? err.message : "Passkey sign-in failed.");
      }
      setPasskeyBusy(false);
    }
  };

  return (
    <AuthFrame footer={<p className="mt-6 text-center text-[11px] text-muted-foreground">GB7RDG · 127.0.0.1:8080</p>}>
      <Card className="p-6">
        <h1 className="text-lg font-semibold">Sign in</h1>
        <p className="mt-1 text-sm text-muted-foreground">Authenticate to manage this node.</p>

        {/* Passwordless WebAuthn. Enabled only in a secure context (HTTPS / localhost). */}
        <Button className="mt-5 w-full" onClick={passkey}
          disabled={!passkeySupported || passkeyBusy}
          title={passkeySupported ? "Sign in with a passkey" : "Passkeys need a secure context (HTTPS or localhost)"}>
          <Icon name="fingerprint" size={16} /> {passkeyBusy ? "Waiting for passkey…" : "Continue with passkey"}
        </Button>
        {!passkeySupported && (
          <p className="mt-1 text-center text-[10px] text-muted-foreground">passkeys need HTTPS or localhost</p>
        )}

        <div className="my-4 flex items-center gap-3 text-[11px] uppercase tracking-wide text-muted-foreground">
          <div className="h-px flex-1 bg-border" />or password<div className="h-px flex-1 bg-border" />
        </div>

        <form className="space-y-3" onSubmit={submit}>
          <Field label="Username">
            <Input value={username} onChange={(e) => setUsername(e.target.value)} className="font-mono" autoComplete="username" autoFocus />
          </Field>
          <Field label="Password">
            <Input type="password" value={pw} onChange={(e) => setPw(e.target.value)} placeholder="••••••••" autoComplete="current-password" />
          </Field>
          {error && (
            <div className="flex items-center gap-2 rounded-md bg-danger/10 px-3 py-2 text-xs text-danger">
              <Icon name="info" size={14} /> {error}
            </div>
          )}
          <Button type="submit" variant="outline" className="w-full" disabled={busy || !username || !pw}>
            {busy ? "Signing in…" : "Sign in"}
          </Button>
        </form>
      </Card>
    </AuthFrame>
  );
}
