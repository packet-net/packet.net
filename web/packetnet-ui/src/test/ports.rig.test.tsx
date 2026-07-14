// Tests for the PortEditor's rig (CAT control) section — plug-and-play rig, stage 2's UI leg:
// scan → pick the detected device → confirm/pick the hamlib model → save through the EXISTING
// port write path (api.editPort). Mounts the Ports screen against the mock API backend and spies
// api.scanRigs / api.getRigModels / api.editPort to drive each state:
//   - "Scan for rigs" calls api.scanRigs and renders the device rows;
//   - a claimed device's row is disabled and says what claims it;
//   - picking the suggested row fills device (byIdPath preferred) + model from the suggestion;
//   - a suggestion-less device leaves the model to the searchable picker (filter-as-you-type);
//   - available:false disables the picker with the "hamlib is not installed" note;
//   - an existing BYO daemon block opens on the daemon path with kind/host/port shown;
//   - read scope renders the section read-only (disable-never-hide);
//   - saving posts the rig block through api.editPort.
// Mirrors headends.panel.test.tsx's mount + spy style (seed scope, spy the API client, testid).
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Ports } from "@/screens/ports";
import { api } from "@/lib/api";
import type { RigScan, RigModelCatalogue, ReconcileResult } from "@/lib/types";

function seedScope(scope: "read" | "operate" | "admin") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

const ICOM_BY_ID = "/dev/serial/by-id/usb-Icom_Inc._IC-7300_IC-7300_02012345-if00-port0";

// Matches the backend contract for GET /api/v1/rigs/scan exactly: a suggested Icom on by-id, a
// claimed device (row disabled), and a bare FTDI CAT cable with no suggestion (model picker required).
const SCAN: RigScan = {
  devices: [
    {
      devicePath: "/dev/ttyUSB3",
      byIdPath: ICOM_BY_ID,
      descriptor: "usb-Icom_Inc._IC-7300_IC-7300_02012345-if00-port0",
      claimedBy: null,
      suggestion: { manufacturer: "Icom", model: "IC-7300", modelNumber: 3073, source: "by-id" },
    },
    { devicePath: "/dev/ttyUSB1", byIdPath: null, descriptor: null, claimedBy: "port 'hf-300' transport (serial-kiss)", suggestion: null },
    {
      devicePath: "/dev/ttyUSB4",
      byIdPath: "/dev/serial/by-id/usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0",
      descriptor: "usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0",
      claimedBy: null,
      suggestion: null,
    },
  ],
  catalogueAvailable: true,
};

// Matches the backend contract for GET /api/v1/rigs/models.
const MODELS: RigModelCatalogue = {
  available: true,
  models: [
    { number: 1, manufacturer: "Hamlib", model: "Dummy", status: "Stable" },
    { number: 2031, manufacturer: "Kenwood", model: "TS-590S", status: "Stable" },
    { number: 3073, manufacturer: "Icom", model: "IC-7300", status: "Stable" },
    { number: 3081, manufacturer: "Icom", model: "IC-9700", status: "Stable" },
  ],
};

const OK_RECONCILE: ReconcileResult = {
  valid: true,
  live: [{ path: "ports.uhf-2", impact: "port-restart", summary: "rig attached" }],
  portRestart: [], nodeReset: [], applied: true,
};

// Mount the Ports screen and open the editor for one port; resolves the rig section's root.
async function openEditor(portId: string, scope: "read" | "operate" | "admin" = "operate"): Promise<HTMLElement> {
  seedScope(scope);
  render(
    <MemoryRouter>
      <AuthProvider>
        <Ports />
      </AuthProvider>
    </MemoryRouter>,
  );
  await screen.findByText(portId);
  // Walk up from the port-id label to the enclosing card (the smoke-test pattern), then Edit.
  let card: HTMLElement | null = screen.getByText(portId);
  while (card && !within(card).queryByRole("button", { name: "Edit" })) card = card.parentElement;
  expect(card).not.toBeNull();
  fireEvent.click(within(card!).getByRole("button", { name: "Edit" }));
  await waitFor(() => expect(screen.getByText(`Edit port — ${portId}`)).toBeInTheDocument());
  return screen.getByTestId("rig-control");
}

// Enable the section's attach switch (the only switch inside the rig section) and scan.
async function attachAndScan(section: HTMLElement): Promise<void> {
  fireEvent.click(within(section).getByRole("switch"));
  fireEvent.click(within(section).getByRole("button", { name: /Scan for rigs/i }));
  await within(section).findByText("usb-Icom_Inc._IC-7300_IC-7300_02012345-if00-port0");
}

beforeEach(() => localStorage.clear());
afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("PortEditor — rig (CAT control) section", () => {
  it("scan calls api.scanRigs and renders every device row, with the suggestion labelled", async () => {
    const scanRigs = vi.spyOn(api, "scanRigs").mockResolvedValue(SCAN);
    vi.spyOn(api, "getRigModels").mockResolvedValue(MODELS);

    const section = await openEditor("uhf-2");
    await attachAndScan(section);

    expect(scanRigs).toHaveBeenCalledTimes(1);
    // The suggested row carries the curated match label: "Icom IC-7300 · hamlib #3073".
    expect(within(section).getByText("Icom IC-7300 · hamlib #3073")).toBeInTheDocument();
    // The suggestion-less FTDI renders by its descriptor.
    expect(within(section).getByText("usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0")).toBeInTheDocument();
    // The claimed device renders by its raw path (no descriptor) and says what claims it.
    expect(within(section).getByText(/claimed by port 'hf-300' transport \(serial-kiss\)/)).toBeInTheDocument();
  });

  it("a claimed device's row is disabled", async () => {
    vi.spyOn(api, "scanRigs").mockResolvedValue(SCAN);
    vi.spyOn(api, "getRigModels").mockResolvedValue(MODELS);

    const section = await openEditor("uhf-2");
    await attachAndScan(section);

    const claimedRow = within(section).getByRole("button", { name: /claimed by port 'hf-300'/ });
    expect(claimedRow).toBeDisabled();
    expect(claimedRow).toHaveAttribute("title", expect.stringMatching(/claimed by port 'hf-300'/));
    // The unclaimed rows stay pickable.
    expect(within(section).getByRole("button", { name: /usb-Icom_Inc/ })).not.toBeDisabled();
  });

  it("picking the suggested row fills device (by-id path) + model, and the section reads attached", async () => {
    vi.spyOn(api, "scanRigs").mockResolvedValue(SCAN);
    vi.spyOn(api, "getRigModels").mockResolvedValue(MODELS);

    const section = await openEditor("uhf-2");
    await attachAndScan(section);
    fireEvent.click(within(section).getByRole("button", { name: /usb-Icom_Inc/ }));

    // The device binds by the stable by-id path, not the raw /dev/ttyUSB3.
    expect(within(section).getByDisplayValue(ICOM_BY_ID)).toBeInTheDocument();
    // The model filled from the suggestion, resolved against the catalogue.
    await within(section).findByText("Icom IC-7300 (#3073)");
    expect(within(section).getByText("attached")).toBeInTheDocument();
  });

  it("a suggestion-less device leaves the model to the picker, which filters as you type", async () => {
    vi.spyOn(api, "scanRigs").mockResolvedValue(SCAN);
    vi.spyOn(api, "getRigModels").mockResolvedValue(MODELS);

    const section = await openEditor("uhf-2");
    await attachAndScan(section);
    fireEvent.click(within(section).getByRole("button", { name: /usb-FTDI_FT232R/ }));

    // Device bound, but no model yet → incomplete, and the picker prompts.
    expect(within(section).getByText("incomplete")).toBeInTheDocument();
    await within(section).findByText(/No model selected yet/);

    // Filter-as-you-type narrows the catalogue: "7300" matches only the IC-7300.
    fireEvent.change(within(section).getByLabelText("Search rig models"), { target: { value: "7300" } });
    const match = within(section).getByRole("button", { name: /Icom IC-7300 \(#3073\)/ });
    expect(within(section).queryByText(/Hamlib Dummy/)).toBeNull();
    expect(within(section).queryByText(/TS-590S/)).toBeNull();

    // Picking the match completes the attachment.
    fireEvent.click(match);
    expect(within(section).getByText("Icom IC-7300 (#3073)")).toBeInTheDocument();
    expect(within(section).getByText("attached")).toBeInTheDocument();
  });

  it("disables the model picker with a note when hamlib is not installed on the node", async () => {
    vi.spyOn(api, "getRigModels").mockResolvedValue({ available: false, models: [] });

    const section = await openEditor("uhf-2");
    fireEvent.click(within(section).getByRole("switch"));

    await within(section).findByText(/hamlib is not installed on the node/i);
    expect(within(section).queryByLabelText("Search rig models")).toBeNull();
  });

  it("an existing BYO daemon block opens on the daemon path with kind + host + port shown", async () => {
    // vhf-1's mock config carries rig: { kind: "flrig", host: "127.0.0.1", port: 12345 }.
    const section = await openEditor("vhf-1");

    expect(within(section).getByTestId("rig-summary")).toHaveTextContent("flrig · 127.0.0.1:12345");
    expect(within(section).getByLabelText("Daemon kind")).toHaveValue("flrig");
    expect(within(section).getByDisplayValue("127.0.0.1")).toBeInTheDocument();
    expect(within(section).getByDisplayValue("12345")).toBeInTheDocument();
  });

  it("renders read-only without the operate scope (disable-never-hide)", async () => {
    // hf-300's mock config carries rig: { kind: "hamlib", host: "127.0.0.1", port: 4532 } —
    // the section stays visible and readable, but every mutating control is disabled.
    const section = await openEditor("hf-300", "read");

    expect(within(section).getByTestId("rig-summary")).toHaveTextContent("hamlib · 127.0.0.1:4532");
    const sw = within(section).getByRole("switch");
    expect(sw).toBeDisabled();
    expect(sw).toHaveAttribute("title", expect.stringMatching(/operate scope/i));
    expect(within(section).getByLabelText("Daemon kind")).toBeDisabled();
    expect(within(section).getByDisplayValue("127.0.0.1")).toBeDisabled();
    expect(within(section).getByDisplayValue("4532")).toBeDisabled();
  });

  it("saving posts the picked rig through the existing port write path (api.editPort)", async () => {
    vi.spyOn(api, "scanRigs").mockResolvedValue(SCAN);
    vi.spyOn(api, "getRigModels").mockResolvedValue(MODELS);
    const editPort = vi.spyOn(api, "editPort").mockResolvedValue(OK_RECONCILE);

    const section = await openEditor("uhf-2");
    await attachAndScan(section);
    fireEvent.click(within(section).getByRole("button", { name: /usb-Icom_Inc/ }));

    // Save → the confirm modal (a rig edit alone is a parameter-level change) → Apply.
    fireEvent.click(screen.getByRole("button", { name: /Save changes/i }));
    await screen.findByText("Apply changes?");
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    await waitFor(() => expect(editPort).toHaveBeenCalledTimes(1));
    expect(editPort).toHaveBeenCalledWith(
      "uhf-2",
      expect.objectContaining({
        id: "uhf-2",
        rig: expect.objectContaining({ kind: "hamlib", device: ICOM_BY_ID, model: 3073 }),
      }),
    );
  });
});
