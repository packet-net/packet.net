// ============================================================
// Shared seams for the LIVE-mode (VITE_API_MODE=live) api.ts suites.
//
// lib/api.ts captures MODE from import.meta.env at module load, so a live-mode test has
// to stub the env and then dynamically import a FRESH copy of the module. That pattern
// (loadLiveApi) started in api.auth.test.tsx and was copy-pasted into api.errors and
// api.stream; it lives here now so the new contract/live suites share one copy.
//
// FakeEventSource is the same idea for SSE: jsdom has no EventSource, so the fake IS the
// seam - it records every instance openStream() opens (with the URL, and therefore the
// token, it opened with) and lets a test drive open/message/CLOSED by hand.
// ============================================================
import { vi } from "vitest";

/** A fresh live-mode copy of lib/api (MODE is read at import time). */
export async function loadLiveApi(): Promise<typeof import("@/lib/api")> {
  vi.resetModules();
  vi.stubEnv("VITE_API_MODE", "live");
  return import("@/lib/api");
}

/** Minimal in-memory localStorage so a test controls the persisted session directly. */
export function installLocalStorage(initial: Record<string, string> = {}): Record<string, string> {
  const store: Record<string, string> = { ...initial };
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => { store[k] = v; },
    removeItem: (k: string) => { delete store[k]; },
    clear: () => { for (const k of Object.keys(store)) delete store[k]; },
  });
  return store;
}

/** Seed the persisted session api.ts reads the bearer token out of. */
export function seedSession(
  store: Record<string, string>,
  s: { token?: string | null; refreshToken?: string | null; username?: string | null; scope?: string | null },
): void {
  store["pdn.session"] = JSON.stringify(s);
}

/** A JSON Response with the given status - what a stubbed fetch hands back. */
export function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

/** A JWT with a chosen `exp` (epoch seconds). Header/signature are throwaway - only the
 *  payload's exp is read by the proactive-refresh decoder. base64url, no padding. */
export function jwtWithExp(expEpochSeconds: number): string {
  const b64u = (o: unknown) =>
    btoa(JSON.stringify(o)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `${b64u({ alg: "HS256", typ: "JWT" })}.${b64u({ exp: expEpochSeconds })}.sig`;
}

/** The EventSource stand-in. Instances are recorded on the static list in open order. */
export class FakeEventSource {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 2;
  static instances: FakeEventSource[] = [];

  readyState = FakeEventSource.CONNECTING;
  readonly url: string;
  private readonly listeners = new Map<string, Set<(e: Event) => void>>();

  constructor(url: string) {
    this.url = url;
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, fn: (e: Event) => void): void {
    const set = this.listeners.get(type) ?? new Set();
    set.add(fn);
    this.listeners.set(type, set);
  }

  removeEventListener(type: string, fn: (e: Event) => void): void {
    this.listeners.get(type)?.delete(fn);
  }

  close(): void {
    this.readyState = FakeEventSource.CLOSED;
  }

  /** The server accepted the subscription (headers arrived). */
  open(): void {
    this.readyState = FakeEventSource.OPEN;
    this.fire("open", { type: "open" });
  }

  /** One named SSE event whose data is the JSON encoding of `payload`. */
  send(name: string, payload: unknown): void {
    this.fire(name, { type: name, data: JSON.stringify(payload) });
  }

  /** One named SSE event whose data is verbatim (for the malformed-payload cases). */
  sendRaw(name: string, data: string): void {
    this.fire(name, { type: name, data });
  }

  /** A transient drop: the browser is still re-dialling, so the helper must leave it be. */
  blip(): void {
    this.readyState = FakeEventSource.CONNECTING;
    this.fire("error", { type: "error" });
  }

  /** CLOSED for good - what a 401 on (re)connect produces per the SSE spec. */
  fail(): void {
    this.readyState = FakeEventSource.CLOSED;
    this.fire("error", { type: "error" });
  }

  /** The query-string access token this instance was opened with (null when absent). */
  get accessToken(): string | null {
    return new URL(this.url, "http://localhost").searchParams.get("access_token");
  }

  /** The path (no query) this instance was opened on. */
  get path(): string {
    return new URL(this.url, "http://localhost").pathname;
  }

  private fire(type: string, e: unknown): void {
    for (const fn of this.listeners.get(type) ?? []) fn(e as Event);
  }
}

/** Install FakeEventSource as the global EventSource and reset the instance list. */
export function installFakeEventSource(): typeof FakeEventSource {
  FakeEventSource.instances = [];
  vi.stubGlobal("EventSource", FakeEventSource);
  return FakeEventSource;
}
