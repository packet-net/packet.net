// ============================================================
// pdn — Apps. PURE MANAGEMENT now: the enabled, web-capable apps live as first-class
// left-nav entries (see <Shell>'s NAV + the AppNav fetch), so this page no longer carries
// a launcher grid — it is the install + manage surface only.
//
// Top: "Available apps" (GET /api/v1/apps/available) — the catalog ⋈ this node's installed
// state. Lists apps that aren't installed (or have an update), each with an Install/Update
// button that flows through the same capability confirm enable uses — the owner sees the
// declared capabilities before the install POST fires. Installing stages the app disabled,
// so on refetch it appears in the manager below as discovered-but-disabled. Plus an
// "upload a .pdnapp" affordance (the air-gapped path).
//
// Below: the management section (GET /api/v1/apps/packages) — every discovered package +
// inline config-authored app, with its supervisor state. Enable/disable/restart/uninstall
// are admin-gated (the server is the real gate; this is the light-touch UI mirror, like
// users.tsx). Enabling shows a confirm listing the manifest's declared capabilities — the
// owner sees what they are trusting before the POST fires. Disabling needs no confirm.
// Uninstall (catalog/upload-installed packages only) removes the installed files, keeps
// app data, and needs the app disabled first. Inline entries are read-only here (their
// enabled flag is config-authored; the API answers 404 for them); a broken package
// (error != null) renders its error and can never be enabled. An ENABLED app whose service
// isn't running (Stopped/Backoff/Faulted) shows a not-running warning on its row — the same
// warning the left-nav entry carries (a disabled app is expected to be stopped → no warning).
//
// The available + package lists are lifted into <Apps> so an enable/disable/install/upload
// can refetch BOTH: a newly-installed app leaves the available list for the manager. The
// left-nav's launcher feed lives in <Shell> and re-fetches on its own polling/navigation.
// ============================================================
import { useEffect, useRef, useState } from "react";
import { Page, PageHeader } from "@/components/layout/shell";
import { Badge, Button, Card, EmptyState, Icon, Modal, Tooltip, type BadgeVariant } from "@/components/ui";
import { AppIcon } from "@/components/icon";
import { api, useQuery, type Query } from "@/lib/api";
import { useAuth } from "@/app/auth";
import { cn } from "@/lib/utils";
import { isAppNotRunning, displayCapability } from "@/lib/types";
import type { AppForward, AppPackage, AppPackageService, AppPackageState, AvailableApp } from "@/lib/types";

// ---- the in-flight verb a control surfaces while its mutation runs --------------
// There is no spinner primitive, so we reuse the `restart` icon with `animate-spin`
// (a circular-arrow glyph) and pair it with a present-progressive label.
type BusyVerb = "enable" | "disable" | "restart" | "uninstall" | "install" | "upload";
const BUSY_LABEL: Record<BusyVerb, string> = {
  enable: "Enabling…",
  disable: "Disabling…",
  restart: "Restarting…",
  uninstall: "Uninstalling…",
  install: "Installing…",
  upload: "Uploading…",
};
// The shared in-progress affordance: a spinning circular-arrow + the verb's label.
function Spinner({ verb }: { verb: BusyVerb }) {
  return <><Icon name="restart" size={14} className="animate-spin" /> {BUSY_LABEL[verb]}</>;
}

export function Apps() {
  // The available + package queries live here so a mutation can refetch whichever lists it
  // touches: install/upload move an app from "Available apps" into the manager. Enable/disable
  // change the manager AND the left-nav launcher — but the nav's feed lives in <Shell> (which
  // re-fetches on its own), so here we just reload the manager (and the available list when
  // install/upload restages an app). The Apps page no longer renders the launcher grid: enabled
  // web apps are first-class left-nav entries now.
  const available = useQuery(api.availableApps, []);
  const packages = useQuery(api.appPackages, []);
  // reloadAll = manager state (enable/disable/restart); reloadBoth = available + manager
  // (install/upload — the app moves from the catalog list into the manager).
  const reloadAll = () => { packages.reload(); };
  const reloadBoth = () => { available.reload(); packages.reload(); };

  return (
    <Page>
      <PageHeader
        title="Apps"
        subtitle="Install and manage this node's apps. Enabled apps with a web UI appear in the sidebar."
      />

      <AvailableApps query={available} reloadBoth={reloadBoth} />
      <PackageManager query={packages} reloadAll={reloadAll} reloadBoth={reloadBoth} />
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
    <section>
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
                    <span className="font-mono">{displayCapability(c)}</span>
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
        {busy ? <Spinner verb="upload" /> : <><Icon name="arrowUp" size={14} /> Upload a .pdnapp</>}
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
      {busy ? <Spinner verb="install" /> : <><Icon name="download" size={14} /> {label}</>}
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
                Capabilities: <span className="font-mono">{a.capabilities.map(displayCapability).join(", ")}</span>
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
function PackageManager({ query, reloadAll, reloadBoth }: { query: Query<AppPackage[]>; reloadAll: () => void; reloadBoth: () => void }) {
  const { has } = useAuth();
  const isAdmin = has("admin");
  const { data, loading, error, reload } = query;
  // The package awaiting its capability confirm (null = no confirm open).
  const [confirming, setConfirming] = useState<AppPackage | null>(null);
  // The package awaiting its uninstall confirm (null = no confirm open).
  const [uninstalling, setUninstalling] = useState<AppPackage | null>(null);
  // The id + verb of the in-flight mutation (null = idle) — drives the row's busy spinner.
  const [busy, setBusy] = useState<{ id: string; verb: BusyVerb } | null>(null);
  // A banner-style notice for a failed mutation (mirrors the ports screen's
  // `notice` surface — there is no toast primitive).
  const [notice, setNotice] = useState<string | null>(null);
  // Guards the post-enable delayed refetch so its timer can't fire after unmount.
  const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);

  const pkgs = data ?? [];

  // Which lists a mutation refetches: enable/disable/restart reload this inventory ("all" ==
  // the manager now — the launcher feed moved to the left-nav in <Shell>, which re-fetches on
  // its own); uninstall reloads the available list too (the app reappears as installable).
  type Scope = "all" | "self" | "both";

  // Run one mutation, then refetch — the server is the source of truth (the same reload-
  // after-mutation idiom as the ports/users screens). The supervised service starts a few
  // seconds after an enable returns, so on success we ALSO schedule one delayed refetch so
  // the StatePill reaches Running (and the row's not-running warning clears) without a refresh.
  const run = async (id: string, verb: BusyVerb, fn: () => Promise<unknown>, fallback: string, scope: Scope = "self") => {
    if (busy) return;
    setBusy({ id, verb });
    setNotice(null);
    const refetch = scope === "all" ? reloadAll : scope === "both" ? reloadBoth : reload;
    try {
      await fn();
      refetch();
      // Enabling starts the service asynchronously — a single ~2.5s nudge catches up.
      if (verb === "enable") setTimeout(() => { if (mounted.current) refetch(); }, 2500);
    } catch (e) {
      setNotice(e instanceof Error ? e.message : fallback);
    } finally {
      setBusy(null);
    }
  };

  // The enable POST only fires from the capability confirm (below). Disable is
  // immediate — there is no trust decision in turning something off. Both refetch the
  // inventory (reloadAll) so the row's state + not-running warning update in step.
  const onToggle = (p: AppPackage, next: boolean) => {
    if (next) setConfirming(p);
    else void run(p.id, "disable", () => api.appPackageDisable(p.id), "Could not disable the app.", "all");
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
                busy={busy?.id === p.id ? busy.verb : null}
                onToggle={(next) => onToggle(p, next)}
                onRestart={() => void run(p.id, "restart", () => api.appPackageRestart(p.id), "Could not restart the app.")}
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
                if (p) void run(p.id, "enable", () => api.appPackageEnable(p.id), "Could not enable the app.", "all");
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
                    <span className="font-mono">{displayCapability(c)}</span>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="rounded-md bg-muted/40 px-2.5 py-1.5 text-xs text-muted-foreground">No declared capabilities.</p>
            )}
            <ForwardList forwards={confirming.forwards} />
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
                if (p) void run(p.id, "uninstall", () => api.appUninstall(p.id), "Could not uninstall the app.", "both");
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
// `busy` is the verb of this row's in-flight mutation (null = idle) — the matching
// control swaps its label for a spinner while it runs.
function PackageRow({ p, isAdmin, busy, onToggle, onRestart, onUninstall }: {
  p: AppPackage;
  isAdmin: boolean;
  busy: BusyVerb | null;
  onToggle: (next: boolean) => void;
  onRestart: () => void;
  onUninstall: () => void;
}) {
  const working = busy !== null;
  const broken = p.error !== null;
  const inline = p.source === "inline";
  // Why the enable/disable control is read-only, in priority order — the title explains it.
  const toggleTitle = !isAdmin ? "Requires admin"
    : inline ? "Inline apps are managed in the node's config file — edit them there."
    : broken ? "A broken package can't be enabled — fix the error below first."
    : working ? "Working…"
    : p.enabled ? "Disable this app" : "Enable this app";
  const toggleDisabled = !isAdmin || inline || broken || working;
  // Restart only makes sense for a managed service that is enabled (a Faulted one
  // included — restarting is exactly how you recover it).
  const showRestart = p.service === "managed" && p.enabled && !broken;
  // Uninstall is offered for discovered packages only (not inline, config-authored). The
  // server is the real gate — it 409s a hand-sideloaded (marker-less) dir; here we keep
  // the affordance present but block it while enabled (uninstall needs it disabled first).
  const showUninstall = p.source === "package";
  const uninstallTitle = !isAdmin ? "Requires admin"
    : p.enabled ? "Disable this app before uninstalling it."
    : working ? "Working…"
    : "Uninstall this app — its files are removed, app data is kept.";
  const uninstallDisabled = !isAdmin || p.enabled || working;
  // A not-running warning: the app is ENABLED (so the supervisor SHOULD be running it) but its
  // service is Stopped/Backoff/Faulted — it crashed or won't start. A disabled app is expected
  // to be stopped, so it never warns. Mirrors the left-nav badge (both use isAppNotRunning).
  const notRunning = p.enabled && isAppNotRunning(p.state);

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
              {notRunning && (
                <span data-warning="not-running">
                  <Badge variant="warning">
                    <Icon name="alert" size={11} className="mr-1" /> not running
                  </Badge>
                </span>
              )}
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
              disabled={!isAdmin || working}
              title={isAdmin ? "Restart the app's service (teardown + bring-up)" : "Requires admin"}
              onClick={onRestart}
            >
              {busy === "restart" ? <Spinner verb="restart" /> : <><Icon name="restart" size={14} /> Restart</>}
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
              {busy === "uninstall" ? <Spinner verb="uninstall" /> : <><Icon name="trash" size={14} /> Uninstall</>}
            </Button>
          )}
          <EnableToggle
            enabled={p.enabled}
            disabled={toggleDisabled}
            title={toggleTitle}
            busy={busy === "enable" || busy === "disable" ? busy : null}
            onToggle={onToggle}
          />
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

// ---- the Enable/Disable segmented control --------------------------------------
// A two-segment toggle replacing the old <Switch>: the segment matching the current
// state is "selected" (a filled/primary look), the other is the available action.
// Clicking the inactive segment fires onToggle(next) — enable still routes through the
// caller's capability confirm, disable is immediate. While the row is busy, the segment
// being applied swaps its label for a spinner; the disable/title gating is the caller's.
function EnableToggle({ enabled, disabled, title, busy, onToggle }: {
  enabled: boolean;
  disabled: boolean;
  title: string;
  busy: "enable" | "disable" | null;
  onToggle: (next: boolean) => void;
}) {
  // Each segment: the active one looks selected (default/primary fill); the inactive one
  // is the click target (ghost). A busy segment shows the spinner in place of its label.
  const seg = (target: "enable" | "disable") => {
    const active = target === "enable" ? enabled : !enabled;
    const working = busy === target;
    return (
      <Button
        variant={active ? "default" : "ghost"}
        size="sm"
        // The active segment reflects state, not an action — only the inactive segment
        // fires. Disable both while gated/busy so neither is clickable mid-flight.
        disabled={disabled || active}
        aria-pressed={active}
        title={title}
        className="rounded-none"
        onClick={() => onToggle(target === "enable")}
      >
        {working
          ? <Spinner verb={target} />
          : target === "enable"
            ? <><Icon name="check" size={14} /> Enable</>
            : <><Icon name="x" size={14} /> Disable</>}
      </Button>
    );
  };
  return (
    <div className="inline-flex overflow-hidden rounded-md border border-input" role="group" aria-label="Enable or disable this app">
      {seg("enable")}
      <span className="w-px self-stretch bg-input" aria-hidden="true" />
      {seg("disable")}
    </div>
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

// ---- declared tailnet forwards: the "exposes on your tailnet" capability -------
// The well-known names for the ports an app is most likely to expose, so the line
// reads "IMAPS :993 → 127.0.0.1:1430" rather than a bare port number.
const FORWARD_PORT_NAMES: Record<number, string> = {
  993: "IMAPS",
  995: "POP3S",
  465: "SMTPS",
  587: "SMTP submission",
  143: "IMAP",
  110: "POP3",
  25: "SMTP",
};

// A plain list of the tailnet exposures the owner is granting by enabling the app —
// each forward is a capability (docs/network-access.md § App-declared port forwarding).
// Renders nothing when the app declares no forwards.
function ForwardList({ forwards }: { forwards: AppForward[] }) {
  if (forwards.length === 0) return null;
  return (
    <div className="space-y-1.5">
      <p className="text-xs font-medium text-foreground">Exposes on your tailnet:</p>
      <ul className="space-y-1.5">
        {forwards.map((f) => {
          const name = FORWARD_PORT_NAMES[f.listen];
          return (
            <li key={f.listen} className="flex items-center gap-2 rounded-md bg-muted/40 px-2.5 py-1.5 text-xs">
              <Icon name="link" size={13} className="shrink-0 text-primary" />
              <span className="font-mono">
                {name ? `${name} :${f.listen}` : `:${f.listen}`} → {f.target}
              </span>
              {f.tls === "raw" && <Badge variant="muted">raw</Badge>}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
