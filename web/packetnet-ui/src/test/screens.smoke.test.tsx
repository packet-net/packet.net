// Render smoke test for every screen: mounts each against the mock API backend
// and asserts it renders without throwing + surfaces a key piece of copy. Catches
// runtime crashes (bad hook order, undefined access, missing context) that the
// type-check can't — the verification gate in lieu of headless-browser screenshots
// (the host LXC blocks Chrome's network sockets, so visual screenshotting isn't
// possible in CI here).
import { describe, it, expect } from "vitest";
import { render, screen, waitFor, fireEvent, within, type RenderResult } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import type { ReactElement } from "react";

import { Dashboard } from "@/screens/dashboard";
import { Monitor } from "@/screens/monitor";
import { Sessions } from "@/screens/sessions";
import { Console } from "@/screens/console";
import { Apps } from "@/screens/apps";
import { Routes } from "@/screens/routes";
import { Capabilities } from "@/screens/capabilities";
import { Ports } from "@/screens/ports";
import { HeadEnds } from "@/screens/headends";
import { Config } from "@/screens/config";
import { Users } from "@/screens/users";
import { Login } from "@/screens/login";
import { Setup } from "@/screens/setup";
import { LinkTuner } from "@/screens/link-tuner";
import { LinkTroubleshoot } from "@/screens/link-troubleshoot";
import { Waterfall } from "@/screens/waterfall";

function mount(node: ReactElement, route = "/"): RenderResult {
  return render(
    <MemoryRouter initialEntries={[route]}>
      <AuthProvider>{node}</AuthProvider>
    </MemoryRouter>,
  );
}

describe("screens render without crashing", () => {
  it("Dashboard surfaces node status", async () => {
    const { container } = mount(<Dashboard />);
    expect(container.firstChild).toBeTruthy();
    await waitFor(() => expect(screen.getAllByText(/GB7RDG/).length).toBeGreaterThan(0));
  });

  it("Monitor shows the live monitor", async () => {
    const { container } = mount(<Monitor />);
    expect(container.firstChild).toBeTruthy();
    await waitFor(() => expect(screen.getByText(/Live monitor/i)).toBeInTheDocument());
  });

  it("Monitor frame table has an RSSI column (link quality)", async () => {
    mount(<Monitor />);
    // The RSSI column header is the entry point for per-frame link quality (dBm + SNR).
    await waitFor(() => expect(screen.getByText("RSSI")).toBeInTheDocument());
  });

  it("Dashboard surfaces the Radios health panel with link quality", async () => {
    mount(<Dashboard />);
    // The payoff view: a radio-attached port's identity + the antenna-health caveat label.
    await waitFor(() => expect(screen.getByText(/^Radios$/)).toBeInTheDocument());
    expect(screen.getAllByText(/Tait TM8110/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Antenna-health trend \(not VSWR\)/i).length).toBeGreaterThan(0);
    // A healthy radio shows its dBm at a glance.
    expect(screen.getAllByText(/dBm/).length).toBeGreaterThan(0);
  });

  it("Dashboard surfaces the Rigs panel with the dial and TX meters", async () => {
    mount(<Dashboard />);
    // The station-control card: model identity, the frequency dial, mode badge, and the
    // TX-meters section (SWR is sampled during transmissions, last sample stays on display).
    await waitFor(() => expect(screen.getByText(/^Rigs$/)).toBeInTheDocument());
    expect(screen.getAllByText(/IC-7300/).length).toBeGreaterThan(0);
    expect(screen.getAllByText("14.074.000").length).toBeGreaterThan(0);
    expect(screen.getAllByText("PKTUSB").length).toBeGreaterThan(0);
    expect(screen.getAllByText(/TX meters/i).length).toBeGreaterThan(0);
    // The configured-but-unreachable flrig renders honestly.
    expect(screen.getAllByText(/not attached/i).length).toBeGreaterThan(0);
    // The TUNE affordance renders on the attached rig only (it advertises frequencySet/
    // modeSet; the unattached flrig gets none). These mounts skip the router gate that
    // enters mock mode as admin, so no scope is held — per the disable-never-hide
    // convention the button renders disabled with the explanatory title.
    const tune = screen.getAllByRole("button", { name: "Tune" });
    expect(tune.length).toBe(1);
    expect(tune[0]).toBeDisabled();
    expect(tune[0]).toHaveAttribute("title", "Retuning a transmitter requires the operate scope");
  });

  it("Rig card Tune opens the retune modal and previews the parsed dial", async () => {
    // Seed an admin session (the Console-test pattern) so has("operate") passes — the smoke
    // mounts skip the router gate that would enterAnonymous("admin") in mock mode.
    localStorage.setItem(
      "pdn.session",
      JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope: "admin" }),
    );
    try {
      mount(<Dashboard />);
      await waitFor(() => expect(screen.getByRole("button", { name: "Tune" })).not.toBeDisabled());
      fireEvent.click(screen.getByRole("button", { name: "Tune" }));

      // The modal renders the no-RF note plus both settable fields (the mock IC-7300
      // advertises frequencySet and modeSet). Scope to the dialog — the card behind it
      // also says "Frequency".
      await waitFor(() => expect(screen.getByText(/No RF is emitted by a retune/i)).toBeInTheDocument());
      const dialog = screen.getByRole("dialog");
      expect(within(dialog).getByText("Frequency")).toBeInTheDocument();
      expect(within(dialog).getByText("Mode")).toBeInTheDocument();

      // MHz-decimal entry previews the parsed Hz through fmtRigFrequency ("14.205" → 14.205.000).
      fireEvent.change(within(dialog).getByPlaceholderText(/14\.074 \(MHz\)/), { target: { value: "14.205" } });
      expect(within(dialog).getByText("14.205.000")).toBeInTheDocument();
    } finally {
      localStorage.clear();
    }
  });

  it("Sessions renders", async () => {
    const { container } = mount(<Sessions />);
    expect(container.firstChild).toBeTruthy();
    await waitFor(() => expect(screen.getAllByText(/Sessions/i).length).toBeGreaterThan(0));
  });

  it("Console renders the node command console terminal", async () => {
    // Admin-gated screen; seed an admin session so it exercises the open path (mock api
    // returns a synthetic id + a banner). The terminal host always mounts.
    localStorage.setItem(
      "pdn.session",
      JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope: "admin" }),
    );
    try {
      const { container } = mount(<Console />, "/console");
      expect(container.firstChild).toBeTruthy();
      await waitFor(() => expect(screen.getByTestId("console-terminal")).toBeInTheDocument());
      // The dense layout dropped the page title; the status strip is the stable key copy.
      expect(screen.getByText(/connecting|connected|closed|unavailable/i)).toBeInTheDocument();
    } finally {
      localStorage.clear();
    }
  });

  it("Apps renders the management surface (no launcher grid — apps live in the nav now)", async () => {
    const { container } = mount(<Apps />);
    expect(container.firstChild).toBeTruthy();
    // The Apps page is pure management now: "Available apps" (install) + "Manage apps". The
    // launcher grid moved to the left-nav (see shell.nav.test.tsx), so WALL appears here only
    // as a management row, never as an /apps/wall/ launcher anchor.
    await waitFor(() => expect(screen.getByText(/Available apps/i)).toBeInTheDocument());
    expect(screen.getByText(/Manage apps/i)).toBeInTheDocument();
    const wallLink = screen.queryAllByText("WALL").map((el) => el.closest("a")).find((a) => a !== null);
    expect(wallLink).toBeUndefined();
  });

  it("Routes renders the destinations/neighbours view", async () => {
    const { container } = mount(<Routes />);
    expect(container.firstChild).toBeTruthy();
    await waitFor(() => expect(screen.getByText(/Destinations/i)).toBeInTheDocument());
  });

  it("Ports renders", async () => {
    const { container } = mount(<Ports />);
    expect(container.firstChild).toBeTruthy();
    await waitFor(() => expect(screen.getAllByText(/Ports/i).length).toBeGreaterThan(0));
  });

  it("Ports editor surfaces the per-port PACLEN (N1) and NET/ROM quality fields", async () => {
    // The custom-tuned mock port (uhf-2) opens the editor with the advanced section
    // expanded, so the N1 / PACLEN field is shown; the NET/ROM quality field is always
    // shown. Proves both new per-port settings (#455 / #458) are wired into the form.
    mount(<Ports />);
    await waitFor(() => expect(screen.getByText("uhf-2")).toBeInTheDocument());

    // Walk up from the uhf-2 label to the enclosing card (the first ancestor that
    // contains an Edit button), then open its editor.
    let card: HTMLElement | null = screen.getByText("uhf-2");
    while (card && !within(card).queryByRole("button", { name: "Edit" })) {
      card = card.parentElement;
    }
    expect(card).not.toBeNull();
    fireEvent.click(within(card!).getByRole("button", { name: "Edit" }));

    // The editor (a Sheet) opens with the per-port fields. Both new settings appear.
    await waitFor(() => expect(screen.getByText(/Edit port — uhf-2/i)).toBeInTheDocument());
    expect(screen.getByText(/Max frame \(PACLEN\)/i)).toBeInTheDocument();
    expect(screen.getByText(/NET\/ROM quality/i)).toBeInTheDocument();
  });

  it("Ports editor surfaces the multipoint-AXUDP peer table + per-port MINQUAL / NODESPACLEN", async () => {
    // The mp-net mock port is an axudp-multipoint transport with 2 peers + a per-port
    // netRomMinQuality (MINQUAL) and nodesPaclen (NODESPACLEN). Opening its editor proves
    // the multipoint editor (local port + peer rows + broadcast switches) and both new
    // per-port NET/ROM number inputs are wired into the Forms editor.
    mount(<Ports />);
    await waitFor(() => expect(screen.getByText("mp-net")).toBeInTheDocument());

    let card: HTMLElement | null = screen.getByText("mp-net");
    while (card && !within(card).queryByRole("button", { name: "Edit" })) {
      card = card.parentElement;
    }
    expect(card).not.toBeNull();
    fireEvent.click(within(card!).getByRole("button", { name: "Edit" }));

    await waitFor(() => expect(screen.getByText(/Edit port — mp-net/i)).toBeInTheDocument());
    // Multipoint transport surface: the AXUDP-multipoint option, the shared local port,
    // the peers table, and both seeded peer callsigns (round-tripped from the fixture).
    expect(screen.getByText(/AXUDP multipoint \(BPQAXIP\)/i)).toBeInTheDocument();
    expect(screen.getByText(/Peers/)).toBeInTheDocument();
    expect((screen.getByDisplayValue("N0CALL-1") as HTMLInputElement).value).toBe("N0CALL-1");
    expect((screen.getByDisplayValue("N0CALL-7") as HTMLInputElement).value).toBe("N0CALL-7");
    // The broadcast flag is a Switch per row — the fixture has one broadcast peer.
    expect(screen.getAllByRole("switch").length).toBeGreaterThanOrEqual(2);
    // The new per-port NET/ROM fields both render.
    expect(screen.getByText(/NET\/ROM min quality/i)).toBeInTheDocument();
    expect(screen.getByText(/NODES PACLEN/i)).toBeInTheDocument();
  });

  it("Ports editor surfaces the Radio control section + Scan for radios (serial-modem ports)", async () => {
    // vhf-1 is a nino-tnc (serial-modem) port with a serial-bound radio in the mock. Opening its
    // editor must show the Radio control section, the "Scan for radios" affordance, and the seeded
    // CCDI serial round-tripped into the bind field — proving the radio block survives openEdit.
    mount(<Ports />);
    await waitFor(() => expect(screen.getByText("vhf-1")).toBeInTheDocument());

    let card: HTMLElement | null = screen.getByText("vhf-1");
    while (card && !within(card).queryByRole("button", { name: "Edit" })) {
      card = card.parentElement;
    }
    expect(card).not.toBeNull();
    fireEvent.click(within(card!).getByRole("button", { name: "Edit" }));

    await waitFor(() => expect(screen.getByText(/Edit port — vhf-1/i)).toBeInTheDocument());
    expect(screen.getByText(/Radio control/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Scan for radios/i })).toBeInTheDocument();
    // The seeded serial (19925328) round-trips into the bind-by-serial input.
    expect((screen.getByDisplayValue("19925328") as HTMLInputElement).value).toBe("19925328");
  });

  it("Ports 'Check radio' opens the capability-doctor checklist (safe form)", async () => {
    // vhf-1 is a NinoTNC + radio port. "Check radio" opens the doctor, which auto-runs the SAFE
    // (non-transmitting) check: the checklist renders, and the transmitting probes are gated as
    // "unknown" with a rerun hint, alongside the offered full-check (interrupt) action.
    mount(<Ports />);
    await waitFor(() => expect(screen.getByText("vhf-1")).toBeInTheDocument());

    let card: HTMLElement | null = screen.getByText("vhf-1");
    while (card && !within(card).queryByRole("button", { name: /Check radio/i })) {
      card = card.parentElement;
    }
    expect(card).not.toBeNull();
    fireEvent.click(within(card!).getByRole("button", { name: /Check radio/i }));

    // The modal auto-runs the safe check; the rows arrive once the mock-async call resolves.
    await waitFor(() => expect(screen.getByText("radio-present")).toBeInTheDocument());
    expect(screen.getByText("tnc-present")).toBeInTheDocument();
    // A transmitting probe is gated on the safe form → shown "unknown" with the rerun hint.
    expect(screen.getAllByText(/requires a brief transmit/i).length).toBeGreaterThan(0);
    // The secondary full-check action (interrupt form) is offered and warns it transmits.
    expect(screen.getByRole("button", { name: /Run full check \(briefly transmits\)/i })).toBeInTheDocument();
  });

  it("Ports doctor renders a red fail + remedy and a no-radio row", async () => {
    // uhf-2 in the mock is a NinoTNC with switch-pinned DIPs and no radio — exercises the fail
    // (red) + remedy line and the "no radio attached" degradation row.
    mount(<Ports />);
    await waitFor(() => expect(screen.getByText("uhf-2")).toBeInTheDocument());

    let card: HTMLElement | null = screen.getByText("uhf-2");
    while (card && !within(card).queryByRole("button", { name: /Check radio/i })) {
      card = card.parentElement;
    }
    expect(card).not.toBeNull();
    fireEvent.click(within(card!).getByRole("button", { name: /Check radio/i }));

    await waitFor(() => expect(screen.getByText("dip-software-control")).toBeInTheDocument());
    expect(screen.getByText(/set all four DIP switches up/i)).toBeInTheDocument();
    expect(screen.getByText(/no radio attached to this port/i)).toBeInTheDocument();
  });

  it("Head-ends renders the fleet scan with an auto pairing + a conflict", async () => {
    mount(<HeadEnds />, "/headends");
    // The mock HEADEND_SCAN seeds shack-north (auto), garage-pi (ambiguous), an unreachable
    // instance, and a duplicate-id conflict — the discover→offer→adopt surface end to end.
    await waitFor(() => expect(screen.getByText("shack-north")).toBeInTheDocument());
    expect(screen.getByText(/suggested pairing/i)).toBeInTheDocument();
    expect(screen.getByText(/choose a pairing/i)).toBeInTheDocument();
    // The conflict + its remediation hint render prominently.
    expect(screen.getByText(/Duplicate head-end id/i)).toBeInTheDocument();
    // An unreachable head-end surfaces its error.
    expect(screen.getByText(/connection refused/i)).toBeInTheDocument();
    // The adopt affordance is present (mock enters as admin ⊇ operate).
    expect(screen.getAllByRole("button", { name: /Adopt/i }).length).toBeGreaterThan(0);
  });

  it("Capabilities renders the per-peer capability cache", async () => {
    const { container } = mount(<Capabilities />);
    expect(container.firstChild).toBeTruthy();
    // The mock fixtures seed three peers; the title renders immediately and a learned
    // peer row arrives once the (mock-async) query resolves — wait for the row.
    expect(screen.getAllByText(/Capabilities/i).length).toBeGreaterThan(0);
    await waitFor(() => expect(screen.getByText("M0LTE")).toBeInTheDocument());
  });

  it("Config renders the editor", async () => {
    const { container } = mount(<Config />);
    expect(container.firstChild).toBeTruthy();
    await waitFor(() => expect(screen.getAllByText(/Identity/i).length).toBeGreaterThan(0));
  });

  it("Config Services tab surfaces the ARDOP + POCSAG audio-service forms", async () => {
    // The two node-level soundmodem services (ardop / paging) are edited on the Services sub-tab —
    // previously reachable only through the Raw YAML tab. Proves both forms + a paging-only field wire in.
    mount(<Config />);
    await waitFor(() => expect(screen.getAllByText(/Identity/i).length).toBeGreaterThan(0));
    fireEvent.click(screen.getByRole("button", { name: "Services" }));
    await waitFor(() => expect(screen.getByText("ARDOP virtual TNC")).toBeInTheDocument());
    expect(screen.getByText("POCSAG paging")).toBeInTheDocument();
    // The POCSAG-only baud picker renders (the paging-specific fields are wired in).
    expect(screen.getByText("Baud")).toBeInTheDocument();
  });

  it("Waterfall surfaces the FrameQuality (FEC/CRC) readout for the selected port", async () => {
    // The soundmodem tuning waterfall polls GET /ports/{id}/quality (#635); the mock snapshot decodes
    // frames, so the FEC-corrected counters render (not the empty "no frames yet" state).
    mount(<Waterfall />, "/tools/waterfall");
    await waitFor(() => expect(screen.getByText(/Frame quality/i)).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText(/FEC-corrected/i)).toBeInTheDocument());
  });

  it("Users renders", async () => {
    const { container } = mount(<Users />);
    expect(container.firstChild).toBeTruthy();
    await waitFor(() => expect(screen.getAllByText(/Users/i).length).toBeGreaterThan(0));
  });

  it("Login is passkey-first in a secure context", () => {
    // The passkey affordance is gated on lib/secureContext.passkeysAvailable()
    // (window.isSecureContext + the WebAuthn API). jsdom defaults both falsy, so
    // simulate a secure context to exercise the passkey-first path.
    const prevSecure = window.isSecureContext;
    const prevPkc = (window as { PublicKeyCredential?: unknown }).PublicKeyCredential;
    Object.defineProperty(window, "isSecureContext", { value: true, configurable: true });
    (window as { PublicKeyCredential?: unknown }).PublicKeyCredential = function () {};
    try {
      mount(<Login />, "/login");
      expect(screen.getByText(/Continue with passkey/i)).toBeInTheDocument();
    } finally {
      Object.defineProperty(window, "isSecureContext", { value: prevSecure, configurable: true });
      (window as { PublicKeyCredential?: unknown }).PublicKeyCredential = prevPkc;
    }
  });

  it("Login degrades to password-only on plain HTTP (no secure context)", () => {
    // jsdom default: isSecureContext is falsy → no passkey button, password remains.
    mount(<Login />, "/login");
    expect(screen.queryByText(/Continue with passkey/i)).toBeNull();
    expect(screen.getByText(/Passkeys need HTTPS/i)).toBeInTheDocument();
    // Password login stays fully available (the LAN flow).
    expect(screen.getByText(/Username/i)).toBeInTheDocument();
    expect(screen.getByText(/Password/i)).toBeInTheDocument();
  });

  it("Setup wizard renders", () => {
    const { container } = mount(<Setup />, "/setup");
    expect(container.firstChild).toBeTruthy();
  });

  it("LinkTuner renders for a port", () => {
    const { container } = mount(<LinkTuner />, "/tools/tuner?port=vhf-1");
    expect(container.firstChild).toBeTruthy();
  });

  it("LinkTuner starts a deviation session and streams live rounds gated by 'Next round'", async () => {
    mount(<LinkTuner />, "/tools/tuner?port=vhf-1");
    await waitFor(() => expect(screen.getByText(/Deviation tuning/i)).toBeInTheDocument());

    // Enter an 8-char peer SDM id and arm the session (once config has loaded the port list, so the
    // Start button — gated on a selected port + a valid peer id — is enabled).
    fireEvent.change(screen.getByPlaceholderText(/8 chars/i), { target: { value: "12345678" } });
    await waitFor(() => expect(screen.getByRole("button", { name: /Start tuning/i })).not.toBeDisabled());
    fireEvent.click(screen.getByRole("button", { name: /Start tuning/i }));

    // The paused/transmitting banner appears and the first round lands in the trend table.
    await waitFor(() => expect(screen.getByText(/paused for tuning/i)).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText("0/5")).toBeInTheDocument(), { timeout: 3000 });

    // Once the round is awaiting the operator, "Next round" enables and advances the trend.
    await waitFor(
      () => expect(screen.getByRole("button", { name: /Next round/i })).not.toBeDisabled(),
      { timeout: 3000 },
    );
    fireEvent.click(screen.getByRole("button", { name: /Next round/i }));
    await waitFor(() => expect(screen.getByText("2/5")).toBeInTheDocument(), { timeout: 3000 });
  });

  it("LinkTroubleshoot renders per-link T1/T3/SRTT/retries", async () => {
    const { container } = mount(<LinkTroubleshoot />, "/links");
    expect(container.firstChild).toBeTruthy();
    await waitFor(() => expect(screen.getByText(/Link troubleshoot/i)).toBeInTheDocument());
    // The mock /links fixtures seed live links — wait for a peer row + the SRTT/retries columns.
    await waitFor(() => expect(screen.getAllByText("M0LTE").length).toBeGreaterThan(0));
    expect(screen.getAllByText(/SRTT/i).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Retries/i).length).toBeGreaterThan(0);
  });
});
