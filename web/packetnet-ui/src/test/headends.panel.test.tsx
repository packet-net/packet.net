// Tests for the Head-ends screen — the split-station "plug into any port and go" adopt surface
// (docs/research/split-station-rf-headend.md § Discovery & adoption flow). Mounts the screen against
// the mock API backend and spies api.getHeadEnds to drive each state:
//   - an AUTO instance (one free TNC + one free radio) → one-click Adopt posts the right body;
//   - an AMBIGUOUS instance → Adopt is disabled until the operator picks a TNC + a radio;
//   - a duplicate-instance-id CONFLICT renders the remediation hint;
//   - an UNREACHABLE instance shows its error;
//   - read-scope gates the Adopt action (operate required).
// Mirrors tailscale.panel.test.tsx's mount + spy style (seed scope, spy the API client, testid panels).
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { HeadEnds } from "@/screens/headends";
import { api } from "@/lib/api";
import type { HeadEndScan, HeadEndInstanceScan, ReconcileResult } from "@/lib/types";

function seedScope(scope: "read" | "operate" | "admin") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

const OK_RECONCILE: ReconcileResult = {
  valid: true,
  live: [{ path: "ports.shack-north", impact: "port-restart", summary: "Head-end port shack-north created." }],
  portRestart: [], nodeReset: [], applied: true,
};

// A reachable instance with one free TNC + one free radio → an auto suggestion.
const AUTO_INSTANCE: HeadEndInstanceScan = {
  instanceId: "shack-north",
  host: "192.168.1.44",
  httpPort: 8080,
  source: "mdns",
  reachable: true,
  error: null,
  devices: [
    { deviceId: "usb-0", kind: "nino-tnc", model: "NinoTNC N9600A4", version: "3.44", serial: null, baud: 57600, free: true },
    { deviceId: "usb-1", kind: "tait-ccdi", model: "Tait TM8110", version: "1.10.0", serial: "19925328", baud: 28800, free: true },
    { deviceId: "usb-2", kind: "tait-ccdi", model: "Tait TM8115", version: "1.10.0", serial: "1G000999", baud: 28800, free: false },
  ],
  proposedPairs: [{ tncDeviceId: "usb-0", radioDeviceId: "usb-1", auto: true }],
  pairingAmbiguous: false,
};

// A reachable instance with two free TNCs + two free radios → ambiguous, operator picks.
const AMBIGUOUS_INSTANCE: HeadEndInstanceScan = {
  instanceId: "garage-pi",
  host: "192.168.1.51",
  httpPort: 8080,
  source: "config",
  reachable: true,
  error: null,
  devices: [
    { deviceId: "acm-0", kind: "nino-tnc", model: "NinoTNC N9600A4", version: "3.44", serial: null, baud: 57600, free: true },
    { deviceId: "acm-1", kind: "nino-tnc", model: "NinoTNC N9600A3", version: "3.41", serial: null, baud: 57600, free: true },
    { deviceId: "usb-0", kind: "tait-ccdi", model: "Tait TM8110", version: "1.10.0", serial: "2G001111", baud: 28800, free: true },
    { deviceId: "usb-1", kind: "tait-ccdi", model: "Tait TM8200", version: "2.03.0", serial: "2G002222", baud: 19200, free: true },
  ],
  proposedPairs: [],
  pairingAmbiguous: true,
};

const UNREACHABLE_INSTANCE: HeadEndInstanceScan = {
  instanceId: "attic-relay",
  host: "192.168.1.77",
  httpPort: 8080,
  source: "mdns",
  reachable: false,
  error: "connection refused — the head-end daemon is not answering",
  devices: [],
  proposedPairs: [],
  pairingAmbiguous: false,
};

function scanWith(instances: HeadEndInstanceScan[], conflicts: HeadEndScan["conflicts"] = []): HeadEndScan {
  return { instances, conflicts };
}

async function mountHeadEnds(scan: HeadEndScan, scope: "read" | "operate" | "admin" = "operate") {
  seedScope(scope);
  vi.spyOn(api, "getHeadEnds").mockResolvedValue(scan);
  render(
    <MemoryRouter>
      <AuthProvider>
        <HeadEnds />
      </AuthProvider>
    </MemoryRouter>,
  );
  // The screen title always renders; the instance cards arrive once the (mock-async) query resolves.
  await screen.findByText("Head-ends");
}

function instancePanel(instanceId: string): HTMLElement {
  const el = document.querySelector(`[data-testid="headend-${instanceId}"]`);
  expect(el).not.toBeNull();
  return el as HTMLElement;
}

beforeEach(() => localStorage.clear());
afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Head-ends — discover → offer → adopt", () => {
  it("an auto instance offers a one-click Adopt that posts the suggested pair", async () => {
    const adopt = vi.spyOn(api, "adoptHeadEnd").mockResolvedValue(OK_RECONCILE);
    await mountHeadEnds(scanWith([AUTO_INSTANCE]));

    const panel = await waitFor(() => instancePanel("shack-north"));
    // The suggested pairing is surfaced.
    expect(within(panel).getByText(/suggested pairing/i)).toBeInTheDocument();

    // One-click adopt — no selection needed.
    const btn = within(panel).getByRole("button", { name: /Adopt/i });
    expect(btn).not.toBeDisabled();
    fireEvent.click(btn);

    // Posts to the right instance with the auto pair's device ids.
    await waitFor(() => expect(adopt).toHaveBeenCalledTimes(1));
    expect(adopt).toHaveBeenCalledWith("shack-north", { tncDeviceId: "usb-0", radioDeviceId: "usb-1" });
  });

  it("renders a device's free vs in-use state", async () => {
    await mountHeadEnds(scanWith([AUTO_INSTANCE]));
    const panel = await waitFor(() => instancePanel("shack-north"));
    // usb-2 is bound to a running port → "in use"; the free ones show "free".
    expect(within(panel).getAllByText(/^free$/i).length).toBeGreaterThanOrEqual(2);
    expect(within(panel).getByText(/^in use$/i)).toBeInTheDocument();
  });

  it("an ambiguous instance gates Adopt until a TNC and a radio are chosen", async () => {
    const adopt = vi.spyOn(api, "adoptHeadEnd").mockResolvedValue(OK_RECONCILE);
    await mountHeadEnds(scanWith([AMBIGUOUS_INSTANCE]));

    const panel = await waitFor(() => instancePanel("garage-pi"));
    expect(within(panel).getByText(/choose a pairing/i)).toBeInTheDocument();

    // Nothing chosen yet → Adopt disabled.
    const btn = within(panel).getByRole("button", { name: /Adopt/i });
    expect(btn).toBeDisabled();

    // Choose the modem only → still disabled (needs both).
    fireEvent.change(within(panel).getByLabelText("Modem (NinoTNC)"), { target: { value: "acm-1" } });
    expect(btn).toBeDisabled();

    // Choose the radio → enabled, and adopt posts the chosen pair.
    fireEvent.change(within(panel).getByLabelText("Radio (Tait CCDI)"), { target: { value: "usb-0" } });
    expect(btn).not.toBeDisabled();
    fireEvent.click(btn);

    await waitFor(() => expect(adopt).toHaveBeenCalledTimes(1));
    expect(adopt).toHaveBeenCalledWith("garage-pi", { tncDeviceId: "acm-1", radioDeviceId: "usb-0" });
  });

  it("an adopt with an optional port id + modem mode posts them in the body", async () => {
    const adopt = vi.spyOn(api, "adoptHeadEnd").mockResolvedValue(OK_RECONCILE);
    await mountHeadEnds(scanWith([AUTO_INSTANCE]));

    const panel = await waitFor(() => instancePanel("shack-north"));
    // Open the options row and fill both fields.
    fireEvent.click(within(panel).getByRole("button", { name: /Options/i }));
    fireEvent.change(within(panel).getByPlaceholderText("shack-north"), { target: { value: "vhf-north" } });
    fireEvent.change(within(panel).getByPlaceholderText("0"), { target: { value: "4" } });

    fireEvent.click(within(panel).getByRole("button", { name: /Adopt/i }));
    await waitFor(() => expect(adopt).toHaveBeenCalledTimes(1));
    expect(adopt).toHaveBeenCalledWith("shack-north", {
      tncDeviceId: "usb-0", radioDeviceId: "usb-1", portId: "vhf-north", mode: 4,
    });
  });

  it("renders a duplicate-instance-id conflict with the remediation hint", async () => {
    await mountHeadEnds(scanWith([AUTO_INSTANCE], [{ instanceId: "spare-pi", addresses: ["192.168.1.90:8080", "192.168.1.91:8080"] }]));

    const conflicts = await waitFor(() => {
      const el = document.querySelector('[data-testid="headend-conflicts"]');
      expect(el).not.toBeNull();
      return el as HTMLElement;
    });
    expect(within(conflicts).getByText(/Duplicate head-end id/i)).toBeInTheDocument();
    // Both clashing addresses + the remediation guidance are shown.
    expect(within(conflicts).getByText("192.168.1.90:8080")).toBeInTheDocument();
    expect(within(conflicts).getByText("192.168.1.91:8080")).toBeInTheDocument();
    expect(within(conflicts).getByText(/pin distinct/i)).toBeInTheDocument();
  });

  it("an unreachable instance shows its error and offers no adopt", async () => {
    await mountHeadEnds(scanWith([UNREACHABLE_INSTANCE]));
    const panel = await waitFor(() => instancePanel("attic-relay"));
    expect(within(panel).getByText(/unreachable/i)).toBeInTheDocument();
    expect(within(panel).getByText(/connection refused/i)).toBeInTheDocument();
    // No adopt affordance on an unreachable head-end.
    expect(within(panel).queryByRole("button", { name: /Adopt/i })).toBeNull();
  });

  it("disables Adopt for a read-only scope (operate required)", async () => {
    await mountHeadEnds(scanWith([AUTO_INSTANCE]), "read");
    const panel = await waitFor(() => instancePanel("shack-north"));
    const btn = within(panel).getByRole("button", { name: /Adopt/i });
    expect(btn).toBeDisabled();
    expect(btn).toHaveAttribute("title", expect.stringMatching(/operate scope/i));
  });

  it("shows an empty state when no head-ends are found", async () => {
    await mountHeadEnds(scanWith([]));
    await waitFor(() => expect(screen.getByText(/No head-ends found/i)).toBeInTheDocument());
  });
});
