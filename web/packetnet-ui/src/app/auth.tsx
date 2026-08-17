// ============================================================
// Client auth state: the access JWT gates the app. It comes from POST /auth/login
// (Argon2id password) or from the passkey ceremony (POST /auth/webauthn/assert/begin
// then /complete, which returns the same token pair). A user is granted exactly ONE
// scope (read/operate/admin) and the implication admin⊃operate⊃read is resolved
// here by `has()`, mirroring the server's AuthScopes.Satisfies rank model.
//
// Persistence: localStorage - the session survives browser restarts and is shared across
// tabs; the refresh-token rotation (+ family revocation on reuse) is what bounds the blast
// radius of a stolen pair. (This paragraph used to trail off into a leftover sentence about
// a short-lived PER-TAB token and "swap KEY's backing to localStorage", describing a
// sessionStorage design that was already gone; #691 C041.)
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
import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from "react";

/** The granted scope on a token, lowest→highest privilege. */
export type Scope = "read" | "operate" | "admin";

/** Rank for the admin⊃operate⊃read implication (matches server AuthScopes.Rank). */
const RANK: Record<string, number> = { read: 1, operate: 2, admin: 3 };

interface Session {
  /** The JWT, or null in auth-off / pre-login states (the app still works tokenless). */
  token: string | null;
  /** The opaque refresh token (one-time-use, rotated on each /auth/refresh), or null
   *  in auth-off / pre-login / mock states. Persisted alongside the access token so a
   *  reload can silently renew an expired access token without a re-login. */
  refreshToken: string | null;
  /** The login name, or null when we entered without logging in (auth off / mock). */
  username: string | null;
  /** The granted scope, or null if unknown. `has()` treats null as "satisfies nothing". */
  scope: Scope | null;
}

interface AuthState extends Session {
  /** Whether the gate has let the operator into the app (token-backed or tokenless). */
  authed: boolean;
  /** Record a successful login (access + refresh token + granted scope + username) and
   *  enter the app. */
  login: (token: string, scope: string, username: string, refreshToken: string | null) => void;
  /** Enter the app on an ALREADY-PERSISTED session, re-reading localStorage and writing
   *  nothing back (see the implementation for why the gate must not re-save). */
  resume: () => void;
  /** Enter the app WITHOUT a token — auth-off probe succeeded, or mock mode. */
  enterAnonymous: (scope?: Scope) => void;
  /** Clear the session (best-effort revoking the refresh token server-side) and drop
   *  back to the login gate. */
  logout: () => void;
  /** Whether the current scope satisfies `required` under admin⊃operate⊃read. */
  has: (required: Scope) => boolean;
}

const AuthContext = createContext<AuthState | null>(null);
const KEY = "pdn.session";

const EMPTY_SESSION: Session = { token: null, refreshToken: null, username: null, scope: null };

/** The event lib/api.ts dispatches when any call comes back 401. */
export const UNAUTHORIZED_EVENT = "pdn:unauthorized";

/** Dispatched after a successful PROACTIVE (tab-focus) token refresh — the pdn_at gateway
 *  cookie has just been re-issued, so a slot iframe whose earlier cookie-authed /apps/* load
 *  may have 401'd/blanked can reload to re-request with the fresh cookie. Consumed by the
 *  AppFrame (screens/app-frame). Carries no payload — it's purely a "the cookie is fresh now"
 *  signal, fired only on an actual rotation so it can't drive a reload loop. */
export const SESSION_REFRESHED_EVENT = "pdn:session-refreshed";

// lib/api.ts registers a best-effort server-side revoke here (POST /auth/logout with
// the stored refresh token). A registration hook — rather than a static import of
// api.ts — keeps auth.tsx free of an import cycle (api.ts already imports from here).
let revokeOnLogout: (() => void) | null = null;
/** Called once by lib/api.ts at module load to wire the logout revoke. */
export function setLogoutRevoker(fn: () => void): void {
  revokeOnLogout = fn;
}

// lib/api.ts also registers its proactive (tab-focus) refresh here — same registration-hook
// pattern, same no-import-cycle reason. The provider calls it when the tab regains focus; the
// function renews the access token IFF it's near/at expiry (and a refresh token is held),
// reusing api.ts's single locked/deduped rotation, and resolves the freshly-persisted token
// pair (or null when no renew happened). It NEVER throws and NEVER logs out on its own.
let refreshIfExpiringSoon: (() => Promise<{ token: string; refreshToken: string | null } | null>) | null = null;
/** Called once by lib/api.ts at module load to wire the proactive focus-refresh. */
export function setFocusRefresher(
  fn: () => Promise<{ token: string; refreshToken: string | null } | null>,
): void {
  refreshIfExpiringSoon = fn;
}

function load(): Session {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return EMPTY_SESSION;
    const s = JSON.parse(raw) as Session;
    return {
      token: s.token ?? null,
      refreshToken: s.refreshToken ?? null,
      username: s.username ?? null,
      scope: s.scope ?? null,
    };
  } catch {
    return EMPTY_SESSION;
  }
}

function save(s: Session): void {
  try {
    if (s.token) localStorage.setItem(KEY, JSON.stringify(s));
    else localStorage.removeItem(KEY);
  } catch {
    /* private-mode / quota — non-fatal, the in-memory state still drives the app */
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  // Rehydrate any persisted token so a reload stays logged in; the gate still
  // probes it for validity (an expired token 401s the probe → relogin).
  const [session, setSession] = useState<Session>(load);
  const [authed, setAuthed] = useState(false);

  // Latest session for the focus-refresh effect to read WITHOUT re-binding the listener on
  // every token rotation (the listener attaches once; the ref keeps it current).
  const sessionRef = useRef(session);
  sessionRef.current = session;

  const logout = () => {
    // Best-effort server-side revoke of the refresh-token family (POST /auth/logout
    // via the registered revoker), then clear local state. The revoke reads the token
    // straight from localStorage, so it must run BEFORE we clear it.
    try { revokeOnLogout?.(); } catch { /* logout must always clear locally */ }
    save(EMPTY_SESSION);
    setSession(EMPTY_SESSION);
    setAuthed(false);
  };

  // A 401 from anywhere (lib/api.ts) clears the session and drops to the gate.
  useEffect(() => {
    const onUnauthorized = () => logout();
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
  }, []);

  // Proactive refresh on tab-focus (Tom's "flip back to the tab after a while" trigger).
  // When the tab becomes visible (or the window regains focus), silently renew the access
  // token IFF it's near/at expiry, so the next navigation — including a slot iframe's
  // cookie-authed /apps/* load — rides a fresh token instead of 401'ing. Guards:
  //   - only when we hold a token-backed session (a real refresh token to rotate);
  //   - debounced so a burst of focus/visibility events fires at most one check per window;
  //   - the renew itself is api.ts's single locked/deduped rotation, so it can't double-spend
  //     the one-time-use refresh token even alongside an in-flight 401 refresh;
  //   - on success we adopt the rotated pair into React state (localStorage is already updated
  //     by api.ts); a failed/again-unneeded refresh is a silent no-op — we NEVER log out here
  //     (the next real request's 401 path owns that decision).
  useEffect(() => {
    let debounce: ReturnType<typeof setTimeout> | null = null;
    let disposed = false;

    const maybeRefresh = () => {
      if (document.visibilityState === "hidden") return;   // focus without visibility — skip
      if (!sessionRef.current.token || !sessionRef.current.refreshToken) return;
      if (!refreshIfExpiringSoon) return;
      if (debounce) clearTimeout(debounce);
      debounce = setTimeout(() => {
        void refreshIfExpiringSoon!().then((next) => {
          if (disposed || !next) return;
          const s: Session = { ...sessionRef.current, token: next.token, refreshToken: next.refreshToken };
          setSession(s);
          // The pdn_at gateway cookie was re-issued by the same /auth/refresh — tell any open
          // slot iframe to reload so it re-requests /apps/* with the fresh cookie. Fired ONLY
          // on an actual rotation (next != null), so it can never loop on a no-op focus.
          window.dispatchEvent(new Event(SESSION_REFRESHED_EVENT));
        });
      }, 250);
    };

    window.addEventListener("focus", maybeRefresh);
    document.addEventListener("visibilitychange", maybeRefresh);
    return () => {
      disposed = true;
      if (debounce) clearTimeout(debounce);
      window.removeEventListener("focus", maybeRefresh);
      document.removeEventListener("visibilitychange", maybeRefresh);
    };
  }, []);

  const value: AuthState = {
    ...session,
    authed,
    login: (token, scope, username, refreshToken) => {
      const s: Session = { token, refreshToken, username, scope: (scope as Scope) ?? null };
      save(s);
      setSession(s);
      setAuthed(true);
    },
    resume: () => {
      // Enter the app on the session that is ALREADY persisted: no save(). The gate calls
      // this after its /status probe, and that probe may have silently rotated the token pair
      // (api.ts's 401 path writes the new pair to localStorage only, never to React state).
      // Re-saving the mount-time copy would put the expired JWT and the CONSUMED one-time-use
      // refresh token back, so the next renew replays it: a doubled 401+retry inside the
      // server's 10 s reuse leeway, and a burnt token family (logged out) past it (#702 C035).
      // So: re-read localStorage and adopt whatever is there now.
      const stored = load();
      if (stored.token) setSession(stored);
      setAuthed(true);
    },
    enterAnonymous: (scope: Scope = "admin") => {
      // No token (auth off, or mock): enter with the given effective scope. Default
      // admin so a tokenless lab node (or mock) exposes every action — the server
      // is the real gate either way.
      const s: Session = { token: null, refreshToken: null, username: null, scope };
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
