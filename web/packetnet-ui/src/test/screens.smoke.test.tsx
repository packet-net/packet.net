// Render smoke test for every screen: mounts each against the mock API backend
// and asserts it renders without throwing + surfaces a key piece of copy. Catches
// runtime crashes (bad hook order, undefined access, missing context) that the
// type-check can't — the verification gate in lieu of headless-browser screenshots
// (the host LXC blocks Chrome's network sockets, so visual screenshotting isn't
// possible in CI here).
import { describe, it, expect } from "vitest";
import { render, screen, waitFor, type RenderResult } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import type { ReactElement } from "react";

import { Dashboard } from "@/screens/dashboard";
import { Monitor } from "@/screens/monitor";
import { Sessions } from "@/screens/sessions";
import { Routes } from "@/screens/routes";
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

  it("Login is passkey-first", () => {
    mount(<Login />, "/login");
    expect(screen.getByText(/Continue with passkey/i)).toBeInTheDocument();
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
