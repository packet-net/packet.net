// App shell — sidebar (7-nav) + translucent topbar + theme toggle + user menu.
// Ported from the handoff ui.jsx Shell; wired to react-router (NavLink/Outlet)
// and the live NodeStatus for the topbar.
import { useEffect, useRef, useState, type ReactNode } from "react";
import { NavLink, Outlet, useNavigate, Link } from "react-router-dom";
import { cn } from "@/lib/utils";
import { Icon, AppIcon, type IconName } from "@/components/icon";
import { Button } from "@/components/ui";
import { useAuth } from "@/app/auth";
import { api, useQuery } from "@/lib/api";
import { fmtUptime } from "@/lib/mock";
import { isAppNotRunning } from "@/lib/types";

export const NAV: { id: string; to: string; label: string; icon: IconName }[] = [
  { id: "dashboard", to: "/", label: "Dashboard", icon: "dashboard" },
  { id: "monitor", to: "/monitor", label: "Monitor", icon: "monitor" },
  { id: "sessions", to: "/sessions", label: "Sessions", icon: "sessions" },
  { id: "apps", to: "/apps", label: "Apps", icon: "apps" },
  { id: "routes", to: "/routes", label: "Routes", icon: "routes" },
  { id: "ports", to: "/ports", label: "Ports", icon: "ports" },
  { id: "config", to: "/config", label: "Config", icon: "config" },
  { id: "users", to: "/users", label: "Users", icon: "users" },
];

export function Logo({ size = 28 }: { size?: number }) {
  return (
    <div className="flex items-center gap-2">
      <div className="grid place-items-center rounded-md bg-primary text-primary-foreground" style={{ width: size, height: size }}>
        <Icon name="radio" size={size * 0.62} />
      </div>
      <span className="font-mono text-[15px] font-semibold tracking-tight">pdn</span>
    </div>
  );
}

export function ThemeToggle() {
  const [dark, setDark] = useState(() => document.documentElement.classList.contains("dark"));
  return (
    <Button variant="ghost" size="iconSm" title="Toggle theme" onClick={() => {
      const next = !dark;
      document.documentElement.classList.toggle("dark", next);
      setDark(next);
    }}>
      <Icon name={dark ? "sun" : "moon"} size={16} />
    </Button>
  );
}

function UserMenu() {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const { username, scope, logout } = useAuth();
  const navigate = useNavigate();
  useEffect(() => {
    const h = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", h);
    return () => document.removeEventListener("mousedown", h);
  }, []);
  // username is null when we entered tokenless (auth off / mock) — label it so.
  const name = username || "node";
  const initials = name.slice(0, 2);
  return (
    <div className="relative" ref={ref}>
      <button onClick={() => setOpen((o) => !o)} className="grid h-8 w-8 place-items-center rounded-full bg-primary/15 text-xs font-semibold uppercase text-primary">{initials}</button>
      {open && (
        <div className="absolute right-0 top-10 z-50 w-48 rounded-lg border border-border bg-popover p-1 shadow-lg">
          <div className="px-3 py-2">
            <p className="text-sm font-medium">{name}</p>
            <p className="text-xs text-muted-foreground">{scope ?? "unauthenticated"}</p>
          </div>
          <div className="my-1 h-px bg-border" />
          <button onClick={() => { logout(); navigate("/login"); }} className="flex w-full items-center gap-2 rounded-md px-3 py-1.5 text-sm text-muted-foreground hover:bg-accent hover:text-foreground">
            <Icon name="power" size={14} /> Sign out
          </button>
        </div>
      )}
    </div>
  );
}

// The dynamic "Apps" nav group: each enabled, web-capable app the node publishes
// (GET /api/v1/apps) becomes a first-class nav entry below the core items, rendered with
// its manifest icon + name. How clicking one opens the app depends on the app's `uiMode`:
//   - standalone → a FULL navigation to the app's own reverse-proxied UI at /apps/{id}/ — an
//     absolute same-origin server route OUTSIDE the SPA router — so a plain <a href>
//     (target="_self"), NOT a react-router <Link>. The historical behaviour.
//   - embedded / slot → an in-panel SPA route /apps/{id} (handled by <AppFrame>) that renders
//     the panel shell around a borderless iframe of the app — so a react-router <Link>.
// An enabled app whose service isn't running (Stopped/Backoff/Faulted) carries a warning glyph
// (the feed lists only enabled apps, so isAppNotRunning(state) IS the not-running condition).
// Empty/erroring feeds render nothing — the group only appears when there is at least one app.
const APP_NAV_CLASS =
  "flex w-full items-center gap-3 rounded-md px-3 py-2 text-sm font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-foreground";

function AppNav({ onNavigate }: { onNavigate: () => void }) {
  const { data } = useQuery(api.apps, []);
  const apps = data ?? [];
  if (apps.length === 0) return null;
  return (
    <div className="pt-3" data-testid="app-nav">
      <p className="px-3 pb-1 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Apps</p>
      {apps.map((app) => {
        const warn = isAppNotRunning(app.state);
        // The shared row content — icon + name + the optional not-running warning glyph.
        const content = (
          <>
            <AppIcon name={app.icon} size={17} />
            <span className="min-w-0 flex-1 truncate">{app.name}</span>
            {warn && (
              <span
                data-warning="not-running"
                title="This app is enabled but not running"
                className="shrink-0 text-warning"
              >
                <Icon name="alert" size={14} />
              </span>
            )}
          </>
        );
        // embedded/slot render in-panel → a SPA <Link> to /apps/{id}. standalone is a full
        // navigation to the reverse-proxied page → a plain <a> (target="_self" keeps this tab).
        return app.uiMode === "embedded" || app.uiMode === "slot" ? (
          <Link
            key={app.id}
            to={`/apps/${app.id}`}
            data-app-nav={app.id}
            data-ui-mode={app.uiMode}
            onClick={onNavigate}
            className={APP_NAV_CLASS}
          >
            {content}
          </Link>
        ) : (
          <a
            key={app.id}
            href={app.url}
            target="_self"
            data-app-nav={app.id}
            data-ui-mode={app.uiMode}
            onClick={onNavigate}
            className={APP_NAV_CLASS}
          >
            {content}
          </a>
        );
      })}
    </div>
  );
}

export function Shell() {
  const [mobileNav, setMobileNav] = useState(false);
  const { data: status } = useQuery(api.status, []);
  const callsign = status?.callsign ?? "—";
  return (
    <div className="flex h-screen w-full overflow-hidden bg-background">
      <aside className={cn("absolute z-40 flex h-full w-60 flex-col border-r border-border bg-card transition-transform md:static md:translate-x-0", mobileNav ? "translate-x-0" : "-translate-x-full")}>
        <div className="flex h-14 items-center justify-between border-b border-border px-4">
          <Logo />
          <Button variant="ghost" size="iconSm" className="md:hidden" onClick={() => setMobileNav(false)}><Icon name="x" /></Button>
        </div>
        <nav className="flex-1 space-y-0.5 overflow-y-auto p-2">
          {NAV.map((item) => (
            <NavLink key={item.id} to={item.to} end={item.to === "/"} onClick={() => setMobileNav(false)}
              className={({ isActive }) => cn("flex w-full items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors", isActive ? "bg-primary/10 text-primary" : "text-muted-foreground hover:bg-accent hover:text-foreground")}>
              <Icon name={item.icon} size={17} />
              {item.label}
            </NavLink>
          ))}
          <AppNav onNavigate={() => setMobileNav(false)} />
        </nav>
        <div className="border-t border-border p-3">
          <div className="flex items-center gap-2 rounded-md px-2 py-1.5 text-xs text-muted-foreground">
            <Icon name="info" size={14} />
            <span className="font-mono">{status?.version ?? "pdn"}</span>
          </div>
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-14 shrink-0 items-center justify-between border-b border-border bg-card/60 px-4 backdrop-blur">
          <div className="flex items-center gap-3">
            <Button variant="ghost" size="iconSm" className="md:hidden" onClick={() => setMobileNav(true)}><Icon name="menu" /></Button>
            <div className="flex items-center gap-2">
              <span className="inline-block h-2 w-2 rounded-full bg-success live-dot" />
              <span className="font-mono text-sm font-semibold">{callsign}</span>
              {status && <span className="hidden text-xs text-muted-foreground sm:inline">· {status.alias} · {status.grid}</span>}
            </div>
          </div>
          <div className="flex items-center gap-1">
            {status && (
              <div className="mr-2 hidden items-center gap-1.5 text-xs text-muted-foreground sm:flex">
                <span className="h-1.5 w-1.5 rounded-full bg-success live-dot" />
                up {fmtUptime(status.uptimeSeconds)}
              </div>
            )}
            <ThemeToggle />
            <UserMenu />
          </div>
        </header>
        <main className="flex-1 overflow-y-auto">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

export function PageHeader({ title, subtitle, actions }: { title: ReactNode; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="mb-5 flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
        {subtitle && <p className="mt-1 text-sm text-muted-foreground">{subtitle}</p>}
      </div>
      {actions && <div className="flex items-center gap-2">{actions}</div>}
    </div>
  );
}

/** The per-route page container (max-width + gutter + transform-only entrance). */
export function Page({ children }: { children: ReactNode }) {
  return <div className="animate-screen-in mx-auto max-w-[1400px] p-4 sm:p-6">{children}</div>;
}
