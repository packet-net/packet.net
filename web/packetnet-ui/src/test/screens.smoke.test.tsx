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
import { Config } from "@/screens/config";
import { Users } from "@/screens/users";
import { Login } from "@/screens/login";
import { Setup } from "@/screens/setup";
import { LinkTuner } from "@/screens/link-tuner";

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
});
