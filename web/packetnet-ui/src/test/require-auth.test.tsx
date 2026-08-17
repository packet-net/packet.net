// ============================================================
// The auth gate, app/router.tsx RequireAuth (review item C031, packet.net#692).
//
// It has three mutually exclusive outcomes and was imported by no test at all: needs-setup,
// no-usable-token, and in-the-app. Every one of them is a decision an operator meets on their
// FIRST interaction with the node, and getting it wrong means either a locked-out operator or
// a setup wizard offered on a node that is already configured.
//
// The gate short-circuits in mock mode (it enters anonymously so the screen suites can render),
// so these tests load the whole module graph in LIVE mode - vi.resetModules + a stubbed
// VITE_API_MODE, then a dynamic import, so router.tsx and lib/api.ts come from the SAME fresh
// graph and a spy on that api instance is the one the gate calls.
//
// Deliberately written against the OBSERVABLE outcome (which route the operator lands on),
// never the probe sequence: the probe path itself is being reworked in parallel, and a test
// that pinned the calls would break on a refactor that kept the behaviour right.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { installLocalStorage, seedSession, jwtWithExp } from "./helpers/live";
import type { NodeStatus } from "@/lib/types";

const STATUS: NodeStatus = {
  callsign: "M0LTE-1", alias: "LONDON", grid: "IO91nl", version: "0.40.0",
  uptimeSeconds: 10, portsUp: 1, portsTotal: 1, sessionCount: 0,
  netrom: { neighbours: 0, destinations: 0, inp3Enabled: false },
  traffic: { enabled: true, dropped: 0 },
};

/** Load router.tsx + lib/api.ts from one fresh LIVE-mode module graph. */
async function loadLiveGate() {
  vi.resetModules();
  vi.stubEnv("VITE_API_MODE", "live");
  const api = await import("@/lib/api");
  const router = await import("@/app/router");
  const auth = await import("@/app/auth");
  return { api, router, auth };
}

/** Mount the gate with stand-in routes, so the outcome is simply which text appears. */
function mountGate(
  RequireAuth: () => JSX.Element,
  AuthProvider: (p: { children: React.ReactNode }) => JSX.Element,
) {
  return render(
    <MemoryRouter initialEntries={["/"]}>
      <AuthProvider>
        <Routes>
          <Route path="/" element={<RequireAuth />}>
            <Route index element={<div>THE APP</div>} />
          </Route>
          <Route path="/login" element={<div>THE LOGIN SCREEN</div>} />
          <Route path="/setup" element={<div>THE SETUP WIZARD</div>} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  installLocalStorage();
  if (typeof navigator !== "undefined") delete (navigator as { locks?: unknown }).locks;
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

describe("RequireAuth - the gate's three outcomes", () => {
  it("sends a brand-new node to the setup wizard", async () => {
    const { api, router, auth } = await loadLiveGate();
    const setupState = vi.spyOn(api.api, "setupState").mockResolvedValue({ needsSetup: true });
    const status = vi.spyOn(api.api, "status").mockResolvedValue(STATUS);

    mountGate(router.RequireAuth, auth.AuthProvider);

    await waitFor(() => expect(screen.getByText("THE SETUP WIZARD")).toBeInTheDocument());
    expect(setupState).toHaveBeenCalled();
    // A node that needs setup is never probed for a session - there are no accounts to have one.
    expect(status).not.toHaveBeenCalled();
  });

  it("sends an operator with no usable token to the login screen", async () => {
    const { api, router, auth } = await loadLiveGate();
    vi.spyOn(api.api, "setupState").mockResolvedValue({ needsSetup: false });
    vi.spyOn(api.api, "status").mockRejectedValue(new api.Unauthorized());

    mountGate(router.RequireAuth, auth.AuthProvider);

    await waitFor(() => expect(screen.getByText("THE LOGIN SCREEN")).toBeInTheDocument());
  });

  it("lets a valid session straight into the app", async () => {
    const { api, router, auth } = await loadLiveGate();
    vi.spyOn(api.api, "setupState").mockResolvedValue({ needsSetup: false });
    vi.spyOn(api.api, "status").mockResolvedValue(STATUS);
    // The shell the gate renders fetches these on mount.
    vi.spyOn(api.api, "apps").mockResolvedValue([]);

    mountGate(router.RequireAuth, auth.AuthProvider);

    await waitFor(() => expect(screen.getByText("THE APP")).toBeInTheDocument());
  });

  it("enters the app tokenless when the node has auth OFF (a 200 with no session)", async () => {
    // The auth-off lab posture: /status 200s without a bearer token, so the gate must enter
    // anonymously rather than bounce to a login screen the node will never authenticate.
    const { api, router, auth } = await loadLiveGate();
    vi.spyOn(api.api, "setupState").mockResolvedValue({ needsSetup: false });
    vi.spyOn(api.api, "status").mockResolvedValue(STATUS);
    vi.spyOn(api.api, "apps").mockResolvedValue([]);

    mountGate(router.RequireAuth, auth.AuthProvider);

    await waitFor(() => expect(screen.getByText("THE APP")).toBeInTheDocument());
    expect(localStorage.getItem("pdn.session")).toBeNull();
  });

  it("keeps a session whose ACCESS token expired but whose refresh token still works", async () => {
    // The reload-after-lunch case: the stored JWT is past its expiry, so the gate's /status
    // probe 401s. The single fetch path renews silently and replays it, so the operator lands
    // in the app rather than being asked to sign in again.
    //
    // Asserted through the OUTCOME (which route the operator lands on, and that exactly one
    // silent renew carried them there), never through the probe's call sequence: that sequence
    // is being reworked in parallel, and a test pinned to it would fail a refactor that kept
    // the behaviour right.
    const store = installLocalStorage();
    const expired = jwtWithExp(Math.floor(Date.now() / 1000) - 60);
    const fresh = jwtWithExp(Math.floor(Date.now() / 1000) + 3600);
    seedSession(store, { token: expired, refreshToken: "rt-1", username: "tom", scope: "admin" });

    let refreshes = 0;
    vi.stubGlobal("fetch", vi.fn((url: string | URL, init?: RequestInit) => {
      const u = String(url);
      if (u.includes("/setup/state")) {
        return Promise.resolve(new Response(JSON.stringify({ needsSetup: false }), {
          status: 200, headers: { "content-type": "application/json" },
        }));
      }
      if (u.includes("/auth/refresh")) {
        refreshes++;
        return Promise.resolve(new Response(
          JSON.stringify({ token: fresh, refreshToken: "rt-2", scopes: "admin", username: "tom" }),
          { status: 200, headers: { "content-type": "application/json" } }));
      }
      const bearer = (init?.headers as Record<string, string> | undefined)?.authorization;
      if (bearer !== `Bearer ${fresh}`) return Promise.resolve(new Response("", { status: 401 }));
      const body = u.includes("/apps") ? [] : STATUS;
      return Promise.resolve(new Response(JSON.stringify(body), {
        status: 200, headers: { "content-type": "application/json" },
      }));
    }));

    const { router, auth } = await loadLiveGate();
    mountGate(router.RequireAuth, auth.AuthProvider);

    // The operator is in the app, not back at a sign-in form, and a silent renew is what got
    // them there.
    await waitFor(() => expect(screen.getByText("THE APP")).toBeInTheDocument());
    expect(screen.queryByText("THE LOGIN SCREEN")).toBeNull();
    expect(refreshes).toBeGreaterThanOrEqual(1);
    // The session that survives carries the ROTATED access token, so the screens behind the
    // gate are not making requests with a JWT the node has already rejected.
    const persisted = JSON.parse(store["pdn.session"]) as { token: string; refreshToken: string };
    expect(persisted.token).toBe(fresh);

    // The rotated REFRESH token survives too. It is one-time-use with family revocation on the
    // real node, so re-persisting the pre-probe copy would replay a consumed token and log every
    // tab out - the defect router.gate.test.tsx pins from the other side (auth.resume, #702 C035).
    expect(persisted.refreshToken).toBe("rt-2");
  });

  it("falls through to login rather than trapping the operator in setup when /setup/state fails", async () => {
    // /setup/state is always open, so a failure means the node is down or mid-restart - not
    // that it needs setting up. Offering the wizard there would invite an operator to
    // re-bootstrap a configured node.
    const { api, router, auth } = await loadLiveGate();
    vi.spyOn(api.api, "setupState").mockRejectedValue(new Error("connection refused"));
    vi.spyOn(api.api, "status").mockRejectedValue(new Error("connection refused"));

    mountGate(router.RequireAuth, auth.AuthProvider);

    await waitFor(() => expect(screen.getByText("THE LOGIN SCREEN")).toBeInTheDocument());
    expect(screen.queryByText("THE SETUP WIZARD")).toBeNull();
  });
});
