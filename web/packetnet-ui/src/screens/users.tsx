// ============================================================
// Users & on-air auth (README §10) — wired to the real /users API
// (usersList / userCreate / userDelete). Admin-only: the whole screen's mutating
// affordances (Add / Delete) hide or disable when the token's scope isn't admin —
// the server is the real gate, this is the light-touch UI mirror.
//
// Each user carries a single granted scope (read/operate/admin; admin⊃operate⊃read).
// Passkeys (node-passkeys): a user manages their OWN passkeys (enrol + list + delete)
// from their own row — the server scopes every WebAuthn call to the authenticated
// principal, so the "Add passkey" / list affordance only lights up on the signed-in
// user's row. The on-air TOTP enrolment flow below is still a design affordance (mock).
// ============================================================
import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Page, PageHeader } from "@/components/layout/shell";
import { Button, Badge, Card, Label, Input, Select, Field, Modal } from "@/components/ui";
import { Icon, type IconName } from "@/components/icon";
import { cn } from "@/lib/utils";
import { api, useQuery } from "@/lib/api";
import { useAuth, type Scope } from "@/app/auth";
import type { UserSummary, WebAuthnCredential } from "@/lib/types";

const MIN_PW = 8;
const SCOPES: Scope[] = ["read", "operate", "admin"];

export function Users() {
  const auth = useAuth();
  const isAdmin = auth.has("admin");
  const { data, loading, reload } = useQuery(api.usersList, []);
  const [enroll, setEnroll] = useState<string | null>(null);
  const [totp, setTotpState] = useState<Record<string, boolean>>({});
  const [adding, setAdding] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const users = data ?? [];
  const setTotp = (name: string, val: boolean) => setTotpState((m) => ({ ...m, [name]: val }));

  const remove = async (username: string) => {
    if (!isAdmin || busy) return;
    setBusy(username);
    setError(null);
    try {
      await api.userDelete(username);
      reload();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Delete failed.");
    } finally {
      setBusy(null);
    }
  };

  return (
    <Page>
      <PageHeader title="Users" subtitle="Operators & access" actions={
        <Button size="sm" variant="outline" disabled={!isAdmin} title={isAdmin ? "Add an operator" : "Requires admin"} onClick={() => setAdding(true)}>
          <Icon name="plus" size={14} /> Add user
        </Button>
      } />

      {!isAdmin && (
        <Card className="mb-3 flex items-start gap-2 p-4 text-sm text-muted-foreground">
          <Icon name="info" size={16} className="mt-0.5 shrink-0" />
          <p>User management requires the <strong className="text-foreground">admin</strong> scope. You can view operators but not add or remove them.</p>
        </Card>
      )}

      {error && (
        <Card className="mb-3 flex items-center gap-2 p-3 text-sm text-danger">
          <Icon name="info" size={16} /> {error}
        </Card>
      )}

      {loading && users.length === 0 && (
        <Card className="p-4 text-sm text-muted-foreground">Loading users…</Card>
      )}

      <div className="space-y-3">
        {users.map((u) => (
          <Card key={u.username} className="p-4">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="flex items-center gap-3">
                <span className="grid h-9 w-9 place-items-center rounded-full bg-primary/15 text-sm font-semibold uppercase text-primary">{u.username.slice(0, 2)}</span>
                <div>
                  <div className="flex items-center gap-2"><span className="font-medium">{u.username}</span><Badge variant="default">{u.scope}</Badge></div>
                  <p className="mt-0.5 text-xs text-muted-foreground">last login {fmtLastLogin(u.lastLoginUtc)}</p>
                </div>
              </div>
              <div className="flex items-center gap-2">
                <Badge variant="muted">{u.scope}</Badge>
                {isAdmin && (
                  <Button variant="ghost" size="xs" className="text-danger" disabled={busy === u.username} onClick={() => remove(u.username)}>
                    <Icon name="trash" size={12} /> {busy === u.username ? "Removing…" : "Remove"}
                  </Button>
                )}
              </div>
            </div>

            <div className="mt-4 space-y-2 border-t border-border pt-4">
              <p className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Web login</p>
              <AuthMethod icon="key" title="Password" sub="Argon2id" enabled action={<Button variant="ghost" size="xs" disabled={!isAdmin}>Reset</Button>} />
              <Passkeys isSelf={auth.username === u.username} />

              <p className="pt-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">On-air auth</p>
              <AuthMethod icon="signal" title="Authenticator (TOTP)" sub={totp[u.username] ? "6-digit code, enrolled" : "prove identity over a packet session"} enabled={!!totp[u.username]}
                action={totp[u.username]
                  ? <div className="flex items-center gap-1"><Button variant="ghost" size="xs" disabled={!isAdmin} onClick={() => setEnroll(u.username)}>Re-enrol</Button><Button variant="ghost" size="xs" className="text-danger" disabled={!isAdmin} onClick={() => setTotp(u.username, false)}>Remove</Button></div>
                  : <Button size="xs" disabled={!isAdmin} onClick={() => setEnroll(u.username)}><Icon name="plus" size={12} /> Enrol</Button>} />
            </div>
          </Card>
        ))}
      </div>

      <div className="mt-4 flex items-start gap-2 rounded-lg border border-border bg-muted/30 p-4 text-sm text-muted-foreground">
        <Icon name="info" size={16} className="mt-0.5 shrink-0" />
        <div>
          <p className="font-medium text-foreground">Two worlds of auth</p>
          <p className="mt-0.5 text-xs"><strong className="text-foreground/80">Web login</strong> uses a password or a passkey (WebAuthn) over a secure context. <strong className="text-foreground/80">On-air auth</strong> uses a TOTP code, because when someone reaches the node over a plain packet session there's no browser — just a 6-digit code they can type.</p>
        </div>
      </div>

      <AddUser open={adding} onClose={() => setAdding(false)} onDone={() => { setAdding(false); reload(); }} />
      <TotpEnroll userName={enroll} onClose={() => setEnroll(null)} onDone={() => { if (enroll) setTotp(enroll, true); setEnroll(null); }} />
    </Page>
  );
}

function fmtLastLogin(iso: string | null): string {
  if (!iso) return "never";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

// Add-operator modal — username + password (min length) + granted scope.
function AddUser({ open, onClose, onDone }: { open: boolean; onClose: () => void; onDone: (u: UserSummary) => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [scope, setScope] = useState<Scope>("read");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { if (open) { setUsername(""); setPassword(""); setScope("read"); setError(null); setBusy(false); } }, [open]);

  const valid = username.trim().length > 0 && password.length >= MIN_PW;
  const create = async () => {
    if (!valid || busy) return;
    setBusy(true);
    setError(null);
    try {
      const u = await api.userCreate(username.trim(), password, scope);
      onDone(u);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Create failed.");
      setBusy(false);
    }
  };

  return (
    <Modal open={open} onClose={onClose} title="Add operator" footer={
      <>
        <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
        <Button size="sm" disabled={!valid || busy} onClick={create}><Icon name="plus" size={14} /> {busy ? "Creating…" : "Create"}</Button>
      </>
    }>
      <div className="space-y-4">
        <Field label="Username"><Input value={username} onChange={(e) => setUsername(e.target.value)} className="font-mono" autoFocus /></Field>
        <Field label="Password" hint={`Min ${MIN_PW} chars · hashed with Argon2id.`}>
          <Input type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder="••••••••" autoComplete="new-password" />
        </Field>
        <Field label="Scope" hint="admin ⊃ operate ⊃ read — a user is granted the highest scope they need.">
          <Select value={scope} onChange={(e) => setScope(e.target.value as Scope)}>
            {SCOPES.map((s) => <option key={s} value={s}>{s}</option>)}
          </Select>
        </Field>
        {error && <div className="flex items-center gap-2 rounded-md bg-danger/10 px-3 py-2 text-xs text-danger"><Icon name="info" size={14} /> {error}</div>}
      </div>
    </Modal>
  );
}

// one authentication method row
function AuthMethod({ icon, title, sub, enabled, action }: {
  icon: IconName; title: string; sub: string; enabled?: boolean; action: ReactNode;
}) {
  return (
    <div className="flex items-center justify-between gap-3 rounded-lg border border-border p-3">
      <div className="flex min-w-0 items-center gap-3">
        <span className={cn("grid h-8 w-8 shrink-0 place-items-center rounded-md", enabled ? "bg-success/15 text-success" : "bg-muted text-muted-foreground")}><Icon name={icon} size={16} /></span>
        <div className="min-w-0">
          <p className="flex items-center gap-2 text-sm font-medium">{title} {enabled ? <Badge variant="success">enabled</Badge> : <Badge variant="muted">not set</Badge>}</p>
          <p className="truncate text-xs text-muted-foreground">{sub}</p>
        </div>
      </div>
      <div className="shrink-0">{action}</div>
    </div>
  );
}

// The passkeys row. A user manages their OWN passkeys (the server scopes every WebAuthn
// call to the authenticated principal), so the affordances only activate on the
// signed-in user's row (`isSelf`). Other rows show a static "passkeys" indicator. The
// "Add passkey" button runs a real WebAuthn enrolment ceremony (api.passkeyRegister);
// it is disabled outside a secure context (HTTPS / localhost) or in mock mode.
function Passkeys({ isSelf }: { isSelf: boolean }) {
  const [creds, setCreds] = useState<WebAuthnCredential[]>([]);
  const [loading, setLoading] = useState(isSelf);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const supported = api.webauthnSupported();

  const reload = useCallback(() => {
    if (!isSelf) return;
    setLoading(true);
    api.passkeyList()
      .then((c) => { setCreds(c); setError(null); })
      .catch((e) => setError(e instanceof Error ? e.message : "Could not load passkeys."))
      .finally(() => setLoading(false));
  }, [isSelf]);

  useEffect(() => { reload(); }, [reload]);

  const add = async () => {
    if (busy || !supported) return;
    setBusy(true);
    setError(null);
    try {
      await api.passkeyRegister();
      reload();
    } catch (e) {
      const aborted = e instanceof DOMException && (e.name === "NotAllowedError" || e.name === "AbortError");
      if (!aborted) setError(e instanceof Error ? e.message : "Passkey enrolment failed.");
    } finally {
      setBusy(false);
    }
  };

  const remove = async (id: string) => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await api.passkeyDelete(id);
      reload();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Delete failed.");
    } finally {
      setBusy(false);
    }
  };

  // Other users' rows: a static, non-actionable indicator (you only manage your own).
  if (!isSelf) {
    return <AuthMethod icon="fingerprint" title="Passkeys" sub="managed by the user themselves" enabled={false}
      action={<span className="text-[10px] text-muted-foreground">self-service</span>} />;
  }

  const enrolled = creds.length > 0;
  const sub = !supported ? "needs HTTPS or localhost"
    : loading ? "loading…"
    : enrolled ? `${creds.length} passkey${creds.length === 1 ? "" : "s"}`
    : "no passkeys yet";

  return (
    <div className="rounded-lg border border-border">
      <AuthMethod icon="fingerprint" title="Passkeys" sub={sub} enabled={enrolled}
        action={<Button size="xs" disabled={!supported || busy} onClick={add}
          title={supported ? "Enrol a passkey on this device" : "Passkeys need a secure context (HTTPS or localhost)"}>
          <Icon name="plus" size={12} /> {busy ? "Working…" : "Add passkey"}
        </Button>} />
      {error && <p className="px-3 pb-2 text-xs text-danger">{error}</p>}
      {enrolled && (
        <ul className="space-y-1 px-3 pb-3">
          {creds.map((c) => (
            <li key={c.id} className="flex items-center justify-between gap-2 rounded-md bg-muted/30 px-2.5 py-1.5 text-xs">
              <span className="min-w-0 truncate font-mono text-muted-foreground" title={c.id}>
                {c.transports ?? "passkey"} · added {fmtLastLogin(c.createdUtc)}
              </span>
              <button type="button" className="shrink-0 text-danger hover:underline" disabled={busy} onClick={() => remove(c.id)}>Remove</button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

// TOTP enrollment flow — scan → verify → recovery codes (client-side affordance;
// no backend yet).
const TOTP_SECRET = "KZXW6YTB OI6Q SESH";
const RECOVERY_CODES = ["8FK2-QP4M", "ZT9X-7HRN", "LM3C-W0AE", "V6BD-1KXP"];

function TotpEnroll({ userName, onClose, onDone }: { userName: string | null; onClose: () => void; onDone: () => void }) {
  const [step, setStep] = useState(0);
  const [code, setCode] = useState("");
  useEffect(() => { if (userName) { setStep(0); setCode(""); } }, [userName]);
  if (!userName) return null;

  const secret = TOTP_SECRET.replace(/ /g, "");
  const uri = `otpauth://totp/pdn:${userName}@GB7RDG?secret=${secret}&issuer=pdn%20GB7RDG&digits=6&period=30`;

  const footer = step === 0 ? (
    <>
      <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
      <Button size="sm" onClick={() => setStep(1)}>I've added it <Icon name="chevRight" size={14} /></Button>
    </>
  ) : step === 1 ? (
    <>
      <Button variant="outline" size="sm" onClick={() => setStep(0)}>Back</Button>
      <Button size="sm" disabled={code.replace(/\D/g, "").length !== 6} onClick={() => setStep(2)}><Icon name="check" size={14} /> Verify & enable</Button>
    </>
  ) : (
    <Button size="sm" onClick={onDone}><Icon name="check" size={14} /> Done</Button>
  );

  return (
    <Modal open onClose={onClose} width="max-w-md" title={`On-air authenticator — ${userName}`} footer={footer}>
      {step === 0 && (
        <div className="space-y-4">
          <p className="text-sm text-muted-foreground">Add this node to an authenticator app (Aegis, 1Password, Google Authenticator…). When this user connects to the node <strong className="text-foreground">on the air</strong>, the node asks for the current 6-digit code.</p>
          <div className="flex gap-4">
            <div className="grid h-32 w-32 shrink-0 place-items-center rounded-lg border border-border bg-muted/40 text-center">
              <div className="text-muted-foreground"><Icon name="radio" size={24} className="mx-auto" /><p className="mt-1 px-2 text-[10px] leading-tight">otpauth QR<br /><span className="text-muted-foreground/60">scan in app</span></p></div>
            </div>
            <div className="min-w-0 flex-1 space-y-2">
              <div>
                <Label>Manual key</Label>
                <div className="mt-1 flex items-center gap-2 rounded-md border border-border bg-background/60 px-2.5 py-1.5">
                  <span className="flex-1 font-mono text-sm tracking-wider">{TOTP_SECRET}</span>
                  <button type="button" className="text-muted-foreground hover:text-foreground" title="Copy" onClick={() => navigator.clipboard?.writeText(secret)}><Icon name="copy" size={14} /></button>
                </div>
              </div>
              <p className="break-all font-mono text-[10px] leading-snug text-muted-foreground/70">{uri}</p>
            </div>
          </div>
        </div>
      )}
      {step === 1 && (
        <div className="space-y-4">
          <p className="text-sm text-muted-foreground">Enter the current 6-digit code from your app to confirm it's set up correctly.</p>
          <Input value={code} onChange={(e) => setCode(e.target.value.replace(/\D/g, "").slice(0, 6))} placeholder="123456" inputMode="numeric"
            className="text-center font-mono text-2xl tracking-[0.4em]" autoFocus />
        </div>
      )}
      {step === 2 && (
        <div className="space-y-4">
          <div className="flex items-center gap-2 rounded-md bg-success/10 px-3 py-2.5 text-sm text-success"><Icon name="check" size={16} /> On-air authenticator enabled for {userName}.</div>
          <div>
            <Label>Recovery codes</Label>
            <p className="mb-2 mt-0.5 text-xs text-muted-foreground">Store these safely — each can be used once if the authenticator is unavailable.</p>
            <div className="grid grid-cols-2 gap-2 rounded-md border border-border bg-muted/30 p-3 font-mono text-sm">
              {RECOVERY_CODES.map((c) => <span key={c}>{c}</span>)}
            </div>
          </div>
        </div>
      )}
    </Modal>
  );
}
