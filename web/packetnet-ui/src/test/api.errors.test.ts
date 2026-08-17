// The error-message extraction in lib/api.ts (review item C029, packet.net#694).
//
// The node answers errors in two shapes: most endpoints use { error }, but anything that runs a
// candidate through the config validator - the app enable/disable and identity 422s among them -
// answers a ValidationProblem, { errors: [{ path, message }] }. errorMessage() read only `error`,
// so those reasons were dropped and the screen showed a bare "(422)" fallback.
//
// MODE is captured at module load, so each block stubs the env then dynamically imports a FRESH
// copy of the module (vi.resetModules), the same pattern api.auth.test.tsx uses.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

function installLocalStorage(initial: Record<string, string> = {}): Record<string, string> {
  const store: Record<string, string> = { ...initial };
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => { store[k] = v; },
    removeItem: (k: string) => { delete store[k]; },
    clear: () => { for (const k of Object.keys(store)) delete store[k]; },
  });
  return store;
}

async function loadLiveApi() {
  vi.resetModules();
  vi.stubEnv("VITE_API_MODE", "live");
  return import("@/lib/api");
}

function json(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

beforeEach(() => {
  const store = installLocalStorage();
  store["pdn.session"] = JSON.stringify({
    token: "access", refreshToken: "rt", username: "tom", scope: "admin",
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

describe("api error messages", () => {
  it("surfaces a 422 ValidationProblem's reasons on an app enable", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(
      { errors: [{ path: "apps.packages[0].callsign", message: "callsign M0LTE-3 is already bound" }] },
      422,
    )));

    const apiMod = await loadLiveApi();

    await expect(apiMod.api.appPackageEnable("bbs")).rejects.toThrow(
      /apps\.packages\[0\]\.callsign: callsign M0LTE-3 is already bound/,
    );
  });

  it("joins several validation errors on a set-identity 422", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json(
      {
        errors: [
          { path: "command", message: "verb 'ports' is reserved" },
          { path: "netromAlias", message: "alias must be 1-6 characters" },
        ],
      },
      422,
    )));

    const apiMod = await loadLiveApi();

    await expect(apiMod.api.appPackageSetIdentity("bbs", { command: "ports" })).rejects.toThrow(
      /verb 'ports' is reserved; netromAlias: alias must be 1-6 characters/,
    );
  });

  it("still prefers a plain { error } body when the server sends one", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ error: "package 'bbs' is broken" }, 409)));

    const apiMod = await loadLiveApi();

    await expect(apiMod.api.appPackageEnable("bbs")).rejects.toThrow("package 'bbs' is broken");
  });

  it("falls back to the status-code message when the body carries neither shape", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("", { status: 503 })));

    const apiMod = await loadLiveApi();

    await expect(apiMod.api.appPackageEnable("bbs")).rejects.toThrow(/503/);
  });
});
