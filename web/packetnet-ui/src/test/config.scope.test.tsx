// The Config screen's scope gating (review item C020). The server gates PUT /config and
// PUT /config/raw on `operate` - the shipped model - while the screen gated Review-and-apply
// on `admin`, so an operator was locked out of the editor they are entitled to use and told
// the wrong scope. The two admin panels inside the Management tab stay admin-gated: the
// self-update is admin server-side, and the passkey-hostname adopt writes management.auth,
// which the server now requires admin for.
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { render, screen, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Config } from "@/screens/config";
import { api } from "@/lib/api";

function seedScope(scope: "read" | "operate" | "admin") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

// Mount Config and switch to the raw-YAML editor, where Review-and-apply is gated on the
// scope alone (the forms mode additionally requires a dirty field).
async function mountRaw(scope: "read" | "operate" | "admin") {
  seedScope(scope);
  render(
    <MemoryRouter>
      <AuthProvider>
        <Config />
      </AuthProvider>
    </MemoryRouter>,
  );
  const raw = await screen.findByRole("button", { name: "Raw YAML" });
  fireEvent.click(raw);
  return await screen.findByRole("button", { name: /Review & apply/i });
}

beforeEach(() => localStorage.clear());
afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Config - who may apply a config change", () => {
  it("lets an operate-scope user apply (the server gates the write on operate)", async () => {
    const apply = await mountRaw("operate");
    expect(apply).not.toBeDisabled();
  });

  it("refuses a read-scope user and names the scope the server actually wants", async () => {
    const apply = await mountRaw("read");
    expect(apply).toBeDisabled();
    expect(apply).toHaveAttribute("title", expect.stringMatching(/operate scope/i));
  });

  it("keeps the self-update panel admin-only for an operate user", async () => {
    seedScope("operate");
    vi.spyOn(api, "systemInfo").mockResolvedValue({
      version: "0.7.0", channel: "apt", updateMechanism: "apt",
      updateAvailable: true, latestVersion: "0.8.0",
    });
    render(
      <MemoryRouter>
        <AuthProvider>
          <Config />
        </AuthProvider>
      </MemoryRouter>,
    );
    const mgmt = await screen.findByRole("button", { name: "Management" });
    fireEvent.click(mgmt);
    const banner = await screen.findByTestId("update-banner");
    const apply = within(banner).getByRole("button");

    expect(apply).toBeDisabled();
    expect(apply).toHaveAttribute("title", expect.stringMatching(/admin scope/i));
  });
});
