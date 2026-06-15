// Management-section tests for the Apps screen (the /apps/packages UI): list
// rendering across the interesting fixture states, the capability confirm that
// gates the enable POST, restart visibility, admin gating, the launcher refetch
// on enable, and the in-flight busy state. Mounts against the mock API backend
// like screens.smoke.test.tsx; mutating calls are spied + stubbed (vi.spyOn(api,
// …).mockResolvedValue) so the shared mock fixtures stay pristine across tests.
//
// The old <Switch> toggle is now a two-segment Enable/Disable control (a button
// group, role="group"): the segment matching the current state is "selected" and
// inert; the other is the action. We click segments by accessible name, scoped to
// the row's group — and scope confirm-modal buttons to the open panel, since the
// modal's footer "Enable" shares its name with the row's Enable segment.
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Apps } from "@/screens/apps";
import { api } from "@/lib/api";
import { APP_PACKAGES } from "@/lib/mock";
import type { AppPackage } from "@/lib/types";

// Seed the persisted session AuthProvider rehydrates from (localStorage
// "pdn.session"), so the screen sees the given scope: admin unlocks the mutating
// controls; read renders them read-only (the users.tsx gating idiom).
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
  // wait for the package list to land (each row carries data-pkg="<id>")
  await waitFor(() => expect(document.querySelector('[data-pkg="lobby"]')).not.toBeNull());
  return result;
}

function row(id: string): HTMLElement {
  const el = document.querySelector(`[data-pkg="${id}"]`);
  expect(el).not.toBeNull();
  return el as HTMLElement;
}

function fixture(id: string): AppPackage {
  const p = APP_PACKAGES.find((x) => x.id === id);
  if (!p) throw new Error(`no fixture '${id}'`);
  return structuredClone(p);
}

// The row's enable/disable segments, by accessible name within the row's group.
function enableSeg(id: string): HTMLElement {
  return within(row(id)).getByRole("button", { name: /Enable/ });
}
function disableSeg(id: string): HTMLElement {
  return within(row(id)).getByRole("button", { name: /Disable/ });
}

// Click a confirm-modal footer button by name. While the modal is open the row's
// Enable segment shares its accessible name, so scope to the open modal panel (the
// wrapper of the title text) to disambiguate.
function clickModalButton(titleRe: RegExp, name: string) {
  const title = screen.getByText(titleRe);
  const modal = title.closest("div.relative") as HTMLElement;
  fireEvent.click(within(modal).getByRole("button", { name }));
}

afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Apps — package management section", () => {
  it("lists every package with its state pill and source badge", async () => {
    await mountApps();
    // status pills
    expect(within(row("wall")).getByText("running")).toBeInTheDocument();
    expect(within(row("lobby")).getByText("stopped")).toBeInTheDocument();
    expect(within(row("bbs-bridge")).getByText("external")).toBeInTheDocument();
    // source badges
    expect(within(row("wall")).getByText("package")).toBeInTheDocument();
    expect(within(row("motd")).getByText("inline")).toBeInTheDocument();
    // a running managed service shows its pid
    expect(within(row("wall")).getByText(/pid 4711/)).toBeInTheDocument();
    // service "none" rows get a neutral dash, not a pill
    expect(within(row("notes")).getByText("—")).toBeInTheDocument();
  });

  it("highlights the current state — the active segment is selected, the other is the action", async () => {
    await mountApps();
    // wall is enabled → Enable is the selected (inert) segment, Disable is the action
    expect(enableSeg("wall")).toHaveAttribute("aria-pressed", "true");
    expect(enableSeg("wall")).toBeDisabled();
    expect(disableSeg("wall")).toHaveAttribute("aria-pressed", "false");
    expect(disableSeg("wall")).toBeEnabled();
    // lobby is disabled → Disable is selected, Enable is the action
    expect(disableSeg("lobby")).toHaveAttribute("aria-pressed", "true");
    expect(disableSeg("lobby")).toBeDisabled();
    expect(enableSeg("lobby")).toBeEnabled();
  });

  it("shows a Faulted service as an error with its crash detail", async () => {
    await mountApps();
    const r = row("quiz");
    expect(within(r).getByText("faulted")).toBeInTheDocument();
    const detail = within(r).getByText(/exited 5 times in 30s/);
    expect(detail.closest("div")).toHaveClass("text-danger");
  });

  it("warns on an ENABLED app's row when its service isn't running, but not on a disabled one", async () => {
    await mountApps();
    // quiz is enabled + Faulted → the not-running warning shows on its row…
    expect(within(row("quiz")).getByText(/not running/i)).toBeInTheDocument();
    // wall is enabled + Running → no warning.
    expect(within(row("wall")).queryByText(/not running/i)).toBeNull();
    // lobby is DISABLED + Stopped → expected to be stopped, so no warning despite a stopped state.
    expect(within(row("lobby")).queryByText(/not running/i)).toBeNull();
    // notes has no service at all (state null) → never warns.
    expect(within(row("notes")).queryByText(/not running/i)).toBeNull();
  });

  it("display-normalises the `network` capability to `packet` in the enable confirm", async () => {
    await mountApps();
    // mail's manifest declares the legacy `network` capability — the confirm shows `packet`.
    fireEvent.click(enableSeg("mail"));
    const title = screen.getByText(/Enable Mail\?/);
    expect(title).toBeInTheDocument();
    // Scope to the open modal: mail's row below now shows the same `packet` chip.
    const modal = title.closest("div.relative") as HTMLElement;
    expect(within(modal).getByText("packet")).toBeInTheDocument();
    expect(within(modal).queryByText("network")).toBeNull();
  });

  it("renders each package's declared capabilities as chips on its row", async () => {
    await mountApps();
    // wall declares ["session", "web"] — both surface on the row.
    const caps = within(row("wall")).getByText("Capabilities:").parentElement as HTMLElement;
    expect(within(caps).getByText("session")).toBeInTheDocument();
    expect(within(caps).getByText("web")).toBeInTheDocument();
  });

  it("display-normalises the `network` capability to `packet` on the row", async () => {
    await mountApps();
    // mail's manifest declares the legacy `network` capability — the row chip reads `packet`.
    const caps = within(row("mail")).getByText("Capabilities:").parentElement as HTMLElement;
    expect(within(caps).getByText("packet")).toBeInTheDocument();
    expect(within(caps).queryByText("network")).toBeNull();
  });

  it("omits the capabilities line for a package that declares none", async () => {
    await mountApps();
    // notes declares [] — no Capabilities: line on its row.
    expect(within(row("notes")).queryByText("Capabilities:")).toBeNull();
    // a broken package (wx, []) likewise shows no capabilities line.
    expect(within(row("wx")).queryByText("Capabilities:")).toBeNull();
  });

  it("shows a broken package's manifest error and keeps both segments disabled", async () => {
    await mountApps();
    const r = row("wx");
    const error = within(r).getByText(/missing required field 'command'/);
    expect(error.closest("div")).toHaveClass("text-danger");
    expect(enableSeg("wx")).toBeDisabled();
    expect(disableSeg("wx")).toBeDisabled();
  });

  it("renders inline entries read-only — the control explains they are managed in config", async () => {
    await mountApps();
    const en = enableSeg("motd");
    expect(en).toBeDisabled();
    expect(en).toHaveAttribute("title", expect.stringMatching(/config/i));
    expect(disableSeg("motd")).toBeDisabled();
  });

  it("enable opens the capability confirm and only POSTs once it is accepted", async () => {
    const enable = vi
      .spyOn(api, "appPackageEnable")
      .mockResolvedValue({ ...fixture("lobby"), enabled: true, state: "Running", pid: 20001 });
    await mountApps();

    fireEvent.click(enableSeg("lobby"));
    // the confirm lists the manifest's declared capabilities before anything fires. Scope to
    // the open modal: rows below now show the same capability text on their chips.
    const title = screen.getByText(/Enable LOBBY\?/);
    expect(title).toBeInTheDocument();
    const modal = title.closest("div.relative") as HTMLElement;
    expect(within(modal).getByText("session")).toBeInTheDocument();
    expect(enable).not.toHaveBeenCalled();

    clickModalButton(/Enable LOBBY\?/, "Enable");
    await waitFor(() => expect(enable).toHaveBeenCalledWith("lobby"));
    expect(enable).toHaveBeenCalledTimes(1);
  });

  it("does not POST when the capability confirm is cancelled", async () => {
    const enable = vi.spyOn(api, "appPackageEnable");
    await mountApps();

    fireEvent.click(enableSeg("lobby"));
    expect(screen.getByText(/Enable LOBBY\?/)).toBeInTheDocument();
    clickModalButton(/Enable LOBBY\?/, "Cancel");

    expect(enable).not.toHaveBeenCalled();
    expect(screen.queryByText(/Enable LOBBY\?/)).not.toBeInTheDocument();
  });

  it("still confirms — saying so — when the manifest declares no capabilities", async () => {
    await mountApps();
    fireEvent.click(enableSeg("notes"));
    expect(screen.getByText(/Enable Notes\?/)).toBeInTheDocument();
    expect(screen.getByText(/No declared capabilities/)).toBeInTheDocument();
  });

  it("lists declared tailnet forwards in the enable confirm — the exposure is a capability", async () => {
    await mountApps();
    fireEvent.click(enableSeg("mail"));

    expect(screen.getByText(/Enable Mail\?/)).toBeInTheDocument();
    expect(screen.getByText(/Exposes on your tailnet:/)).toBeInTheDocument();
    // The well-known port name + the loopback target the sidecar proxies to.
    expect(screen.getByText(/IMAPS :993 → 127\.0\.0\.1:1430/)).toBeInTheDocument();
    expect(screen.getByText(/SMTPS :465 → 127\.0\.0\.1:1465/)).toBeInTheDocument();
  });

  it("shows no forwards line for an app that declares none", async () => {
    await mountApps();
    fireEvent.click(enableSeg("lobby"));
    expect(screen.getByText(/Enable LOBBY\?/)).toBeInTheDocument();
    expect(screen.queryByText(/Exposes on your tailnet:/)).not.toBeInTheDocument();
  });

  it("disable POSTs immediately — no confirm step", async () => {
    const disable = vi
      .spyOn(api, "appPackageDisable")
      .mockResolvedValue({ ...fixture("wall"), enabled: false, state: "Stopped", pid: null });
    await mountApps();

    fireEvent.click(disableSeg("wall"));
    await waitFor(() => expect(disable).toHaveBeenCalledWith("wall"));
    expect(screen.queryByText(/Enable WALL\?/)).not.toBeInTheDocument();
  });

  it("refetches the package inventory after enabling — the row's state updates without a refresh", async () => {
    vi
      .spyOn(api, "appPackageEnable")
      .mockResolvedValue({ ...fixture("lobby"), enabled: true, state: "Running", pid: 20001 });
    // The launcher feed (api.apps) now lives in <Shell>'s nav, not on the Apps page — enabling
    // here refetches the package inventory (api.appPackages); the nav re-fetches on its own.
    const packages = vi.spyOn(api, "appPackages");
    await mountApps();
    // the inventory fetched once on mount
    await waitFor(() => expect(packages).toHaveBeenCalledTimes(1));

    fireEvent.click(enableSeg("lobby"));
    clickModalButton(/Enable LOBBY\?/, "Enable");
    // the enable's reloadAll() refetches the inventory (a second api.appPackages call)
    await waitFor(() => expect(packages.mock.calls.length).toBeGreaterThanOrEqual(2));
  });

  it("shows an in-flight busy label on the segment being applied while the mutation runs", async () => {
    // A deferred enable so we can observe the in-progress state before it resolves.
    let resolve!: (p: AppPackage) => void;
    vi.spyOn(api, "appPackageEnable").mockReturnValue(new Promise((r) => { resolve = r; }));
    await mountApps();

    fireEvent.click(enableSeg("lobby"));
    clickModalButton(/Enable LOBBY\?/, "Enable");
    // while in flight the row's Enable segment shows the spinner + "Enabling…"
    await waitFor(() => expect(within(row("lobby")).getByText("Enabling…")).toBeInTheDocument());

    resolve({ ...fixture("lobby"), enabled: true, state: "Running", pid: 20001 });
    await waitFor(() => expect(within(row("lobby")).queryByText("Enabling…")).not.toBeInTheDocument());
  });

  it("offers Restart only on enabled managed services", async () => {
    await mountApps();
    expect(within(row("wall")).getByRole("button", { name: "Restart" })).toBeEnabled();
    // a Faulted managed service is restartable — that is how you recover it
    expect(within(row("quiz")).getByRole("button", { name: "Restart" })).toBeEnabled();
    // disabled / external / inline / service-less / broken rows get no restart at all
    expect(within(row("lobby")).queryByRole("button", { name: "Restart" })).toBeNull();
    expect(within(row("bbs-bridge")).queryByRole("button", { name: "Restart" })).toBeNull();
    expect(within(row("motd")).queryByRole("button", { name: "Restart" })).toBeNull();
    expect(within(row("notes")).queryByRole("button", { name: "Restart" })).toBeNull();
    expect(within(row("wx")).queryByRole("button", { name: "Restart" })).toBeNull();
  });

  it("Restart calls the restart endpoint for the row's package", async () => {
    const restart = vi
      .spyOn(api, "appPackageRestart")
      .mockResolvedValue({ ...fixture("quiz"), state: "Running", pid: 20002, detail: null });
    await mountApps();

    fireEvent.click(within(row("quiz")).getByRole("button", { name: "Restart" }));
    await waitFor(() => expect(restart).toHaveBeenCalledWith("quiz"));
  });

  it("read scope sees state but every mutating control is disabled", async () => {
    await mountApps("read");
    // the list itself still renders (read-gated endpoint)
    expect(within(row("wall")).getByText("running")).toBeInTheDocument();
    // every enable/disable segment is read-only, titled with the admin requirement
    for (const r of ["wall", "lobby", "quiz", "notes"]) {
      expect(enableSeg(r)).toBeDisabled();
      expect(enableSeg(r)).toHaveAttribute("title", "Requires admin");
      expect(disableSeg(r)).toBeDisabled();
    }
    const restart = within(row("wall")).getByRole("button", { name: "Restart" });
    expect(restart).toBeDisabled();
    expect(restart).toHaveAttribute("title", "Requires admin");
  });
});
