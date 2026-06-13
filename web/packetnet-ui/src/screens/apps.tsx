// ============================================================
// pdn — Apps. Top: the launcher — the node's registered apps that expose a web UI
// (GET /api/v1/apps) as a responsive grid of tiles. Each tile is a plain anchor
// to the app's reverse-proxied absolute URL (/apps/{id}/) — a server route OUTSIDE
// the SPA router, so a normal <a href>, not a react-router <Link>. Mobile-first.
//
// Middle: "Available apps" (GET /api/v1/apps/available) — the catalog ⋈ this node's
// installed state. Lists apps that aren't installed (or have an update), each with an
// Install/Update button that flows through the same capability confirm enable uses —
// the owner sees the declared capabilities before the install POST fires. Installing
// stages the app disabled, so on refetch it appears in the manager below as
// discovered-but-disabled. Plus an "upload a .pdnapp" affordance (the air-gapped path).
//
// Below: the management section (GET /api/v1/apps/packages) — every discovered package +
// inline config-authored app, with its supervisor state. Enable/disable/restart/uninstall
// are admin-gated (the server is the real gate; this is the light-touch UI mirror, like
// users.tsx). Enabling shows a confirm listing the manifest's declared capabilities — the
// owner sees what they are trusting before the POST fires. Disabling needs no confirm.
// Uninstall (catalog/upload-installed packages only) removes the installed files, keeps
// app data, and needs the app disabled first. Inline entries are read-only here (their
// enabled flag is config-authored; the API answers 404 for them); a broken package
// (error != null) renders its error and can never be enabled.
//
// The available + package lists are lifted into <Apps> so an install/upload can refetch
// BOTH (a newly-installed app leaves the available list and appears in the manager).
// ============================================================
import { useState } from "react";
import { Page, PageHeader } from "@/components/layout/shell";
import { Badge, Button, Card, EmptyState, Icon, Modal, Switch, Tooltip, type BadgeVariant } from "@/components/ui";
import { AppIcon } from "@/components/icon";
import { api, listApps, useQuery, type Query } from "@/lib/api";
import { useAuth } from "@/app/auth";
import { cn } from "@/lib/utils";
import type { AppPackage, AppPackageService, AppPackageState, AvailableApp } from "@/lib/types";

export function Apps() {
  const { data, loading, error } = useQuery(listApps);
  const apps = data ?? [];
  // The available + package queries live here so an install/upload can refetch BOTH:
  // a freshly-installed app drops out of "Available apps" and shows up in the manager.
  const available = useQuery(api.availableApps, []);
  const packages = useQuery(api.appPackages, []);
  const reloadBoth = () => { available.reload(); packages.reload(); };

  return (
    <Page>
      <PageHeader
        title="Apps"
        subtitle="Apps published on this node — tap one to open it"
      />

      {error && <EmptyState icon="alert" title="Couldn't load apps" body={error} />}

      {!error && loading && !data && (
        <div className="py-10 text-center text-sm text-muted-foreground">Loading apps…</div>
      )}

      {!error && data && apps.length === 0 && (
        <EmptyState
          icon="apps"
          title="No apps yet"
          body="Apps the node owner registers with a web UI appear here."
        />
      )}

      {!error && apps.length > 0 && (
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4">
          {apps.map((app) => (
            // Absolute, same-origin server route the node reverse-proxies — NOT a SPA
            // route, so a plain <a> (target="_self" keeps it in this tab).
            <a
              key={app.id}
              href={app.url}
              target="_self"
              className="block rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1 focus-visible:ring-offset-background"
            >
              <Card className="flex h-full flex-col items-center gap-3 p-5 text-center transition-colors hover:border-primary/40 hover:bg-accent/40">
                <div className="grid h-12 w-12 place-items-center rounded-lg bg-primary/15 text-primary">
                  <AppIcon name={app.icon} size={24} />
                </div>
                <div className="min-w-0">
                  <p className="truncate text-sm font-semibold">{app.name}</p>
                  <p className="truncate font-mono text-[11px] text-muted-foreground">{app.id}</p>
                </div>
              </Card>
            </a>
          ))}
        </div>
      )}

      <AvailableApps query={available} reloadBoth={reloadBoth} />
      <PackageManager query={packages} reloadBoth={reloadBoth} />
    </Page>
  );
}

// ---- "Available apps": catalog entries this node can install ---------------------
function AvailableApps({ query, reloadBoth }: { query: Query<AvailableApp[]>; reloadBoth: () => void }) {
  const { has } = useAuth();
  const isAdmin = has("admin");
  const { data, loading, error } = query;
  // The app awaiting its capability confirm (null = no confirm open).
  const [confirming, setConfirming] = useState<AvailableApp | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  // Installed-and-current apps live in the package manager below — here we only show
  // what can be added (not installed) or refreshed (an update is available).
  const items = (data ?? []).filter((a) => !a.installed || a.updateAvailable);

  // Run one mutation, then refetch BOTH lists — the installed app moves from here into
  // the manager (the same reload-after-mutation idiom as the package controls).
  const run = async (key: string, fn: () => Promise<unknown>, fallback: string) => {
    if (busy) return;
    setBusy(key);
    setNotice(null);
    try {
      await fn();
      reloadBoth();
    } catch (e) {
      setNotice(e instanceof Error ? e.message : fallback);
    } finally {
      setBusy(null);
    }
  };

  // Install/Update only fires from the capability confirm — the owner sees the declared
  // capabilities before the POST goes out (the same trust gate enable uses).
  const onInstall = (a: AvailableApp) => setConfirming(a);

  const onUpload = (file: File | undefined) => {
    if (!file) return;
    void run(`upload:${file.name}`, () => api.appUpload(file), "Could not upload the app.");
  };

  return (
    <section className="mt-8">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold">Available apps</h2>
          <p className="mt-0.5 text-xs text-muted-foreground">
            Apps this node can install. Installing stages an app disabled — enable it below to grant the capabilities its manifest declares.
          </p>
        </div>
        <UploadButton isAdmin={isAdmin} busy={busy?.startsWith("upload:") ?? false} onPick={onUpload} />
      </div>

      {notice && (
        <div className="mt-3 flex items-start gap-2 rounded-md border border-danger/30 bg-danger/5 px-3 py-2 text-sm text-danger">
          <Icon name="alert" size={15} className="mt-0.5 shrink-0" />
          <span className="flex-1">{notice}</span>
          <button onClick={() => setNotice(null)} className="shrink-0 opacity-70 hover:opacity-100"><Icon name="x" size={14} /></button>
        </div>
      )}

      {error && (
        <div className="mt-3"><EmptyState icon="alert" title="Couldn't load available apps" body={error} /></div>
      )}

      {!error && loading && !data && (
        <div className="py-6 text-center text-sm text-muted-foreground">Loading available apps…</div>
      )}

      {!error && data && items.length === 0 && (
        <div className="mt-3">
          <EmptyState icon="apps" title="Nothing to install" body="Every app in the catalog is already installed and up to date. You can still upload a .pdnapp above." />
        </div>
      )}

      {items.length > 0 && (
        <div className="mt-3 space-y-3">
          {items.map((a) => (
            <div key={a.id} data-available={a.id}>
              <AvailableRow
                a={a}
                isAdmin={isAdmin}
                busy={busy === `install:${a.id}`}
                onInstall={() => onInstall(a)}
              />
            </div>
          ))}
        </div>
      )}

      {/* The capability confirm — same shape as the enable confirm. The owner sees what
          the manifest asks for before the install POST fires. Cancelling fires nothing. */}
      <Modal
        open={confirming !== null}
        onClose={() => setConfirming(null)}
        title={`${confirming?.updateAvailable ? "Update" : "Install"} ${confirming?.name ?? ""}?`}
        width="max-w-md"
        footer={
          <>
            <Button variant="outline" size="sm" onClick={() => setConfirming(null)}>Cancel</Button>
            <Button
              size="sm"
              onClick={() => {
                const a = confirming;
                setConfirming(null);
                if (a) void run(`install:${a.id}`, () => api.appInstall(a.id), "Could not install the app.");
              }}
            >
              <Icon name="download" size={14} /> {confirming?.updateAvailable ? "Update" : "Install"}
            </Button>
          </>
        }
      >
        {confirming && (
          <div className="space-y-3">
            <p className="text-sm text-muted-foreground">
              {confirming.updateAvailable
                ? <>Updating <strong className="text-foreground">{confirming.name}</strong> to v{confirming.version} restages it disabled, keeping its data. It will run with the capabilities its manifest declares:</>
                : <>Installing <strong className="text-foreground">{confirming.name}</strong> v{confirming.version} stages it disabled. Once you enable it, it runs with the capabilities its manifest declares:</>}
            </p>
            {confirming.capabilities.length > 0 ? (
              <ul className="space-y-1.5">
                {confirming.capabilities.map((c) => (
                  <li key={c} className="flex items-center gap-2 rounded-md bg-muted/40 px-2.5 py-1.5 text-xs">
                    <Icon name="check" size={13} className="shrink-0 text-primary" />
                    <span className="font-mono">{c}</span>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="rounded-md bg-muted/40 px-2.5 py-1.5 text-xs text-muted-foreground">No declared capabilities.</p>
            )}
          </div>
        )}
      </Modal>
    </section>
  );
}

// ---- the "upload a .pdnapp" affordance (a hidden file input behind a Button) ----
function UploadButton({ isAdmin, busy, onPick }: { isAdmin: boolean; busy: boolean; onPick: (f: File | undefined) => void }) {
  const id = "pdnapp-upload";
  return (
    <div className="shrink-0">
      {/* A label-wrapped file input keyed to the button — admin/busy disable both. */}
      <input
        id={id}
        type="file"
        accept=".pdnapp,application/gzip,application/x-gzip"
        className="sr-only"
        disabled={!isAdmin || busy}
        onChange={(e) => { onPick(e.target.files?.[0]); e.target.value = ""; }}
      />
      <Button
        variant="outline"
        size="sm"
        disabled={!isAdmin || busy}
        title={isAdmin ? "Upload a .pdnapp package to install it directly" : "Requires admin"}
        onClick={() => document.getElementById(id)?.click()}
      >
        <Icon name="arrowUp" size={14} /> {busy ? "Uploading…" : "Upload a .pdnapp"}
      </Button>
    </div>
  );
}

// ---- one available-app row: identity + install/update control -------------------
function AvailableRow({ a, isAdmin, busy, onInstall }: {
  a: AvailableApp;
  isAdmin: boolean;
  busy: boolean;
  onInstall: () => void;
}) {
  const label = a.updateAvailable ? "Update" : "Install";
  // Why the action is blocked, in priority order — the title/tooltip explains it.
  const disabledReason = !isAdmin ? "Requires admin"
    : !a.installable ? "No build for this node's architecture"
    : busy ? "Working…"
    : null;
  const disabled = disabledReason !== null;
  const btn = (
    <Button size="sm" disabled={disabled} title={disabledReason ?? undefined} onClick={onInstall}>
      <Icon name="download" size={14} /> {busy ? "Installing…" : label}
    </Button>
  );

  return (
    <Card className="p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex min-w-0 items-center gap-3">
          <div className="grid h-9 w-9 shrink-0 place-items-center rounded-md bg-primary/15 text-primary">
            <AppIcon name={a.icon} size={18} />
          </div>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm font-semibold">{a.name}</span>
              <span className="font-mono text-[11px] text-muted-foreground">{a.id} · v{a.version}</span>
              {a.updateAvailable && (
                <Badge variant="warning">update{a.installedVersion ? ` · v${a.installedVersion} → v${a.version}` : ""}</Badge>
              )}
              {!a.installable && <Badge variant="muted">no build for this node</Badge>}
            </div>
            {a.description && <p className="mt-0.5 text-xs text-muted-foreground">{a.description}</p>}
            {a.capabilities.length > 0 && (
              <p className="mt-1 text-[11px] text-muted-foreground">
                Capabilities: <span className="font-mono">{a.capabilities.join(", ")}</span>
              </p>
            )}
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {/* A disabled button doesn't fire hover events, so wrap the not-installable
              case in a Tooltip to keep the hint reachable (matches the table-hint idiom). */}
          {!isAdmin || a.installable ? btn : <Tooltip text={disabledReason}>{btn}</Tooltip>}
        </div>
      </div>
    </Card>
  );
}

// ---- the management section: every package + inline app, with controls --------
function PackageManager({ query, reloadBoth }: { query: Query<AppPackage[]>; reloadBoth: () => void }) {
  const { has } = useAuth();
  const isAdmin = has("admin");
  const { data, loading, error, reload } = query;
  // The package awaiting its capability confirm (null = no confirm open).
  const [confirming, setConfirming] = useState<AppPackage | null>(null);
  // The package awaiting its uninstall confirm (null = no confirm open).
  const [uninstalling, setUninstalling] = useState<AppPackage | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  // A banner-style notice for a failed mutation (mirrors the ports screen's
  // `notice` surface — there is no toast primitive).
  const [notice, setNotice] = useState<string | null>(null);

  const pkgs = data ?? [];

  // Run one mutation, then refetch the list — the server is the source of truth (the
  // same reload-after-mutation idiom as the ports/users screens). Enable/disable/restart
  // only touch this list; uninstall passes reloadBoth so the app reappears as available.
  const run = async (id: string, fn: () => Promise<unknown>, fallback: string, both = false) => {
    if (busy) return;
    setBusy(id);
    setNotice(null);
    try {
      await fn();
      if (both) reloadBoth(); else reload();
    } catch (e) {
      setNotice(e instanceof Error ? e.message : fallback);
    } finally {
      setBusy(null);
    }
  };

  // The enable POST only fires from the capability confirm (below). Disable is
  // immediate — there is no trust decision in turning something off.
  const onToggle = (p: AppPackage, next: boolean) => {
    if (next) setConfirming(p);
    else void run(p.id, () => api.appPackageDisable(p.id), "Could not disable the app.");
  };

  return (
    <section className="mt-8">
      <h2 className="text-sm font-semibold">Manage apps</h2>
      <p className="mt-0.5 text-xs text-muted-foreground">
        Every app package on this node — enable, disable, restart or uninstall them. Enabling an app grants it the capabilities its manifest declares.
      </p>

      {notice && (
        <div className="mt-3 flex items-start gap-2 rounded-md border border-danger/30 bg-danger/5 px-3 py-2 text-sm text-danger">
          <Icon name="alert" size={15} className="mt-0.5 shrink-0" />
          <span className="flex-1">{notice}</span>
          <button onClick={() => setNotice(null)} className="shrink-0 opacity-70 hover:opacity-100"><Icon name="x" size={14} /></button>
        </div>
      )}

      {error && (
        <div className="mt-3"><EmptyState icon="alert" title="Couldn't load app packages" body={error} /></div>
      )}

      {!error && loading && !data && (
        <div className="py-6 text-center text-sm text-muted-foreground">Loading app packages…</div>
      )}

      {!error && data && pkgs.length === 0 && (
        <div className="mt-3">
          <EmptyState icon="apps" title="No app packages" body="Packages dropped into the node's apps directory appear here." />
        </div>
      )}

      {pkgs.length > 0 && (
        <div className="mt-3 space-y-3">
          {pkgs.map((p) => (
            <div key={p.id} data-pkg={p.id}>
              <PackageRow
                p={p}
                isAdmin={isAdmin}
                busy={busy === p.id}
                onToggle={(next) => onToggle(p, next)}
                onRestart={() => void run(p.id, () => api.appPackageRestart(p.id), "Could not restart the app.")}
                onUninstall={() => setUninstalling(p)}
              />
            </div>
          ))}
        </div>
      )}

      {/* The capability confirm — the owner sees what the manifest asks for
          before the enable POST fires. Cancelling fires nothing. */}
      <Modal
        open={confirming !== null}
        onClose={() => setConfirming(null)}
        title={`Enable ${confirming?.name ?? ""}?`}
        width="max-w-md"
        footer={
          <>
            <Button variant="outline" size="sm" onClick={() => setConfirming(null)}>Cancel</Button>
            <Button
              size="sm"
              onClick={() => {
                const p = confirming;
                setConfirming(null);
                if (p) void run(p.id, () => api.appPackageEnable(p.id), "Could not enable the app.");
              }}
            >
              <Icon name="check" size={14} /> Enable
            </Button>
          </>
        }
      >
        {confirming && (
          <div className="space-y-3">
            <p className="text-sm text-muted-foreground">
              Enabling <strong className="text-foreground">{confirming.name}</strong> lets it run on this node with the capabilities its manifest declares:
            </p>
            {confirming.capabilities.length > 0 ? (
              <ul className="space-y-1.5">
                {confirming.capabilities.map((c) => (
                  <li key={c} className="flex items-center gap-2 rounded-md bg-muted/40 px-2.5 py-1.5 text-xs">
                    <Icon name="check" size={13} className="shrink-0 text-primary" />
                    <span className="font-mono">{c}</span>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="rounded-md bg-muted/40 px-2.5 py-1.5 text-xs text-muted-foreground">No declared capabilities.</p>
            )}
          </div>
        )}
      </Modal>

      {/* The uninstall confirm — removes the installed files, keeps app data. */}
      <Modal
        open={uninstalling !== null}
        onClose={() => setUninstalling(null)}
        title={`Uninstall ${uninstalling?.name ?? ""}?`}
        width="max-w-md"
        footer={
          <>
            <Button variant="outline" size="sm" onClick={() => setUninstalling(null)}>Cancel</Button>
            <Button
              variant="destructive"
              size="sm"
              onClick={() => {
                const p = uninstalling;
                setUninstalling(null);
                if (p) void run(p.id, () => api.appUninstall(p.id), "Could not uninstall the app.", true);
              }}
            >
              <Icon name="trash" size={14} /> Uninstall
            </Button>
          </>
        }
      >
        {uninstalling && (
          <p className="text-sm text-muted-foreground">
            Uninstall <strong className="text-foreground">{uninstalling.name}</strong>? Its installed files are removed; app data is kept.
          </p>
        )}
      </Modal>
    </section>
  );
}

// ---- one package row: identity + state, then the admin controls ----------------
function PackageRow({ p, isAdmin, busy, onToggle, onRestart, onUninstall }: {
  p: AppPackage;
  isAdmin: boolean;
  busy: boolean;
  onToggle: (next: boolean) => void;
  onRestart: () => void;
  onUninstall: () => void;
}) {
  const broken = p.error !== null;
  const inline = p.source === "inline";
  // Why the toggle is read-only, in priority order — the title explains it.
  const toggleTitle = !isAdmin ? "Requires admin"
    : inline ? "Inline apps are managed in the node's config file — edit them there."
    : broken ? "A broken package can't be enabled — fix the error below first."
    : busy ? "Working…"
    : p.enabled ? "Disable this app" : "Enable this app";
  const toggleDisabled = !isAdmin || inline || broken || busy;
  // Restart only makes sense for a managed service that is enabled (a Faulted one
  // included — restarting is exactly how you recover it).
  const showRestart = p.service === "managed" && p.enabled && !broken;
  // Uninstall is offered for discovered packages only (not inline, config-authored). The
  // server is the real gate — it 409s a hand-sideloaded (marker-less) dir; here we keep
  // the affordance present but block it while enabled (uninstall needs it disabled first).
  const showUninstall = p.source === "package";
  const uninstallTitle = !isAdmin ? "Requires admin"
    : p.enabled ? "Disable this app before uninstalling it."
    : busy ? "Working…"
    : "Uninstall this app — its files are removed, app data is kept.";
  const uninstallDisabled = !isAdmin || p.enabled || busy;

  return (
    <Card className={cn("p-4", (broken || p.state === "Faulted") && "border-danger/40")}>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex min-w-0 items-center gap-3">
          <div className="grid h-9 w-9 shrink-0 place-items-center rounded-md bg-primary/15 text-primary">
            <AppIcon name={p.icon} size={18} />
          </div>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm font-semibold">{p.name}</span>
              <span className="font-mono text-[11px] text-muted-foreground">{p.id}{p.version ? ` · v${p.version}` : ""}</span>
              <Badge variant={inline ? "muted" : "secondary"}>{p.source}</Badge>
              <StatePill state={p.state} service={p.service} />
              {p.pid !== null && <span className="font-mono text-[11px] text-muted-foreground">pid {p.pid}</span>}
            </div>
            {p.description && <p className="mt-0.5 text-xs text-muted-foreground">{p.description}</p>}
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {showRestart && (
            <Button
              variant="ghost"
              size="sm"
              disabled={!isAdmin || busy}
              title={isAdmin ? "Restart the app's service (teardown + bring-up)" : "Requires admin"}
              onClick={onRestart}
            >
              <Icon name="restart" size={14} /> Restart
            </Button>
          )}
          {showUninstall && (
            <Button
              variant="ghost"
              size="sm"
              disabled={uninstallDisabled}
              title={uninstallTitle}
              onClick={onUninstall}
            >
              <Icon name="trash" size={14} /> Uninstall
            </Button>
          )}
          <Switch checked={p.enabled} disabled={toggleDisabled} title={toggleTitle} onChange={onToggle} />
        </div>
      </div>

      {broken && (
        <div className="mt-3 flex items-center gap-2 rounded-md bg-danger/10 px-2.5 py-1.5 text-xs text-danger">
          <Icon name="alert" size={13} className="shrink-0" /> {p.error}
        </div>
      )}
      {!broken && p.detail && (
        <div className={cn(
          "mt-3 flex items-center gap-2 rounded-md px-2.5 py-1.5 text-xs",
          p.state === "Faulted" ? "bg-danger/10 text-danger" : "bg-warning/10 text-warning",
        )}>
          <Icon name="alert" size={13} className="shrink-0" /> {p.detail}
        </div>
      )}
    </Card>
  );
}

// ---- service-state pill (service "none" = nothing to run → a neutral dash) -----
const STATE_BADGE: Record<AppPackageState, BadgeVariant> = {
  Running: "success",
  Starting: "warning",
  Stopped: "muted",
  Backoff: "warning",
  Faulted: "danger",
  External: "default",
};

function StatePill({ state, service }: { state: AppPackageState | null; service: AppPackageService }) {
  if (service === "none" || state === null) {
    return <span className="text-xs text-muted-foreground">—</span>;
  }
  return <Badge variant={STATE_BADGE[state]}>{state.toLowerCase()}</Badge>;
}
