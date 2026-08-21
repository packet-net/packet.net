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
import { passkeysAvailable } from "@/lib/secureContext";
import { cn } from "@/lib/utils";

function AuthFrame({ children }: { children: ReactNode }) {
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
      </div>
    </div>
  );
}

// Where to land after a successful sign-in: the `?next=` the server's app-gateway re-auth
// redirect set (a same-site SPA route like /apps/bbs), else the dashboard. Open-redirect
// guard: only a single-leading-slash relative path is honoured — reject `//host`
// (protocol-relative), any scheme, and backslashes; everything else falls back to "/".
function safeNext(): string {
  try {
    const n = new URLSearchParams(window.location.search).get("next");
    if (n && n.startsWith("/") && !n.startsWith("//") && !n.includes("\\") && !n.includes(":")) {
      return n;
    }
  } catch { /* malformed URL — fall through */ }
  return "/";
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
  // present; on a plain-HTTP LAN node the affordance is HIDDEN (not just disabled) and
  // password login carries the flow. lib/secureContext is the single secure-context
  // probe (network-access.md S1); api.passkeyAssert still no-ops/throws in mock mode.
  const passkeySupported = passkeysAvailable();

  const submit = async (e?: FormEvent) => {
    e?.preventDefault();
    if (!username || !pw || busy) return;
    setBusy(true);
    setError(null);
    try {
      const res = await api.login(username, pw);
      auth.login(res.token, res.scopes, res.username, res.refreshToken);
      navigate(safeNext(), { replace: true });
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
      // Use the server-resolved username, NOT the typed box: a discoverable passkey
      // sign-in leaves the box empty, and the identity comes from the signed credential.
      auth.login(res.token, res.scopes, res.username, res.refreshToken);
      navigate(safeNext(), { replace: true });
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

  // No footer identity line. It used to read "GB7RDG · 127.0.0.1:8080" - a design-mock
  // callsign and a loopback address, printed on every real node's sign-in page (#691 C041).
  // Nothing pdn serves before authentication carries the node's identity: /healthz answers
  // {status:"ok"}, /setup/state answers {needsSetup}, and /config + /status are both gated.
  // An honest footer needs the node to publish its callsign pre-auth, which is an
  // auth-surface decision (#693); until then the page says nothing rather than something
  // false.
  return (
    <AuthFrame>
      <Card className="p-6">
        <h1 className="text-lg font-semibold">Sign in</h1>
        <p className="mt-1 text-sm text-muted-foreground">Authenticate to manage this node.</p>

        {/* Passwordless WebAuthn is offered ONLY where the browser can actually run the
            ceremony (a secure context: HTTPS or localhost). Where it can't, the affordance
            is simply absent - no explanatory hint. The node has no way to know how the
            operator SHOULD have reached it (Tailscale, a reverse proxy, a real certificate,
            localhost), so the old "reach this node over Tailscale or localhost" line was
            guessing on the operator's behalf; password (+ over-RF TOTP) login below is the
            path either way, and it is right there. */}
        {passkeySupported && (
          <>
            <Button className="mt-5 w-full" onClick={passkey} disabled={passkeyBusy}
              title="Sign in with a passkey">
              <Icon name="fingerprint" size={16} /> {passkeyBusy ? "Waiting for passkey…" : "Continue with passkey"}
            </Button>

            <div className="my-4 flex items-center gap-3 text-[11px] uppercase tracking-wide text-muted-foreground">
              <div className="h-px flex-1 bg-border" />or password<div className="h-px flex-1 bg-border" />
            </div>
          </>
        )}

        <form className={cn("space-y-3", !passkeySupported && "mt-5")} onSubmit={submit}>
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
