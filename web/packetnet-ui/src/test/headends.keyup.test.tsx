// Tests for the Head-ends keyup-pairing flow (#579): the RF-emitting physical modem↔radio resolver
// (POST /api/v1/radios/headends/{id}/pair-by-keyup, ADMIN scope — it transmits, the same bar as
// hail/tuning/doctor). Mounts the screen against a spied api client and drives:
//   - the admin-scope gate (operate sees the button disabled with an admin-scope title);
//   - the RF-warning confirm dialog (nothing posts until the operator confirms);
//   - the confirmed run: posts to the right instance, renders the resolved pairs + the server's
//     caveat, refreshes the scan, and PRE-SELECTS the adopt pickers with the resolved pair;
//   - the honest reachable:false result (error shown, nothing pre-selected).
// Mirrors headends.panel.test.tsx's mount + spy style.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { HeadEnds } from "@/screens/headends";
import { api } from "@/lib/api";
import type { HeadEndScan, HeadEndInstanceScan, HeadEndKeyupResult } from "@/lib/types";

function seedScope(scope: "read" | "operate" | "admin") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

const CAVEAT =
  "RF WARNING: this action briefly keyed (transmitted through) each free NinoTNC on the head-end " +
  "to discover its physically-cabled radio by the PTT it asserts.";

// An ambiguous instance (two free TNCs + two free radios) — where keyup pairing earns its keep.
const AMBIGUOUS_INSTANCE: HeadEndInstanceScan = {
  instanceId: "garage-pi",
  host: "192.168.1.51",
  httpPort: 8080,
  source: "config",
  reachable: true,
  error: null,
  devices: [
    { deviceId: "acm-0", kind: "nino-tnc", model: "NinoTNC N9600A4", version: "3.44", serial: null, baud: 57600, free: true, bandCode: null, amateurBand: null, idSource: "by-path", idStable: true },
    { deviceId: "acm-1", kind: "nino-tnc", model: "NinoTNC N9600A3", version: "3.41", serial: null, baud: 57600, free: true, bandCode: null, amateurBand: null, idSource: "by-path", idStable: true },
    { deviceId: "usb-0", kind: "tait-ccdi", model: "Tait TM8110", version: "1.10.0", serial: "2G001111", baud: 28800, free: true, bandCode: "B1", amateurBand: "2m", idSource: "by-path", idStable: true },
    { deviceId: "usb-1", kind: "tait-ccdi", model: "Tait TM8200", version: "2.03.0", serial: "2G002222", baud: 19200, free: true, bandCode: "H5", amateurBand: "70cm", idSource: "by-path", idStable: true },
  ],
  proposedPairs: [],
  pairingAmbiguous: true,
};

const RESOLVED: HeadEndKeyupResult = {
  instanceId: "garage-pi",
  reachable: true,
  error: null,
  pairs: [
    { tncDeviceId: "acm-1", radioDeviceId: "usb-0" },
    { tncDeviceId: "acm-0", radioDeviceId: "usb-1" },
  ],
  unpairedTncs: [],
  unpairedRadios: [],
  ambiguous: [],
  caveat: CAVEAT,
};

function scanWith(instances: HeadEndInstanceScan[]): HeadEndScan {
  return { instances, conflicts: [] };
}

async function mountHeadEnds(scan: HeadEndScan, scope: "read" | "operate" | "admin") {
  seedScope(scope);
  const getHeadEnds = vi.spyOn(api, "getHeadEnds").mockResolvedValue(scan);
  render(
    <MemoryRouter>
      <AuthProvider>
        <HeadEnds />
      </AuthProvider>
    </MemoryRouter>,
  );
  await screen.findByText("Head-ends");
  return getHeadEnds;
}

function instancePanel(instanceId: string): HTMLElement {
  const el = document.querySelector(`[data-testid="headend-${instanceId}"]`);
  expect(el).not.toBeNull();
  return el as HTMLElement;
}

const keyupButton = (panel: HTMLElement) =>
  within(panel).getByRole("button", { name: /Resolve physically/i });

beforeEach(() => localStorage.clear());
afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Head-ends — keyup pairing (RF-emitting, admin scope)", () => {
  it("gates the keyup button on the admin scope (operate is not enough — it transmits)", async () => {
    await mountHeadEnds(scanWith([AMBIGUOUS_INSTANCE]), "operate");
    const panel = await waitFor(() => instancePanel("garage-pi"));

    const btn = keyupButton(panel);
    expect(btn).toBeDisabled();
    expect(btn).toHaveAttribute("title", expect.stringMatching(/admin scope/i));
  });

  it("confirms before keying: the RF warning shows and nothing posts until confirmed", async () => {
    const pair = vi.spyOn(api, "pairHeadEndByKeyup").mockResolvedValue(RESOLVED);
    await mountHeadEnds(scanWith([AMBIGUOUS_INSTANCE]), "admin");
    const panel = await waitFor(() => instancePanel("garage-pi"));

    const btn = keyupButton(panel);
    expect(btn).not.toBeDisabled();
    fireEvent.click(btn);

    // The confirm dialog carries the RF warning; the API has NOT been hit yet.
    const warning = await screen.findByTestId("keyup-rf-warning");
    expect(warning.textContent).toMatch(/this transmits/i);
    expect(warning.textContent).toMatch(/licensed and clear to key/i);
    expect(pair).not.toHaveBeenCalled();

    // Cancel keys nothing.
    fireEvent.click(screen.getByRole("button", { name: /^Cancel$/i }));
    expect(pair).not.toHaveBeenCalled();
    expect(screen.queryByTestId("keyup-rf-warning")).toBeNull();
  });

  it("a confirmed run posts, renders the resolved pairs + caveat, rescans, and pre-selects the pickers", async () => {
    const pair = vi.spyOn(api, "pairHeadEndByKeyup").mockResolvedValue(RESOLVED);
    const getHeadEnds = await mountHeadEnds(scanWith([AMBIGUOUS_INSTANCE]), "admin");
    const panel = await waitFor(() => instancePanel("garage-pi"));

    fireEvent.click(keyupButton(panel));
    fireEvent.click(await screen.findByRole("button", { name: /Key up and resolve/i }));

    // Posted to the right instance, once.
    await waitFor(() => expect(pair).toHaveBeenCalledTimes(1));
    expect(pair).toHaveBeenCalledWith("garage-pi");

    // The result phase lists the physically-resolved pairs and quotes the server's RF caveat.
    const result = await screen.findByTestId("keyup-result");
    expect(within(result).getByText(/2 physical pairs resolved/i)).toBeInTheDocument();
    expect(result.textContent).toContain("acm-1");
    expect(result.textContent).toContain("RF WARNING");

    // The scan refreshed (initial mount + post-keyup rescan).
    await waitFor(() => expect(getHeadEnds.mock.calls.length).toBeGreaterThanOrEqual(2));

    // The first resolved pair pre-selected the adopt pickers → Adopt is armed with ground truth.
    fireEvent.click(screen.getByRole("button", { name: /^Done$/i }));
    expect((within(panel).getByLabelText("Modem (NinoTNC)") as HTMLSelectElement).value).toBe("acm-1");
    expect((within(panel).getByLabelText("Radio (Tait CCDI)") as HTMLSelectElement).value).toBe("usb-0");

    // ... and adopting now posts the physically-verified pair (with the radio's band).
    const adopt = vi.spyOn(api, "adoptHeadEnd").mockResolvedValue({
      valid: true, live: [], portRestart: [], nodeReset: [], applied: true,
    });
    fireEvent.click(within(panel).getByRole("button", { name: /Adopt/i }));
    await waitFor(() => expect(adopt).toHaveBeenCalledTimes(1));
    expect(adopt).toHaveBeenCalledWith("garage-pi", { tncDeviceId: "acm-1", radioDeviceId: "usb-0", amateurBand: "2m" });
  });

  it("an honest reachable:false result shows its error and pre-selects nothing", async () => {
    vi.spyOn(api, "pairHeadEndByKeyup").mockResolvedValue({
      instanceId: "garage-pi", reachable: false, error: "another probe is already in flight",
      pairs: [], unpairedTncs: [], unpairedRadios: [], ambiguous: [], caveat: CAVEAT,
    });
    await mountHeadEnds(scanWith([AMBIGUOUS_INSTANCE]), "admin");
    const panel = await waitFor(() => instancePanel("garage-pi"));

    fireEvent.click(keyupButton(panel));
    fireEvent.click(await screen.findByRole("button", { name: /Key up and resolve/i }));

    expect(await screen.findByText(/another probe is already in flight/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /^Done$/i }));
    expect((within(panel).getByLabelText("Modem (NinoTNC)") as HTMLSelectElement).value).toBe("");
    expect((within(panel).getByLabelText("Radio (Tait CCDI)") as HTMLSelectElement).value).toBe("");
  });

  it("offers no keyup on an instance without a free TNC + radio to key", async () => {
    const noFreeRadio: HeadEndInstanceScan = {
      ...AMBIGUOUS_INSTANCE,
      instanceId: "tnc-only",
      devices: AMBIGUOUS_INSTANCE.devices.filter((d) => d.kind === "nino-tnc"),
      pairingAmbiguous: false,
    };
    await mountHeadEnds(scanWith([noFreeRadio]), "admin");
    const panel = await waitFor(() => instancePanel("tnc-only"));
    expect(within(panel).queryByRole("button", { name: /Resolve physically/i })).toBeNull();
  });
});
