// The auth gate (app/router.tsx RequireAuth) on the already-have-a-token path, in LIVE mode.
//
// The bug (#702 C035): AuthProvider loads the persisted pair into React state ONCE at mount,
// and api.ts reads/writes tokens straight from localStorage — so when the gate's GET /status
// probe 401s and silently rotates, React never hears about it. The gate then called
// auth.login(auth.token, …, auth.refreshToken) with the MOUNT-TIME values, and login() save()s
// them: the expired JWT and the consumed one-time-use refresh token went back into
// localStorage, to be replayed by the next renew (a doubled 401+retry inside the server's 10 s
// reuse leeway; a burnt token family, i.e. logged out, past it). The fix is auth.resume(): read
// the persisted session back, write nothing.
//
// MODE is captured at module load, so this stubs the env and dynamically imports a FRESH module
// graph (vi.resetModules), the api.auth.test.tsx idiom. The Shell is stubbed out — RequireAuth
// renders it on success and it would drag every screen's queries into this test.
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

async function loadLiveGate() {
  vi.resetModules();
  vi.stubEnv("VITE_API_MODE", "live");
  vi.doMock("@/components/layout/shell", async (importOriginal) => ({
    ...(await importOriginal<typeof import("@/components/layout/shell")>()),
    Shell: () => <div data-testid="shell">app</div>,
  }));
  const apiMod = await import("@/lib/api");
  const { AuthProvider } = await import("@/app/auth");
  const { RequireAuth } = await import("@/app/router");
  return { apiMod, AuthProvider, RequireAuth };
}

afterEach(() => {
  localStorage.clear();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.doUnmock("@/components/layout/shell");
  vi.restoreAllMocks();
});

describe("RequireAuth — the stored-token probe path", () => {
  it("keeps the pair the probe rotated, and the next authFetch carries it", async () => {
    localStorage.setItem("pdn.session", JSON.stringify({
      token: "old.access", refreshToken: "rt-1", username: "tom", scope: "operate",
    }));

    // The gate's two calls, with a silent rotation in the middle of the second:
    //   /setup/state → 200 · /status → 401 · /auth/refresh → 200 (rotated pair) · /status → 200
    // Anything the app asks for afterwards gets a bare 200 {}.
    const calls: { url: string; headers: Record<string, string> }[] = [];
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, headers: (init?.headers ?? {}) as Record<string, string> });
      if (url.includes("/setup/state")) return Promise.resolve(jsonResponse({ needsSetup: false }));
      if (url.includes("/auth/refresh")) {
        return Promise.resolve(jsonResponse({
          token: "new.access", refreshToken: "rt-2", scopes: "operate", username: "tom",
        }));
      }
      if (url.includes("/status")) {
        const authorization = ((init?.headers ?? {}) as Record<string, string>).authorization;
        return Promise.resolve(authorization === "Bearer old.access"
          ? new Response("", { status: 401 })
          : jsonResponse({ ok: true }));
      }
      return Promise.resolve(jsonResponse({}));
    });
    vi.stubGlobal("fetch", fetchMock);

    const { apiMod, AuthProvider, RequireAuth } = await loadLiveGate();
    render(
      <MemoryRouter>
        <AuthProvider><RequireAuth /></AuthProvider>
      </MemoryRouter>,
    );

    // The gate entered the app (the probe succeeded on the retry).
    await waitFor(() => expect(screen.getByTestId("shell")).toBeInTheDocument());

    // The rotated pair is still the persisted one — the gate did not write the pre-probe
    // pair back over it.
    const persisted = JSON.parse(localStorage.getItem("pdn.session") ?? "{}") as {
      token: string; refreshToken: string; username: string; scope: string;
    };
    expect(persisted.token).toBe("new.access");
    expect(persisted.refreshToken).toBe("rt-2");
    // …and the rest of the session survived the resume.
    expect(persisted.username).toBe("tom");
    expect(persisted.scope).toBe("operate");

    // The next call off the gate carries the ROTATED access token (under the bug it would
    // have re-sent "old.access" and burned another refresh).
    calls.length = 0;
    await apiMod.api.status();
    expect(calls[0].headers.authorization).toBe("Bearer new.access");

    // Exactly one rotation happened in the whole flow.
    expect(fetchMock.mock.calls.filter((c) => String(c[0]).includes("/auth/refresh"))).toHaveLength(1);
  });

  it("enters the app tokenless when there is no stored session (auth off)", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("/setup/state")) return Promise.resolve(jsonResponse({ needsSetup: false }));
      return Promise.resolve(jsonResponse({ ok: true }));
    });
    vi.stubGlobal("fetch", fetchMock);

    const { AuthProvider, RequireAuth } = await loadLiveGate();
    render(
      <MemoryRouter>
        <AuthProvider><RequireAuth /></AuthProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByTestId("shell")).toBeInTheDocument());
    // Nothing was persisted — an anonymous entry must not invent a session.
    expect(localStorage.getItem("pdn.session")).toBeNull();
  });
});
