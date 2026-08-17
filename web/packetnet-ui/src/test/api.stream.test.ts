// Tests for lib/api.ts's SSE plumbing in LIVE mode (VITE_API_MODE=live) - the three review
// items behind #689 that live on the client:
//
//   C023  a stream whose access token expired must not die silently: the EventSource closes on
//         the 401, and the helper renews the token and re-subscribes with backoff.
//   C045  the console/session backlog arrives as its own `backlog` event, so a reconnect
//         RESETS the consumer's buffer instead of appending a second copy of the history.
//   C046  `seq` restarts at 1 on every node boot, so the monitor's buffer must flush on a
//         restart rather than swallowing the new boot's frames as duplicates.
//
// MODE is captured at module load, so each block stubs the env then dynamically imports a
// FRESH copy of the module (mirrors api.auth.test.tsx). EventSource does not exist in jsdom,
// so the fake below IS the seam: it records every instance the helper opens (with the URL, and
// therefore the token, it opened with) and lets a test drive open/message/closed by hand.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import type { MonitorEvent } from "@/lib/types";

class FakeEventSource {
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

  // --- test drivers ---------------------------------------------------------
  /** The server accepted the subscription (headers arrived). */
  open(): void {
    this.readyState = FakeEventSource.OPEN;
    this.fire("open", { type: "open" });
  }

  /** One named SSE event whose data is the JSON encoding of `payload`. */
  send(name: string, payload: unknown): void {
    this.fire(name, { type: name, data: JSON.stringify(payload) });
  }

  /** The stream is CLOSED for good - what a 401 on (re)connect produces per the SSE spec. */
  fail(): void {
    this.readyState = FakeEventSource.CLOSED;
    this.fire("error", { type: "error" });
  }

  private fire(type: string, e: unknown): void {
    for (const fn of this.listeners.get(type) ?? []) fn(e as Event);
  }
}

/** A JWT with a chosen `exp` (epoch seconds) - only the payload's exp is read. */
function jwtWithExp(expEpochSeconds: number): string {
  const b64u = (o: unknown) =>
    btoa(JSON.stringify(o)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `${b64u({ alg: "HS256", typ: "JWT" })}.${b64u({ exp: expEpochSeconds })}.sig`;
}

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

/** Just enough of a MonitorEvent for the buffer logic (seq / timestamp / bootId). */
function frame(seq: number, timestamp: string, bootId?: string): MonitorEvent {
  return {
    seq, timestamp, bootId, portId: "vhf-1", direction: "in", source: "M0LTE", dest: "GB7RDG",
    type: "UI", classKind: "U", pid: null, pidName: null, ns: null, nr: null, pf: 0,
    command: true, length: 20, summary: "UI", raw: [], path: [], control: 0x03, infoLength: 5,
  } as MonitorEvent;
}

beforeEach(() => {
  FakeEventSource.instances = [];
  vi.stubGlobal("EventSource", FakeEventSource);
  // jsdom has no Web Locks API; the cross-tab refresh serializer then takes the direct path.
  if (typeof navigator !== "undefined") {
    delete (navigator as { locks?: unknown }).locks;
  }
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

describe("subscribeFrames - a stream killed by an expired token heals itself (C023)", () => {
  it("renews the access token and re-subscribes with the rotated one", async () => {
    vi.useFakeTimers();
    const expired = jwtWithExp(Math.floor(Date.now() / 1000) - 10);
    const store = installLocalStorage();
    store["pdn.session"] = JSON.stringify({ token: expired, refreshToken: "rt-1" });

    const fresh = jwtWithExp(Math.floor(Date.now() / 1000) + 3600);
    const fetchMock = vi.fn((_url: string | URL, _init?: RequestInit) => Promise.resolve(new Response(
      JSON.stringify({ token: fresh, refreshToken: "rt-2", scopes: "admin", username: "tom" }),
      { status: 200, headers: { "content-type": "application/json" } },
    )));
    vi.stubGlobal("fetch", fetchMock);

    const api = await loadLiveApi();
    const statuses: boolean[] = [];
    const unsub = api.subscribeFrames(() => {}, { onStatus: (c) => statuses.push(c) });

    // The first subscription carried the (soon to be dead) token.
    expect(FakeEventSource.instances).toHaveLength(1);
    const first = FakeEventSource.instances[0];
    expect(first.url).toContain(encodeURIComponent(expired));
    first.open();
    expect(statuses).toEqual([true]);

    // The node rotates the token; the browser's own reconnect replays the dead one and is
    // refused, which per spec CLOSES the EventSource for good. Before this fix that was the
    // end of the feed - the screen kept showing a live dot over a dead socket.
    first.fail();
    expect(statuses).toEqual([true, false]);

    // Backoff, then a silent renew and a fresh subscription - with the NEW token.
    await vi.advanceTimersByTimeAsync(2000);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0][0])).toContain("/auth/refresh");
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances[1].url).toContain(encodeURIComponent(fresh));
    expect(FakeEventSource.instances[1].url).not.toContain(encodeURIComponent(expired));

    // Unsubscribing stops the reconnect loop: a later failure opens nothing more.
    FakeEventSource.instances[1].open();
    unsub();
    FakeEventSource.instances[1].fail();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(FakeEventSource.instances).toHaveLength(2);
  });
});

describe("subscribeSessionOutput - the replayed backlog resets the buffer (C045)", () => {
  it("a reconnect re-renders the history instead of appending a second copy", async () => {
    vi.useFakeTimers();
    installLocalStorage()["pdn.session"] = JSON.stringify({ token: "t", refreshToken: null });
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(new Response("", { status: 401 }))));

    const api = await loadLiveApi();
    let buffer = "";
    const unsub = api.subscribeSessionOutput(
      "s-1",
      (chunk) => { buffer += chunk; },
      { onReset: () => { buffer = ""; } },
    );

    // First subscription: the node replays what the browser missed, then streams live.
    const first = FakeEventSource.instances[0];
    first.open();
    first.send("backlog", "BANNER\r\n");
    first.send("output", "line one\r\n");
    expect(buffer).toBe("BANNER\r\nline one\r\n");

    // The stream drops and the helper re-subscribes. The node replays the WHOLE backlog again
    // (it always does), so without the reset the drawer would now hold two copies of it.
    first.fail();
    await vi.advanceTimersByTimeAsync(2000);
    const second = FakeEventSource.instances[1];
    second.open();
    second.send("backlog", "BANNER\r\nline one\r\n");
    second.send("output", "line two\r\n");

    expect(buffer).toBe("BANNER\r\nline one\r\nline two\r\n");
    unsub();
  });
});

describe("mergeLiveFrame - the monitor buffer across a node restart (C046)", () => {
  it("flushes the previous boot's frames instead of swallowing the new ones", async () => {
    const api = await loadLiveApi();
    const cap = 500;

    // Boot A: three frames, newest first.
    let buffer: MonitorEvent[] = [];
    for (const f of [frame(1, "2026-08-16T10:00:01Z", "boot-a"),
                     frame(2, "2026-08-16T10:00:02Z", "boot-a"),
                     frame(3, "2026-08-16T10:00:03Z", "boot-a")]) {
      buffer = api.mergeLiveFrame(buffer, f, cap);
    }
    expect(buffer.map((f) => f.seq)).toEqual([3, 2, 1]);

    // The bootstrap/live overlap still dedupes: the same frame twice is held once.
    buffer = api.mergeLiveFrame(buffer, frame(3, "2026-08-16T10:00:03Z", "boot-a"), cap);
    expect(buffer.map((f) => f.seq)).toEqual([3, 2, 1]);

    // Boot B: the node restarted, so seq starts again at 1. Every one of these used to be
    // dropped as an "already buffered" duplicate - the monitor went dead with no clue why.
    buffer = api.mergeLiveFrame(buffer, frame(1, "2026-08-16T10:05:01Z", "boot-b"), cap);
    expect(buffer.map((f) => f.seq)).toEqual([1]);
    expect(buffer[0].bootId).toBe("boot-b");
    buffer = api.mergeLiveFrame(buffer, frame(2, "2026-08-16T10:05:02Z", "boot-b"), cap);
    expect(buffer.map((f) => f.seq)).toEqual([2, 1]);
  });

  it("detects the restart from the seq regression alone when no bootId is served", async () => {
    const api = await loadLiveApi();
    const cap = 500;

    let buffer: MonitorEvent[] = [];
    for (const f of [frame(1, "2026-08-16T10:00:01Z"), frame(2, "2026-08-16T10:00:02Z")]) {
      buffer = api.mergeLiveFrame(buffer, f, cap);
    }
    // Same (seq, timestamp) is the bootstrap/live overlap, not a restart.
    buffer = api.mergeLiveFrame(buffer, frame(2, "2026-08-16T10:00:02Z"), cap);
    expect(buffer.map((f) => f.seq)).toEqual([2, 1]);
    // Same seq, LATER timestamp means a new boot numbering from the start.
    buffer = api.mergeLiveFrame(buffer, frame(1, "2026-08-16T10:05:01Z"), cap);
    expect(buffer.map((f) => f.seq)).toEqual([1]);
  });

  it("keeps the ring bounded at the cap", async () => {
    const api = await loadLiveApi();
    let buffer: MonitorEvent[] = [];
    for (let i = 1; i <= 5; i++) {
      buffer = api.mergeLiveFrame(buffer, frame(i, `2026-08-16T10:00:0${i}Z`, "boot-a"), 3);
    }
    expect(buffer.map((f) => f.seq)).toEqual([5, 4, 3]);
  });
});
