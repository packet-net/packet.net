// The Ports screen follows a port that is still on its way somewhere (#777): a restart after a
// save can pass through one failed open before it comes up, and a single fetch taken in that
// window used to freeze the retry's error on the card until the operator refreshed the page.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Ports } from "@/screens/ports";
import { api } from "@/lib/api";
import { NODE_CONFIG } from "@/lib/mock";
import type { NodeConfig, PortConfig, PortStatus } from "@/lib/types";

const PORT: PortConfig = {
  id: "2m", enabled: true, transport: { kind: "serial-kiss", device: "/dev/serial/by-id/usb-MCP2221-if00", baud: 57600 },
  profile: null, ax25: null, kiss: null, beacon: null,
};

const status = (over: Partial<PortStatus>): PortStatus => ({
  id: "2m", enabled: true, state: "up", sessionCount: 0, lastError: null,
  framesIn: 0, framesOut: 0, degraded: [], since: "2026-08-23T12:00:00+00:00", channelBusy: null,
  ...over,
});

const DENIED = "Access to the port '/dev/serial/by-id/usb-MCP2221-if00' is denied.";

beforeEach(() => {
  localStorage.clear();
  localStorage.setItem("pdn.session", JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope: "operate" }));
  const cfg: NodeConfig = { ...NODE_CONFIG, ports: [PORT] };
  vi.spyOn(api, "config").mockResolvedValue(cfg);
  vi.spyOn(api, "linkStats").mockResolvedValue([]);
});
afterEach(() => vi.restoreAllMocks());

function mount() {
  render(
    <MemoryRouter>
      <AuthProvider>
        <Ports />
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe("a port still on its way up is looked at again", () => {
  it("a retry's error clears on its own once the port comes up, and the looking stops", async () => {
    // First fetch: the restart's first open failed and the port is retrying. Then: up.
    const ports = vi.spyOn(api, "ports")
      .mockResolvedValueOnce([status({ state: "retrying", lastError: DENIED })])
      .mockResolvedValue([status({ state: "up" })]);
    mount();

    await screen.findByText(DENIED);
    await waitFor(() => expect(screen.queryByText(DENIED)).toBeNull(), { timeout: 5000 });
    expect(ports.mock.calls.length).toBeGreaterThanOrEqual(2);

    // Settled: no further fetches.
    const settledAt = ports.mock.calls.length;
    await new Promise((r) => setTimeout(r, 2000));
    expect(ports.mock.calls.length).toBe(settledAt);
  }, 15000);

  it("a port that is up from the start is fetched once", async () => {
    const ports = vi.spyOn(api, "ports").mockResolvedValue([status({ state: "up" })]);
    mount();
    await screen.findByText("2m");
    await new Promise((r) => setTimeout(r, 2000));
    expect(ports).toHaveBeenCalledTimes(1);
  }, 10000);
});
