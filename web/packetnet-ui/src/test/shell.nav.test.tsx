// Tests for the left-nav "Apps" group (components/layout/shell AppNav): enabled, web-capable
// apps the node publishes (GET /api/v1/apps) become first-class nav entries below the core
// items, each a FULL navigation to its reverse-proxied /apps/{id}/ URL (a plain <a href>, not
// a SPA route), with a not-running warning when an enabled app's service is down. Mounts the
// real <Shell> against the mock API backend; the launcher feed is spied + stubbed so each test
// controls the app states it exercises without touching the shared mock fixtures.
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Shell } from "@/components/layout/shell";
import { api } from "@/lib/api";
import type { NodeApp } from "@/lib/types";

function seedScope(scope: "read" | "admin" = "admin") {
  localStorage.setItem(
    "pdn.session",
    JSON.stringify({ token: "test.jwt", refreshToken: null, username: "tom", scope }),
  );
}

// Mount the real Shell. Shell uses useNavigate + renders an <Outlet>, so wrap it in a route.
async function mountShell(apps: NodeApp[]) {
  seedScope();
  vi.spyOn(api, "apps").mockResolvedValue(apps);
  const result = render(
    <MemoryRouter initialEntries={["/"]}>
      <AuthProvider>
        <Routes>
          <Route path="/" element={<Shell />}>
            <Route index element={<div>home</div>} />
          </Route>
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
  // The core nav always renders; wait for it so the shell is mounted.
  await waitFor(() => expect(screen.getByRole("link", { name: /Dashboard/ })).toBeInTheDocument());
  return result;
}

function navEntry(id: string): HTMLElement {
  const el = document.querySelector(`[data-app-nav="${id}"]`);
  expect(el).not.toBeNull();
  return el as HTMLElement;
}

afterEach(() => {
  localStorage.clear();
  vi.restoreAllMocks();
});

describe("Shell — Apps nav group", () => {
  it("lists each enabled web app as a nav entry linking to its reverse-proxied /apps/{id}/ URL", async () => {
    await mountShell([
      { id: "wall", name: "WALL", icon: "message-square", url: "/apps/wall/", uiMode: "standalone", state: "Running" },
      { id: "lobby", name: "LOBBY", icon: "users", url: "/apps/lobby/", uiMode: "standalone", state: "Running" },
    ]);

    await waitFor(() => expect(document.querySelector('[data-app-nav="wall"]')).not.toBeNull());

    // A full navigation to an absolute same-origin server route — a plain <a href>, NOT a SPA
    // route, so it must be an anchor with the literal /apps/{id}/ href (target stays this tab).
    const wall = navEntry("wall");
    expect(wall.tagName).toBe("A");
    expect(wall).toHaveAttribute("href", "/apps/wall/");
    expect(wall).toHaveAttribute("target", "_self");
    expect(within(wall).getByText("WALL")).toBeInTheDocument();

    expect(navEntry("lobby")).toHaveAttribute("href", "/apps/lobby/");

    // The group has a heading.
    expect(screen.getByText("Apps", { selector: "p" })).toBeInTheDocument();
  });

  it("branches the nav entry by uiMode: standalone = full-nav <a>, embedded/slot = SPA <Link>", async () => {
    await mountShell([
      { id: "wall", name: "WALL", icon: "message-square", url: "/apps/wall/", uiMode: "standalone", state: "Running" },
      { id: "lobby", name: "LOBBY", icon: "users", url: "/apps/lobby/", uiMode: "slot", state: "Running" },
      { id: "quiz", name: "QUIZ", icon: null, url: "/apps/quiz/", uiMode: "embedded", state: "Running" },
    ]);

    await waitFor(() => expect(document.querySelector('[data-app-nav="wall"]')).not.toBeNull());

    // standalone → a plain <a> to the reverse-proxied page (full navigation, trailing slash).
    const wall = navEntry("wall");
    expect(wall.tagName).toBe("A");
    expect(wall).toHaveAttribute("href", "/apps/wall/");
    expect(wall).toHaveAttribute("target", "_self");

    // slot + embedded → a react-router <Link> to the in-panel SPA route /apps/{id} (no trailing
    // slash, no target=_self). A rendered <Link> is still an <a>, but with the SPA href.
    const lobby = navEntry("lobby");
    expect(lobby).toHaveAttribute("href", "/apps/lobby");
    expect(lobby).not.toHaveAttribute("target", "_self");

    const quiz = navEntry("quiz");
    expect(quiz).toHaveAttribute("href", "/apps/quiz");
    expect(quiz).not.toHaveAttribute("target", "_self");
  });

  it("warns on a nav entry when an enabled app is not running (Stopped/Backoff/Faulted)", async () => {
    await mountShell([
      { id: "wall", name: "WALL", icon: "message-square", url: "/apps/wall/", uiMode: "standalone", state: "Running" },
      { id: "quiz", name: "QUIZ", icon: null, url: "/apps/quiz/", uiMode: "embedded", state: "Faulted" },
      { id: "lobby", name: "LOBBY", icon: "users", url: "/apps/lobby/", uiMode: "slot", state: "Backoff" },
      { id: "notes", name: "Notes", icon: "sticky-note", url: "/apps/notes/", uiMode: "standalone", state: null },
    ]);

    await waitFor(() => expect(document.querySelector('[data-app-nav="quiz"]')).not.toBeNull());

    // Faulted + Backoff → warning; Running + null (no service) → no warning. The warning is
    // mode-independent — an embedded/slot entry warns exactly like a standalone one.
    expect(navEntry("quiz").querySelector('[data-warning="not-running"]')).not.toBeNull();
    expect(navEntry("lobby").querySelector('[data-warning="not-running"]')).not.toBeNull();
    expect(navEntry("wall").querySelector('[data-warning="not-running"]')).toBeNull();
    expect(navEntry("notes").querySelector('[data-warning="not-running"]')).toBeNull();
  });

  it("renders no Apps group when the node publishes no web apps", async () => {
    await mountShell([]);
    // The core nav is present; the dynamic group is not.
    expect(screen.getByRole("link", { name: /Dashboard/ })).toBeInTheDocument();
    expect(document.querySelector('[data-testid="app-nav"]')).toBeNull();
  });
});
