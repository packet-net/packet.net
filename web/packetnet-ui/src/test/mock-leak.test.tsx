// The screens show THIS node, never the fixture node (#691 WP4).
//
// lib/mock.ts is a fake node - callsign GB7RDG, ports vhf-1 / uhf-2 / link-dn / mp-net /
// hf-300 - and every screen that reached into it for a default or a loading-state fallback
// was showing real operators a node that does not exist:
//   C021 connect-out defaulted its via-port to the mock's first port ("vhf-1") while the
//        select DISPLAYED the node's real first port, so the form showed one port and
//        POSTed another, and the node answered 404 "Port 'vhf-1' is not running"; the ping
//        dialog built BOTH its default and its options from the mock list, so it could
//        never name a real port at all, and the Ports header pre-targeted the ping at the
//        mock's own callsign;
//   C022 the Ports screen painted the mock's five ports while /config loaded, and forever
//        if it failed, with Edit / Remove / Tune / bring-up wired to the real API for ids
//        the node has never had;
//   C042 the Routes -> Sessions hand-off (?connect=&port=) cleared the query params in the
//        same effect that opened the modal, and the modal re-seeded from the resulting
//        nulls, so the operator got an EMPTY form.
// Every case here therefore seeds /config with port ids no fixture has, and asserts on what
// is posted and what is on screen. Mount + spy style mirrors ports.editor.test.tsx.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Sessions } from "@/screens/sessions";
import { Ports } from "@/screens/ports";
import { api } from "@/lib/api";
import { NODE_CONFIG } from "@/lib/mock";
import type {
  NetRomRoutingSnapshot, NodeConfig, PingResult, PortConfig, PortStatus, SessionInfo,
} from "@/lib/types";

// Port ids no fixture has, so any fixture leak is unmistakable rather than a coincidence.
const LIVE_PORTS: PortConfig[] = [
  {
    id: "radio-a", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 },
    profile: null, ax25: null, kiss: null, beacon: null,
  },
  {
    id: "radio-b", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8002 },
    profile: null, ax25: null, kiss: null, beacon: null,
  },
];
const LIVE_CONFIG: NodeConfig = { ...NODE_CONFIG, ports: LIVE_PORTS };

const LIVE_PORT_STATUS: PortStatus[] = [
  {
    id: "radio-a", enabled: true, state: "up", sessionCount: 0, lastError: null,
    framesIn: 12, framesOut: 9, degraded: [], since: "2026-08-17T11:52:00+00:00", channelBusy: false,
  },
  {
    id: "radio-b", enabled: true, state: "up", sessionCount: 0, lastError: null,
    framesIn: 0, framesOut: 0, degraded: [], since: "2026-08-17T11:52:00+00:00", channelBusy: null,
  },
];

const NO_ROUTES: NetRomRoutingSnapshot = {
  generatedAt: "2026-08-16T00:00:00Z", neighbours: [], destinations: [],
};

const SESSION: SessionInfo = {
  id: "s1", portId: "radio-a", peer: "GB7CIP", role: "console", state: "Connected",
  vs: 0, vr: 0, window: 4, uptimeSeconds: 1, bytesIn: 0, bytesOut: 0, lastActivity: "0:00:00",
};

const PING_OK: PingResult = {
  replies: [{ seq: 1, rttMs: 420, timeout: false }], minMs: 420, avgMs: 420, maxMs: 420, lossPct: 0,
};

// Every port id the fixture node has. If one of these is on screen, a fixture leaked.
const FIXTURE_PORT_IDS = ["vhf-1", "uhf-2", "link-dn", "mp-net", "hf-300"];
function expectNoFixtureLeak(): void {
  const text = document.body.textContent ?? "";
  for (const id of FIXTURE_PORT_IDS) expect(text).not.toContain(id);
}

// A promise the test resolves by hand, so the PENDING state is observable. mockResolvedValue
// settles on the next microtask, which is too early to catch a loading state.
function deferred<T>(): { promise: Promise<T>; resolve: (v: T) => void; reject: (e: unknown) => void } {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

function seedScope(scope: "read" | "operate" | "admin" = "operate") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

// Everything the two screens read apart from /config, which each test seeds itself.
function seedReads() {
  vi.spyOn(api, "sessions").mockResolvedValue([]);
  vi.spyOn(api, "routes").mockResolvedValue(NO_ROUTES);
  vi.spyOn(api, "ports").mockResolvedValue(LIVE_PORT_STATUS);
  vi.spyOn(api, "linkStats").mockResolvedValue([]);
}

function mount(node: React.ReactElement, route = "/") {
  seedScope();
  return render(
    <MemoryRouter initialEntries={[route]}>
      <AuthProvider>{node}</AuthProvider>
    </MemoryRouter>,
  );
}

beforeEach(() => localStorage.clear());
afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Sessions connect-out - the port it posts is the port it shows (#691 C021)", () => {
  it("waits for /config, then defaults the via-port to the node's first LIVE port", async () => {
    seedReads();
    const cfg = deferred<NodeConfig>();
    vi.spyOn(api, "config").mockReturnValue(cfg.promise);
    const connect = vi.spyOn(api, "connectSession").mockResolvedValue(SESSION);

    mount(<Sessions />);
    fireEvent.click(await screen.findByRole("button", { name: /Connect/i }));
    const dialog = await screen.findByRole("dialog");

    // Before /config answers there is no port worth posting, so the action is refused
    // rather than quietly sending a port id this node may not have.
    fireEvent.change(within(dialog).getByRole("textbox"), { target: { value: "GB7CIP" } });
    expect(within(dialog).getByRole("button", { name: /Connect/i })).toBeDisabled();

    cfg.resolve(LIVE_CONFIG);
    await waitFor(() => expect(within(dialog).getByRole("button", { name: /Connect/i })).not.toBeDisabled());

    // What the operator sees IS what gets posted - that equality is the whole bug.
    const select = within(dialog).getByRole("combobox") as HTMLSelectElement;
    expect(select.value).toBe("radio-a");
    fireEvent.click(within(dialog).getByRole("button", { name: /Connect/i }));

    await waitFor(() => expect(connect).toHaveBeenCalledTimes(1));
    expect(connect).toHaveBeenCalledWith("GB7CIP", "radio-a");
    expect(connect.mock.calls[0][1]).not.toBe("vhf-1");
  });

  it("posts a re-picked port, and offers only this node's ports", async () => {
    seedReads();
    vi.spyOn(api, "config").mockResolvedValue(LIVE_CONFIG);
    const connect = vi.spyOn(api, "connectSession").mockResolvedValue(SESSION);

    mount(<Sessions />);
    fireEvent.click(await screen.findByRole("button", { name: /Connect/i }));
    const dialog = await screen.findByRole("dialog");
    const select = await waitFor(() => {
      const s = within(dialog).getByRole("combobox") as HTMLSelectElement;
      expect(s.value).toBe("radio-a");
      return s;
    });

    // The option list is the node's ports and nothing else.
    expect([...select.options].map((o) => o.value)).toEqual(["radio-a", "radio-b"]);

    fireEvent.change(within(dialog).getByRole("textbox"), { target: { value: "gb7cip" } });
    fireEvent.change(select, { target: { value: "radio-b" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Connect/i }));

    await waitFor(() => expect(connect).toHaveBeenCalledWith("GB7CIP", "radio-b"));
  });
});

describe("Routes -> Sessions hand-off prefills the modal (#691 C042)", () => {
  it("survives the query params being cleared, and connects to what it shows", async () => {
    seedReads();
    vi.spyOn(api, "config").mockResolvedValue(LIVE_CONFIG);
    const connect = vi.spyOn(api, "connectSession").mockResolvedValue(SESSION);

    mount(<Sessions />, "/sessions?connect=GB7CIP&port=radio-b");

    // The modal opens by itself, carrying BOTH halves of the hand-off. It used to open
    // empty: the effect cleared the params, and the next render re-seeded the form from
    // the nulls that were left.
    const dialog = await screen.findByRole("dialog");
    expect((within(dialog).getByRole("textbox") as HTMLInputElement).value).toBe("GB7CIP");
    await waitFor(() =>
      expect((within(dialog).getByRole("combobox") as HTMLSelectElement).value).toBe("radio-b"));

    fireEvent.click(within(dialog).getByRole("button", { name: /Connect/i }));
    await waitFor(() => expect(connect).toHaveBeenCalledWith("GB7CIP", "radio-b"));
  });

  it("keeps the hand-off's callsign after the config settles the port", async () => {
    seedReads();
    const cfg = deferred<NodeConfig>();
    vi.spyOn(api, "config").mockReturnValue(cfg.promise);

    mount(<Sessions />, "/sessions?connect=GB7BNS&port=radio-b");
    const dialog = await screen.findByRole("dialog");
    expect((within(dialog).getByRole("textbox") as HTMLInputElement).value).toBe("GB7BNS");

    // /config arriving late must settle the PORT without touching what the operator (or the
    // hand-off) put in the callsign box.
    cfg.resolve(LIVE_CONFIG);
    await waitFor(() =>
      expect((within(dialog).getByRole("combobox") as HTMLSelectElement).value).toBe("radio-b"));
    expect((within(dialog).getByRole("textbox") as HTMLInputElement).value).toBe("GB7BNS");
  });
});

describe("AX.25 ping names a port this node has (#691 C021)", () => {
  it("posts a live port id from the Ports header, with no pre-targeted station", async () => {
    seedReads();
    vi.spyOn(api, "config").mockResolvedValue(LIVE_CONFIG);
    const ping = vi.spyOn(api, "pingTarget").mockResolvedValue(PING_OK);

    mount(<Ports />);
    fireEvent.click(await screen.findByRole("button", { name: /AX\.25 ping/i }));
    const dialog = await screen.findByRole("dialog");

    // The header ping is "ping a station", not "ping GB7RDG": the box was prefilled with
    // the MOCK node's own callsign.
    const station = within(dialog).getByRole("textbox") as HTMLInputElement;
    expect(station.value).toBe("");

    const select = await waitFor(() => {
      const s = within(dialog).getByRole("combobox") as HTMLSelectElement;
      expect(s.value).toBe("radio-a");
      return s;
    });
    expect([...select.options].map((o) => o.value)).toEqual(["radio-a", "radio-b"]);

    fireEvent.change(station, { target: { value: "gb7cip" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /Send TEST frames/i }));

    await waitFor(() => expect(ping).toHaveBeenCalledTimes(1));
    const [call, portId] = ping.mock.calls[0];
    expect(call).toBe("GB7CIP");
    expect(portId).toBe("radio-a");
    expect(FIXTURE_PORT_IDS).not.toContain(portId);
  });
});

describe("Ports screen renders its own state, never the fixtures (#691 C022)", () => {
  it("shows a loading state while /config is in flight - not five invented ports", async () => {
    seedReads();
    const cfg = deferred<NodeConfig>();
    vi.spyOn(api, "config").mockReturnValue(cfg.promise);

    mount(<Ports />);
    expect(await screen.findByText(/Loading ports/i)).toBeInTheDocument();
    expectNoFixtureLeak();

    cfg.resolve(LIVE_CONFIG);
    expect(await screen.findByText("radio-a")).toBeInTheDocument();
    expect(screen.getByText("radio-b")).toBeInTheDocument();
    expect(screen.queryByText(/Loading ports/i)).toBeNull();
    expectNoFixtureLeak();
  });

  it("shows the failure when /config errors - not a permanent lie about somebody's node", async () => {
    seedReads();
    vi.spyOn(api, "config").mockRejectedValue(new Error("Failed to fetch"));

    mount(<Ports />);
    expect(await screen.findByText(/Couldn't load this node's ports/i)).toBeInTheDocument();
    // The reason reaches the operator instead of being swallowed by a fixture fallback.
    expect(document.body.textContent).toContain("Failed to fetch");
    expectNoFixtureLeak();
  });

  it("says so when the node genuinely has no ports", async () => {
    seedReads();
    vi.spyOn(api, "config").mockResolvedValue({ ...NODE_CONFIG, ports: [] });

    mount(<Ports />);
    expect(await screen.findByText(/No ports configured/i)).toBeInTheDocument();
    expectNoFixtureLeak();
  });
});
