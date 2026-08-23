// The Ports editor's "Program radio" panel (#779): write ONE channel - frequency, bandwidth, power -
// into a port's attached Tait, optionally applying a pdn upgrade profile.
//
// The panel is deliberately conservative about when it appears at all: a run acts on the LIVE node,
// so it is gated on the SAVED radio, and it is only offered for a radio there is a local programming
// interface to (not a head-end-bound one at the far end of a TCP bridge, not a `rig` CAT daemon).
// Getting that gate wrong either hides the feature from an operator who needs it or offers them a
// button that can only ever 400, so most of what is asserted here is the gate.
// Mount + spy style mirrors ports.editor.test.tsx.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Ports, isProgrammableRadio, mhzToHz } from "@/screens/ports";
import { api } from "@/lib/api";
import { NODE_CONFIG } from "@/lib/mock";
import type { NodeConfig, PortConfig, TaitProgramInfo } from "@/lib/types";

function seedScope(scope: "read" | "operate" | "admin" = "admin") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

// A NinoTNC port with a locally-cabled Tait CCDI radio bound by its CCDI serial - the shape the
// panel is for.
const TAIT_PORT: PortConfig = {
  id: "vhf-1", enabled: true,
  transport: { kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 },
  profile: null, ax25: null, kiss: null, beacon: null,
  radio: { kind: "tait-ccdi", serial: "19925328", baud: 28800 },
};

// The same port with its radio on a head-end: no local programming interface.
const HEAD_END_PORT: PortConfig = {
  id: "2m", enabled: true,
  transport: { kind: "nino-tnc-tcp", headEndId: "shack-pi", deviceId: "nino-0", mode: 4 },
  profile: null, ax25: null, kiss: null, beacon: null,
  radio: { kind: "tait-ccdi", headEndId: "shack-pi", deviceId: "tait-0" },
};

// A port with no radio at all.
const BARE_PORT: PortConfig = {
  id: "sim", enabled: true, transport: { kind: "kiss-tcp", host: "127.0.0.1", port: 8001 },
  profile: null, ax25: null, kiss: null, beacon: null,
};

const RUN: TaitProgramInfo = {
  portId: "vhf-1",
  state: "done",
  startedAt: "2026-08-23T10:15:00Z",
  finishedAt: "2026-08-23T10:18:00Z",
  devicePath: "/dev/ttyUSB1",
  plan: { rxFrequencyHz: 144_812_500, txFrequencyHz: 144_812_500, bandwidth: "narrow", power: "high", profile: "pdn-extra" },
  radioModel: "TMAB12-B100_0201",
  radioSerial: "19925328",
  backupPath: "/var/lib/packetnet/codeplug-backups/tait-19925328-20260823-101500.m8p",
  error: null,
};

function seedConfig(...ports: PortConfig[]) {
  const cfg: NodeConfig = { ...NODE_CONFIG, ports };
  vi.spyOn(api, "config").mockResolvedValue(cfg);
}

async function mountPorts(scope: "read" | "operate" | "admin" = "admin"): Promise<void> {
  seedScope(scope);
  render(
    <MemoryRouter>
      <AuthProvider>
        <Ports />
      </AuthProvider>
    </MemoryRouter>,
  );
}

async function openEditor(portId: string): Promise<void> {
  await screen.findByText(portId);
  let card: HTMLElement | null = screen.getByText(portId);
  while (card && !within(card).queryByRole("button", { name: "Edit" })) card = card.parentElement;
  expect(card).not.toBeNull();
  fireEvent.click(within(card!).getByRole("button", { name: "Edit" }));
  await waitFor(() => expect(screen.getByText(`Edit port — ${portId}`)).toBeInTheDocument());
}

beforeEach(() => {
  localStorage.clear();
  // Nothing has ever been programmed unless a test says so.
  vi.spyOn(api, "radioProgram").mockResolvedValue(null);
});
afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("isProgrammableRadio - which radios the panel is offered for", () => {
  it("a locally-cabled Tait bound by CCDI serial or device path is programmable", () => {
    expect(isProgrammableRadio({ kind: "tait-ccdi", serial: "19925328" })).toBe(true);
    expect(isProgrammableRadio({ kind: "tait-ccdi", port: "/dev/ttyUSB0" })).toBe(true);
  });

  it("a head-end-bound Tait is not - the boot latch needs a directly-cabled line", () => {
    expect(isProgrammableRadio({ kind: "tait-ccdi", headEndId: "shack-pi", deviceId: "tait-0" })).toBe(false);
  });

  it("a rig-backed radio is not - it is a CAT daemon, not a Tait", () => {
    expect(isProgrammableRadio({ kind: "rig" })).toBe(false);
  });

  it("no radio, or one bound to nothing, is not", () => {
    expect(isProgrammableRadio(null)).toBe(false);
    expect(isProgrammableRadio(undefined)).toBe(false);
    expect(isProgrammableRadio({ kind: "tait-ccdi" })).toBe(false);
  });
});

describe("mhzToHz - what the frequency box accepts", () => {
  it("converts MHz to whole hertz", () => {
    expect(mhzToHz("144.812500")).toBe(144_812_500);
    expect(mhzToHz(" 433.4 ")).toBe(433_400_000);
  });

  it("refuses anything no Tait band split reaches, so the button never sends a guaranteed 400", () => {
    expect(mhzToHz("")).toBeNull();
    expect(mhzToHz("not a frequency")).toBeNull();
    expect(mhzToHz("14.100")).toBeNull();
    expect(mhzToHz("2400")).toBeNull();
  });
});

describe("PortEditor - the Program radio panel", () => {
  it("shows for a port with a locally-cabled Tait", async () => {
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");

    const panel = await screen.findByTestId("radio-programming");
    expect(panel).toHaveTextContent("Program radio");
    // The three profile choices the issue asked for.
    expect(panel).toHaveTextContent("Don't apply one");
    expect(panel).toHaveTextContent("pdn-basic");
    expect(panel).toHaveTextContent("pdn-extra");
    // And the consequences, said out loud rather than discovered.
    expect(panel).toHaveTextContent(/stops the port/i);
    expect(panel).toHaveTextContent(/power-cycle the radio/i);
  });

  it("stays away for a head-end-bound radio", async () => {
    seedConfig(HEAD_END_PORT);
    await mountPorts();
    await openEditor("2m");

    expect(screen.getByTestId("radio-managed")).toBeInTheDocument();
    expect(screen.queryByTestId("radio-programming")).not.toBeInTheDocument();
  });

  it("stays away for a port with no radio attached", async () => {
    seedConfig(BARE_PORT);
    await mountPorts();
    await openEditor("sim");

    expect(screen.queryByTestId("radio-programming")).not.toBeInTheDocument();
  });

  it("posts hertz, the chosen bandwidth/power and the chosen profile", async () => {
    const start = vi.spyOn(api, "startRadioProgram").mockResolvedValue({ ...RUN, state: "starting", finishedAt: null });
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    fireEvent.change(within(panel).getByLabelText("Receive (MHz)"), { target: { value: "144.9375" } });
    fireEvent.change(within(panel).getByDisplayValue("Narrow - 12.5 kHz"), { target: { value: "wide" } });
    fireEvent.change(within(panel).getByDisplayValue("High"), { target: { value: "low" } });
    fireEvent.click(within(panel).getByLabelText(/pdn-extra/i, { selector: "input" }));

    fireEvent.click(within(panel).getByRole("button", { name: /Program radio/i }));
    // A run is consequential enough to confirm rather than fire on one click.
    await screen.findByText("Program this radio?");
    fireEvent.click(screen.getByRole("button", { name: /^Program radio$/ }));

    await waitFor(() => expect(start).toHaveBeenCalledTimes(1));
    expect(start.mock.calls[0]).toEqual(["vhf-1", {
      rxFrequencyHz: 144_937_500,
      txFrequencyHz: 144_937_500,
      bandwidth: "wide",
      power: "low",
      profile: "pdn-extra",
    }]);
  });

  it("sends a split channel when 'same as receive' is unticked", async () => {
    const start = vi.spyOn(api, "startRadioProgram").mockResolvedValue({ ...RUN, state: "starting", finishedAt: null });
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    fireEvent.change(within(panel).getByLabelText("Receive (MHz)"), { target: { value: "145.625" } });
    fireEvent.click(within(panel).getByLabelText(/same as receive/i));
    fireEvent.change(within(panel).getByLabelText("Transmit (MHz)"), { target: { value: "145.025" } });

    fireEvent.click(within(panel).getByRole("button", { name: /Program radio/i }));
    await screen.findByText("Program this radio?");
    fireEvent.click(screen.getByRole("button", { name: /^Program radio$/ }));

    await waitFor(() => expect(start).toHaveBeenCalledTimes(1));
    expect(start.mock.calls[0][1].rxFrequencyHz).toBe(145_625_000);
    expect(start.mock.calls[0][1].txFrequencyHz).toBe(145_025_000);
  });

  it("will not offer to start until a reachable frequency is typed", async () => {
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    const button = within(panel).getByRole("button", { name: /Program radio/i });
    expect(button).toBeDisabled();

    fireEvent.change(within(panel).getByLabelText("Receive (MHz)"), { target: { value: "14.100" } });
    expect(button).toBeDisabled();

    fireEvent.change(within(panel).getByLabelText("Receive (MHz)"), { target: { value: "144.8125" } });
    expect(button).toBeEnabled();
  });

  it("is read-only without the admin scope - visible, but it will not run", async () => {
    seedConfig(TAIT_PORT);
    await mountPorts("operate");
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    fireEvent.change(within(panel).getByLabelText("Receive (MHz)"), { target: { value: "144.8125" } });

    const button = within(panel).getByRole("button", { name: /Program radio/i });
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute("title", expect.stringContaining("admin"));
  });

  it("re-attaches to the run the node already has, so a reopened editor is not a blank panel", async () => {
    vi.spyOn(api, "radioProgram").mockResolvedValue(RUN);
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    await waitFor(() => expect(panel).toHaveTextContent("Programmed"));
    expect(panel).toHaveTextContent("TMAB12-B100_0201");
    expect(panel).toHaveTextContent("codeplug backed up to");
    // The button reads as a repeat, not a first run.
    expect(within(panel).getByRole("button", { name: /Program again/i })).toBeInTheDocument();
  });

  it("surfaces a refusal from the node rather than swallowing it", async () => {
    vi.spyOn(api, "startRadioProgram").mockRejectedValue(new Error("port 'vhf-1' is busy with a tuning session - stop it first"));
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    fireEvent.change(within(panel).getByLabelText("Receive (MHz)"), { target: { value: "144.8125" } });
    fireEvent.click(within(panel).getByRole("button", { name: /Program radio/i }));
    await screen.findByText("Program this radio?");
    fireEvent.click(screen.getByRole("button", { name: /^Program radio$/ }));

    await waitFor(() => expect(panel).toHaveTextContent("busy with a tuning session"));
  });
});
