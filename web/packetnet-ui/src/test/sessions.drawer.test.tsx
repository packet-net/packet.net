// ============================================================
// The Sessions screen's session drawer, screens/sessions.tsx (review item C031, packet.net#692).
//
// sessions.actions.test.tsx (#702 C048) covers the connect-out button's debounce and the operate
// gate. This covers the drawer itself, which nothing touched: `subscribeSessionOutput`,
// `sendSessionLine` and `disconnectSession` had no spy anywhere in the suite. The drawer is the
// sysop's actual terminal onto a live circuit - if its buffer does not reset on the replayed
// backlog, or a send is not echoed, or a disconnect leaves the row up, the operator is looking
// at a screen that disagrees with the node.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Sessions } from "@/screens/sessions";
import * as apiModule from "@/lib/api";
import type { StreamOptions } from "@/lib/api";
import type { NodeConfig, SessionInfo } from "@/lib/types";

const { api } = apiModule;

const SESSION: SessionInfo = {
  id: "vhf-1:M0LTE", portId: "vhf-1", peer: "M0LTE", role: "console", state: "Connected",
  vs: 12, vr: 11, window: 4, uptimeSeconds: 842, bytesIn: 4821, bytesOut: 19_233,
  lastActivity: "0:00:02",
};

/** A stand-in for the SSE feed: the test drives chunks and the backlog reset by hand. */
interface FakeFeed {
  id: string;
  chunk: (text: string) => void;
  reset: () => void;
  unsubscribed: boolean;
}
let feeds: FakeFeed[] = [];

function stubSessionStream(): void {
  vi.spyOn(apiModule, "subscribeSessionOutput").mockImplementation(
    (id: string, onChunk: (t: string) => void, opts?: StreamOptions) => {
      const feed: FakeFeed = {
        id,
        chunk: onChunk,
        reset: () => opts?.onReset?.(),
        unsubscribed: false,
      };
      feeds.push(feed);
      return () => { feed.unsubscribed = true; };
    },
  );
}

function mountSessions(scope: "read" | "operate" | "admin" = "admin") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
  return render(
    <MemoryRouter initialEntries={["/sessions"]}>
      <AuthProvider><Sessions /></AuthProvider>
    </MemoryRouter>,
  );
}

/** Open the drawer for the seeded session by clicking its peer callsign in the table. */
async function openDrawer(): Promise<HTMLElement> {
  await waitFor(() => expect(screen.getByRole("button", { name: "M0LTE" })).toBeInTheDocument());
  fireEvent.click(screen.getByRole("button", { name: "M0LTE" }));
  const heading = await screen.findByText(/Session . M0LTE/);
  return (heading.closest("div[class*='fixed'], aside, section") as HTMLElement | null) ?? document.body;
}

function consolePane(): HTMLElement {
  const label = screen.getByText("Console");
  const pane = label.parentElement?.querySelector("div.overflow-y-auto");
  expect(pane, "the drawer's output pane").not.toBeNull();
  return pane as HTMLElement;
}

beforeEach(() => {
  feeds = [];
  localStorage.clear();
  vi.spyOn(api, "sessions").mockResolvedValue([SESSION]);
  vi.spyOn(api, "routes").mockResolvedValue({ generatedAt: "", neighbours: [], destinations: [] });
  vi.spyOn(api, "config").mockResolvedValue({ ports: [{ id: "vhf-1" }] } as unknown as NodeConfig);
  stubSessionStream();
});

afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Sessions drawer - the live output feed", () => {
  it("subscribes to the opened session and renders its output", async () => {
    mountSessions();
    await openDrawer();

    expect(feeds).toHaveLength(1);
    expect(feeds[0].id).toBe("vhf-1:M0LTE");
    // The pane says so until something arrives, rather than looking like an empty terminal.
    expect(screen.getByText(/Waiting for output/i)).toBeInTheDocument();

    feeds[0].chunk("GB7RDG:GB7RDG} Welcome\r\n");
    await waitFor(() => expect(consolePane()).toHaveTextContent("GB7RDG:GB7RDG} Welcome"));
  });

  it("folds a bare CR to a newline so BPQ's line endings do not collapse onto one row", async () => {
    // Packet stations terminate lines with a bare CR; the pre-wrap pane only breaks on LF.
    mountSessions();
    await openDrawer();

    feeds[0].chunk("first\rsecond\r\nthird\n");
    await waitFor(() => expect(consolePane().textContent).toBe("first\nsecond\nthird\n"));
  });

  it("resets the buffer when the node replays the backlog, so a reconnect does not double it", async () => {
    // The node replays the whole history on EVERY subscription, including the stream helper's
    // silent reconnect after a token rotation (#689 C045).
    mountSessions();
    await openDrawer();

    feeds[0].chunk("BANNER\nline one\n");
    await waitFor(() => expect(consolePane().textContent).toBe("BANNER\nline one\n"));

    feeds[0].reset();
    feeds[0].chunk("BANNER\nline one\n");
    feeds[0].chunk("line two\n");
    await waitFor(() => expect(consolePane().textContent).toBe("BANNER\nline one\nline two\n"));
  });

  it("unsubscribes when the drawer closes, so a background session stops streaming", async () => {
    mountSessions();
    await openDrawer();
    expect(feeds[0].unsubscribed).toBe(false);

    // The sheet has two ways out: the header's dismiss glyph and the footer's Close. Either
    // must tear the subscription down; the footer one is the labelled button.
    const closes = screen.getAllByRole("button", { name: /^Close$/i });
    fireEvent.click(closes[closes.length - 1]);

    await waitFor(() => expect(feeds[0].unsubscribed).toBe(true));
  });
});

describe("Sessions drawer - sending a line", () => {
  it("sends the typed line to THIS session and echoes it locally", async () => {
    const send = vi.spyOn(api, "sendSessionLine").mockResolvedValue(undefined);
    mountSessions();
    await openDrawer();

    const input = screen.getByPlaceholderText(/type a command/i);
    fireEvent.change(input, { target: { value: "HELP" } });
    fireEvent.click(screen.getByRole("button", { name: /Send/i }));

    await waitFor(() => expect(send).toHaveBeenCalledWith("vhf-1:M0LTE", "HELP"));
    // The node does not echo, so the pane must, or the operator cannot see what they sent.
    await waitFor(() => expect(consolePane()).toHaveTextContent("» HELP"));
    // The box is cleared, ready for the next line.
    expect((input as HTMLInputElement).value).toBe("");
  });

  it("sends on Enter as well as the button, and never sends a blank line", async () => {
    const send = vi.spyOn(api, "sendSessionLine").mockResolvedValue(undefined);
    mountSessions();
    await openDrawer();
    const input = screen.getByPlaceholderText(/type a command/i);

    fireEvent.keyDown(input, { key: "Enter" });
    expect(send).not.toHaveBeenCalled();      // nothing typed yet

    fireEvent.change(input, { target: { value: "   " } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(send).not.toHaveBeenCalled();      // whitespace is not a command

    fireEvent.change(input, { target: { value: "PORTS" } });
    fireEvent.keyDown(input, { key: "Enter" });
    await waitFor(() => expect(send).toHaveBeenCalledWith("vhf-1:M0LTE", "PORTS"));
  });

  it("banners a failed send on the screen behind the drawer", async () => {
    vi.spyOn(api, "sendSessionLine").mockRejectedValue(new Error("session vhf-1:M0LTE is closed"));
    mountSessions();
    await openDrawer();

    fireEvent.change(screen.getByPlaceholderText(/type a command/i), { target: { value: "HELP" } });
    fireEvent.click(screen.getByRole("button", { name: /Send/i }));

    await waitFor(() => expect(screen.getByText(/session vhf-1:M0LTE is closed/)).toBeInTheDocument());
  });
});

describe("Sessions drawer - disconnecting", () => {
  it("drops the circuit, closes the drawer and takes the row off the table", async () => {
    const drop = vi.spyOn(api, "disconnectSession").mockResolvedValue(undefined);
    mountSessions();
    const drawer = await openDrawer();

    fireEvent.click(within(drawer).getByRole("button", { name: /Disconnect/i }));

    await waitFor(() => expect(drop).toHaveBeenCalledWith("vhf-1:M0LTE"));
    await waitFor(() => expect(screen.queryByText("Console")).toBeNull());
    await waitFor(() => expect(screen.getByText(/No active sessions/i)).toBeInTheDocument());
  });

  it("keeps the row and says why when the disconnect fails", async () => {
    vi.spyOn(api, "disconnectSession").mockRejectedValue(new Error("no such session"));
    mountSessions();
    const drawer = await openDrawer();

    fireEvent.click(within(drawer).getByRole("button", { name: /Disconnect/i }));

    await waitFor(() => expect(screen.getByText(/no such session/)).toBeInTheDocument());
    expect(screen.getByRole("button", { name: "M0LTE" })).toBeInTheDocument();
  });
});
