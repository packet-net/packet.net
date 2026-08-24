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
import { Ports, isProgrammableRadio, parseFrequencyHz } from "@/screens/ports";
import { api } from "@/lib/api";
import { NODE_CONFIG } from "@/lib/mock";
import type { NodeConfig, PortConfig, TaitProgramInfo, TaitTestTxResult } from "@/lib/types";

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
  mode: "program",
  state: "done",
  startedAt: "2026-08-23T10:15:00Z",
  finishedAt: "2026-08-23T10:18:00Z",
  devicePath: "/dev/ttyUSB1",
  plan: {
    rxFrequencyHz: 144_812_500, txFrequencyHz: 144_812_500,
    bandwidth: "narrow", power: "high", profile: "pdn-extra", replaceChannelTable: true,
  },
  current: null,
  radioModel: "TMAB12-B100_0201",
  radioSerial: "19925328",
  backupPath: "/var/lib/packetnet/codeplug-backups/tait-19925328-20260823-101500.m8p",
  error: null,
  failedState: null,
  log: [],
};

// A finished READ run: no plan, and what the radio turned out to be set to.
const READ_RUN: TaitProgramInfo = {
  ...RUN,
  mode: "read",
  plan: null,
  startedAt: "2026-08-23T11:00:00Z",
  finishedAt: "2026-08-23T11:02:00Z",
  current: {
    rxFrequencyHz: 145_287_500, txFrequencyHz: 145_287_500,
    bandwidth: "wide", power: "medium", profile: "pdn-basic",
    channelCount: 6, databaseVersion: "0095", rxTone: "none", txTone: "none",
  },
};

// Bandwidth and power start blank, so a test that means to START a run has to choose them the way
// an operator would. Reading the radio fills them in instead; typing them is the other half.
function fillChannel(panel: HTMLElement, mhz: string) {
  fireEvent.change(within(panel).getByLabelText("Frequency"), { target: { value: mhz } });
  fireEvent.change(within(panel).getByLabelText("Bandwidth"), { target: { value: "narrow" } });
  fireEvent.change(within(panel).getByLabelText("Transmit power"), { target: { value: "high" } });
}

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

describe("parseFrequencyHz - what the frequency box accepts", () => {
  it("reads decimal megahertz", () => {
    expect(parseFrequencyHz("144.812500")).toBe(144_812_500);
    expect(parseFrequencyHz(" 433.4 ")).toBe(433_400_000);
    expect(parseFrequencyHz("144")).toBe(144_000_000);
  });

  it("reads plain hertz too - the magnitude tells them apart with no overlap to worry about", () => {
    expect(parseFrequencyHz("144812500")).toBe(144_812_500);
    expect(parseFrequencyHz("144,812,500")).toBe(144_812_500);
    expect(parseFrequencyHz("433 400 000")).toBe(433_400_000);
  });

  it("honours a written-out unit over the magnitude rule", () => {
    expect(parseFrequencyHz("144.8125 MHz")).toBe(144_812_500);
    expect(parseFrequencyHz("144812500 Hz")).toBe(144_812_500);
    expect(parseFrequencyHz("144812.5 kHz")).toBe(144_812_500);
  });

  it("refuses anything no Tait band split reaches, so the button never sends a guaranteed 400", () => {
    expect(parseFrequencyHz("")).toBeNull();
    expect(parseFrequencyHz("not a frequency")).toBeNull();
    expect(parseFrequencyHz("14.100")).toBeNull();
    expect(parseFrequencyHz("2400")).toBeNull();
    expect(parseFrequencyHz("14100000")).toBeNull();
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

  it("posts hertz, the chosen bandwidth/power and the chosen profile - and no TX frequency, because packet is simplex", async () => {
    const start = vi.spyOn(api, "startRadioProgram").mockResolvedValue({ ...RUN, state: "starting", finishedAt: null });
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    fireEvent.change(within(panel).getByLabelText("Frequency"), { target: { value: "144.9375" } });
    fireEvent.change(within(panel).getByLabelText("Bandwidth"), { target: { value: "wide" } });
    fireEvent.change(within(panel).getByLabelText("Transmit power"), { target: { value: "low" } });
    fireEvent.click(within(panel).getByLabelText(/pdn-extra/i, { selector: "input" }));

    fireEvent.click(within(panel).getByRole("button", { name: /Program radio/i }));
    // A run is consequential enough to confirm rather than fire on one click.
    await screen.findByText("Program this radio?");
    fireEvent.click(screen.getByRole("button", { name: /^Program radio$/ }));

    await waitFor(() => expect(start).toHaveBeenCalledTimes(1));
    expect(start.mock.calls[0]).toEqual(["vhf-1", {
      rxFrequencyHz: 144_937_500,
      bandwidth: "wide",
      power: "low",
      profile: "pdn-extra",
      replaceChannelTable: true,
    }]);
  });

  it("takes the frequency in hertz as readily as in megahertz", async () => {
    const start = vi.spyOn(api, "startRadioProgram").mockResolvedValue({ ...RUN, state: "starting", finishedAt: null });
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    fillChannel(panel, "144812500");
    // The box says back what it made of it, so a mistyped unit is visible before the run starts.
    expect(panel).toHaveTextContent("144.812500 MHz");

    fireEvent.click(within(panel).getByRole("button", { name: /Program radio/i }));
    await screen.findByText("Program this radio?");
    fireEvent.click(screen.getByRole("button", { name: /^Program radio$/ }));

    await waitFor(() => expect(start).toHaveBeenCalledTimes(1));
    expect(start.mock.calls[0][1].rxFrequencyHz).toBe(144_812_500);
  });

  it("leaves the other channels alone when asked to", async () => {
    const start = vi.spyOn(api, "startRadioProgram").mockResolvedValue({ ...RUN, state: "starting", finishedAt: null });
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    fillChannel(panel, "144.8125");
    fireEvent.click(within(panel).getByLabelText(/Delete the radio's other channels/i));

    fireEvent.click(within(panel).getByRole("button", { name: /Program radio/i }));
    await screen.findByText("Program this radio?");
    fireEvent.click(screen.getByRole("button", { name: /^Program radio$/ }));

    await waitFor(() => expect(start).toHaveBeenCalledTimes(1));
    expect(start.mock.calls[0][1].replaceChannelTable).toBe(false);
  });

  it("reads the radio without writing it, and fills the form in from what came back", async () => {
    // Resolved already-finished: the run itself is exercised by the node-side tests, and what
    // this asserts is that a finished read lands in the form.
    const read = vi.spyOn(api, "readRadioProgram").mockResolvedValue(READ_RUN);
    vi.spyOn(api, "radioProgram").mockResolvedValue(null);
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    fireEvent.click(within(panel).getByRole("button", { name: /Read from radio/i }));
    await screen.findByText("Read this radio?");
    fireEvent.click(screen.getByRole("button", { name: /^Read from radio$/ }));

    await waitFor(() => expect(read).toHaveBeenCalledWith("vhf-1"));
    // Nothing was written, and the form now says what the radio is actually set to.
    await waitFor(() => expect(within(panel).getByLabelText("Frequency")).toHaveValue("145.287500"));
    expect(within(panel).getByLabelText("Bandwidth")).toHaveValue("wide");
    expect(within(panel).getByLabelText("Transmit power")).toHaveValue("medium");
    expect(panel).toHaveTextContent("6 channels");
    expect(panel).toHaveTextContent("database 0095");
  });

  it("will not offer to start until a reachable frequency is typed", async () => {
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    const button = within(panel).getByRole("button", { name: /Program radio/i });
    expect(button).toBeDisabled();

    fireEvent.change(within(panel).getByLabelText("Frequency"), { target: { value: "14.100" } });
    expect(button).toBeDisabled();
    expect(panel).toHaveTextContent(/Not a frequency a Tait can reach/i);

    // A reachable frequency is not enough on its own: bandwidth and power start blank, because the
    // panel has no idea what the radio is set to until it has read it.
    fireEvent.change(within(panel).getByLabelText("Frequency"), { target: { value: "144.8125" } });
    expect(within(panel).getByLabelText("Bandwidth")).toHaveValue("");
    expect(within(panel).getByLabelText("Transmit power")).toHaveValue("");
    expect(button).toBeDisabled();

    fireEvent.change(within(panel).getByLabelText("Bandwidth"), { target: { value: "narrow" } });
    expect(button).toBeDisabled();
    fireEvent.change(within(panel).getByLabelText("Transmit power"), { target: { value: "high" } });
    expect(button).toBeEnabled();
  });

  it("is read-only without the admin scope - visible, but it will not run", async () => {
    seedConfig(TAIT_PORT);
    await mountPorts("operate");
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    fillChannel(panel, "144.8125");

    const button = within(panel).getByRole("button", { name: /Program radio/i });
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute("title", expect.stringContaining("admin"));
    expect(within(panel).getByRole("button", { name: /Read from radio/i })).toBeDisabled();
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

    fillChannel(panel, "144.8125");
    fireEvent.click(within(panel).getByRole("button", { name: /Program radio/i }));
    await screen.findByText("Program this radio?");
    fireEvent.click(screen.getByRole("button", { name: /^Program radio$/ }));

    await waitFor(() => expect(panel).toHaveTextContent("busy with a tuning session"));
  });

  it("says why a run failed, what it was doing at the time, and shows the log it produced", async () => {
    vi.spyOn(api, "radioProgram").mockResolvedValue({
      ...RUN,
      state: "failed",
      failedState: "writing",
      error: "refusing to write: the radio's database version '0091' is not one the write path is validated for (0094, 0095)",
      backupPath: null,
      log: ["port stopped; programming the radio on /dev/ttyUSB1", "read 1204 records", "writing rx=144.8125 MHz"],
    });
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    const outcome = await within(panel).findByTestId("radio-programming-outcome");
    await waitFor(() => expect(outcome).toHaveTextContent("while writing the codeplug"));
    expect(outcome).toHaveTextContent("database version '0091'");
    expect(outcome).toHaveTextContent("Run log (3 lines)");
  });

  it("falls back to the node's log rather than a bare 'it failed' when no reason came back", async () => {
    vi.spyOn(api, "radioProgram").mockResolvedValue({ ...RUN, state: "failed", error: null, backupPath: null, log: [] });
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-programming");

    const outcome = await within(panel).findByTestId("radio-programming-outcome");
    await waitFor(() => expect(outcome).toHaveTextContent("journalctl -u packetnet"));
  });
});

// The Test transmit panel: key the radio for a second, read its power detectors, say what they mean.
// The thing that must never happen here is a reassuring verdict over a disconnected antenna, so what
// is asserted is that the failure verdicts and the "this is an estimate" caveat both reach the page.
const TEST_TX_OK: TaitTestTxResult = {
  portId: "vhf-1",
  at: "2026-08-23T12:00:00Z",
  keyedMilliseconds: 1000,
  radioModel: "TMAB12-B100_0201",
  radioSerial: "19925328",
  band: "B1",
  keyed: true,
  inhibited: false,
  idleForwardMillivolts: 10,
  idleReverseMillivolts: 4,
  forwardMillivolts: 1730,
  reverseMillivolts: 176,
  forwardOverIdleMillivolts: 1720,
  reverseOverIdleMillivolts: 172,
  reflectionCoefficient: 0.1,
  vswr: 1.2222,
  foldback: false,
  verdict: "ok",
  reference: {
    code: "B1", highPowerForwardMinMillivolts: 1100,
    highPowerForwardMaxMillivolts: 3400, reverseCeilingMillivolts: 500,
  },
  notes: ["The VSWR figure is an ESTIMATE from uncalibrated detectors."],
  samples: 8,
};

describe("PortEditor - the Test transmit panel", () => {
  it("says out loud that it transmits, and needs confirming before it does", async () => {
    const test = vi.spyOn(api, "radioTestTx").mockResolvedValue(TEST_TX_OK);
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-test-tx");

    expect(panel).toHaveTextContent(/transmits/i);
    expect(panel).toHaveTextContent(/antenna or a dummy load/i);

    fireEvent.click(within(panel).getByRole("button", { name: /Test TX/i }));
    expect(test).not.toHaveBeenCalled();

    await screen.findByText("Transmit a test carrier?");
    fireEvent.click(screen.getByRole("button", { name: /^Transmit$/ }));
    await waitFor(() => expect(test).toHaveBeenCalledWith("vhf-1"));
  });

  it("shows the detector readings and the estimated VSWR, with the estimate said to be one", async () => {
    vi.spyOn(api, "radioTestTx").mockResolvedValue(TEST_TX_OK);
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-test-tx");

    fireEvent.click(within(panel).getByRole("button", { name: /Test TX/i }));
    await screen.findByText("Transmit a test carrier?");
    fireEvent.click(screen.getByRole("button", { name: /^Transmit$/ }));

    const result = await within(panel).findByTestId("radio-test-tx-result");
    expect(result).toHaveTextContent("1720 mV");
    expect(result).toHaveTextContent("172 mV");
    expect(result).toHaveTextContent("1.22:1");
    expect(result).toHaveTextContent(/ESTIMATE/);
    expect(panel).toHaveTextContent("Looks fine");
  });

  it("does not dress a folded-back PA up as a healthy antenna", async () => {
    vi.spyOn(api, "radioTestTx").mockResolvedValue({
      ...TEST_TX_OK,
      verdict: "foldback",
      foldback: true,
      vswr: null,
      reflectionCoefficient: null,
      notes: ["The forward power COLLAPSED during the key. Check the antenna, the feeder and the connectors."],
    });
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-test-tx");

    fireEvent.click(within(panel).getByRole("button", { name: /Test TX/i }));
    await screen.findByText("Transmit a test carrier?");
    fireEvent.click(screen.getByRole("button", { name: /^Transmit$/ }));

    await waitFor(() => expect(panel).toHaveTextContent("PA folded back"));
    expect(panel).toHaveTextContent(/COLLAPSED/);
  });

  it("surfaces a refusal from the node rather than a silent no-op", async () => {
    vi.spyOn(api, "radioTestTx").mockRejectedValue(new Error("port 'vhf-1' is busy with a tuning session - stop it first"));
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-test-tx");

    fireEvent.click(within(panel).getByRole("button", { name: /Test TX/i }));
    await screen.findByText("Transmit a test carrier?");
    fireEvent.click(screen.getByRole("button", { name: /^Transmit$/ }));

    await waitFor(() => expect(panel).toHaveTextContent("busy with a tuning session"));
  });

  it("is disabled without the admin scope, and stays away from a port with no Tait", async () => {
    seedConfig(TAIT_PORT);
    await mountPorts("operate");
    await openEditor("vhf-1");
    const panel = await screen.findByTestId("radio-test-tx");
    expect(within(panel).getByRole("button", { name: /Test TX/i })).toBeDisabled();
  });

  it("says WHY the radio is missing when the port is serving without it", async () => {
    // The bug: the port is up (it carries traffic), the radio section above is configured and
    // looks right, and the only clue that the control channel never opened was a line in the
    // service log. From this pane an operator saw a bare "this port has no Tait CCDI radio".
    vi.spyOn(api, "ports").mockResolvedValue([{
      id: "vhf-1", enabled: true, state: "degraded", sessionCount: 0, lastError:
        "radio (tait-ccdi on serial:19925328): no tait-ccdi radio with CCDI serial '19925328' answered at 28800 baud.",
      framesIn: 0, framesOut: 0, degraded: ["radio"], since: "2026-08-24T09:00:00Z", channelBusy: null,
    }]);
    seedConfig(TAIT_PORT);
    await mountPorts();
    await openEditor("vhf-1");

    // Once at the top of the editor, and again against the button it stops from working.
    expect(await screen.findByTestId("port-editor-degraded")).toHaveTextContent("19925328");
    const panel = await screen.findByTestId("radio-test-tx");
    expect(panel).toHaveTextContent(/answered at 28800 baud/);
    expect(panel).toHaveTextContent(/refuse until the radio is back/);
  });

  it("stays away for a port with no radio at all", async () => {
    seedConfig(BARE_PORT);
    await mountPorts();
    await openEditor("sim");
    expect(screen.queryByTestId("radio-test-tx")).not.toBeInTheDocument();
  });
});
