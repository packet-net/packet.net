// Minimal client auth gate (mock). Production: the JWT from /auth/login (passkey
// or Argon2id password) gates these states; scopes (read/operate/admin) come off
// the token. For now a session flag drives setup → login → app.
import { createContext, useContext, useState, type ReactNode } from "react";

interface AuthState {
  authed: boolean;
  user: { name: string; role: string; scopes: string[] };
  login: () => void;
  logout: () => void;
}

const AuthContext = createContext<AuthState | null>(null);
const KEY = "pdn.authed";

export function AuthProvider({ children }: { children: ReactNode }) {
  const [authed, setAuthed] = useState(() => sessionStorage.getItem(KEY) === "1");
  const value: AuthState = {
    authed,
    user: { name: "tom", role: "admin", scopes: ["read", "operate", "admin"] },
    login: () => { sessionStorage.setItem(KEY, "1"); setAuthed(true); },
    logout: () => { sessionStorage.removeItem(KEY); setAuthed(false); },
  };
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
