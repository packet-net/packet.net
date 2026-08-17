// Tests for the Config screen's "Remote access (Tailscale)" panel (network-access.md S2).
// Mounts the Config screen against the mock API backend, switches to the Management tab,
// and spies api.tailscaleStatus to drive each state: needs-login (the authorize link),
// running (the reachable FQDN), and the RP-id adoption button (shown when fqdn ≠ the
// current relyingPartyId, admin-gated). Mirrors apps.available.test.tsx's mount + spy style.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Config } from "@/screens/config";
import { api } from "@/lib/api";
import type { TailscaleStatus } from "@/lib/types";

function seedScope(scope: "read" | "admin") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

// Mount Config, wait for the draft to seed (the Management tab button appears), then open it.
async function mountManagement(status: TailscaleStatus, scope: "read" | "admin" = "admin") {
  seedScope(scope);
  vi.spyOn(api, "tailscaleStatus").mockResolvedValue(status);
  render(
    <MemoryRouter>
      <AuthProvider>
        <Config />
      </AuthProvider>
    </MemoryRouter>,
  );
  const mgmt = await screen.findByRole("button", { name: "Management" });
  fireEvent.click(mgmt);
  await waitFor(() => expect(document.querySelector('[data-testid="tailscale-panel"]')).not.toBeNull());
}

function panel(): HTMLElement {
  const el = document.querySelector('[data-testid="tailscale-panel"]');
  expect(el).not.toBeNull();
  return el as HTMLElement;
}

beforeEach(() => {
  localStorage.clear();
});
afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Config — Remote access (Tailscale) panel", () => {
  it("renders the authorize link when the sidecar needs interactive login", async () => {
    await mountManagement({
      enabled: true, state: "needs-login",
      authUrl: "https://login.tailscale.com/a/abc123", fqdn: null, funnel: false,
    });
    await waitFor(() => {
      const link = within(panel()).getByRole("link", { name: /authorize this node/i });
      expect(link).toHaveAttribute("href", "https://login.tailscale.com/a/abc123");
    });
  });

  it("shows the reachable FQDN when running", async () => {
    await mountManagement({
      enabled: true, state: "running",
      fqdn: "pdn.example.ts.net", authUrl: null, funnel: false,
    });
    await waitFor(() => {
      const link = within(panel()).getByRole("link", { name: "https://pdn.example.ts.net" });
      expect(link).toHaveAttribute("href", "https://pdn.example.ts.net");
    });
  });

  it("offers the RP-id adoption button when the fqdn differs from the relying-party id", async () => {
    // The mock NodeConfig's relyingPartyId is "localhost", so a running .ts.net fqdn differs.
    await mountManagement({
      enabled: true, state: "running",
      fqdn: "pdn.example.ts.net", authUrl: null, funnel: false,
    });
    await waitFor(() => {
      expect(within(panel()).getByRole("button", { name: /use .*pdn\.example\.ts\.net.* for passkeys/i })).toBeInTheDocument();
    });
  });

  it("hides the RP-id button when the fqdn already matches the relying-party id", async () => {
    // localhost matches the mock relyingPartyId → no adoption needed.
    await mountManagement({
      enabled: true, state: "running",
      fqdn: "localhost", authUrl: null, funnel: false,
    });
    await waitFor(() => expect(within(panel()).getByRole("link", { name: "https://localhost" })).toBeInTheDocument());
    expect(within(panel()).queryByRole("button", { name: /for passkeys/i })).toBeNull();
  });

  it("clicking the RP-id button calls useFqdnForPasskeys with the fqdn", async () => {
    const adopt = vi.spyOn(api, "useFqdnForPasskeys").mockResolvedValue(
      { valid: true, live: [], portRestart: [], nodeReset: [], applied: true });
    await mountManagement({
      enabled: true, state: "running",
      fqdn: "pdn.example.ts.net", authUrl: null, funnel: false,
    });
    const btn = await within(panel()).findByRole("button", { name: /for passkeys/i });
    fireEvent.click(btn);
    await waitFor(() => expect(adopt).toHaveBeenCalledWith("pdn.example.ts.net"));
  });

  it("adopting the fqdn keeps unsaved edits and merges the new RP id into the draft", async () => {
    // #702 C039: the adopt used to call reload(), which refetched /config and re-seeded the
    // draft from it - silently discarding every in-flight edit while `dirty` still claimed
    // they were pending. The write is already applied server-side, so the client merges just
    // that block instead.
    const adopt = vi.spyOn(api, "useFqdnForPasskeys").mockResolvedValue(
      { valid: true, live: [], portRestart: [], nodeReset: [], applied: true });
    await mountManagement({
      enabled: true, state: "running",
      fqdn: "pdn.example.ts.net", authUrl: null, funnel: false,
    });

    // An unsaved edit in the same tab: the management HTTP port.
    fireEvent.change(screen.getByDisplayValue("8080"), { target: { value: "8090" } });
    expect(screen.getByDisplayValue("8090")).toBeInTheDocument();

    fireEvent.click(await within(panel()).findByRole("button", { name: /for passkeys/i }));
    await waitFor(() => expect(adopt).toHaveBeenCalledWith("pdn.example.ts.net"));

    // The adopted RP id landed in the draft (the button has nothing left to offer)…
    await waitFor(() => expect(within(panel()).queryByRole("button", { name: /for passkeys/i })).toBeNull());
    // …and the edit is still there, still counted as pending.
    expect(screen.getByDisplayValue("8090")).toBeInTheDocument();
    expect(within(screen.getByRole("button", { name: /Review & apply/i })).getByText("1"))
      .toBeInTheDocument();
  });

  it("disables the RP-id button for a non-admin (read) scope", async () => {
    await mountManagement({
      enabled: true, state: "running",
      fqdn: "pdn.example.ts.net", authUrl: null, funnel: false,
    }, "read");
    const btn = await within(panel()).findByRole("button", { name: /for passkeys/i });
    expect(btn).toBeDisabled();
  });

  it("shows the disabled state when the sidecar is off", async () => {
    await mountManagement({
      enabled: false, state: "disabled", fqdn: null, authUrl: null, funnel: false,
    });
    await waitFor(() => expect(within(panel()).getByText(/HTTP-only/i)).toBeInTheDocument());
  });
});
