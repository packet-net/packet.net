// ============================================================
// pdn — in-panel app frame (the panel side of the app `ui.mode` contract).
//
// The route /apps/:id renders this when an app declares ui.mode = embedded | slot (the nav
// routes such apps here with a SPA <Link>; a standalone app is a full navigation and never
// reaches this screen). It looks the app up in the launcher feed (GET /api/v1/apps), renders
// the panel shell (left nav comes from <Shell>; here we add a <PageHeader> with the app's name
// + an "open in new tab" affordance) and a BORDERLESS, content-area-filling <iframe> whose
// src is the app's reverse-proxied page at /apps/{id}/:
//   - embedded → src = url (the app renders its own full page inside the frame).
//   - slot     → src = url + "?pdn_embed=1" (the app renders chrome-less so it blends into the
//                single PDN chrome).
// Why an iframe (not inline DOM injection): the apps are server-rendered with forms; an iframe
// is a real browser context where their links/forms/navigation work natively. Inline injection
// would mean intercepting all in-app navigation — out of scope (see docs/app-packages.md).
//
// Graceful fallbacks: an unknown id (not in the feed → not an enabled web app), or an app the
// feed reports as standalone (not meant to embed), renders an EmptyState with a plain link to
// the app's own page rather than forcing it into a frame.
// ============================================================
import { useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import { Page, PageHeader } from "@/components/layout/shell";
import { EmptyState } from "@/components/ui";
import { Icon } from "@/components/icon";
import { api, useQuery } from "@/lib/api";

// The panel's active theme — the manual toggle (<ThemeToggle> in shell.tsx) flips a `.dark`
// class on <html>. An iframe is a separate document that can't see that class, so it falls back
// to prefers-color-scheme (the OS), which won't match a manual panel toggle. We read the class
// here and thread it to the slot app as &theme= so the app can override its own prefers-* default.
function readPanelTheme(): "dark" | "light" {
  return document.documentElement.classList.contains("dark") ? "dark" : "light";
}

export function AppFrame() {
  const { id } = useParams<{ id: string }>();
  const { data, loading } = useQuery(api.apps, []);
  const apps = data ?? [];
  const app = apps.find((a) => a.id === id);

  // Track the panel's active theme and keep it LIVE: a MutationObserver on <html>'s class
  // attribute re-derives the theme when the user toggles it while a slot app is open, so the
  // iframe src updates (&theme= flips, the frame reloads with the matching theme).
  const [theme, setTheme] = useState<"dark" | "light">(readPanelTheme);
  useEffect(() => {
    const root = document.documentElement;
    const observer = new MutationObserver(() => setTheme(readPanelTheme()));
    observer.observe(root, { attributes: true, attributeFilter: ["class"] });
    // Re-sync in case the class changed between the initial render and the observer attaching.
    setTheme(readPanelTheme());
    return () => observer.disconnect();
  }, []);

  // Still fetching the feed — say so rather than flashing the not-found state.
  if (loading && !data) {
    return (
      <Page>
        <div className="py-12 text-center text-sm text-muted-foreground">Loading app…</div>
      </Page>
    );
  }

  // Unknown id: no such enabled web app in the feed. Offer the direct path in case the app is
  // reachable but not (yet) in the launcher (a stale nav, a just-disabled app).
  if (!app) {
    return (
      <Page>
        <PageHeader title="App not found" />
        <EmptyState
          icon="apps"
          title={`No app “${id}” is available`}
          body="It may be disabled, not installed, or not a web app. Open the Apps page to manage it."
        />
      </Page>
    );
  }

  // A standalone app shouldn't be framed — the nav routes it as a full navigation, so reaching
  // this screen means a stale/typed URL. Don't trap it in a frame; offer its own page instead.
  if (app.uiMode === "standalone") {
    return (
      <Page>
        <PageHeader title={app.name} />
        <EmptyState
          icon="external"
          title={`${app.name} opens in its own page`}
          body={
            <>
              This app isn’t meant to run inside the panel.{" "}
              <a href={app.url} target="_self" className="text-primary underline">Open {app.name}</a>.
            </>
          }
        />
      </Page>
    );
  }

  // embedded → the app's own page; slot → chrome-less (?pdn_embed=1) so it blends into the
  // single PDN chrome, PLUS &theme= so the app matches the panel's active (manually-toggled)
  // theme instead of falling back to the OS prefers-color-scheme. The iframe is borderless and
  // fills the content area below the header.
  const src = app.uiMode === "slot" ? `${app.url}?pdn_embed=1&theme=${theme}` : app.url;

  return (
    <Page>
      <PageHeader
        title={app.name}
        actions={
          // "Open in new tab" → the app's own full page (not the chrome-less slot variant).
          // An anchor styled as an outline button (Button always renders a <button>, so a
          // real navigation target has to be a hand-styled <a>).
          <a
            href={app.url}
            target="_blank"
            rel="noopener noreferrer"
            data-app-open={app.id}
            title={`Open ${app.name} in a new tab`}
            className="inline-flex h-8 items-center justify-center gap-1.5 whitespace-nowrap rounded-md border border-input bg-transparent px-3 text-xs font-medium transition-colors hover:bg-accent hover:text-accent-foreground"
          >
            <Icon name="external" size={14} /> Open in new tab
          </a>
        }
      />
      {/* Borderless, content-area-filling frame. The height tracks the viewport minus the
          topbar (h-14 = 3.5rem) and the Page gutter + PageHeader, so the app gets the whole
          content area. A real browser context — the app's links/forms/navigation work natively. */}
      <iframe
        src={src}
        title={app.name}
        data-app-frame={app.id}
        data-ui-mode={app.uiMode}
        className="block h-[calc(100vh-9rem)] w-full rounded-lg border-0 bg-background"
      />
    </Page>
  );
}
