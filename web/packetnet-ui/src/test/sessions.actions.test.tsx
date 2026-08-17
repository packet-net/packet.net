// Sessions screen action guards (#702 C048).
//
// Two things were missing on a screen whose every endpoint is Operate-scoped:
//   1. Connect-out had no in-flight guard. POST /sessions awaits the connector's ConnectAsync
//      with a 30 s dial timeout and de-dups nothing per target, and the modal only closes on
//      SUCCESS — so a second click during the dial started a second outbound dial.
//   2. Only the header's Connect was scope-gated; the row Disconnect, the drawer Disconnect
//      and the drawer Send rendered live for a read-only operator (a guaranteed 403 on click).
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider, type Scope } from "@/app/auth";
import { Sessions } from "@/screens/sessions";
import { api } from "@/lib/api";

function seedScope(scope: Scope) {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

async function mountSessions(scope: Scope) {
  seedScope(scope);
  const result = render(
    <MemoryRouter>
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
    // The via-port select settles from /config; until it does there is no port worth POSTing
    // and the button is legitimately disabled.
    await waitFor(() => expect(within(modal).getByRole("combobox")).toBeEnabled());

    fireEvent.click(within(modal).getByRole("button", { name: /^Connect$/ }));

    // The button says what is happening and refuses further clicks.
    await waitFor(() =>
      expect(within(modal).getByRole("button", { name: /Connecting…/ })).toBeDisabled());
    fireEvent.click(within(modal).getByRole("button", { name: /Connecting…/ }));
    fireEvent.click(within(modal).getByRole("button", { name: /Connecting…/ }));

    expect(connect).toHaveBeenCalledTimes(1);
    expect(connect).toHaveBeenCalledWith("GB7RDG", "vhf-1");
  });

  it("re-enables Connect after a failed dial", async () => {
    vi.spyOn(api, "connectSession").mockRejectedValue(new Error("no route to GB7RDG"));
    await mountSessions("operate");

    fireEvent.click(screen.getByRole("button", { name: /^Connect$/ }));
    const modal = panel(/Connect out/);
    fireEvent.change(within(modal).getByPlaceholderText(/GB7CIP/), { target: { value: "GB7RDG" } });
    await waitFor(() => expect(within(modal).getByRole("combobox")).toBeEnabled());
    fireEvent.click(within(modal).getByRole("button", { name: /^Connect$/ }));

    await waitFor(() => expect(screen.getByText(/no route to GB7RDG/)).toBeInTheDocument());
    expect(within(modal).getByRole("button", { name: /^Connect$/ })).toBeEnabled();
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
