// ============================================================
// Client auth state — the JWT from POST /auth/login (Argon2id password; passkey
// deferred) gates the app. A user is granted exactly ONE scope (read/operate/
// admin) and the implication admin⊃operate⊃read is resolved here by `has()`,
// mirroring the server's AuthScopes.Satisfies rank model.
//
// Persistence: sessionStorage (per-tab, cleared when the tab closes) — a control
// panel for a node you actively manage, not a "remember me" consumer app, so a
// short-lived per-tab token is the safer default. Swap KEY's backing to
// localStorage if cross-tab persistence is ever wanted.
//
// Works in BOTH server modes (the gate, in router.tsx, decides which):
//   - auth OFF  → requests 200 tokenless; the gate probes /status, gets 200,
//                 and enters the app with no token (token === null, but authed).
//   - auth ON   → an unauthorised probe/call 401s; the gate / the 401 handler
//                 drive the operator to /login.
// In mock mode there is no real auth: the gate enters directly with a synthetic
// admin session so every screen renders (the vitest smoke test relies on this).
//
// 401 handling: lib/api.ts owns the single fetch path; on a 401 it dispatches a
// window "pdn:unauthorized" event. The provider listens and runs logout() →
// token cleared, gate falls back to /login. (An event keeps api.ts free of a
// React-context import — it's plain TS.)
import { createContext, useContext, useEffect, useState, type ReactNode } from "react";

/** The granted scope on a token, lowest→highest privilege. */
export type Scope = "read" | "operate" | "admin";

/** Rank for the admin⊃operate⊃read implication (matches server AuthScopes.Rank). */
const RANK: Record<string, number> = { read: 1, operate: 2, admin: 3 };

interface Session {
  /** The JWT, or null in auth-off / pre-login states (the app still works tokenless). */
  token: string | null;
  /** The login name, or null when we entered without logging in (auth off / mock). */
  username: string | null;
  /** The granted scope, or null if unknown. `has()` treats null as "satisfies nothing". */
  scope: Scope | null;
}

interface AuthState extends Session {
  /** Whether the gate has let the operator into the app (token-backed or tokenless). */
  authed: boolean;
  /** Record a successful login (token + granted scope + username) and enter the app. */
  login: (token: string, scope: string, username: string) => void;
  /** Enter the app WITHOUT a token — auth-off probe succeeded, or mock mode. */
  enterAnonymous: (scope?: Scope) => void;
  /** Clear the session and drop back to the login gate. */
  logout: () => void;
  /** Whether the current scope satisfies `required` under admin⊃operate⊃read. */
  has: (required: Scope) => boolean;
}

const AuthContext = createContext<AuthState | null>(null);
const KEY = "pdn.session";

/** The event lib/api.ts dispatches when any call comes back 401. */
export const UNAUTHORIZED_EVENT = "pdn:unauthorized";

function load(): Session {
  try {
    const raw = sessionStorage.getItem(KEY);
    if (!raw) return { token: null, username: null, scope: null };
    const s = JSON.parse(raw) as Session;
    return { token: s.token ?? null, username: s.username ?? null, scope: s.scope ?? null };
  } catch {
    return { token: null, username: null, scope: null };
  }
}

function save(s: Session): void {
  try {
    if (s.token) sessionStorage.setItem(KEY, JSON.stringify(s));
    else sessionStorage.removeItem(KEY);
  } catch {
    /* private-mode / quota — non-fatal, the in-memory state still drives the app */
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  // Rehydrate any persisted token so a reload stays logged in; the gate still
  // probes it for validity (an expired token 401s the probe → relogin).
  const [session, setSession] = useState<Session>(load);
  const [authed, setAuthed] = useState(false);

  const logout = () => {
    save({ token: null, username: null, scope: null });
    setSession({ token: null, username: null, scope: null });
    setAuthed(false);
  };

  // A 401 from anywhere (lib/api.ts) clears the session and drops to the gate.
  useEffect(() => {
    const onUnauthorized = () => logout();
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
  }, []);

  const value: AuthState = {
    ...session,
    authed,
    login: (token, scope, username) => {
      const s: Session = { token, username, scope: (scope as Scope) ?? null };
      save(s);
      setSession(s);
      setAuthed(true);
    },
    enterAnonymous: (scope: Scope = "admin") => {
      // No token (auth off, or mock): enter with the given effective scope. Default
      // admin so a tokenless lab node (or mock) exposes every action — the server
      // is the real gate either way.
      const s: Session = { token: null, username: null, scope };
      setSession(s);
      setAuthed(true);
    },
    logout,
    has: (required) => {
      const granted = session.scope;
      const needed = RANK[required] ?? 0;
      return needed > 0 && (RANK[granted ?? ""] ?? 0) >= needed;
    },
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
