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
// user's row. The over-RF sysop code (TOTP, node-sysop-totp) is the same shape: a user
// enrols / inspects / removes their OWN rolling code from their own row, wired to the real
// /auth/totp/enroll endpoints.
// ============================================================
import { useCallback, useEffect, useState, type ReactNode } from "react";
import { QRCodeSVG } from "qrcode.react";
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
  const [adding, setAdding] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const users = data ?? [];

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
              <OverRfTotp isSelf={auth.username === u.username} />
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

// The over-RF sysop code (TOTP) row. Like Passkeys, a user manages their OWN code (the
// server scopes every /auth/totp/enroll call to the authenticated principal), so the
// affordances only activate on the signed-in user's row (`isSelf`); other rows show a
// static self-service indicator. "Enrol authenticator" opens the real begin→confirm flow;
// the enrolled state (callsign + Remove) reflects GET /auth/totp/enroll.
function OverRfTotp({ isSelf }: { isSelf: boolean }) {
  const supported = api.totpSupported();
  const [enrolled, setEnrolled] = useState(false);
  const [callsign, setCallsign] = useState<string | null>(null);
  const [loading, setLoading] = useState(isSelf && supported);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [enrolling, setEnrolling] = useState(false);

  const reload = useCallback(() => {
    if (!isSelf || !supported) return;
    setLoading(true);
    api.totpState()
      .then((s) => { setEnrolled(s.enrolled); setCallsign(s.callsign); setError(null); })
      .catch((e) => setError(e instanceof Error ? e.message : "Could not load enrolment state."))
      .finally(() => setLoading(false));
  }, [isSelf, supported]);

  useEffect(() => { reload(); }, [reload]);

  const remove = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await api.totpRemove();
      reload();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Remove failed.");
    } finally {
      setBusy(false);
    }
  };

  // Other users' rows: a static, non-actionable indicator (you only manage your own).
  if (!isSelf) {
    return <AuthMethod icon="signal" title="Authenticator (TOTP)" sub="managed by the user themselves" enabled={false}
      action={<span className="text-[10px] text-muted-foreground">self-service</span>} />;
  }

  const sub = !supported ? "needs a live node"
    : loading ? "loading…"
    : enrolled ? `enrolled · ${callsign ?? "callsign set"}`
    : "prove identity over a packet session";

  return (
    <div className="rounded-lg border border-border">
      <AuthMethod icon="signal" title="Authenticator (TOTP)" sub={sub} enabled={enrolled}
        action={enrolled
          ? <Button variant="ghost" size="xs" className="text-danger" disabled={!supported || busy} onClick={remove}>
              <Icon name="trash" size={12} /> {busy ? "Removing…" : "Remove"}
            </Button>
          : <Button size="xs" disabled={!supported || busy || loading} onClick={() => setEnrolling(true)}
              title={supported ? "Enrol an authenticator for over-RF sysop access" : "Over-RF enrolment needs a live node"}>
              <Icon name="plus" size={12} /> Enrol authenticator
            </Button>} />
      {error && <p className="px-3 pb-2 text-xs text-danger">{error}</p>}
      <TotpEnroll open={enrolling} onClose={() => setEnrolling(false)} onDone={() => { setEnrolling(false); reload(); }} />
    </div>
  );
}

// The over-RF code enrolment flow, wired to the real endpoints:
//   begin → render the otpauth URI as a QR (+ base32 fallback) + a callsign + code input
//         → complete (verify the code, bind the callsign).
// The username is the authenticated principal on the server side — never sent.
function TotpEnroll({ open, onClose, onDone }: { open: boolean; onClose: () => void; onDone: () => void }) {
  const [secret, setSecret] = useState<string | null>(null);
  const [uri, setUri] = useState<string | null>(null);
  const [callsign, setCallsign] = useState("");
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // On open, kick off begin (mint the secret server-side). Reset everything on close.
  useEffect(() => {
    if (!open) { setSecret(null); setUri(null); setCallsign(""); setCode(""); setError(null); setBusy(false); return; }
    let alive = true;
    setBusy(true);
    api.totpEnrollBegin()
      .then((r) => { if (alive) { setSecret(r.secret); setUri(r.otpauthUri); setError(null); } })
      .catch((e) => { if (alive) setError(e instanceof Error ? e.message : "Could not start enrolment."); })
      .finally(() => { if (alive) setBusy(false); });
    return () => { alive = false; };
  }, [open]);

  const valid = callsign.trim().length > 0 && code.replace(/\D/g, "").length === 6 && secret !== null;

  const confirm = async () => {
    if (!valid || busy) return;
    setBusy(true);
    setError(null);
    try {
      await api.totpEnrollComplete(code.replace(/\D/g, ""), callsign.trim().toUpperCase());
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Enrolment failed.");
      setBusy(false);
    }
  };

  return (
    <Modal open={open} onClose={onClose} width="max-w-md" title="Over-RF sysop code (TOTP)" footer={
      <>
        <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
        <Button size="sm" disabled={!valid || busy} onClick={confirm}><Icon name="check" size={14} /> {busy ? "Confirming…" : "Confirm"}</Button>
      </>
    }>
      <div className="space-y-4">
        <p className="text-sm text-muted-foreground">Add this node to an authenticator app (Aegis, 1Password, Google Authenticator…). When you connect to the node <strong className="text-foreground">on the air</strong>, the node asks for the current 6-digit code to elevate your session.</p>
        <div className="flex gap-4">
          <div className="grid h-32 w-32 shrink-0 place-items-center rounded-lg border border-border bg-white p-1">
            {uri
              ? <QRCodeSVG value={uri} size={120} marginSize={0} />
              : <div className="text-center text-muted-foreground"><Icon name="radio" size={24} className="mx-auto" /><p className="mt-1 px-2 text-[10px] leading-tight">{busy ? "generating…" : "otpauth QR"}</p></div>}
          </div>
          <div className="min-w-0 flex-1 space-y-2">
            <div>
              <Label>Manual key</Label>
              <div className="mt-1 flex items-center gap-2 rounded-md border border-border bg-background/60 px-2.5 py-1.5">
                <span className="flex-1 break-all font-mono text-xs tracking-wider">{secret ?? "—"}</span>
                {secret && <button type="button" className="shrink-0 text-muted-foreground hover:text-foreground" title="Copy" onClick={() => navigator.clipboard?.writeText(secret)}><Icon name="copy" size={14} /></button>}
              </div>
            </div>
            <p className="text-[11px] text-muted-foreground">Scan the QR, or type the key into your app by hand.</p>
          </div>
        </div>
        <Field label="Callsign" hint="The callsign you'll present over the air — bound to your account.">
          <Input value={callsign} onChange={(e) => setCallsign(e.target.value.toUpperCase())} placeholder="G7XYZ" className="font-mono uppercase" />
        </Field>
        <Field label="Current code" hint="The 6-digit code from your app, to confirm it's set up correctly.">
          <Input value={code} onChange={(e) => setCode(e.target.value.replace(/\D/g, "").slice(0, 6))} placeholder="123456" inputMode="numeric"
            className="text-center font-mono text-2xl tracking-[0.4em]" />
        </Field>
        {error && <div className="flex items-center gap-2 rounded-md bg-danger/10 px-3 py-2 text-xs text-danger"><Icon name="info" size={14} /> {error}</div>}
      </div>
    </Modal>
  );
}
