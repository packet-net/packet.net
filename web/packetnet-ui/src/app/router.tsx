import { useEffect, useState } from "react";
import { createBrowserRouter, Navigate } from "react-router-dom";
import { Shell } from "@/components/layout/shell";
import { useAuth } from "@/app/auth";
import { api, apiMode, Unauthorized } from "@/lib/api";
import { Login } from "@/screens/login";
import { Setup } from "@/screens/setup";
import { Dashboard } from "@/screens/dashboard";
import { Monitor } from "@/screens/monitor";
import { Sessions } from "@/screens/sessions";
import { Console } from "@/screens/console";
import { Apps } from "@/screens/apps";
import { AppFrame } from "@/screens/app-frame";
import { Routes } from "@/screens/routes";
import { Capabilities } from "@/screens/capabilities";
import { Ports } from "@/screens/ports";
import { HeadEnds } from "@/screens/headends";
import { Config } from "@/screens/config";
import { Users } from "@/screens/users";
import { LinkTuner } from "@/screens/link-tuner";
import { Waterfall } from "@/screens/waterfall";
import { LinkTroubleshoot } from "@/screens/link-troubleshoot";

// ============================================================
// The gate decides, on load, which of setup / login / app to show — and works
// whether the server has auth OFF or ON (we deploy the auth-wired UI to the lab
// before flipping the flag). Flow (see the task contract):
//   1. GET /setup/state. needsSetup → /setup.
//   2. Else probe GET /status with the stored token (if any):
//        200 → enter the app. Auth OFF 200s tokenless (enterAnonymous), so the app
//              just works with no login. Auth ON with a valid token also 200s.
//        401 → no/expired token under auth ON → /login.
// A 401 from ANY later call clears the token + returns to login (auth.tsx listens
// for the api.ts "unauthorized" event). Mock mode skips the probe and enters
// directly (no real auth → every screen renders for the vitest smoke test).
// ============================================================
function RequireAuth() {
  const auth = useAuth();
  const [phase, setPhase] = useState<"probing" | "app" | "login" | "setup">(
    () => (auth.authed ? "app" : "probing"),
  );

  useEffect(() => {
    if (auth.authed) { setPhase("app"); return; }

    // Mock mode has no real auth — enter straight away with a synthetic admin
    // session so every screen renders (and the smoke test stays green).
    if (apiMode === "mock") { auth.enterAnonymous("admin"); return; }

    let live = true;
    (async () => {
      try {
        const state = await api.setupState();
        if (!live) return;
        if (state.needsSetup) { setPhase("setup"); return; }
      } catch {
        // /setup/state is always open; a failure here (node down) shouldn't trap
        // the operator in setup — fall through to the status probe.
      }
      try {
        await api.status(); // probes the stored token (if any)
        if (!live) return;
        // 200: auth off (tokenless) or a valid token. If we already have a token
        // the provider state survives (carry the refresh token through too); otherwise
        // enter anonymously (auth-off lab).
        if (auth.token) { auth.login(auth.token, auth.scope ?? "admin", auth.username ?? "", auth.refreshToken); }
        else { auth.enterAnonymous("admin"); }
      } catch (e) {
        if (!live) return;
        // 401 (Unauthorized) → auth on, no/expired token → login. Any other error
        // (network/node down) also lands on login rather than a blank app.
        if (e instanceof Unauthorized) auth.logout();
        setPhase("login");
      }
    })();
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auth.authed]);

  if (phase === "setup") return <Navigate to="/setup" replace />;
  if (phase === "login") return <Navigate to="/login" replace />;
  if (phase === "app" || auth.authed) return <Shell />;
  return <BootSplash />;
}

// A quiet "checking…" splash while the gate probes the node.
function BootSplash() {
  return (
    <div className="flex min-h-screen items-center justify-center bg-background text-sm text-muted-foreground">
      <span className="inline-block h-2 w-2 animate-pulse rounded-full bg-primary" />
      <span className="ml-2">Connecting to node…</span>
    </div>
  );
}

// Bounce an already-authed operator away from the auth screens.
function AuthOnly({ children }: { children: React.ReactNode }) {
  const { authed } = useAuth();
  return authed ? <Navigate to="/" replace /> : <>{children}</>;
}

export const router = createBrowserRouter([
  { path: "/login", element: <AuthOnly><Login /></AuthOnly> },
  { path: "/setup", element: <AuthOnly><Setup /></AuthOnly> },
  {
    path: "/",
    element: <RequireAuth />,
    children: [
      { index: true, element: <Dashboard /> },
      { path: "monitor", element: <Monitor /> },
      { path: "sessions", element: <Sessions /> },
      { path: "console", element: <Console /> },
      { path: "apps", element: <Apps /> },
      // The in-panel app frame: an embedded/slot app's nav <Link> lands here (a standalone app is
      // a full navigation to /apps/{id}/ — the reverse-proxied server route — and never matches
      // this SPA route, since that path has a trailing slash + extra segments the router ignores).
      { path: "apps/:id", element: <AppFrame /> },
      { path: "routes", element: <Routes /> },
      { path: "capabilities", element: <Capabilities /> },
      { path: "ports", element: <Ports /> },
      { path: "headends", element: <HeadEnds /> },
      { path: "config", element: <Config /> },
      { path: "users", element: <Users /> },
      { path: "links", element: <LinkTroubleshoot /> },
      { path: "tools/tuner", element: <LinkTuner /> },
      { path: "tools/waterfall", element: <Waterfall /> },
      { path: "*", element: <Navigate to="/" replace /> },
    ],
  },
]);
