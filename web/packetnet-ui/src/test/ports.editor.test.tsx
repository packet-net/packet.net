// The Ports editor's round-trip fidelity, mounted against a seeded /config (#690 WP3).
// Every case here is a bug the 2026-08-16 review found on the live node:
//   C002 the stock Add-port body carried a UI catalogue id in `profile` (a guaranteed 422) and
//        an edit re-derived `profile` from a demo table keyed by port id;
//   C003/C004 the save rebuilt the port field-by-field, so mqttInstance, an adopted head-end
//        transport and its head-end radio were nulled by a PUT that replaces the entry wholesale;
//   C005 opening a port with ax25:/kiss: null spread UI defaults into the draft, so ANY save
//        persisted timings the operator never chose;
//   C036 the editor kept the previous draft when reopened;
//   C037 the 10 ms-unit KISS fields rewrote the box between keystrokes;
//   C040 a 422 rendered as a page banner behind the drawer's own modal overlay.
// Mount + spy style mirrors ports.rig.test.tsx.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Ports, previewDisruption } from "@/screens/ports";
import { api, ConfigRejected } from "@/lib/api";
import { NODE_CONFIG } from "@/lib/mock";
import type { NodeConfig, PortConfig, ReconcileResult } from "@/lib/types";

function seedScope(scope: "read" | "operate" | "admin" = "operate") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

const OK_RECONCILE: ReconcileResult = {
  valid: true, live: [], portRestart: [], nodeReset: [], applied: true,
};

// An untuned port: no channel profile, no AX.25 / KISS block, plus the MQTT label the head-end
// adopt flow writes and no screen edits.
const UNTUNED: PortConfig = {
  id: "sim", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 },
  profile: null, ax25: null, kiss: null, beacon: null, mqttInstance: "2m",
};

// An adopted split-station port exactly as HeadEndAdoption writes it.
const ADOPTED: PortConfig = {
  id: "2m", enabled: true,
  transport: { kind: "nino-tnc-tcp", headEndId: "shack-pi", deviceId: "nino-0", mode: 4 },
  profile: null, ax25: null, kiss: { txDelay: 30, persistence: 63 }, beacon: null,
  radio: { kind: "tait-ccdi", headEndId: "shack-pi", deviceId: "tait-0" },
  mqttInstance: "2m",
};

// A tuned port whose TX delay is 400 ms (40 wire units) - the field C037 mangles.
const TUNED: PortConfig = {
  id: "vhf-9", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8002 },
  profile: null, ax25: null, kiss: { txDelay: 40, persistence: 63, slotTime: 10, txTail: 0 }, beacon: null,
};

function seedConfig(...ports: PortConfig[]) {
  const cfg: NodeConfig = { ...NODE_CONFIG, ports };
  vi.spyOn(api, "config").mockResolvedValue(cfg);
}

async function mountPorts(): Promise<void> {
  seedScope();
  render(
    <MemoryRouter>
      <AuthProvider>
        <Ports />
      </AuthProvider>
    </MemoryRouter>,
  );
}

// Open one port's editor (the smoke-test walk: from the id label up to the card with an Edit button).
async function openEditor(portId: string): Promise<void> {
  await screen.findByText(portId);
  let card: HTMLElement | null = screen.getByText(portId);
  while (card && !within(card).queryByRole("button", { name: "Edit" })) card = card.parentElement;
  expect(card).not.toBeNull();
  fireEvent.click(within(card!).getByRole("button", { name: "Edit" }));
  // (the drawer title is the screen's own existing string, em dash and all)
  await waitFor(() => expect(screen.getByText(`Edit port — ${portId}`)).toBeInTheDocument());
}

// Save changes → confirm → Apply (the confirm button is worded by the disruption class).
async function saveAndApply(): Promise<void> {
  fireEvent.click(screen.getByRole("button", { name: /Save changes/i }));
  await screen.findByText("Apply changes?");
  fireEvent.click(screen.getByRole("button", { name: /^Apply( anyway)?$/i }));
}

beforeEach(() => localStorage.clear());
afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("PortEditor - what the save actually sends", () => {
  it("a stock Add port POSTs profile null and no AX.25 / KISS block (#690 C002/C005)", async () => {
    const addPort = vi.spyOn(api, "addPort").mockResolvedValue(OK_RECONCILE);
    seedConfig(UNTUNED);
    await mountPorts();

    fireEvent.click(await screen.findByRole("button", { name: /Add port/i }));
    fireEvent.change(screen.getByPlaceholderText("vhf-1"), { target: { value: "new-1" } });
    await saveAndApply();

    await waitFor(() => expect(addPort).toHaveBeenCalledTimes(1));
    const body = addPort.mock.calls[0][0];
    expect(body.id).toBe("new-1");
    // The server channel profile - NOT a RADIO_PROFILES id, which ChannelProfiles.IsKnown rejects.
    expect(body.profile).toBeNull();
    expect(body.ax25).toBeNull();
    expect(body.kiss).toBeNull();
    // Nothing from a demo fixture leaked in.
    expect(JSON.stringify(body)).not.toMatch(/vhf-fm-1200|uhf-data-9600|hf-robust-300/);
  });

  it("an untouched edit of a null-block port PUTs those nulls back and keeps mqttInstance (#690 C003/C005)", async () => {
    const editPort = vi.spyOn(api, "editPort").mockResolvedValue(OK_RECONCILE);
    seedConfig(UNTUNED);
    await mountPorts();
    await openEditor("sim");
    await saveAndApply();

    await waitFor(() => expect(editPort).toHaveBeenCalledTimes(1));
    const [id, body] = editPort.mock.calls[0];
    expect(id).toBe("sim");
    expect(body.ax25).toBeNull();
    expect(body.kiss).toBeNull();
    expect(body.mqttInstance).toBe("2m");
  });

  it("an adopted head-end port opens read-only and round-trips through a save unchanged (#690 C004)", async () => {
    const editPort = vi.spyOn(api, "editPort").mockResolvedValue(OK_RECONCILE);
    seedConfig(ADOPTED);
    await mountPorts();
    await openEditor("2m");

    // The transport kind is shown, not offered for re-typing (there is no head-end device picker
    // here, and re-typing would strand the radio pinned to that head-end).
    expect(screen.getByTestId("transport-locked")).toHaveTextContent("shack-pi/nino-0");
    expect(screen.getByTestId("radio-managed")).toHaveTextContent("shack-pi/tait-0");

    await saveAndApply();
    await waitFor(() => expect(editPort).toHaveBeenCalledTimes(1));
    const [, body] = editPort.mock.calls[0];
    expect(body.transport).toEqual(ADOPTED.transport);
    expect(body.radio).toEqual(ADOPTED.radio);
    expect(body.kiss).toEqual(ADOPTED.kiss);
    expect(body.mqttInstance).toBe("2m");
  });

  it("typing 150 into TX delay sends 15 wire units, not 100 ms (#690 C037)", async () => {
    const editPort = vi.spyOn(api, "editPort").mockResolvedValue(OK_RECONCILE);
    seedConfig(TUNED);
    await mountPorts();
    await openEditor("vhf-9");

    // The port is tuned, so the advanced section is open: 40 units renders as 400 ms.
    const field = screen.getByDisplayValue("400") as HTMLInputElement;
    // Type it the way an operator does - each keystroke appends to what the box currently shows,
    // which is what the round-trip-every-keystroke conversion used to corrupt.
    fireEvent.change(field, { target: { value: "" } });
    for (const ch of "150") {
      fireEvent.change(field, { target: { value: field.value + ch } });
    }
    expect(field.value).toBe("150");
    fireEvent.blur(field);

    await saveAndApply();
    await waitFor(() => expect(editPort).toHaveBeenCalledTimes(1));
    expect(editPort.mock.calls[0][1].kiss).toMatchObject({ txDelay: 15 });
  });

  it("reopening Add port yields an empty id (#690 C036)", async () => {
    seedConfig(UNTUNED);
    await mountPorts();

    fireEvent.click(await screen.findByRole("button", { name: /Add port/i }));
    fireEvent.change(screen.getByPlaceholderText("vhf-1"), { target: { value: "first-port" } });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    fireEvent.click(screen.getByRole("button", { name: /Add port/i }));
    await waitFor(() => expect(screen.getByText("Add port", { selector: "h2,h3,div,span" })).toBeInTheDocument());
    expect((screen.getByPlaceholderText("vhf-1") as HTMLInputElement).value).toBe("");
  });

  it("reopening Edit shows server state, not the abandoned draft (#690 C036)", async () => {
    seedConfig(UNTUNED);
    await mountPorts();

    await openEditor("sim");
    fireEvent.change(screen.getByPlaceholderText("vhf-1"), { target: { value: "scribbled" } });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    await openEditor("sim");
    expect((screen.getByPlaceholderText("vhf-1") as HTMLInputElement).value).toBe("sim");
  });

  it("a 422 renders inside the drawer, against the field it names, and dismisses (#690 C040)", async () => {
    vi.spyOn(api, "editPort").mockRejectedValue(new ConfigRejected({
      errors: [{
        path: "Ports[0].Profile",
        message: "Port.Profile 'vhf-fm-1200' is not a known channel profile (expected one of: slow-afsk1200).",
      }],
    }));
    seedConfig(UNTUNED);
    await mountPorts();
    await openEditor("sim");
    await saveAndApply();

    // Inside the still-open drawer - not a page banner dimmed behind its overlay.
    const box = await screen.findByTestId("port-save-error");
    expect(box).toHaveTextContent(/not a known channel profile/);
    // The path is shown field-relative (the array index is noise to the operator).
    expect(box).toHaveTextContent("Profile");
    expect(screen.getByText("Edit port — sim")).toBeInTheDocument();

    fireEvent.click(within(box).getByTitle("Dismiss"));
    expect(screen.queryByTestId("port-save-error")).toBeNull();
  });

  it("maps a per-field 422 onto the port id input", async () => {
    vi.spyOn(api, "editPort").mockRejectedValue(new ConfigRejected({
      errors: [{ path: "Ports[0].Id", message: "Port.Id is required (it is the reconcile key)." }],
    }));
    seedConfig(UNTUNED);
    await mountPorts();
    await openEditor("sim");
    await saveAndApply();

    await screen.findByTestId("port-save-error");
    // The message is repeated against the field itself (once in the banner, once inline).
    expect(screen.getAllByText(/is the reconcile key/)).toHaveLength(2);
  });
});

describe("PortEditor - what the confirmation promises", () => {
  it("attaching a radio is announced as a single-port restart, not a live change (#690 C038)", async () => {
    seedConfig({ ...TUNED, transport: { kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 } });
    await mountPorts();
    await openEditor("vhf-9");

    // Attach a radio (the Radio control section's switch) and bind it.
    const radio = screen.getByText("Radio control").closest("div")!.parentElement!;
    fireEvent.click(within(radio).getByRole("switch"));
    fireEvent.change(within(radio).getByPlaceholderText("e.g. 19925328"), { target: { value: "19925328" } });

    fireEvent.click(screen.getByRole("button", { name: /Save changes/i }));
    await screen.findByText("Apply changes?");
    expect(screen.getByText(/will restart/i)).toBeInTheDocument();
  });

  it("renaming a port is a per-port teardown + bring-up, not a node reset (#690 C038)", async () => {
    seedConfig(UNTUNED);
    await mountPorts();
    await openEditor("sim");
    fireEvent.change(screen.getByPlaceholderText("vhf-1"), { target: { value: "sim-2" } });

    fireEvent.click(screen.getByRole("button", { name: /Save changes/i }));
    await screen.findByText("Apply changes?");
    expect(screen.getByText(/torn down and brought back up as sim-2/i)).toBeInTheDocument();
    // The node-wide reset is reserved for a callsign change (ReconcilePlanner).
    expect(screen.queryByText(/every session on every port/i)).toBeNull();
  });

  it("an untouched port says there is nothing to apply", async () => {
    seedConfig(UNTUNED);
    await mountPorts();
    await openEditor("sim");

    fireEvent.click(screen.getByRole("button", { name: /Save changes/i }));
    await screen.findByText("Apply changes?");
    expect(screen.getByText(/No changes to apply to sim/i)).toBeInTheDocument();
  });

  it("renders the node's dry-run answer in the node's own grouping", async () => {
    // The live-mode path replaces the local estimate with the node's ReconcilePreview (POST/PUT
    // ?dryRun=true). This pins the mapping from that answer to what the operator is told.
    const change = (impact: "live" | "port-restart" | "node-reset", summary: string) =>
      ({ path: "ports.sim", impact, summary });

    expect(previewDisruption(
      { valid: true, applied: false, live: [], portRestart: [], nodeReset: [change("node-reset", "callsign changed")] },
      "sim", " No sessions are connected.",
    )).toMatchObject({ tone: "danger" });

    const restart = previewDisruption(
      { valid: true, applied: false, live: [], portRestart: [change("port-restart", "radio attached")], nodeReset: [] },
      "sim", " 2 sessions on this port will drop.",
    );
    expect(restart.tone).toBe("warning");
    expect(restart.text).toContain("Port sim will restart.");
    expect(restart.text).toContain("2 sessions on this port will drop.");

    expect(previewDisruption(
      { valid: true, applied: false, live: [change("live", "kiss params")], portRestart: [], nodeReset: [] },
      "sim", "",
    )).toMatchObject({ tone: "success" });

    // Nothing at all to do.
    expect(previewDisruption({ valid: true, applied: false, live: [], portRestart: [], nodeReset: [] }, "sim", ""))
      .toEqual({ tone: "success", text: "No change to the running node." });
  });

  it("shows what the node reported after the apply", async () => {
    vi.spyOn(api, "editPort").mockResolvedValue({
      valid: true, live: [], applied: true, nodeReset: [],
      portRestart: [{ path: "ports.sim", impact: "port-restart", summary: "transport changed" }],
    });
    seedConfig(UNTUNED);
    await mountPorts();
    await openEditor("sim");
    fireEvent.change(screen.getByDisplayValue("8001"), { target: { value: "8010" } });
    await saveAndApply();

    expect(await screen.findByText(/Port restarted: transport changed/i)).toBeInTheDocument();
  });
});
