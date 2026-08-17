// ============================================================
// The first-run setup wizard, screens/setup.tsx (review item C031, packet.net#692).
//
// The only test it had asserted `container.firstChild` was truthy, so nothing checked the one
// thing that matters: the POST /setup body. That body is a node's ENTIRE first configuration -
// identity, the first admin, and optionally the first port - it is one-shot (403 once a user
// exists), and it is the first thing an operator ever does with pdn. A malformed firstPort
// there means the wizard 422s on a node with no other way in.
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Setup } from "@/screens/setup";
import { api, ConfigRejected } from "@/lib/api";
import type { SetupRequest } from "@/lib/types";

const navigate = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => navigate,
}));

function mountWizard() {
  return render(
    <MemoryRouter initialEntries={["/setup"]}>
      <AuthProvider><Setup /></AuthProvider>
    </MemoryRouter>,
  );
}

/** Fill step 1 (identity) and step 2 (admin), leaving the wizard on step 3 (first port). */
function fillToLastStep(opts: { callsign?: string; alias?: string; grid?: string } = {}) {
  fireEvent.change(screen.getByPlaceholderText("GB7RDG"), { target: { value: opts.callsign ?? "m0lte-1" } });
  if (opts.alias !== undefined) {
    fireEvent.change(screen.getByPlaceholderText("RDGGW"), { target: { value: opts.alias } });
  }
  if (opts.grid !== undefined) {
    fireEvent.change(screen.getByPlaceholderText("IO91nl"), { target: { value: opts.grid } });
  }
  fireEvent.click(screen.getByRole("button", { name: /Continue/i }));

  const passwords = screen.getAllByPlaceholderText("••••••••");
  fireEvent.change(passwords[0], { target: { value: "hunter2hunter2" } });
  fireEvent.change(passwords[1], { target: { value: "hunter2hunter2" } });
  fireEvent.click(screen.getByRole("button", { name: /Continue/i }));
}

afterEach(() => {
  navigate.mockClear();
  vi.restoreAllMocks();
});

describe("Setup wizard - the one-shot bootstrap POST", () => {
  it("posts the identity, the admin and a valid first port, then sends the operator to sign in", async () => {
    const setup = vi.spyOn(api, "setup").mockResolvedValue({ username: "admin", scope: "admin" });
    mountWizard();

    fillToLastStep({ alias: "london", grid: "IO91nl" });
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
      transport: { kind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600, mode: 4 },
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
    fireEvent.click(screen.getByRole("button", { name: /Finish setup/i }));

    await waitFor(() => expect(screen.getByText(/Setup failed \(403\)/)).toBeInTheDocument());
    expect(navigate).not.toHaveBeenCalled();
  });

  it("will not advance without a callsign, or with a password that is too short or mistyped", async () => {
    mountWizard();
    // Step 1 needs a callsign.
    expect(screen.getByRole("button", { name: /Continue/i })).toBeDisabled();
    fireEvent.change(screen.getByPlaceholderText("GB7RDG"), { target: { value: "M0LTE-1" } });
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

  it("builds the right transport union member for each port kind the wizard offers", async () => {
    // The wizard reuses one device/baud pair for every kind, so a host/port kind maps them onto
    // host + port. Getting that mapping wrong writes a transport the server rejects.
    const setup = vi.spyOn(api, "setup").mockResolvedValue({ username: "admin", scope: "admin" });
    mountWizard();
    fillToLastStep();

    const kind = screen.getByDisplayValue("nino-tnc");
    fireEvent.change(kind, { target: { value: "kiss-tcp" } });
    fireEvent.change(screen.getByDisplayValue("/dev/ttyACM0"), { target: { value: "127.0.0.1" } });
    fireEvent.change(screen.getByDisplayValue("57600"), { target: { value: "8001" } });
    fireEvent.click(screen.getByRole("button", { name: /Finish setup/i }));

    await waitFor(() => expect(setup).toHaveBeenCalled());
    expect((setup.mock.calls[0][0] as SetupRequest).firstPort?.transport)
      .toEqual({ kind: "kiss-tcp", host: "127.0.0.1", port: 8001 });
  });
});
