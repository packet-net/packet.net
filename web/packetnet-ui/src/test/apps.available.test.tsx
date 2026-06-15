// Tests for the Apps screen's "Available apps" section (the /apps/available UI) +
// the uninstall control on the package-manager rows. Mounts against the mock API
// backend like apps.packages.test.tsx; mutating calls are spied + stubbed
// (vi.spyOn(api, …)) so the shared mock fixtures stay pristine across tests.
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Apps } from "@/screens/apps";
import { api } from "@/lib/api";

// Seed the persisted session AuthProvider rehydrates from (localStorage
// "pdn.session") — admin unlocks the mutating controls; read renders them disabled.
function seedScope(scope: "read" | "admin") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

async function mountApps(scope: "read" | "admin" = "admin") {
  seedScope(scope);
  const result = render(
    <MemoryRouter>
      <AuthProvider>
        <Apps />
      </AuthProvider>
    </MemoryRouter>,
  );
  // wait for the available list to land (each row carries data-available="<id>")
  await waitFor(() => expect(document.querySelector('[data-available="dapps"]')).not.toBeNull());
  return result;
}

function avail(id: string): HTMLElement {
  const el = document.querySelector(`[data-available="${id}"]`);
  expect(el).not.toBeNull();
  return el as HTMLElement;
}

function pkg(id: string): HTMLElement {
  const el = document.querySelector(`[data-pkg="${id}"]`);
  expect(el).not.toBeNull();
  return el as HTMLElement;
}

// Click a confirm-modal footer button by name. The row's action button shares the same
// accessible name while the modal is open, so scope to the open modal panel (the wrapper
// of the title text) to disambiguate.
function clickModalButton(titleRe: RegExp, name: string) {
  const title = screen.getByText(titleRe);
  const modal = title.closest("div.relative") as HTMLElement;
  fireEvent.click(within(modal).getByRole("button", { name }));
}

afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Apps — available apps section", () => {
  it("lists not-installed + updatable apps, hiding installed-and-current ones", async () => {
    await mountApps();
    // dapps: not installed → Install
    expect(within(avail("dapps")).getByRole("button", { name: "Install" })).toBeInTheDocument();
    // bpqchat: installed but updateAvailable → Update + the version delta badge
    expect(within(avail("bpqchat")).getByRole("button", { name: "Update" })).toBeInTheDocument();
    expect(within(avail("bpqchat")).getByText(/v0\.0\.9 → v0\.1\.0/)).toBeInTheDocument();
    // convers: not installable for this node → the Install button is disabled, hinted
    const conversBtn = within(avail("convers")).getByRole("button", { name: "Install" });
    expect(conversBtn).toBeDisabled();
    expect(conversBtn).toHaveAttribute("title", "No build for this node's architecture");
    expect(within(avail("convers")).getByText(/no build for this node/i)).toBeInTheDocument();
  });

  it("Install opens the capability confirm and only POSTs once accepted", async () => {
    const install = vi.spyOn(api, "appInstall").mockResolvedValue({ ok: true, id: "dapps", version: "0.34.1" });
    await mountApps();

    fireEvent.click(within(avail("dapps")).getByRole("button", { name: "Install" }));
    // the confirm lists the declared capabilities before anything fires — the legacy `network`
    // declaration is display-normalised to `packet`.
    expect(screen.getByText(/Install DAPPS\?/)).toBeInTheDocument();
    expect(screen.getByText("packet")).toBeInTheDocument();
    expect(screen.queryByText("network")).toBeNull();
    expect(install).not.toHaveBeenCalled();

    clickModalButton(/Install DAPPS\?/, "Install");
    await waitFor(() => expect(install).toHaveBeenCalledWith("dapps"));
    expect(install).toHaveBeenCalledTimes(1);
  });

  it("does not POST when the capability confirm is cancelled", async () => {
    const install = vi.spyOn(api, "appInstall");
    await mountApps();

    fireEvent.click(within(avail("dapps")).getByRole("button", { name: "Install" }));
    expect(screen.getByText(/Install DAPPS\?/)).toBeInTheDocument();
    clickModalButton(/Install DAPPS\?/, "Cancel");

    expect(install).not.toHaveBeenCalled();
    expect(screen.queryByText(/Install DAPPS\?/)).not.toBeInTheDocument();
  });

  it("an update goes through a confirm titled Update and POSTs the same install call", async () => {
    const install = vi.spyOn(api, "appInstall").mockResolvedValue({ ok: true, id: "bpqchat", version: "0.1.0" });
    await mountApps();

    fireEvent.click(within(avail("bpqchat")).getByRole("button", { name: "Update" }));
    expect(screen.getByText(/Update BPQ Chat\?/)).toBeInTheDocument();
    clickModalButton(/Update BPQ Chat\?/, "Update");
    await waitFor(() => expect(install).toHaveBeenCalledWith("bpqchat"));
  });

  it("surfaces a server install error as a banner", async () => {
    vi.spyOn(api, "appInstall").mockRejectedValue(new Error("sha256 mismatch — refusing to install"));
    await mountApps();

    fireEvent.click(within(avail("dapps")).getByRole("button", { name: "Install" }));
    clickModalButton(/Install DAPPS\?/, "Install");
    await waitFor(() => expect(screen.getByText(/sha256 mismatch/)).toBeInTheDocument());
  });

  it("read scope sees the available list but every action is disabled", async () => {
    await mountApps("read");
    expect(within(avail("dapps")).getByRole("button", { name: "Install" })).toBeDisabled();
    expect(within(avail("bpqchat")).getByRole("button", { name: "Update" })).toBeDisabled();
    // the upload affordance is admin-gated too
    expect(screen.getByRole("button", { name: /Upload a \.pdnapp/ })).toBeDisabled();
  });
});

describe("Apps — uninstall on package rows", () => {
  it("offers Uninstall on disabled discovered packages, blocked while enabled", async () => {
    await mountApps();
    // lobby: disabled package → uninstall enabled
    const lobbyBtn = within(pkg("lobby")).getByRole("button", { name: "Uninstall" });
    expect(lobbyBtn).toBeEnabled();
    // wall: enabled package → uninstall present but disabled (must disable first)
    const wallBtn = within(pkg("wall")).getByRole("button", { name: "Uninstall" });
    expect(wallBtn).toBeDisabled();
    expect(wallBtn).toHaveAttribute("title", expect.stringMatching(/disable.*before/i));
    // inline app → no uninstall at all (config-authored)
    expect(within(pkg("motd")).queryByRole("button", { name: "Uninstall" })).toBeNull();
  });

  it("Uninstall confirms, then POSTs the uninstall endpoint for the row's package", async () => {
    const uninstall = vi.spyOn(api, "appUninstall").mockResolvedValue({ ok: true, id: "lobby" });
    await mountApps();

    fireEvent.click(within(pkg("lobby")).getByRole("button", { name: "Uninstall" }));
    expect(screen.getByText(/Uninstall LOBBY\?/)).toBeInTheDocument();
    expect(uninstall).not.toHaveBeenCalled();

    // Confirm in the modal — its footer button shares the row button's name, so scope to
    // the open modal panel to disambiguate.
    clickModalButton(/Uninstall LOBBY\?/, "Uninstall");
    await waitFor(() => expect(uninstall).toHaveBeenCalledWith("lobby"));
  });
});
