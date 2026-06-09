// ============================================================
// Users & on-air auth (README §10) — single-admin in this slice.
// Per-user card with two visually-separated auth worlds:
//   Web login (Password + Passkeys) and On-air auth (TOTP).
// Enrolling TOTP opens the scan → verify → recovery-codes flow.
// ============================================================
import { useEffect, useState, type ReactNode } from "react";
import { Page, PageHeader } from "@/components/layout/shell";
import { Button, Badge, Card, Label, Input, Modal } from "@/components/ui";
import { Icon, type IconName } from "@/components/icon";
import { cn } from "@/lib/utils";
import { api, useQuery } from "@/lib/api";
import type { User } from "@/lib/types";

interface UserRow extends User { totpEnrolled: boolean }

export function Users() {
  const { data, loading } = useQuery(api.users, []);
  const [users, setUsers] = useState<UserRow[]>([]);
  const [enroll, setEnroll] = useState<string | null>(null);

  // hydrate the editable view once the (mock) API resolves
  useEffect(() => {
    if (data) setUsers(data.map((u) => ({ ...u, totpEnrolled: false })));
  }, [data]);

  const setTotp = (name: string, val: boolean) =>
    setUsers((us) => us.map((u) => (u.name === name ? { ...u, totpEnrolled: val } : u)));

  return (
    <Page>
      <PageHeader title="Users" subtitle="Operators & access — single-admin in this slice" actions={
        <Button size="sm" variant="outline" disabled title="Multi-user is a later extension"><Icon name="plus" size={14} /> Add user</Button>
      } />

      {loading && users.length === 0 && (
        <Card className="p-4 text-sm text-muted-foreground">Loading users…</Card>
      )}

      <div className="space-y-3">
        {users.map((u) => (
          <Card key={u.name} className="p-4">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="flex items-center gap-3">
                <span className="grid h-9 w-9 place-items-center rounded-full bg-primary/15 text-sm font-semibold uppercase text-primary">{u.name.slice(0, 2)}</span>
                <div>
                  <div className="flex items-center gap-2"><span className="font-medium">{u.name}</span><Badge variant="default">{u.role}</Badge></div>
                  <p className="mt-0.5 text-xs text-muted-foreground">last login {u.lastLogin}</p>
                </div>
              </div>
              <div className="flex flex-wrap gap-1">{u.scopes.map((s) => <Badge key={s} variant="muted">{s}</Badge>)}</div>
            </div>

            <div className="mt-4 space-y-2 border-t border-border pt-4">
              <p className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Web login</p>
              <AuthMethod icon="key" title="Password" sub="Argon2id" enabled action={<Button variant="ghost" size="xs">Reset</Button>} />
              <AuthMethod icon="fingerprint" title="Passkeys" sub={u.passkeys > 0 ? `${u.passkeys} enrolled` : "none"} enabled={u.passkeys > 0} action={<Button variant="ghost" size="xs"><Icon name="fingerprint" size={12} /> Add passkey</Button>} />

              <p className="pt-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">On-air auth</p>
              <AuthMethod icon="signal" title="Authenticator (TOTP)" sub={u.totpEnrolled ? "6-digit code, enrolled" : "prove identity over a packet session"} enabled={u.totpEnrolled}
                action={u.totpEnrolled
                  ? <div className="flex items-center gap-1"><Button variant="ghost" size="xs" onClick={() => setEnroll(u.name)}>Re-enrol</Button><Button variant="ghost" size="xs" className="text-danger" onClick={() => setTotp(u.name, false)}>Remove</Button></div>
                  : <Button size="xs" onClick={() => setEnroll(u.name)}><Icon name="plus" size={12} /> Enrol</Button>} />
            </div>
          </Card>
        ))}
      </div>

      <div className="mt-4 flex items-start gap-2 rounded-lg border border-border bg-muted/30 p-4 text-sm text-muted-foreground">
        <Icon name="info" size={16} className="mt-0.5 shrink-0" />
        <div>
          <p className="font-medium text-foreground">Two worlds of auth</p>
          <p className="mt-0.5 text-xs"><strong className="text-foreground/80">Web login</strong> uses passkeys / password over HTTPS. <strong className="text-foreground/80">On-air auth</strong> uses a TOTP code, because when someone reaches the node over a plain packet session there's no browser — just a 6-digit code they can type. Full multi-user CRUD is a later extension.</p>
        </div>
      </div>

      <TotpEnroll userName={enroll} onClose={() => setEnroll(null)} onDone={() => { if (enroll) setTotp(enroll, true); setEnroll(null); }} />
    </Page>
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

// TOTP enrollment flow — scan → verify → recovery codes
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
