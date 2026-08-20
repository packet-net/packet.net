// ============================================================
// The first-run setup wizard, screens/setup.tsx (review item C031, packet.net#692).
//
// The only test it had asserted `container.firstChild` was truthy, so nothing checked the one
// thing that matters: the POST /setup body. That body is a node's ENTIRE first configuration -
// identity, the first admin, and optionally the first port - it is one-shot (403 once a user
// exists), and it is the first thing an operator ever does with pdn. A malformed firstPort
// there means the wizard 422s on a node with no other way in.
//
// The port step also no longer asks anyone to type a device path: it calls GET /setup/devices and
// offers what the node found, having asked the plausible NinoTNCs for their firmware version. The
// tests below pin the parts of that with teeth - the STABLE by-id path is what gets written into
// the transport, the NinoTNC wire speed is not editable, and the manual escape hatch still works
// when discovery finds nothing.
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Setup } from "@/screens/setup";
import { api, ConfigRejected } from "@/lib/api";
import type { ModemScan, SetupRequest } from "@/lib/types";

const navigate = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => navigate,
}));

// A NinoTNC that answered GETVER, on a device udev gave a stable by-id name, plus a bridge-chip
// port that did not identify. The by-id path is deliberately different from the kernel path: the
// wizard must bind the port to the STABLE one.
const NINO_BY_ID = "/dev/serial/by-id/usb-Microchip_Technology_Inc._NinoTNC-if00";
const SCAN: ModemScan = {
  devices: [
    { devicePath: NINO_BY_ID, kernelPath: "/dev/ttyACM0", descriptor: "usb-Microchip_Technology_Inc._NinoTNC-if00", kind: "nino-tnc", firmwareVersion: "3.44", claimedBy: null, probeError: null },
    { devicePath: "/dev/ttyUSB0", kernelPath: "/dev/ttyUSB0", descriptor: null, kind: "serial", firmwareVersion: null, claimedBy: null, probeError: null },
  ],
  permissionDenied: false,
};

function mockScan(scan: ModemScan = SCAN) {
  return vi.spyOn(api, "setupDevices").mockResolvedValue(scan);
}

function mountWizard() {
  return render(
    <MemoryRouter initialEntries={["/setup"]}>
      <AuthProvider><Setup /></AuthProvider>
    </MemoryRouter>,
  );
}

/** Fill step 1 (identity) and step 2 (admin), leaving the wizard on step 3 (first port). */
function fillToLastStep(opts: { callsign?: string; alias?: string; grid?: string } = {}) {
  fireEvent.change(screen.getByPlaceholderText("GB7AAA"), { target: { value: opts.callsign ?? "m0lte-1" } });
  if (opts.alias !== undefined) {
    fireEvent.change(screen.getByPlaceholderText("MYNODE"), { target: { value: opts.alias } });
  }
  if (opts.grid !== undefined) {
    fireEvent.change(screen.getByPlaceholderText("AA00aa"), { target: { value: opts.grid } });
  }
  fireEvent.click(screen.getByRole("button", { name: /Continue/i }));

  const passwords = screen.getAllByPlaceholderText("••••••••");
  fireEvent.change(passwords[0], { target: { value: "hunter2hunter2" } });
  fireEvent.change(passwords[1], { target: { value: "hunter2hunter2" } });
  fireEvent.click(screen.getByRole("button", { name: /Continue/i }));
}

/** Step 3 fetches the device list; wait for the scan to land before asserting on the picker. */
async function awaitScan() {
  await waitFor(() => expect(api.setupDevices).toHaveBeenCalled());
  await waitFor(() => expect(screen.queryByText(/Scanning\.\.\./)).not.toBeInTheDocument());
}

beforeEach(() => {
  mockScan();
});

afterEach(() => {
  navigate.mockClear();
  vi.restoreAllMocks();
});

describe("Setup wizard - the one-shot bootstrap POST", () => {
  it("posts the identity, the admin and a valid first port, then sends the operator to sign in", async () => {
    const setup = vi.spyOn(api, "setup").mockResolvedValue({ username: "admin", scope: "admin" });
    mountWizard();

    fillToLastStep({ alias: "london", grid: "IO91nl" });
    await awaitScan();
    fireEvent.click(screen.getByRole("button", { name: /Finish setup/i }));

    await waitFor(() => expect(setup).toHaveBeenCalledTimes(1));
    const body = setup.mock.calls[0][0] as SetupRequest;

    // Callsign and alias are upper-cased as typed; the locator keeps its mixed case (Maidenhead
    // is conventionally IO91nl, and the server accepts it either way).
    expect(body.identity).toEqual({ callsign: "M0LTE-1", alias: "LONDON", grid: "IO91nl" });
    expect(body.admin).toEqual({ username: "admin", password: "hunter2hunter2" });

    // The first port must be a candidate the server's validator accepts. `profile` in
    // particular is a SERVER channel-profile name or null - never a UI catalogue id, which is
    // what made "Add port" 422 on a fresh node (#690 C002).
    expect(body.firstPort).toMatchObject({
      id: "vhf-1",
      enabled: true,
      // The discovered NinoTNC was pre-selected, and it is the STABLE by-id path that gets
      // written - a port pinned to /dev/ttyACM0 moves to the wrong modem the day a second one
      // is plugged in.
      transport: { kind: "nino-tnc", device: NINO_BY_ID, baud: 57600, mode: 4 },
      profile: null,
      ax25: null,
      kiss: null,
      beacon: null,
    });

    // Setup returns no token, so the operator signs in with the credentials they just chose.
    expect(navigate).toHaveBeenCalledWith("/login", { replace: true });
  });

  it("sends a null alias and locator rather than empty strings when they are left blank", async () => {
    // Identity.Alias / .Grid are `string?` on the server. An empty string is a SET value there,
    // so a node would advertise a zero-length NET/ROM alias rather than none at all.
    const setup = vi.spyOn(api, "setup").mockResolvedValue({ username: "admin", scope: "admin" });
    mountWizard();

    fillToLastStep();
    await awaitScan();
    fireEvent.click(screen.getByRole("button", { name: /Finish setup/i }));

    await waitFor(() => expect(setup).toHaveBeenCalled());
    const body = setup.mock.calls[0][0] as SetupRequest;
    expect(body.identity.alias).toBeNull();
    expect(body.identity.grid).toBeNull();
  });

  it("omits the first port entirely when the operator declines one", async () => {
    const setup = vi.spyOn(api, "setup").mockResolvedValue({ username: "admin", scope: "admin" });
    mountWizard();

    fillToLastStep();
    await awaitScan();
    fireEvent.click(screen.getByText(/Add a first port now/i));
    fireEvent.click(screen.getByRole("button", { name: /Finish setup/i }));

    await waitFor(() => expect(setup).toHaveBeenCalled());
    expect((setup.mock.calls[0][0] as SetupRequest).firstPort).toBeNull();
  });

  it("shows the server's per-field reasons on a 422 and stays put", async () => {
    // A rejected bootstrap must say WHY and leave the form filled in: this is a one-shot
    // endpoint on a node with no other way in, so bouncing to /login would strand the operator.
    vi.spyOn(api, "setup").mockRejectedValue(new ConfigRejected({
      errors: [{ path: "Identity.Callsign", message: "'M0LTE-1' is not a valid callsign" }],
    }));
    mountWizard();

    fillToLastStep();
    await awaitScan();
    fireEvent.click(screen.getByRole("button", { name: /Finish setup/i }));

    await waitFor(() => expect(screen.getByText(/is not a valid callsign/)).toBeInTheDocument());
    expect(navigate).not.toHaveBeenCalled();
    // The Finish button is live again, so a corrected retry is possible.
    expect(screen.getByRole("button", { name: /Finish setup/i })).not.toBeDisabled();
  });

  it("surfaces a plain failure (403 already set up) without navigating away", async () => {
    vi.spyOn(api, "setup").mockRejectedValue(new Error("Setup failed (403)."));
    mountWizard();

    fillToLastStep();
    await awaitScan();
    fireEvent.click(screen.getByRole("button", { name: /Finish setup/i }));

    await waitFor(() => expect(screen.getByText(/Setup failed \(403\)/)).toBeInTheDocument());
    expect(navigate).not.toHaveBeenCalled();
  });

  it("will not advance without a callsign, or with a password that is too short or mistyped", () => {
    mountWizard();
    // Step 1 needs a callsign.
    expect(screen.getByRole("button", { name: /Continue/i })).toBeDisabled();
    fireEvent.change(screen.getByPlaceholderText("GB7AAA"), { target: { value: "M0LTE-1" } });
    expect(screen.getByRole("button", { name: /Continue/i })).not.toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: /Continue/i }));

    // Step 2 needs >= 8 characters, entered twice identically.
    const passwords = screen.getAllByPlaceholderText("••••••••");
    fireEvent.change(passwords[0], { target: { value: "short" } });
    fireEvent.change(passwords[1], { target: { value: "short" } });
    expect(screen.getByRole("button", { name: /Continue/i })).toBeDisabled();

    fireEvent.change(passwords[0], { target: { value: "hunter2hunter2" } });
    fireEvent.change(passwords[1], { target: { value: "hunter2hunter3" } });
    expect(screen.getByRole("button", { name: /Continue/i })).toBeDisabled();
    expect(screen.getByText(/Passwords don't match/i)).toBeInTheDocument();

    fireEvent.change(passwords[1], { target: { value: "hunter2hunter2" } });
    expect(screen.getByRole("button", { name: /Continue/i })).not.toBeDisabled();
  });

  it("reserves the mismatch hint's line so the centred card does not jump while typing", () => {
    // The hint used to be rendered only on a mismatch, so the card grew and shrank by a line as
    // the operator typed the confirmation - and being vertically centred, it moved under their
    // cursor. The placeholder must be a NON-BREAKING space: an ordinary one collapses, the
    // paragraph gets no line box, and the reservation quietly does nothing.
    const { container } = mountWizard();
    fireEvent.change(screen.getByPlaceholderText("GB7AAA"), { target: { value: "M0LTE-1" } });
    fireEvent.click(screen.getByRole("button", { name: /Continue/i }));

    const hint = () => container.querySelector(".invisible");
    expect(hint()?.textContent).toBe("\u00A0");

    const passwords = screen.getAllByPlaceholderText("\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022");
    fireEvent.change(passwords[0], { target: { value: "hunter2hunter2" } });
    fireEvent.change(passwords[1], { target: { value: "hunter2hunter3" } });
    expect(screen.getByText(/Passwords don't match/i)).toBeInTheDocument();
    expect(hint()).toBeNull();   // the same slot, now carrying the real message

    fireEvent.change(passwords[1], { target: { value: "hunter2hunter2" } });
    expect(hint()?.textContent).toBe("\u00A0");
  });

  it("builds the right transport union member for each port kind the wizard offers", async () => {
    // The wizard reuses one device/baud pair for every kind, so a host/port kind maps them onto
    // host + port. Getting that mapping wrong writes a transport the server rejects.
    const setup = vi.spyOn(api, "setup").mockResolvedValue({ username: "admin", scope: "admin" });
    mountWizard();
    fillToLastStep();
    await awaitScan();

    fireEvent.change(screen.getByLabelText("Transport"), { target: { value: "kiss-tcp" } });
    fireEvent.change(screen.getByLabelText("Host"), { target: { value: "127.0.0.1" } });
    fireEvent.change(screen.getByLabelText("Port"), { target: { value: "8001" } });
    fireEvent.click(screen.getByRole("button", { name: /Finish setup/i }));

    await waitFor(() => expect(setup).toHaveBeenCalled());
    expect((setup.mock.calls[0][0] as SetupRequest).firstPort?.transport)
      .toEqual({ kind: "kiss-tcp", host: "127.0.0.1", port: 8001 });
  });
});

describe("Setup wizard - the first-port device picker", () => {
  it("offers what the node found, names the NinoTNC by its firmware, and pre-selects it", async () => {
    mountWizard();
    fillToLastStep();
    await awaitScan();

    const picker = screen.getByLabelText("Device") as HTMLSelectElement;
    expect(picker.value).toBe(NINO_BY_ID);
    // The identified modem is labelled by what it IS, not by a path the operator has to decode.
    expect(screen.getByRole("option", { name: "NinoTNC 3.44 - ttyACM0" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "ttyUSB0" })).toBeInTheDocument();
    // And the wizard says, in as many words, that it talked to the modem...
    expect(screen.getByText(/NinoTNC answered, firmware 3\.44/)).toBeInTheDocument();
    // ...and shows the stable path the port will actually be bound to.
    expect(screen.getByText(NINO_BY_ID)).toBeInTheDocument();
  });

  it("shows the NinoTNC USB wire speed as fixed, with no way to type a different one", async () => {
    // 57600 is baked into the NinoTNC firmware. An editable box here is an invitation to break
    // the port in a way whose only symptom is silence.
    mountWizard();
    fillToLastStep();
    await awaitScan();

    expect(screen.getByTestId("nino-baud-fixed")).toHaveTextContent("57600");
    expect(screen.queryByLabelText("Baud")).not.toBeInTheDocument();

    // Generic KISS has no such constraint, so its baud IS editable.
    fireEvent.change(screen.getByLabelText("Transport"), { target: { value: "serial-kiss" } });
    expect(screen.queryByTestId("nino-baud-fixed")).not.toBeInTheDocument();
  });

  it("keeps a NinoTNC at 57600 even when it is driven as a Generic KISS TNC", async () => {
    // A NinoTNC on the generic transport is still a NinoTNC on the wire. The generic 9600
    // default would build a port whose only symptom is silence, so the wizard carries the
    // fixed speed across and says why the native transport is the better choice.
    const setup = vi.spyOn(api, "setup").mockResolvedValue({ username: "admin", scope: "admin" });
    mountWizard();
    fillToLastStep();
    await awaitScan();

    fireEvent.change(screen.getByLabelText("Transport"), { target: { value: "serial-kiss" } });
    expect((screen.getByLabelText("Baud") as HTMLInputElement).value).toBe("57600");
    expect(screen.getByText(/the NinoTNC transport drives it properly/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /Finish setup/i }));
    await waitFor(() => expect(setup).toHaveBeenCalled());
    expect((setup.mock.calls[0][0] as SetupRequest).firstPort?.transport)
      .toEqual({ kind: "serial-kiss", device: NINO_BY_ID, baud: 57600 });
  });

  it("says why a device could not be identified, and still lets the operator use it", async () => {
    mockScan({
      devices: [{
        devicePath: "/dev/ttyACM0", kernelPath: "/dev/ttyACM0", descriptor: null,
        kind: "serial", firmwareVersion: null, claimedBy: null, probeError: "no reply",
      }],
      permissionDenied: false,
    });
    const setup = vi.spyOn(api, "setup").mockResolvedValue({ username: "admin", scope: "admin" });
    mountWizard();
    fillToLastStep();
    await awaitScan();

    expect(screen.getByText(/did not answer as a NinoTNC \(no reply\)/)).toBeInTheDocument();
    // Not a blocker: the operator may know better than the probe.
    expect(screen.getByRole("button", { name: /Finish setup/i })).not.toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: /Finish setup/i }));
    await waitFor(() => expect(setup).toHaveBeenCalled());
  });

  it("turns a device-permission failure into the fix, not an empty list", async () => {
    // This is the one scan outcome with an actionable cause: the node's user is not in dialout.
    // A silently empty picker would send the operator hunting through journalctl.
    mockScan({ devices: [], permissionDenied: true });
    mountWizard();
    fillToLastStep();
    await awaitScan();

    expect(screen.getByText(/usermod -aG dialout packetnet/)).toBeInTheDocument();
  });

  it("falls back to a typed path when discovery finds nothing", async () => {
    mockScan({ devices: [], permissionDenied: false });
    const setup = vi.spyOn(api, "setup").mockResolvedValue({ username: "admin", scope: "admin" });
    mountWizard();
    fillToLastStep();
    await awaitScan();

    // Nothing found, so nothing is selected and the wizard will not submit a device-less port.
    expect(screen.getByRole("button", { name: /Finish setup/i })).toBeDisabled();

    fireEvent.change(screen.getByLabelText("Device"), { target: { value: " manual" } });
    fireEvent.change(screen.getByLabelText("Device path"), { target: { value: "/dev/ttyS3" } });
    fireEvent.click(screen.getByRole("button", { name: /Finish setup/i }));

    await waitFor(() => expect(setup).toHaveBeenCalled());
    expect((setup.mock.calls[0][0] as SetupRequest).firstPort?.transport)
      .toEqual({ kind: "nino-tnc", device: "/dev/ttyS3", baud: 57600, mode: 4 });
  });

  it("rescans on demand", async () => {
    mountWizard();
    fillToLastStep();
    await awaitScan();

    fireEvent.click(screen.getByRole("button", { name: /Rescan/i }));
    await waitFor(() => expect(api.setupDevices).toHaveBeenCalledTimes(2));
  });
});
