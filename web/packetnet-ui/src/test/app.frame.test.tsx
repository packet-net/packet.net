// Tests for the in-panel app frame (screens/app-frame AppFrame), the panel side of the app
// `ui.mode` contract. The route /apps/:id renders an embedded/slot app inside the panel shell
// in a borderless, content-area-filling iframe whose src is the app's reverse-proxied page:
//   - embedded → src = url            (the app renders its own full page in the frame)
//   - slot     → src = url + ?pdn_embed=1  (the app renders chrome-less, single PDN chrome)
// A standalone app (not meant to embed) and an unknown id fall back to a link/empty state
// rather than a frame. The launcher feed (api.apps) is spied so each test controls its app.
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { AppFrame } from "@/screens/app-frame";
import { api } from "@/lib/api";
import type { NodeApp } from "@/lib/types";

// Mount the AppFrame at /apps/:id with the given launcher feed. The frame reads :id from the
// route, so we enter at /apps/<id> and register the parameterised route.
async function mountFrame(apps: NodeApp[], id: string) {
  vi.spyOn(api, "apps").mockResolvedValue(apps);
  return render(
    <MemoryRouter initialEntries={[`/apps/${id}`]}>
      <AuthProvider>
        <Routes>
          <Route path="/apps/:id" element={<AppFrame />} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

function frame(id: string): HTMLIFrameElement | null {
  return document.querySelector(`iframe[data-app-frame="${id}"]`) as HTMLIFrameElement | null;
}

beforeEach(() => {
  // Each test controls the panel theme via the <html>.dark class; start from a clean (light) slate.
  document.documentElement.classList.remove("dark");
});

afterEach(() => {
  vi.restoreAllMocks();
  document.documentElement.classList.remove("dark");
});

describe("AppFrame — in-panel app frame", () => {
  it("a slot app renders a borderless iframe whose src carries ?pdn_embed=1 (+ &theme=)", async () => {
    await mountFrame(
      [{ id: "lobby", name: "LOBBY", icon: "users", url: "/apps/lobby/", uiMode: "slot", state: "Running" }],
      "lobby",
    );

    await waitFor(() => expect(frame("lobby")).not.toBeNull());
    const f = frame("lobby")!;
    // slot → the app's page WITH the chrome-less signal appended, plus the panel's active theme
    // (light here — no .dark class) so the framed app matches the panel rather than the OS.
    expect(f).toHaveAttribute("src", "/apps/lobby/?pdn_embed=1&theme=light");
    expect(f).toHaveAttribute("data-ui-mode", "slot");
    // Borderless (the "single PDN chrome" look) — no border, fills the width.
    expect(f.className).toContain("border-0");
    // The header shows the app's name.
    expect(screen.getByText("LOBBY")).toBeInTheDocument();
  });

  it("a slot iframe src carries &theme=dark when <html> has .dark, &theme=light otherwise", async () => {
    // Panel in DARK mode (the manual toggle flips <html>.dark) → the slot app must follow.
    document.documentElement.classList.add("dark");
    await mountFrame(
      [{ id: "bbs", name: "BBS", icon: "mail", url: "/apps/bbs/", uiMode: "slot", state: "Running" }],
      "bbs",
    );

    await waitFor(() => expect(frame("bbs")).not.toBeNull());
    expect(frame("bbs")!).toHaveAttribute("src", "/apps/bbs/?pdn_embed=1&theme=dark");

    // Removing the class (light panel) — the observer re-derives and the src reloads with theme=light.
    document.documentElement.classList.remove("dark");
    await waitFor(() =>
      expect(frame("bbs")!).toHaveAttribute("src", "/apps/bbs/?pdn_embed=1&theme=light"),
    );
  });

  it("an embedded app renders the iframe WITHOUT the ?pdn_embed=1 param", async () => {
    await mountFrame(
      [{ id: "quiz", name: "QUIZ", icon: null, url: "/apps/quiz/", uiMode: "embedded", state: "Running" }],
      "quiz",
    );

    await waitFor(() => expect(frame("quiz")).not.toBeNull());
    const f = frame("quiz")!;
    // embedded → the app's own full page, no signal param.
    expect(f).toHaveAttribute("src", "/apps/quiz/");
    expect(f.getAttribute("src")).not.toContain("pdn_embed");
    expect(f).toHaveAttribute("data-ui-mode", "embedded");
  });

  it("a standalone app is not framed — it falls back to a link to its own page", async () => {
    await mountFrame(
      [{ id: "wall", name: "WALL", icon: "message-square", url: "/apps/wall/", uiMode: "standalone", state: "Running" }],
      "wall",
    );

    await waitFor(() => expect(screen.getByText(/opens in its own page/i)).toBeInTheDocument());
    // No iframe for a standalone app — the open affordance is a plain anchor to the page.
    expect(frame("wall")).toBeNull();
    const open = screen.getByRole("link", { name: /Open WALL/i });
    expect(open).toHaveAttribute("href", "/apps/wall/");
  });

  it("an unknown id renders a graceful not-found state, not a frame", async () => {
    await mountFrame(
      [{ id: "wall", name: "WALL", icon: "message-square", url: "/apps/wall/", uiMode: "slot", state: "Running" }],
      "ghost",
    );

    await waitFor(() => expect(screen.getByText(/No app/i)).toBeInTheDocument());
    expect(frame("ghost")).toBeNull();
  });
});
