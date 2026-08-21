// Render smoke test for every screen: mounts each against the mock API backend
// and asserts it renders without throwing + surfaces a key piece of copy. Catches
// runtime crashes (bad hook order, undefined access, missing context) that the
// type-check can't — the verification gate in lieu of headless-browser screenshots
// (the host LXC blocks Chrome's network sockets, so visual screenshotting isn't
// possible in CI here).
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within, act, type RenderResult } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { api } from "@/lib/api";
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

// Seed the persisted session AuthProvider rehydrates from, for the screens whose controls are
// scope-gated (the behaviour suites' idiom). Cleared after every test so the default here stays
// "no session" - which is what most of these smoke mounts assert against.
function seedScope(scope: "read" | "operate" | "admin") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
  // A test that faked time hands it back here, so a later mount never inherits a frozen clock.
  vi.useRealTimers();
});

describe("screens render without crashing", () => {
  it("Dashboard surfaces node status", async () => {
    mount(<Dashboard />);
    await waitFor(() => expect(screen.getAllByText(/GB7RDG/).length).toBeGreaterThan(0));
  });

  it("Monitor shows the live monitor", async () => {
    mount(<Monitor />);
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

  it("Sessions lists the live circuits, not just the page heading", async () => {
    mount(<Sessions />);
    await waitFor(() => expect(screen.getAllByText(/Sessions/i).length).toBeGreaterThan(0));
    // The payload is the circuit table: a seeded peer with its port, role and AX.25 state.
    await waitFor(() => expect(screen.getByText("M0LTE")).toBeInTheDocument());
    const row = screen.getByText("M0LTE").closest("tr")!;
    expect(within(row).getByText("vhf-1")).toBeInTheDocument();
    expect(within(row).getByText("console")).toBeInTheDocument();
    expect(within(row).getByText("Connected")).toBeInTheDocument();
    // A peer in timer recovery renders its own state, so the column is per-row, not a constant.
    expect(screen.getByText("TimerRecovery")).toBeInTheDocument();
  });

  it("Console renders the node command console terminal", async () => {
    // Admin-gated screen; seed an admin session so it exercises the open path (mock api
    // returns a synthetic id + a banner). The terminal host always mounts.
    localStorage.setItem(
      "pdn.session",
      JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope: "admin" }),
    );
    try {
      mount(<Console />, "/console");
      await waitFor(() => expect(screen.getByTestId("console-terminal")).toBeInTheDocument());
      // The dense layout dropped the page title; the status strip is the stable key copy.
      expect(screen.getByText(/connecting|connected|closed|unavailable/i)).toBeInTheDocument();
    } finally {
      localStorage.clear();
    }
  });

  it("Apps renders the management surface (no launcher grid — apps live in the nav now)", async () => {
    mount(<Apps />);
    // The Apps page is pure management now: "Available apps" (install) + "Manage apps". The
    // launcher grid moved to the left-nav (see shell.nav.test.tsx), so WALL appears here only
    // as a management row, never as an /apps/wall/ launcher anchor.
    await waitFor(() => expect(screen.getByText(/Available apps/i)).toBeInTheDocument());
    expect(screen.getByText(/Manage apps/i)).toBeInTheDocument();
    const wallLink = screen.queryAllByText("WALL").map((el) => el.closest("a")).find((a) => a !== null);
    expect(wallLink).toBeUndefined();
  });

  it("Routes renders the destinations/neighbours view", async () => {
    mount(<Routes />);
    await waitFor(() => expect(screen.getByText(/Destinations/i)).toBeInTheDocument());
    // The default tab is the destinations table, and a learned destination is a whole row:
    // its alias and the neighbour its best route goes through. A heading renders over an
    // empty snapshot too, which is what this test used to settle for.
    await waitFor(() => expect(screen.getByText("G1MNW-2")).toBeInTheDocument());
    const row = screen.getByText("G1MNW-2").closest("tr")!;
    expect(within(row).getByText("READNG")).toBeInTheDocument();
    expect(within(row).getByText("G8PZT-7")).toBeInTheDocument();
  });

  it("Ports lists the configured ports, not just the page heading", async () => {
    mount(<Ports />);
    await waitFor(() => expect(screen.getAllByText(/Ports/i).length).toBeGreaterThan(0));
    // Every mock port gets a card, each with its own Edit affordance - an empty shell would
    // still render the heading, so the port ids are the assertion that matters here.
    await waitFor(() => expect(screen.getByText("vhf-1")).toBeInTheDocument());
    for (const id of ["uhf-2", "link-dn", "mp-net"]) {
      expect(screen.getByText(id)).toBeInTheDocument();
    }
    expect(screen.getAllByRole("button", { name: "Edit" }).length).toBeGreaterThanOrEqual(4);
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

  it("Ports editor surfaces the per-port link setup (dial version + pre-connect XID)", async () => {
    // The hf-300 mock port is the BPQ-facing one: link { dial: V20, preConnectXid: On }. A
    // non-default link policy counts as "tuned", so the editor opens with Advanced expanded and
    // both dropdowns show the port's stored policy rather than the Auto placeholder (#724).
    mount(<Ports />);
    await waitFor(() => expect(screen.getByText("hf-300")).toBeInTheDocument());

    let card: HTMLElement | null = screen.getByText("hf-300");
    while (card && !within(card).queryByRole("button", { name: "Edit" })) {
      card = card.parentElement;
    }
    expect(card).not.toBeNull();
    fireEvent.click(within(card!).getByRole("button", { name: "Edit" }));

    await waitFor(() => expect(screen.getByText(/Edit port .* hf-300/i)).toBeInTheDocument());
    expect(screen.getByText(/Link setup/i)).toBeInTheDocument();

    // The Field label is not htmlFor-wired to its control, so walk from the label text to the
    // enclosing Field and read the select inside it (the idiom the other editor smokes use).
    const selectUnder = (label: RegExp): HTMLSelectElement => {
      let host: HTMLElement | null = screen.getByText(label);
      while (host && !within(host).queryByRole("combobox")) {
        host = host.parentElement;
      }
      expect(host, `no select under ${label}`).not.toBeNull();
      return within(host!).getByRole("combobox") as HTMLSelectElement;
    };

    expect(selectUnder(/Outgoing call version/i).value).toBe("V20");
    expect(selectUnder(/Ask for SREJ before a 2.0 call/i).value).toBe("On");
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

  it("a panel-created port POSTs TX delay in 10 ms wire units, not milliseconds", async () => {
    // #692 C032: the ms->units conversion was pinned against the catalogue helper only, so
    // nothing proved the Add-port POST body actually carries it. A stock 300 ms TX delay must
    // reach the server as the byte 30; sending 300 is not even representable in a KISS byte.
    seedScope("operate");
    const addPort = vi.spyOn(api, "addPort").mockResolvedValue({
      valid: true, live: [], portRestart: [], nodeReset: [], applied: true,
    });
    mount(<Ports />);

    fireEvent.click(await screen.findByRole("button", { name: /Add port/i }));
    fireEvent.change(screen.getByPlaceholderText("vhf-1"), { target: { value: "new-1" } });

    // A stock new port is untuned, so the advanced section starts closed; open it to reach the
    // modem keying knobs, then walk from the field's label to its input.
    fireEvent.click(screen.getByText(/Advanced parameters/i));
    const txDelayField = screen.getByText("TX delay").closest("div")!.parentElement!;
    const txDelay = within(txDelayField).getByRole("spinbutton") as HTMLInputElement;
    fireEvent.change(txDelay, { target: { value: "300" } });
    fireEvent.blur(txDelay);
    // The operator's units stay on screen; only the wire is in 10 ms steps.
    expect(txDelay.value).toBe("300");

    fireEvent.click(screen.getByRole("button", { name: /Save changes/i }));
    await screen.findByText("Apply changes?");
    fireEvent.click(screen.getByRole("button", { name: /^Apply( anyway)?$/i }));

    await waitFor(() => expect(addPort).toHaveBeenCalledTimes(1));
    expect(addPort.mock.calls[0][0].kiss).toMatchObject({ txDelay: 30 });
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
    mount(<Capabilities />);
    // The mock fixtures seed three peers; the title renders immediately and a learned
    // peer row arrives once the (mock-async) query resolves — wait for the row.
    expect(screen.getAllByText(/Capabilities/i).length).toBeGreaterThan(0);
    await waitFor(() => expect(screen.getByText("M0LTE")).toBeInTheDocument());
  });

  it("Config renders the editor seeded from the loaded config", async () => {
    mount(<Config />);
    await waitFor(() => expect(screen.getAllByText(/Identity/i).length).toBeGreaterThan(0));
    // The draft is seeded from GET /config, so the station identity is on screen in editable
    // fields - an editor that rendered its tabs over an empty draft would pass a heading check.
    expect((screen.getByDisplayValue("GB7RDG") as HTMLInputElement).value).toBe("GB7RDG");
    expect(screen.getByDisplayValue("RDGGW")).toBeInTheDocument();
    expect(screen.getByDisplayValue("IO91nl")).toBeInTheDocument();
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

  it("toggling NET/ROM compression PUTs netRom.compress on the wire", async () => {
    // #692 C032: the GB7RDG migration's L4Compress leg was "tested" by JSON.stringify/parse of
    // the mock, which is true of any JSON-safe object. What actually has to hold is that the
    // Forms editor's save body carries the toggled flag - the Raw-YAML tab already did.
    seedScope("operate");
    const putConfig = vi.spyOn(api, "putConfig").mockResolvedValue({
      valid: true, live: [], portRestart: [], nodeReset: [], applied: false,
    });
    mount(<Config />);
    await waitFor(() => expect(screen.getAllByText(/Identity/i).length).toBeGreaterThan(0));
    fireEvent.click(screen.getByRole("button", { name: "NET/ROM + INP3" }));
    await waitFor(() => expect(screen.getByText("Compress circuit data")).toBeInTheDocument());

    const row = screen.getByText("Compress circuit data").closest("div")!.parentElement!;
    const toggle = within(row).getByRole("switch");
    expect(toggle).toHaveAttribute("aria-checked", "false");   // off in the mock config
    fireEvent.click(toggle);

    // Review & apply sends the draft to the server's dry run first - that body is the save body.
    fireEvent.click(screen.getByRole("button", { name: /Review & apply/i }));
    await waitFor(() => expect(putConfig).toHaveBeenCalled());
    const [body, opts] = putConfig.mock.calls[0];
    expect(opts).toEqual({ dryRun: true });
    expect(body.netRom.compress).toBe(true);
    // The rest of the block rides along untouched (the editor sends the whole document).
    expect(body.netRom.routing).toBe("Transit");
    expect(body.identity.callsign).toBe("GB7RDG");
  });

  it("Waterfall surfaces the FrameQuality (FEC/CRC) readout for the selected port", async () => {
    // The soundmodem tuning waterfall polls GET /ports/{id}/quality (#635); the mock snapshot decodes
    // frames, so the FEC-corrected counters render (not the empty "no frames yet" state).
    mount(<Waterfall />, "/tools/waterfall");
    await waitFor(() => expect(screen.getByText(/Frame quality/i)).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText(/FEC-corrected/i)).toBeInTheDocument());
  });

  it("Users lists each operator with their scope and auth methods", async () => {
    mount(<Users />);
    await waitFor(() => expect(screen.getAllByText(/Users/i).length).toBeGreaterThan(0));
    // One card per operator from GET /users, each carrying the scope badge and the auth-method
    // rows (the screen's actual content - the heading renders even with an empty list).
    await waitFor(() => expect(screen.getByText("tom")).toBeInTheDocument());
    expect(screen.getAllByText("admin").length).toBeGreaterThan(0);
    // (the two auth headings appear on the user's card and again in the explanatory footer)
    expect(screen.getAllByText("Web login").length).toBeGreaterThan(0);
    expect(screen.getAllByText("On-air auth").length).toBeGreaterThan(0);
    expect(screen.getByText("Password")).toBeInTheDocument();
    expect(screen.getByText("Passkeys")).toBeInTheDocument();
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
    // No affordance and no hint - the node does not prescribe how to reach it.
    expect(screen.queryByText(/Passkeys need HTTPS/i)).toBeNull();
    expect(screen.queryByText(/Tailscale/i)).toBeNull();
    // Password login stays fully available (the LAN flow).
    expect(screen.getByText(/Username/i)).toBeInTheDocument();
    expect(screen.getByText(/Password/i)).toBeInTheDocument();
  });

  it("Setup wizard renders its three steps, starting on station identity", () => {
    mount(<Setup />, "/setup");
    // The stepper names all three steps up front (an operator can see what first run involves).
    for (const step of ["Station identity", "Create admin", "First port"]) {
      expect(screen.getByText(step)).toBeInTheDocument();
    }
    // Step one is the callsign, and it gates Continue - the wizard cannot be walked past an
    // empty identity, which is the only thing the node genuinely cannot default.
    expect(screen.getByText(/Callsign \(required\)/i)).toBeInTheDocument();
    const callsign = screen.getByPlaceholderText("GB7AAA") as HTMLInputElement;
    expect(callsign.value).toBe("");
    expect(screen.getByRole("button", { name: /Continue/i })).toBeDisabled();
    fireEvent.change(callsign, { target: { value: "m0lte" } });
    // Callsigns are upper-cased as typed, and Continue unlocks.
    expect(callsign.value).toBe("M0LTE");
    expect(screen.getByRole("button", { name: /Continue/i })).not.toBeDisabled();
  });

  it("LinkTuner renders the deviation-tuning setup for a port", async () => {
    mount(<LinkTuner />, "/tools/tuner?port=vhf-1");
    expect(screen.getByText(/Deviation tuning/i)).toBeInTheDocument();
    // The peer SDM id is the one thing the operator must supply before a session can be armed,
    // and Start stays disabled until it is 8 characters long.
    const peer = screen.getByPlaceholderText(/8 chars/i) as HTMLInputElement;
    expect(peer.value).toBe("");
    expect(screen.getByRole("button", { name: /Start tuning/i })).toBeDisabled();
    // ?port= selects that port out of the loaded config rather than defaulting to the first.
    // (The Port picker is the first of the form's two selects; the second is the role.)
    await waitFor(() => expect((screen.getAllByRole("combobox")[0] as HTMLSelectElement).value).toBe("vhf-1"));
  });

  it("LinkTuner starts a deviation session and streams live rounds gated by 'Next round'", async () => {
    // Start/Next/Stop are admin-gated (the /tuning endpoints are Admin-only, #702 C047).
    seedScope("admin");
    mount(<LinkTuner />, "/tools/tuner?port=vhf-1");
    await waitFor(() => expect(screen.getByText(/Deviation tuning/i)).toBeInTheDocument());

    // Enter an 8-char peer SDM id and arm the session (once config has loaded the port list, so the
    // Start button — gated on a selected port + a valid peer id — is enabled).
    fireEvent.change(screen.getByPlaceholderText(/8 chars/i), { target: { value: "12345678" } });
    await waitFor(() => expect(screen.getByRole("button", { name: /Start tuning/i })).not.toBeDisabled());

    // From here the test drives the mock feed's clock instead of waiting on it: lib/mock.ts
    // driveTuneStream schedules peer-connected at 500 ms, the first round at 1200 ms and the
    // awaiting-adjustment that unlocks "Next round" 400 ms after each round, and this test used
    // to spend all ~1.9 s of that for real under 3 s waitFors. The timers are faked only now, so
    // the ones being faked are the session's; and no waitFor may be used past this point, since
    // vitest defines no `jest` global and testing-library therefore cannot auto-advance them.
    vi.useFakeTimers();
    fireEvent.click(screen.getByRole("button", { name: /Start tuning/i }));

    // api.startTune's mock sleeps 200 ms before the session comes back, then the SSE attaches.
    await act(async () => { await vi.advanceTimersByTimeAsync(200); });
    expect(screen.getByText(/paused for tuning/i)).toBeInTheDocument();

    // 1200 ms in, the first measurement round lands in the trend table (0 of 5 decoded).
    await act(async () => { await vi.advanceTimersByTimeAsync(1200); });
    expect(screen.getByText("0/5")).toBeInTheDocument();
    // ... and it is NOT yet the operator's turn: the pot has not been measured as settled.
    expect(screen.getByRole("button", { name: /Next round/i })).toBeDisabled();

    // 400 ms later the round reports awaiting-adjustment and "Next round" unlocks.
    await act(async () => { await vi.advanceTimersByTimeAsync(400); });
    expect(screen.getByRole("button", { name: /Next round/i })).not.toBeDisabled();

    // The gate is the whole point: rounds are operator-driven, so no amount of elapsed time
    // advances the trend on its own. Ten seconds of the session's clock, still one round.
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
    expect(screen.queryByText("2/5")).toBeNull();
    expect(screen.getAllByText("0/5")).toHaveLength(1);

    // Only the click advances it.
    fireEvent.click(screen.getByRole("button", { name: /Next round/i }));
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(screen.getByText("2/5")).toBeInTheDocument();
  });

  it("LinkTroubleshoot renders per-link T1/T3/SRTT/retries", async () => {
    mount(<LinkTroubleshoot />, "/links");
    await waitFor(() => expect(screen.getByText(/Link troubleshoot/i)).toBeInTheDocument());
    // The mock /links fixtures seed live links — wait for a peer row + the SRTT/retries columns.
    await waitFor(() => expect(screen.getAllByText("M0LTE").length).toBeGreaterThan(0));
    expect(screen.getAllByText(/SRTT/i).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Retries/i).length).toBeGreaterThan(0);
  });
});
