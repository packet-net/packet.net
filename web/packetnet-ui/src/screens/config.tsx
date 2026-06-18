// ============================================================
// Config editor — the whole NodeConfig, validated before apply (README §8/§9).
// Forms vs Raw YAML; left sub-nav (Identity / Services / Management /
// NET/ROM + INP3 / Beacons / Ports →); edits accumulate a dirty set; a
// "Review & apply" opens the reconcile preview, which groups the pending
// changes by disruption (apply live / restart a port / reset the node) in
// plain language and applies them atomically.
// ============================================================
import { useEffect, useRef, useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { Page, PageHeader } from "@/components/layout/shell";
import {
  Button, Badge, Card, Input, Label, Field, InfoHint, Switch, Select, ImpactBadge, Tabs, Modal, Icon,
} from "@/components/ui";
import { cn } from "@/lib/utils";
import type { NodeConfig, ApplyImpact, FieldHelp, ToggleHelp, NetRomRouting, ReconcileResult, ValidationProblem, ReconcileChange, PortBeacon, TailscaleStatus, SystemInfo, InstallChannelName } from "@/lib/types";
import { api, useQuery, ConfigRejected } from "@/lib/api";
import { useAuth } from "@/app/auth";
import {
  APPLY_IMPACT, NETROM_TOGGLE_HELP, NETROM_ROUTING_HELP, NETROM_FIELD_HELP, INP3_FIELD_HELP,
} from "@/lib/mock";

// a pending change, identified by its dotted config path + apply impact
interface DirtyEntry { path: string; impact: ApplyImpact }

type FormTab = "identity" | "services" | "management" | "netrom" | "beacons" | "oarc";
const TABS: { id: FormTab; label: string }[] = [
  { id: "identity", label: "Identity" },
  { id: "services", label: "Services" },
  { id: "management", label: "Management" },
  { id: "netrom", label: "NET/ROM + INP3" },
  { id: "beacons", label: "Beacons" },
  { id: "oarc", label: "OARC Network Map" },
];

export function Config() {
  const navigate = useNavigate();
  const { has } = useAuth();
  const canApply = has("admin"); // config write is admin-scoped (server is the real gate)
  const { data, reload } = useQuery(api.config, []);

  const [tab, setTab] = useState<FormTab>("identity");
  const [mode, setMode] = useState<"forms" | "raw">("forms");
  const [cfg, setCfg] = useState<NodeConfig | null>(null);
  const [dirty, setDirty] = useState<DirtyEntry[]>([]);
  const [showReconcile, setShowReconcile] = useState(false);

  // server-driven reconcile state (the authoritative preview + validation)
  const [preview, setPreview] = useState<ReconcileResult | null>(null);
  const [problem, setProblem] = useState<ValidationProblem | null>(null);
  const [busy, setBusy] = useState(false);
  const [rawText, setRawText] = useState<string | null>(null);

  // Seed the editable draft from the loaded config. Re-seeds when `data` changes,
  // which only happens on mount and after an apply's reload() — never mid-edit
  // (the query has no other refetch trigger), so this can't clobber in-progress edits.
  useEffect(() => {
    if (data) setCfg(structuredClone(data));
  }, [data]);

  // Seed the raw-YAML buffer from the live node the first time the Raw tab opens.
  useEffect(() => {
    if (mode === "raw" && rawText == null) {
      api.getConfigRaw().then(setRawText).catch((e) => setRawText(`# failed to load raw config: ${e}\n`));
    }
  }, [mode, rawText]);

  // Open the review modal and fetch the server's dry-run reconcile preview.
  const openReview = async () => {
    setShowReconcile(true);
    setPreview(null);
    setProblem(null);
    setBusy(true);
    try {
      const r = mode === "raw" && rawText != null
        ? await api.putConfigRaw(rawText, { dryRun: true })
        : cfg ? await api.putConfig(cfg, { dryRun: true }) : null;
      setPreview(r);
    } catch (e) {
      setProblem(e instanceof ConfigRejected ? e.problem : { errors: [{ path: "(error)", message: String((e as Error)?.message ?? e) }] });
    } finally {
      setBusy(false);
    }
  };

  // Apply for real, then reseed from the now-current config.
  const doApply = async () => {
    setBusy(true);
    setProblem(null);
    try {
      if (mode === "raw" && rawText != null) await api.putConfigRaw(rawText);
      else if (cfg) await api.putConfig(cfg);
      setShowReconcile(false);
      setPreview(null);
      setDirty([]);
      setRawText(null);   // re-pull raw on next open
      reload();           // refetch /config → the seed effect reseeds the draft
    } catch (e) {
      setProblem(e instanceof ConfigRejected ? e.problem : { errors: [{ path: "(error)", message: String((e as Error)?.message ?? e) }] });
    } finally {
      setBusy(false);
    }
  };

  // record a changed path (impact comes from APPLY_IMPACT) — dedup by path
  const touch = (path: string, impact: ApplyImpact) =>
    setDirty((d) => (d.some((x) => x.path === path) ? d : [...d, { path, impact }]));

  // immutable nested set by dotted path + mark dirty
  const set = (path: string, val: unknown, impact: ApplyImpact) => {
    touch(path, impact);
    setCfg((c) => {
      if (!c) return c;
      const next = structuredClone(c) as unknown as Record<string, unknown>;
      const keys = path.split(".");
      let o: Record<string, unknown> = next;
      for (let i = 0; i < keys.length - 1; i++) o = o[keys[i]] as Record<string, unknown>;
      o[keys[keys.length - 1]] = val;
      return next as unknown as NodeConfig;
    });
  };

  return (
    <Page>
      <PageHeader
        title="Config"
        subtitle="Edit the whole node configuration — checked before apply, every write through the reconcile path"
        actions={
          <div className="flex items-center gap-2">
            <Tabs active={mode} onChange={(m) => setMode(m as "forms" | "raw")} tabs={[{ id: "forms", label: "Forms" }, { id: "raw", label: "Raw YAML" }]} />
            <Button size="sm" disabled={!canApply || (mode === "forms" && dirty.length === 0)} onClick={openReview}
              title={canApply ? undefined : "Config changes require the admin scope"}>
              <Icon name="check" size={14} /> Review &amp; apply
              {dirty.length > 0 && <Badge variant="secondary" className="ml-1">{dirty.length}</Badge>}
            </Button>
          </div>
        }
      />

      {!cfg ? null : mode === "forms" ? (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-[200px_1fr]">
          {/* left sub-nav */}
          <div className="flex gap-1 overflow-x-auto lg:flex-col">
            {TABS.map((t) => (
              <button
                key={t.id}
                onClick={() => setTab(t.id)}
                className={cn(
                  "whitespace-nowrap rounded-md px-3 py-2 text-left text-sm font-medium transition-colors",
                  tab === t.id ? "bg-primary/10 text-primary" : "text-muted-foreground hover:bg-accent hover:text-foreground",
                )}
              >
                {t.label}
              </button>
            ))}
            <button
              onClick={() => navigate("/ports")}
              title="Ports are edited on the Ports screen"
              className="flex items-center justify-between gap-2 whitespace-nowrap rounded-md px-3 py-2 text-left text-sm font-medium text-muted-foreground hover:bg-accent hover:text-foreground"
            >
              Ports <Icon name="external" size={13} />
            </button>
          </div>

          <Card className="p-5">
            {tab === "identity" && (
              <section className="max-w-md space-y-4">
                <Field label="Callsign (required)" impact="node-reset" hint="Changing identity resets the node.">
                  <Input value={cfg.identity.callsign} onChange={(e) => set("identity.callsign", e.target.value, "node-reset")} className="font-mono" />
                </Field>
                <Field label="Alias" info="The node mnemonic (≤6 chars) — shown on the network map and advertised as the NET/ROM alias. Long friendly text belongs in the service banner." impact="node-reset">
                  <Input value={cfg.identity.alias ?? ""} maxLength={6} onChange={(e) => set("identity.alias", e.target.value.toUpperCase(), "node-reset")} className="font-mono" />
                </Field>
                <Field label="Locator (grid)" impact="live">
                  <Input value={cfg.identity.grid ?? ""} onChange={(e) => set("identity.grid", e.target.value, "live")} className="font-mono" />
                </Field>
              </section>
            )}

            {tab === "services" && (
              <section className="max-w-xl space-y-4">
                <Field label="Banner" hint="{node} and {call} are templated." impact="live">
                  <Input value={cfg.services.banner} onChange={(e) => set("services.banner", e.target.value, "live")} className="font-mono text-xs" />
                </Field>
                <Field label="Prompt" impact="live">
                  <Input value={cfg.services.prompt} onChange={(e) => set("services.prompt", e.target.value, "live")} className="font-mono" />
                </Field>
              </section>
            )}

            {tab === "management" && (
              <section className="max-w-xl space-y-5">
                <div className="rounded-lg border border-border p-3">
                  <div className="mb-3 flex items-center justify-between">
                    <Label className="text-foreground">HTTP (this UI)</Label>
                    <ImpactBadge impact={APPLY_IMPACT["management.http"]} />
                  </div>
                  <div className="grid grid-cols-2 gap-3">
                    <Field label="Bind"><Input value={cfg.management.http.bind} onChange={(e) => set("management.http.bind", e.target.value, "node-reset")} className="font-mono" /></Field>
                    <Field label="Port"><Input type="number" value={cfg.management.http.port} onChange={(e) => set("management.http.port", +e.target.value, "node-reset")} className="font-mono" /></Field>
                  </div>
                </div>
                <div className="rounded-lg border border-border p-3">
                  <div className="mb-3 flex items-center justify-between">
                    <span className="flex items-center gap-1.5">
                      <Label className="text-foreground">HTTPS (TLS)</Label>
                      <InfoHint text="Serve this panel over TLS so the password and token aren't sent in clear over the network. Off by default. When on, a self-signed cert is generated on first start (encrypts the channel; browsers warn until it's trusted) — or set a certificate path to a trusted .pfx. Passkeys need a trusted secure context (a trusted cert, or access via localhost)." />
                    </span>
                    <ImpactBadge impact={APPLY_IMPACT["management.http"]} />
                  </div>
                  <div className="grid grid-cols-3 gap-3">
                    <Field label="Enabled"><div className="flex h-9 items-center"><Switch checked={cfg.management.https?.enabled ?? false} onChange={(v) => set("management.https.enabled", v, "node-reset")} /></div></Field>
                    <Field label="Bind"><Input value={cfg.management.https?.bind ?? "127.0.0.1"} onChange={(e) => set("management.https.bind", e.target.value, "node-reset")} className="font-mono" /></Field>
                    <Field label="Port"><Input type="number" value={cfg.management.https?.port ?? 8443} onChange={(e) => set("management.https.port", +e.target.value, "node-reset")} className="font-mono" /></Field>
                  </div>
                  {(cfg.management.https?.enabled ?? false) && (
                    <div className="mt-3">
                      <Field label="Certificate path" info="Path to a PKCS#12 (.pfx) cert+key the clients trust. Leave blank to auto-generate a self-signed cert (channel-encrypting, but browsers warn until trusted).">
                        <Input value={cfg.management.https?.certificatePath ?? ""} placeholder="(auto self-signed)" onChange={(e) => set("management.https.certificatePath", e.target.value || null, "node-reset")} className="font-mono" />
                      </Field>
                    </div>
                  )}
                </div>
                <div className="rounded-lg border border-border p-3">
                  <div className="mb-3 flex items-center justify-between">
                    <Label className="text-foreground">Telnet console</Label>
                    <ImpactBadge impact={APPLY_IMPACT["management.telnet"]} />
                  </div>
                  <div className="grid grid-cols-3 gap-3">
                    <Field label="Enabled"><div className="flex h-9 items-center"><Switch checked={cfg.management.telnet.enabled} onChange={(v) => set("management.telnet.enabled", v, "port-restart")} /></div></Field>
                    <Field label="Bind"><Input value={cfg.management.telnet.bind} onChange={(e) => set("management.telnet.bind", e.target.value, "port-restart")} className="font-mono" /></Field>
                    <Field label="Port"><Input type="number" value={cfg.management.telnet.port} onChange={(e) => set("management.telnet.port", +e.target.value, "port-restart")} className="font-mono" /></Field>
                  </div>
                </div>
                <RemoteAccessSection cfg={cfg} canAdmin={canApply} onAdopted={reload} />
                <SystemPanel canAdmin={canApply} />
              </section>
            )}

            {tab === "netrom" && <NetRomSection cfg={cfg} set={set} />}

            {tab === "beacons" && <BeaconsSection cfg={cfg} set={set} />}

            {tab === "oarc" && <OarcSection cfg={cfg} set={set} />}
          </Card>
        </div>
      ) : (
        <RawYaml text={rawText} onChange={setRawText} onValidate={openReview} />
      )}

      <ReconcilePreview
        open={showReconcile}
        result={preview}
        problem={problem}
        busy={busy}
        onClose={() => setShowReconcile(false)}
        onApply={doApply}
      />
    </Page>
  );
}

// ---------- Remote access (Tailscale) -----------------------
// The embedded tsnet sidecar's live status (network-access.md § Status surfacing). Polls
// GET /api/v1/system/tailscale while the panel is mounted (the Management tab is open):
//   - needs-login → a prominent "Authorize this node →" link (the operator's interactive
//     first-join, S3).
//   - running with an FQDN → "Reachable at https://<fqdn>".
//   - fqdn set AND ≠ the current WebAuthn RP id → an admin-gated "Use <fqdn> for passkeys"
//     button that writes relyingPartyId = fqdn + adds https://<fqdn> to allowedOrigins.
//     Operator-initiated only — never automatic (it invalidates existing passkeys).
function RemoteAccessSection({ cfg, canAdmin, onAdopted }: { cfg: NodeConfig; canAdmin: boolean; onAdopted: () => void }) {
  const [status, setStatus] = useState<TailscaleStatus | null>(null);
  const [adopting, setAdopting] = useState(false);
  const [adoptError, setAdoptError] = useState<string | null>(null);

  // Poll while mounted. Cleared on unmount (tab change / screen leave).
  useEffect(() => {
    let alive = true;
    const tick = () => api.tailscaleStatus().then((s) => { if (alive) setStatus(s); }).catch(() => { /* transient */ });
    tick();
    const t = setInterval(tick, 1500);
    return () => { alive = false; clearInterval(t); };
  }, []);

  const rpId = cfg.management.auth.webAuthn.relyingPartyId;
  const fqdn = status?.fqdn ?? null;
  const showAdopt = fqdn != null && fqdn !== rpId;

  const adopt = async () => {
    if (fqdn == null) return;
    setAdopting(true);
    setAdoptError(null);
    try {
      await api.useFqdnForPasskeys(fqdn);
      onAdopted();   // refetch /config → the RP id now matches; the button hides
    } catch (e) {
      setAdoptError(e instanceof ConfigRejected ? e.problem.errors.map((x) => x.message).join("; ") : String((e as Error)?.message ?? e));
    } finally {
      setAdopting(false);
    }
  };

  return (
    <div className="rounded-lg border border-border p-3" data-testid="tailscale-panel">
      <div className="mb-3 flex items-center justify-between">
        <span className="flex items-center gap-1.5">
          <Label className="text-foreground">Remote access (Tailscale)</Label>
          <InfoHint text="An embedded Tailscale node that joins your tailnet and gets a real Let's Encrypt cert for pdn.<tailnet>.ts.net — so passkeys work remotely with no public DNS, port-forward, or cert management. Off by default; enable it in the tailscale: config block." />
        </span>
        <Badge variant={status?.state === "running" ? "secondary" : "muted"}>{status?.state ?? "…"}</Badge>
      </div>

      {status == null ? (
        <p className="text-xs text-muted-foreground">Checking…</p>
      ) : status.state === "disabled" || !status.enabled ? (
        <p className="text-xs text-muted-foreground">
          Disabled — pdn stays HTTP-only. Enable the embedded Tailscale node in the <span className="font-mono">tailscale:</span> config block to reach this node remotely (and use passkeys over the network).
        </p>
      ) : (
        <div className="space-y-2">
          {status.state === "needs-login" && status.authUrl && (
            <div className="rounded-md border border-primary/30 bg-primary/5 p-3">
              <p className="text-xs text-muted-foreground">This node needs to be authorised on your tailnet.</p>
              <a href={status.authUrl} target="_blank" rel="noreferrer"
                className="mt-1.5 inline-flex items-center gap-1.5 text-sm font-semibold text-primary hover:underline">
                Authorize this node <Icon name="external" size={13} />
              </a>
            </div>
          )}

          {status.state === "running" && fqdn && (
            <p className="text-xs text-muted-foreground">
              Reachable at{" "}
              <a href={`https://${fqdn}`} target="_blank" rel="noreferrer" className="font-mono font-semibold text-primary hover:underline">
                https://{fqdn}
              </a>
              {status.funnel && <Badge variant="muted" className="ml-2">funnel (public)</Badge>}
            </p>
          )}

          {status.state === "error" && (
            <p className="text-xs text-danger">The Tailscale sidecar reported an error — see the node log.</p>
          )}

          {showAdopt && (
            <div className="rounded-md border border-border bg-muted/30 p-3">
              <p className="text-xs text-muted-foreground">
                Passkeys are currently scoped to <span className="font-mono text-foreground/80">{rpId}</span>. Adopt the Tailscale hostname so passkeys work over the network.
              </p>
              <Button size="xs" className="mt-2" disabled={!canAdmin || adopting} onClick={adopt}
                title={canAdmin ? undefined : "Changing the passkey hostname requires the admin scope"}>
                {adopting ? "Applying…" : <>Use <span className="font-mono">{fqdn}</span> for passkeys</>}
              </Button>
              {adoptError && <p className="mt-1.5 text-[11px] text-danger">{adoptError}</p>}
              <p className="mt-1.5 text-[11px] text-muted-foreground">
                Changing the passkey hostname invalidates existing passkeys — they&apos;ll need re-enrolling.
              </p>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ---------- About this node + self-update (Phase 7) ---------
// Shows the node's running version + install channel (closing the "nothing
// distinguishes the running version" gap — docs/node-self-update-design.md), and when
// the node reports an update available, an "update available · vX → vY" banner with an
// admin-gated Apply button.
//
// Apply is the fire-and-acknowledge reconnect (the design's "UI reconnect, not in-band
// result"): POST /system/update returns 202 and restarts the very process that handled
// it, so we can't read the outcome in-band. Instead we poll GET /system/info (the
// authoritative signal — the running version changing proves the new binary is up) plus
// GET /healthz (liveness) until the version differs from the one we started on, or a
// timeout. Apply is disabled when the channel is "unknown" (the node won't self-update).
const UPDATE_POLL_TIMEOUT_MS = 180_000; // 3 min — an apt/dpkg upgrade + restart can be slow
const UPDATE_POLL_INTERVAL_MS = 2_000;

// Friendly label for the install channel + a one-liner on how this node updates.
const CHANNEL_LABEL: Record<InstallChannelName, string> = {
  apt: "apt",
  github: "GitHub Releases",
  selfcontained: "self-contained",
  unknown: "unknown",
};
function channelHelp(ch: InstallChannelName): string {
  switch (ch) {
    case "apt": return "Installed from an apt repository — Apply runs a targeted apt upgrade of this package. dpkg stays the owner of the files.";
    case "github": return "Installed from a GitHub release .deb — Apply downloads the next release, verifies its checksum, and installs it through dpkg.";
    case "selfcontained": return "Self-contained install — Apply downloads the next release, verifies its checksum, and atomically swaps it in (with rollback).";
    case "unknown": return "This node's install channel can't be determined, so it won't self-update. Update it via your package manager or by reinstalling.";
  }
}

type ApplyPhase = "idle" | "applying" | "waiting" | "done" | "timeout" | "error";

function SystemPanel({ canAdmin }: { canAdmin: boolean }) {
  const [info, setInfo] = useState<SystemInfo | null>(null);
  const [phase, setPhase] = useState<ApplyPhase>("idle");
  const [error, setError] = useState<string | null>(null);

  // Load once on mount, and again after a confirmed restart (to show the new version).
  const loadInfo = () => api.systemInfo().then(setInfo).catch(() => { /* transient — keep last */ });
  useEffect(() => { loadInfo(); }, []);

  const busy = phase === "applying" || phase === "waiting";
  const ch: InstallChannelName = info?.channel ?? "unknown";
  const canUpdate = ch !== "unknown";

  // The fire-and-acknowledge Apply: POST /update (202), then poll /info + /healthz until
  // the running version changes (the new binary is up) or we time out. The polling tab can
  // close mid-flight — the loop is bounded and self-cancels on unmount via `alive`.
  const aliveRef = useRef(true);
  useEffect(() => () => { aliveRef.current = false; }, []);

  const apply = async () => {
    if (!info || !canUpdate || busy) return;
    const fromVersion = info.version;
    setError(null);
    setPhase("applying");
    try {
      await api.systemUpdate();
    } catch (e) {
      setError(String((e as Error)?.message ?? e) || "Could not start the update.");
      setPhase("error");
      return;
    }
    // Dispatched. Now wait for the node to come back on a different version.
    setPhase("waiting");
    const deadline = Date.now() + UPDATE_POLL_TIMEOUT_MS;
    while (Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, UPDATE_POLL_INTERVAL_MS));
      if (!aliveRef.current) return; // panel unmounted — abandon the poll
      // /healthz answering is necessary-but-not-sufficient: it can be up on the OLD
      // version mid-restart. The authoritative signal is /info reporting a NEW version.
      if (!(await api.nodeHealthy())) continue;
      let next: SystemInfo | null = null;
      try { next = await api.systemInfo(); } catch { continue; } // still restarting
      if (!aliveRef.current) return;
      if (next && next.version !== fromVersion) {
        setInfo(next);
        setPhase("done");
        return;
      }
    }
    if (aliveRef.current) setPhase("timeout");
  };

  return (
    <div className="rounded-lg border border-border p-3" data-testid="system-panel">
      <div className="mb-3 flex items-center justify-between">
        <span className="flex items-center gap-1.5">
          <Label className="text-foreground">About this node</Label>
          <InfoHint text="The version of pdn this node is running and how it was installed. When a newer version is available, an admin can apply it here — the node updates and restarts, then this panel reconnects on the new version." />
        </span>
        <Badge variant={ch === "unknown" ? "muted" : "secondary"}>{CHANNEL_LABEL[ch]}</Badge>
      </div>

      {info == null ? (
        <p className="text-xs text-muted-foreground">Checking…</p>
      ) : (
        <div className="space-y-3">
          <div className="space-y-1.5 text-sm">
            <div className="flex items-center justify-between">
              <span className="text-muted-foreground">Version</span>
              <span className="font-mono font-semibold" data-testid="node-version">{info.version}</span>
            </div>
            <div className="flex items-center justify-between">
              <span className="text-muted-foreground">Update channel</span>
              <span className="font-mono">{CHANNEL_LABEL[ch]}</span>
            </div>
          </div>
          <p className="text-[11px] text-muted-foreground">{channelHelp(ch)}</p>

          {/* update-available banner + Apply */}
          {info.updateAvailable && info.latestVersion && phase !== "done" && (
            <div className="rounded-md border border-primary/30 bg-primary/5 p-3" data-testid="update-banner">
              <div className="flex items-start gap-2">
                <Icon name="download" size={15} className="mt-0.5 shrink-0 text-primary" />
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-semibold text-foreground">
                    Update available — v{info.version} → v{info.latestVersion}
                  </p>
                  <p className="mt-0.5 text-[11px] text-muted-foreground">
                    {channelHelp(ch)}
                  </p>
                  <Button
                    size="sm"
                    className="mt-2.5"
                    disabled={!canAdmin || !canUpdate || busy}
                    title={
                      !canUpdate
                        ? "This node's install channel is unknown, so it can't self-update."
                        : !canAdmin
                          ? "Applying an update requires the admin scope."
                          : undefined
                    }
                    onClick={apply}
                  >
                    {phase === "applying"
                      ? "Starting update…"
                      : phase === "waiting"
                        ? "Updating — reconnecting…"
                        : <><Icon name="download" size={14} /> Apply update</>}
                  </Button>
                  {busy && (
                    <p className="mt-2 flex items-center gap-1.5 text-[11px] text-muted-foreground">
                      <span className="inline-block h-1.5 w-1.5 animate-pulse rounded-full bg-primary" />
                      The node is updating and will restart. This page reconnects automatically when it&apos;s back — please don&apos;t close it.
                    </p>
                  )}
                  {phase === "timeout" && (
                    <p className="mt-2 text-[11px] text-warning">
                      The node hasn&apos;t come back on a new version yet. It may still be updating — refresh in a moment to check, or see the node log if it doesn&apos;t recover.
                    </p>
                  )}
                  {phase === "error" && error && (
                    <p className="mt-2 text-[11px] text-danger">{error}</p>
                  )}
                </div>
              </div>
            </div>
          )}

          {phase === "done" && (
            <div className="rounded-md border border-success/30 bg-success/5 p-3" data-testid="update-done">
              <p className="flex items-center gap-1.5 text-sm font-medium text-success">
                <Icon name="check" size={15} /> Updated — now running v{info.version}.
              </p>
            </div>
          )}

          {!info.updateAvailable && phase === "idle" && (
            <p className="flex items-center gap-1.5 text-xs text-success" data-testid="up-to-date">
              <Icon name="check" size={14} /> Up to date.
            </p>
          )}
        </div>
      )}
    </div>
  );
}

// ---------- NET/ROM + INP3: guidance, not jargon ------------
function NetRomSection({ cfg, set }: { cfg: NodeConfig; set: (path: string, val: unknown, impact: ApplyImpact) => void }) {
  const nr = cfg.netRom;
  const inp3 = nr.inp3;
  const toggleKeys = ["enabled", "broadcast"] as const;
  const numKeys = ["defaultNeighbourQuality", "minQuality", "sweepIntervalSeconds", "timeToLive", "window"] as const;
  const routingDesc = NETROM_ROUTING_HELP.options.find((o) => o.value === nr.routing)?.desc;
  const inp3Keys = ["l3RttInterval", "l3RttResetWindow", "rifInterval", "positiveDebounce"] as const;
  const nrRec = nr as unknown as Record<string, number | undefined>;
  const inp3Rec = inp3 as unknown as Record<string, number>;

  return (
    <section className="space-y-5">
      <p className="max-w-2xl text-sm text-muted-foreground">
        NET/ROM is the layer that turns your node from a single radio link into part of a routed network — it learns which stations are reachable and relays traffic between them. The switches below decide how much your node takes part.
      </p>

      <div className="space-y-2">
        {toggleKeys.map((k) => {
          const help: ToggleHelp = NETROM_TOGGLE_HELP[k];
          return (
            <ToggleRow
              key={k}
              label={help.label}
              desc={help.desc}
              checked={nr[k]}
              onChange={(v) => set("netRom." + k, v, "live")}
            />
          );
        })}
      </div>

      {/* Routing role: the single 3-state successor to the old connect + forward toggles. */}
      <div className="max-w-md rounded-lg border border-border p-3">
        <Field label={NETROM_ROUTING_HELP.label} info={NETROM_ROUTING_HELP.help}>
          <Select
            value={nr.routing}
            onChange={(e) => set("netRom.routing", e.target.value as NetRomRouting, "live")}
          >
            {NETROM_ROUTING_HELP.options.map((o) => (
              <option key={o.value} value={o.value}>{o.label}</option>
            ))}
          </Select>
        </Field>
        {routingDesc && <p className="mt-2 text-xs leading-snug text-muted-foreground">{routingDesc}</p>}
      </div>

      <AdvancedDetails title="Advanced routing tuning">
        <p className="mb-3 text-xs text-muted-foreground">Most nodes never touch these — the defaults are sensible. Adjust only if you understand the trade-off.</p>
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
          {numKeys.map((k) => (
            <GuidedNum key={k} meta={NETROM_FIELD_HELP[k]} value={nrRec[k] ?? 0} onChange={(v) => set("netRom." + k, v, "live")} />
          ))}
        </div>
      </AdvancedDetails>

      <div className="rounded-lg border border-primary/30 bg-primary/5 p-4">
        <Label className="flex items-center gap-1.5 text-primary"><Icon name="signal" size={14} /> INP3 time-routing</Label>
        <p className="mt-1.5 max-w-2xl text-xs text-muted-foreground">
          An overlay that measures the <strong>actual round-trip time</strong> to each destination. Plain NET/ROM picks routes by a static quality score; INP3 lets the node prefer the route that&apos;s genuinely fastest right now.
        </p>
        <div className="mt-3 space-y-2">
          <ToggleRow
            label="Use INP3 time-routing"
            desc="Measure and track real path times across the network."
            checked={inp3.enabled}
            onChange={(v) => set("netRom.inp3.enabled", v, "live")}
          />
          <ToggleRow
            label="Prefer the faster route"
            desc="When a measured time is available, choose routes by speed ahead of the static quality score."
            checked={inp3.preferInp3Routes}
            onChange={(v) => set("netRom.inp3.preferInp3Routes", v, "live")}
          />
        </div>
        <div className="mt-3">
          <AdvancedDetails title="INP3 timing intervals">
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              {inp3Keys.map((k) => (
                <GuidedNum key={k} meta={INP3_FIELD_HELP[k]} value={inp3Rec[k]} onChange={(v) => set("netRom.inp3." + k, v, "live")} />
              ))}
            </div>
          </AdvancedDetails>
        </div>
      </div>
    </section>
  );
}

// labelled on/off choice with a one-line plain-English description
function ToggleRow({ label, desc, checked, onChange }: { label: string; desc?: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <div className="flex items-start justify-between gap-4 rounded-lg border border-border p-3">
      <div className="min-w-0">
        <p className="text-sm font-medium text-foreground">{label}</p>
        {desc && <p className="mt-0.5 text-xs leading-snug text-muted-foreground">{desc}</p>}
      </div>
      <div className="shrink-0 pt-0.5"><Switch checked={checked} onChange={onChange} /></div>
    </div>
  );
}

// numeric field driven by a {label, unit, help} descriptor (tooltip + unit suffix)
function GuidedNum({ meta, value, onChange }: { meta: FieldHelp; value: number; onChange: (v: number) => void }) {
  const suffix = meta.unit && !meta.unit.includes("–") ? meta.unit : null;
  return (
    <Field label={meta.label} info={meta.help}>
      <div className="relative">
        <Input type="number" value={value} onChange={(e) => onChange(+e.target.value)} className={cn("font-mono", suffix && "pr-16")} />
        {suffix && <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-[11px] text-muted-foreground">{suffix}</span>}
      </div>
    </Field>
  );
}

// collapsible "advanced" panel (closed by default)
function AdvancedDetails({ title, children }: { title: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="rounded-lg border border-border">
      <button onClick={() => setOpen((o) => !o)} className="flex w-full items-center justify-between p-3 text-sm font-medium text-foreground">
        <span className="flex items-center gap-2"><Icon name="config" size={14} className="text-muted-foreground" /> {title}</span>
        <Icon name="chevDown" size={15} className={cn("text-muted-foreground transition-transform", open && "rotate-180")} />
      </button>
      {open && <div className="border-t border-border p-3">{children}</div>}
    </div>
  );
}

// ---------- Beacons (README §9): system default + per-port ----
// Wired to the live NodeConfig: the system default is cfg.beacon and per-port
// overrides are cfg.ports[i].beacon (null = inherit the default wholesale). All
// edits go through the same dirty-set/save path the other tabs use (impact "live"
// — a beacon edit re-arms the timers without restarting a port).
function BeaconsSection({ cfg, set }: { cfg: NodeConfig; set: (path: string, val: unknown, impact: ApplyImpact) => void }) {
  const def = cfg.beacon;

  // Per-port override patch: build the whole PortBeacon and set it at ports.{i}.beacon
  // (null = remove the override → inherit the default).
  const setPortBeacon = (i: number, value: PortBeacon | null) => set(`ports.${i}.beacon`, value, "live");

  return (
    <section className="space-y-5">
      <div className="rounded-lg border border-border p-4">
        <div className="mb-3 flex items-center justify-between">
          <span className="flex items-center gap-1.5">
            <Label className="text-foreground">System default beacon</Label>
            <InfoHint text="A periodic ID frame pdn sends on each port (dest BEACON, PID 0xF0) unless that port overrides it. {node} and {call} are filled in automatically. Off by default." />
          </span>
          <Switch checked={def.enabled} onChange={(v) => set("beacon.enabled", v, "live")} />
        </div>
        {def.enabled ? (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-[160px_1fr]">
            <Field label="Every" info="How often the ID beacon is transmitted.">
              <div className="relative">
                <Input type="number" min={1} value={def.intervalMinutes} onChange={(e) => set("beacon.intervalMinutes", +e.target.value, "live")} className="pr-16 font-mono" />
                <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-[11px] text-muted-foreground">minutes</span>
              </div>
            </Field>
            <Field label="Text"><Input value={def.text} onChange={(e) => set("beacon.text", e.target.value, "live")} className="font-mono text-xs" /></Field>
          </div>
        ) : (
          <p className="text-xs text-muted-foreground">The node does not beacon. Turn this on to announce the node's presence on each port, or override per port below.</p>
        )}
      </div>

      <div>
        <p className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Per-port</p>
        <div className="space-y-2">
          {cfg.ports.map((port, i) => {
            const b = port.beacon;                          // PortBeacon | null
            const overriding = b != null;
            // Effective enabled/interval/text for display (override wins; null fields inherit).
            const enabled = overriding ? b.enabled : def.enabled;
            const interval = overriding ? (b.intervalMinutes ?? def.intervalMinutes) : def.intervalMinutes;
            return (
              <div key={port.id} className="rounded-lg border border-border p-3">
                <div className="flex items-center justify-between">
                  <span className="flex items-center gap-2.5">
                    <Switch
                      checked={enabled}
                      onChange={(v) => setPortBeacon(i, { enabled: v, intervalMinutes: b?.intervalMinutes ?? null, text: b?.text ?? null })}
                    />
                    <span className="font-mono text-sm font-semibold">{port.id}</span>
                    {!enabled && <Badge variant="muted">no beacon</Badge>}
                    {!overriding && <Badge variant="secondary">default</Badge>}
                  </span>
                  {enabled && overriding && (
                    <span className="flex items-center gap-2 text-xs text-muted-foreground">
                      every
                      <Input type="number" min={1} value={interval} onChange={(e) => setPortBeacon(i, { ...b!, intervalMinutes: +e.target.value })} className="h-7 w-16 font-mono text-xs" />
                      min
                    </span>
                  )}
                </div>
                {enabled && (
                  <div className="mt-3">
                    {overriding ? (
                      <Field
                        label="Custom text"
                        badge={<button onClick={() => setPortBeacon(i, null)} className="text-[11px] text-muted-foreground hover:text-primary">use default</button>}
                      >
                        <Input value={b.text ?? def.text} onChange={(e) => setPortBeacon(i, { ...b, text: e.target.value })} className="font-mono text-xs" />
                      </Field>
                    ) : (
                      <div className="flex items-center justify-between rounded-md bg-muted/40 px-2.5 py-2 text-xs">
                        <span className="text-muted-foreground">Uses default — <span className="font-mono text-foreground/70">{def.text}</span></span>
                        <button onClick={() => setPortBeacon(i, { enabled: def.enabled, intervalMinutes: def.intervalMinutes, text: def.text })} className="shrink-0 font-medium text-primary hover:underline">Override</button>
                      </div>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>
    </section>
  );
}

// ---------- OARC network map (outbound telemetry) -----------
// Reports this node's telemetry to the OARC packet-network map. Outbound only — the
// node POSTs to the collector; nothing reaches in. Default-off: with `enabled` off
// nothing is sent. Every write is hot-applied (the node re-reads the reporter config
// without restarting), so all edits go through set(..., "live"). The collector only
// places a node on the map when it carries a valid Maidenhead locator, so when status
// reporting is on but identity.grid is missing/invalid we surface an inline warning
// pointing back at the Identity tab.
const MAIDENHEAD_GRID = /^[A-R]{2}\d{2}[A-Xa-x]{2}$/;
function OarcSection({ cfg, set }: { cfg: NodeConfig; set: (path: string, val: unknown, impact: ApplyImpact) => void }) {
  const o = cfg.oarc;
  const grid = cfg.identity.grid ?? "";
  const gridMissing = o.enabled && o.reportNodeStatus && !MAIDENHEAD_GRID.test(grid);

  return (
    <section className="space-y-5">
      <div className="rounded-lg border border-border p-4">
        <div className="flex items-start justify-between gap-4">
          <div className="min-w-0">
            <Label className="text-foreground">Report to the OARC network map</Label>
            <p className="mt-1.5 max-w-2xl text-xs leading-snug text-muted-foreground">
              Reports this node&apos;s telemetry to the OARC packet-network map — outbound only, and off by default. Traces are the highest-volume and most revealing category.
            </p>
          </div>
          <div className="shrink-0 pt-0.5"><Switch checked={o.enabled} onChange={(v) => set("oarc.enabled", v, "live")} /></div>
        </div>
      </div>

      {o.enabled && (
        <>
          {gridMissing && (
            <div className="rounded-lg border border-warning/30 bg-warning/5 p-3 text-warning">
              <p className="flex items-center gap-2 text-sm font-medium"><Icon name="alert" size={14} /> No valid locator</p>
              <p className="mt-1 text-xs leading-snug opacity-90">Set a valid Maidenhead grid in Identity — the node can&apos;t appear on the map without one.</p>
            </div>
          )}

          <div>
            <p className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">What to report</p>
            <div className="space-y-2">
              <ToggleRow
                label="Node status"
                desc="The node coming up, periodic status, and going down."
                checked={o.reportNodeStatus}
                onChange={(v) => set("oarc.reportNodeStatus", v, "live")}
              />
              <ToggleRow
                label="Links (L2)"
                desc="AX.25 link events and per-link statistics."
                checked={o.reportLinks}
                onChange={(v) => set("oarc.reportLinks", v, "live")}
              />
              <ToggleRow
                label="Circuits (L4)"
                desc="NET/ROM circuit events and statistics."
                checked={o.reportCircuits}
                onChange={(v) => set("oarc.reportCircuits", v, "live")}
              />
              <ToggleRow
                label="Traces (per-frame L2)"
                desc="The per-frame trace firehose — the highest-volume and most revealing category. Off by default."
                checked={o.reportTraces}
                onChange={(v) => set("oarc.reportTraces", v, "live")}
              />
              {o.reportTraces && (
                <ToggleRow
                  label="Over-air frames only"
                  desc="When tracing, only report frames seen over the air (RF) — not loopback or sim traffic."
                  checked={o.tracesRfOnly}
                  onChange={(v) => set("oarc.tracesRfOnly", v, "live")}
                />
              )}
            </div>
          </div>

          <ToggleRow
            label="Publish exact position"
            desc="Publish the node's exact latitude/longitude rather than only its Maidenhead locator."
            checked={o.publishExactPosition}
            onChange={(v) => set("oarc.publishExactPosition", v, "live")}
          />

          <div className="max-w-xl">
            <Field label="Collector URL" info="The OARC collector this node POSTs its telemetry to. An absolute http(s) URL.">
              <Input value={o.baseUrl} onChange={(e) => set("oarc.baseUrl", e.target.value, "live")} className="font-mono text-xs" />
            </Field>
          </div>

          <AdvancedDetails title="Reporting cadence">
            <div className="grid grid-cols-2 gap-3">
              <Field label="Node-status interval" info="How often the node-status heartbeat is sent.">
                <div className="relative">
                  <Input type="number" min={1} value={o.statusIntervalSecs} onChange={(e) => set("oarc.statusIntervalSecs", +e.target.value, "live")} className="pr-16 font-mono" />
                  <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-[11px] text-muted-foreground">seconds</span>
                </div>
              </Field>
              <Field label="Link/circuit interval" info="How often link and circuit status is refreshed.">
                <div className="relative">
                  <Input type="number" min={1} value={o.sessionStatusIntervalSecs} onChange={(e) => set("oarc.sessionStatusIntervalSecs", +e.target.value, "live")} className="pr-16 font-mono" />
                  <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-[11px] text-muted-foreground">seconds</span>
                </div>
              </Field>
            </div>
          </AdvancedDetails>
        </>
      )}
    </section>
  );
}

// ---------- Raw YAML view -----------------------------------
// Controlled, seeded from GET /config/raw (the live node's serialised config) and
// applied through PUT /config/raw — the server is the source of truth, not a
// client-side approximation.
function RawYaml({ text, onChange, onValidate }: { text: string | null; onChange: (t: string) => void; onValidate: () => void }) {
  return (
    <Card className="overflow-hidden p-0">
      <div className="flex items-center justify-between border-b border-border bg-muted/30 px-4 py-2">
        <span className="flex items-center gap-2 text-xs text-muted-foreground"><Icon name="config" size={13} /> node-config.yaml · advanced</span>
        <Button variant="outline" size="xs" onClick={onValidate} disabled={text == null}>Validate &amp; preview</Button>
      </div>
      <textarea
        spellCheck={false}
        value={text ?? "# loading…"}
        onChange={(e) => onChange(e.target.value)}
        className="h-[calc(100vh-20rem)] w-full resize-none bg-background/40 p-4 font-mono text-xs leading-relaxed text-foreground/90 focus:outline-none"
      />
    </Card>
  );
}

// ---------- Reconcile preview: the safety story -------------
// Server-authoritative: the grouping + plain-language summaries come from the
// node's reconcile planner (dry-run), and a rejected edit surfaces the node's own
// per-field validation problems — a bad edit never reaches the running node.
function ReconcilePreview({ open, result, problem, busy, onClose, onApply }: {
  open: boolean;
  result: ReconcileResult | null;
  problem: ValidationProblem | null;
  busy: boolean;
  onClose: () => void;
  onApply: () => void;
}) {
  const reset = result?.nodeReset ?? [];
  const hasReset = reset.length > 0;
  const canApply = !busy && problem == null && result != null;
  return (
    <Modal
      open={open}
      onClose={onClose}
      width="max-w-lg"
      title="Review & apply"
      footer={
        <>
          <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
          <Button size="sm" onClick={onApply} disabled={!canApply} className={hasReset ? "bg-danger text-danger-foreground hover:bg-danger/90" : undefined}>
            {busy ? "Working…" : hasReset ? <><Icon name="alert" size={14} /> Apply — resets the node</> : <><Icon name="check" size={14} /> Apply all at once</>}
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        {problem ? (
          <div className="rounded-lg border border-danger/30 bg-danger/5 p-3 text-danger">
            <p className="flex items-center gap-2 text-sm font-semibold"><Icon name="alert" size={14} /> {problem.errors.length} problem{problem.errors.length === 1 ? "" : "s"} — not applied</p>
            <ul className="mt-2 space-y-1">
              {problem.errors.map((e, i) => (
                <li key={i} className="text-[11px] text-foreground/80"><span className="font-mono text-danger/90">{e.path}</span> — {e.message}</li>
              ))}
            </ul>
          </div>
        ) : (
          <>
            <p className="text-sm text-muted-foreground">
              Your changes are checked before anything is applied — a bad edit never reaches the running node. Valid changes are then applied all at once.
            </p>
            {busy && result == null && <p className="text-sm text-muted-foreground">Checking…</p>}
            <ReconcileGroup variant="success" icon="check" title={`${result?.live.length ?? 0} apply live`} items={result?.live ?? []} desc="hot-applied while the node keeps running — nothing drops." />
            <ReconcileGroup variant="warning" icon="restart" title={`${result?.portRestart.length ?? 0} restart a port`} items={result?.portRestart ?? []} desc="the affected port bounces; sessions on it drop." />
            <ReconcileGroup variant="danger" icon="alert" title={`${reset.length} reset the node`} items={reset} desc="applies on a node restart — every session on every port drops." />
            {result != null && result.live.length + result.portRestart.length + reset.length === 0 && (
              <p className="text-sm text-muted-foreground">No effective changes.</p>
            )}
          </>
        )}
      </div>
    </Modal>
  );
}

function ReconcileGroup({ variant, icon, title, items, desc }: { variant: "success" | "warning" | "danger"; icon: string; title: string; items: ReconcileChange[]; desc: string }) {
  if (items.length === 0) return null;
  const c = {
    success: "border-success/30 bg-success/5 text-success",
    warning: "border-warning/30 bg-warning/5 text-warning",
    danger: "border-danger/30 bg-danger/5 text-danger",
  }[variant];
  return (
    <div className={cn("rounded-lg border p-3", c)}>
      <p className="flex items-center gap-2 text-sm font-semibold"><Icon name={icon} size={14} /> {title}</p>
      <p className="mt-0.5 text-xs opacity-80">{desc}</p>
      <ul className="mt-2 space-y-1">
        {items.map((i) => (
          <li key={i.path} className="text-[11px] text-foreground/80">{i.summary} <span className="font-mono text-foreground/40">({i.path})</span></li>
        ))}
      </ul>
    </div>
  );
}
