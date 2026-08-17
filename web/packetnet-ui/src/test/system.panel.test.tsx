// Tests for the Config screen's "About this node" panel (node self-update UI,
// docs/node-self-update-design.md Slice 3). Mounts the Config screen against the mock
// API backend, switches to the Management tab, and spies api.systemInfo to drive each
// state: the version/channel display, the "update available · vX → vY" banner, the
// admin-gated Apply button, the unknown-channel disable, and the fire-and-acknowledge
// poll-reconnect (POST /update → poll /info + /healthz until the version changes).
// Mirrors tailscale.panel.test.tsx's mount + spy style.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within, act } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Config } from "@/screens/config";
import { api } from "@/lib/api";
import type { SystemInfo } from "@/lib/types";

function seedScope(scope: "read" | "admin") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

// Mount Config, wait for the draft to seed (the Management tab button appears), then open
// it and wait for the System panel to render.
async function mountSystem(info: SystemInfo, scope: "read" | "admin" = "admin") {
  seedScope(scope);
  vi.spyOn(api, "systemInfo").mockResolvedValue(info);
  render(
    <MemoryRouter>
      <AuthProvider>
        <Config />
      </AuthProvider>
    </MemoryRouter>,
  );
  const mgmt = await screen.findByRole("button", { name: "Management" });
  fireEvent.click(mgmt);
  await waitFor(() => expect(document.querySelector('[data-testid="system-panel"]')).not.toBeNull());
}

function panel(): HTMLElement {
  const el = document.querySelector('[data-testid="system-panel"]');
  expect(el).not.toBeNull();
  return el as HTMLElement;
}

const APT_UPDATE: SystemInfo = {
  version: "0.7.0", channel: "apt", updateMechanism: "apt",
  updateAvailable: true, latestVersion: "0.8.0",
};

// config.tsx's UPDATE_POLL_INTERVAL_MS - the wait between /info + /healthz probes.
const UPDATE_POLL_INTERVAL_MS = 2_000;

beforeEach(() => {
  localStorage.clear();
});
afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
  // Any test that faked time hands it back here, so a later test never inherits a frozen clock.
  vi.useRealTimers();
});

describe("Config — About this node (self-update) panel", () => {
  it("shows the running version + install channel", async () => {
    await mountSystem({
      version: "0.7.0", channel: "github", updateMechanism: "github",
      updateAvailable: false, latestVersion: null,
    });
    await waitFor(() => expect(within(panel()).getByTestId("node-version")).toHaveTextContent("0.7.0"));
    // the channel shows by its friendly label
    expect(within(panel()).getAllByText("GitHub Releases").length).toBeGreaterThan(0);
  });

  it("reports up to date when no update is available", async () => {
    await mountSystem({
      version: "0.7.0", channel: "github", updateMechanism: "github",
      updateAvailable: false, latestVersion: null,
    });
    await waitFor(() => expect(within(panel()).getByTestId("up-to-date")).toBeInTheDocument());
    expect(within(panel()).queryByTestId("update-banner")).toBeNull();
  });

  it("banners the version delta and offers Apply when an update is available", async () => {
    await mountSystem(APT_UPDATE);
    await waitFor(() => expect(within(panel()).getByTestId("update-banner")).toBeInTheDocument());
    expect(within(panel()).getByText("Update available - v0.7.0 → v0.8.0")).toBeInTheDocument();
    expect(within(panel()).getByRole("button", { name: /Apply update/ })).toBeEnabled();
  });

  it("disables Apply for a non-admin (read) scope", async () => {
    await mountSystem(APT_UPDATE, "read");
    const btn = await within(panel()).findByRole("button", { name: /Apply update/ });
    expect(btn).toBeDisabled();
    expect(btn).toHaveAttribute("title", expect.stringMatching(/admin scope/i));
  });

  it("disables Apply when the channel is unknown", async () => {
    await mountSystem({
      version: "0.7.0", channel: "unknown", updateMechanism: "none",
      updateAvailable: true, latestVersion: "0.8.0",
    });
    const btn = await within(panel()).findByRole("button", { name: /Apply update/ });
    expect(btn).toBeDisabled();
    expect(btn).toHaveAttribute("title", expect.stringMatching(/can't self-update|cannot self-update|won't self-update/i));
  });

  it("Apply POSTs the update then polls /info + /healthz until the version changes", async () => {
    const update = vi.spyOn(api, "systemUpdate").mockResolvedValue(undefined);
    const healthy = vi.spyOn(api, "nodeHealthy").mockResolvedValue(true);
    // systemInfo: first the pre-update version, then (after the restart) the new one.
    const infoSpy = vi.spyOn(api, "systemInfo")
      .mockResolvedValueOnce(APT_UPDATE)                                   // initial load
      .mockResolvedValue({ ...APT_UPDATE, version: "0.8.0", updateAvailable: false, latestVersion: null });

    seedScope("admin");
    render(
      <MemoryRouter>
        <AuthProvider>
          <Config />
        </AuthProvider>
      </MemoryRouter>,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Management" }));
    await waitFor(() => expect(within(panel()).getByTestId("update-banner")).toBeInTheDocument());

    // The poll loop sleeps UPDATE_POLL_INTERVAL_MS between probes, which this test used to
    // spend for real (~2.1 s of the suite's wall clock, under a 10 s waitFor). Fake the clock
    // from here - after the mount has settled, so the timers being faked are only the poll's -
    // and step it by exactly one interval instead. waitFor is deliberately not used beyond this
    // point: vitest defines no `jest` global, so testing-library cannot auto-advance vi's fake
    // timers and a waitFor under them would simply hang until it timed out.
    vi.useFakeTimers();
    fireEvent.click(within(panel()).getByRole("button", { name: /Apply update/ }));
    await act(async () => { await vi.advanceTimersByTimeAsync(UPDATE_POLL_INTERVAL_MS); });

    expect(update).toHaveBeenCalledTimes(1);
    // it reconnects on the new version (the poll loop sees a different version)
    expect(within(panel()).getByTestId("update-done")).toBeInTheDocument();
    expect(within(panel()).getByText(/now running v0\.8\.0/)).toBeInTheDocument();
    expect(healthy).toHaveBeenCalled();
    expect(infoSpy.mock.calls.length).toBeGreaterThan(1);
  });

  it("keeps waiting while the node answers on the version it started on", async () => {
    // The other half of the poll contract, and only affordable now the clock is faked: /healthz
    // answering is NOT the signal (it can be up on the old binary mid-restart), so a probe that
    // still reports 0.7.0 must leave the panel waiting rather than declaring the update done.
    vi.spyOn(api, "systemUpdate").mockResolvedValue(undefined);
    vi.spyOn(api, "nodeHealthy").mockResolvedValue(true);
    // Note the spy is installed here rather than through mountSystem, which installs its own
    // systemInfo spy and would replace this one.
    const infoSpy = vi.spyOn(api, "systemInfo").mockResolvedValue(APT_UPDATE); // never changes

    seedScope("admin");
    render(
      <MemoryRouter>
        <AuthProvider>
          <Config />
        </AuthProvider>
      </MemoryRouter>,
    );
    fireEvent.click(await screen.findByRole("button", { name: "Management" }));
    await waitFor(() => expect(within(panel()).getByTestId("update-banner")).toBeInTheDocument());

    vi.useFakeTimers();
    fireEvent.click(within(panel()).getByRole("button", { name: /Apply update/ }));
    await act(async () => { await vi.advanceTimersByTimeAsync(UPDATE_POLL_INTERVAL_MS * 3); });

    expect(within(panel()).queryByTestId("update-done")).toBeNull();
    // It really is still polling (not stalled): three intervals bought three more /info probes.
    expect(infoSpy.mock.calls.length).toBeGreaterThanOrEqual(4);
  });

  it("surfaces a server error from the update launch as a banner", async () => {
    vi.spyOn(api, "systemUpdate").mockRejectedValue(
      new Error("Could not start the update: no systemd"));
    await mountSystem(APT_UPDATE);

    fireEvent.click(within(panel()).getByRole("button", { name: /Apply update/ }));
    await waitFor(() => expect(within(panel()).getByText(/Could not start the update/)).toBeInTheDocument());
  });
});
