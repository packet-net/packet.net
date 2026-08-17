// Sessions screen action guards (#702 C048).
//
// Two things were missing on a screen whose every endpoint is Operate-scoped:
//   1. Connect-out had no in-flight guard. POST /sessions awaits the connector's ConnectAsync
//      with a 30 s dial timeout and de-dups nothing per target, and the modal only closes on
//      SUCCESS — so a second click during the dial started a second outbound dial.
//   2. Only the header's Connect was scope-gated; the row Disconnect, the drawer Disconnect
//      and the drawer Send rendered live for a read-only operator (a guaranteed 403 on click).
//
// The third block here is the connect-out VIA PORT (#727, hotfix node-v0.41.1): since
// node-v0.41.0 a portId in POST /sessions means a DIRECT AX.25 dial on that port and never a
// NET/ROM-routed one, and the dialog always sent one - so an alias, or a destination clicked
// through from Routes, went out as a raw SABM and 504'd after the 30 s dial timeout.
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes as RouterRoutes } from "react-router-dom";
import { AuthProvider, type Scope } from "@/app/auth";
import { Sessions } from "@/screens/sessions";
import { Routes as RoutesScreen } from "@/screens/routes";
import { api } from "@/lib/api";
import { NODE_CONFIG } from "@/lib/mock";
import type { NodeConfig, SessionInfo } from "@/lib/types";

function seedScope(scope: Scope) {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

async function mountSessions(scope: Scope, route = "/sessions") {
  seedScope(scope);
  const result = render(
    <MemoryRouter initialEntries={[route]}>
      <AuthProvider><Sessions /></AuthProvider>
    </MemoryRouter>,
  );
  // The mock session table has landed once a peer row renders.
  await waitFor(() => expect(screen.getByRole("button", { name: "M0LTE" })).toBeInTheDocument());
  return result;
}

/** The row containing `peer`'s cell (the <tr>). */
function row(peer: string): HTMLElement {
  return screen.getByRole("button", { name: peer }).closest("tr") as HTMLElement;
}

/** The open modal panel wrapping `title` (the Modal primitive's card). */
function panel(title: RegExp): HTMLElement {
  return screen.getByText(title).closest("div.relative") as HTMLElement;
}

/** The open session drawer (the Sheet primitive renders a radix dialog into a portal). */
function drawer(): HTMLElement {
  return screen.getByRole("dialog");
}

afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Sessions — connect-out", () => {
  it("dials once however many times Connect is clicked while it is in flight", async () => {
    // A dial that never resolves during the test: the modal stays open, exactly as it does
    // for the real 30 s DialTimeout.
    const connect = vi.spyOn(api, "connectSession").mockReturnValue(new Promise(() => {}));
    await mountSessions("operate");

    fireEvent.click(screen.getByRole("button", { name: /^Connect$/ }));
    const modal = panel(/Connect out/);
    fireEvent.change(within(modal).getByPlaceholderText(/GB7CIP/), { target: { value: "GB7RDG" } });
    // No waiting for /config any more: auto is a legitimate via-port choice and is postable
    // straight away, so the button is live as soon as there is a target (#727).
    fireEvent.click(within(modal).getByRole("button", { name: /^Connect$/ }));

    // The button says what is happening and refuses further clicks.
    await waitFor(() =>
      expect(within(modal).getByRole("button", { name: /Connecting…/ })).toBeDisabled());
    fireEvent.click(within(modal).getByRole("button", { name: /Connecting…/ }));
    fireEvent.click(within(modal).getByRole("button", { name: /Connecting…/ }));

    expect(connect).toHaveBeenCalledTimes(1);
    // The fake node does NET/ROM Transit routing, so the dialog is on auto and the empty
    // port is what api.connectSession turns into a body with no portId. The via-port block
    // below owns that behaviour; here it just has to be ONE call.
    expect(connect).toHaveBeenCalledWith("GB7RDG", "");
  });

  it("re-enables Connect after a failed dial", async () => {
    vi.spyOn(api, "connectSession").mockRejectedValue(new Error("no route to GB7RDG"));
    await mountSessions("operate");

    fireEvent.click(screen.getByRole("button", { name: /^Connect$/ }));
    const modal = panel(/Connect out/);
    fireEvent.change(within(modal).getByPlaceholderText(/GB7CIP/), { target: { value: "GB7RDG" } });
    fireEvent.click(within(modal).getByRole("button", { name: /^Connect$/ }));

    await waitFor(() => expect(screen.getByText(/no route to GB7RDG/)).toBeInTheDocument());
    expect(within(modal).getByRole("button", { name: /^Connect$/ })).toBeEnabled();
  });
});

// ---- the via port: auto (the node routes it) vs a named port (a direct AX.25 dial) ----
// A session the connect returns, so the parent can open its drawer (mock-leak's idiom).
const CONNECTED: SessionInfo = {
  id: "vhf-1:GB7CIP", portId: "vhf-1", peer: "GB7CIP", role: "console", state: "Connected",
  vs: 0, vr: 0, window: 4, uptimeSeconds: 0, bytesIn: 0, bytesOut: 0, lastActivity: "0:00:00",
};

describe("Sessions connect-out - the via port (#727)", () => {
  /** Open the connect-out modal from the screen header. */
  function openConnectOut(): HTMLElement {
    fireEvent.click(screen.getByRole("button", { name: /^Connect$/ }));
    return panel(/Connect out/);
  }

  /** The via-port select, once /config has settled this node's ports into its options. */
  async function viaPort(modal: HTMLElement, aPortOfThisNode: string): Promise<HTMLSelectElement> {
    const select = within(modal).getByRole("combobox") as HTMLSelectElement;
    await waitFor(() =>
      expect([...select.options].map((o) => o.value)).toContain(aPortOfThisNode));
    return select;
  }

  it("defaults to auto and posts NO port when this node routes connects over NET/ROM", async () => {
    // The fake node is netRom.enabled with Transit routing, which is the server's
    // NetRomService.ConnectEnabled predicate, so the node has a NET/ROM connector to route
    // this call with and the dialog must not pre-empt it with a port.
    const connect = vi.spyOn(api, "connectSession").mockResolvedValue(CONNECTED);
    await mountSessions("operate");

    const modal = openConnectOut();
    const select = await viaPort(modal, "vhf-1");
    expect(select.options[0].value).toBe("");
    expect(select.options[0].textContent).toMatch(/Auto \(NET\/ROM routing\)/);
    expect(select.value).toBe("");

    fireEvent.change(within(modal).getByPlaceholderText(/GB7CIP/), { target: { value: "CIPGW" } });
    fireEvent.click(within(modal).getByRole("button", { name: /^Connect$/ }));

    // The empty port is the sentinel api.connectSession turns into a body with no portId at
    // all (asserted on the wire in api.live.test.ts), which is what makes the node dial
    // through its NET/ROM-wrapped default connector instead of a raw SABM on an RF port.
    await waitFor(() => expect(connect).toHaveBeenCalledWith("CIPGW", ""));
  });

  it("posts the port shown in the select when the operator names one", async () => {
    const connect = vi.spyOn(api, "connectSession").mockResolvedValue(CONNECTED);
    await mountSessions("operate");

    const modal = openConnectOut();
    const select = await viaPort(modal, "uhf-2");
    fireEvent.change(within(modal).getByPlaceholderText(/GB7CIP/), { target: { value: "gb7cip" } });
    fireEvent.change(select, { target: { value: "uhf-2" } });
    expect(select.value).toBe("uhf-2");

    // Naming a port is a deliberate DIRECT dial on it, and still has to be honoured.
    fireEvent.click(within(modal).getByRole("button", { name: /^Connect$/ }));
    await waitFor(() => expect(connect).toHaveBeenCalledWith("GB7CIP", "uhf-2"));
  });

  it("defaults to the node's first port when NET/ROM connect routing is off", async () => {
    // Passive NET/ROM: the node hears and maintains the routing table but opens no
    // interlinks, so there is no NET/ROM connector to route through and a port is the only
    // thing a dial can mean. This is the pre-#727 behaviour, which must not change.
    const passive: NodeConfig = {
      ...NODE_CONFIG,
      netRom: { ...NODE_CONFIG.netRom, routing: "None", effectiveRouting: "None" },
    };
    vi.spyOn(api, "config").mockResolvedValue(passive);
    const connect = vi.spyOn(api, "connectSession").mockResolvedValue(CONNECTED);
    await mountSessions("operate");

    const modal = openConnectOut();
    const select = await viaPort(modal, "vhf-1");
    await waitFor(() => expect(select.value).toBe("vhf-1"));

    fireEvent.change(within(modal).getByPlaceholderText(/GB7CIP/), { target: { value: "GB7CIP" } });
    fireEvent.click(within(modal).getByRole("button", { name: /^Connect$/ }));
    await waitFor(() => expect(connect).toHaveBeenCalledWith("GB7CIP", "vhf-1"));
  });

  it("lands in auto when Routes hands off a NET/ROM destination", async () => {
    // The whole path, end to end: GB7SAN is only reachable THROUGH the neighbour GB7BNS, and
    // the row's Connect used to hand off GB7BNS's port - which the node then read as "dial
    // GB7SAN directly on vhf-1", a SABM into thin air and a 504 after 30 s.
    seedScope("operate");
    const connect = vi.spyOn(api, "connectSession").mockResolvedValue(CONNECTED);
    render(
      <MemoryRouter initialEntries={["/routes"]}>
        <AuthProvider>
          <RouterRoutes>
            <Route path="/routes" element={<RoutesScreen />} />
            <Route path="/sessions" element={<Sessions />} />
          </RouterRoutes>
        </AuthProvider>
      </MemoryRouter>,
    );

    const destRow = (await screen.findByText("GB7SAN")).closest("tr") as HTMLElement;
    fireEvent.click(within(destRow).getByRole("button", { name: /Connect/ }));

    await screen.findByText(/Connect out/);
    const modal = panel(/Connect out/);
    expect((within(modal).getByPlaceholderText(/GB7CIP/) as HTMLInputElement).value).toBe("GB7SAN");
    const select = await viaPort(modal, "vhf-1");
    expect(select.value).toBe("");

    fireEvent.click(within(modal).getByRole("button", { name: /^Connect$/ }));
    await waitFor(() => expect(connect).toHaveBeenCalledWith("GB7SAN", ""));
  });
});

describe("Sessions — operate gating", () => {
  it("disables Disconnect and Send for a read-only operator, with the reason", async () => {
    await mountSessions("read");

    // The header Connect was always gated; the row action is the one that was not. The row's
    // buttons in order: the peer (opens the drawer), Open, Disconnect.
    const drop = within(row("M0LTE")).getAllByRole("button")[2];
    expect(drop).toBeDisabled();
    expect(drop).toHaveAttribute("title", "Disconnecting requires the operate scope");

    // The drawer's own Disconnect + Send, same treatment.
    fireEvent.click(screen.getByRole("button", { name: "M0LTE" }));
    await waitFor(() => expect(screen.getByText(/Session — M0LTE/)).toBeInTheDocument());
    const sheet = drawer();
    expect(within(sheet).getByRole("button", { name: /Disconnect/ })).toBeDisabled();
    expect(within(sheet).getByRole("button", { name: /Send/ })).toBeDisabled();
    expect(within(sheet).getByPlaceholderText(/requires the operate scope/)).toBeDisabled();
  });

  it("leaves them live for an operator", async () => {
    await mountSessions("operate");

    const drop = within(row("M0LTE")).getAllByRole("button")[2];
    expect(drop).toBeEnabled();
    expect(drop).toHaveAttribute("title", "Disconnect");

    fireEvent.click(screen.getByRole("button", { name: "M0LTE" }));
    await waitFor(() => expect(screen.getByText(/Session — M0LTE/)).toBeInTheDocument());
    expect(within(drawer()).getByRole("button", { name: /Send/ })).toBeEnabled();
  });
});
