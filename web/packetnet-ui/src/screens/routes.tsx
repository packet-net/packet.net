// ============================================================
// pdn — NET/ROM routes (§6): Destinations / Neighbours tabs, quality bars,
// INP3 measured time, per-row Ping + Connect. Read-only.
// Ported from the handoff screens-net.jsx.
// ============================================================
import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Page, PageHeader } from "@/components/layout/shell";
import {
  Button, Badge, Card, QualityBar, Th, Td, Tabs, InfoHint, EmptyState, Icon,
} from "@/components/ui";
import { PingButton } from "@/components/ping";
import { api, useQuery } from "@/lib/api";
import type { NetRomRoutingSnapshot } from "@/lib/types";

export function Routes() {
  const navigate = useNavigate();
  const { data, loading, error } = useQuery(api.routes);
  const [tab, setTab] = useState<"destinations" | "neighbours">("destinations");

  const subtitle = data
    ? `The network view — quality and INP3 time, side by side · updated ${new Date(data.generatedAt).toLocaleTimeString("en-GB", { hour12: false })}`
    : "The network view — quality and INP3 time, side by side";

  return (
    <Page>
      <PageHeader
        title="NET/ROM routes"
        subtitle={subtitle}
        actions={
          <Tabs
            active={tab}
            onChange={(id) => setTab(id as "destinations" | "neighbours")}
            tabs={[
              { id: "destinations", label: `Destinations · ${data?.destinations.length ?? 0}` },
              { id: "neighbours", label: `Neighbours · ${data?.neighbours.length ?? 0}` },
            ]}
          />
        }
      />

      {error && <EmptyState icon="alert" title="Couldn't load routes" body={error} />}
      {!error && loading && !data && (
        <div className="py-10 text-center text-sm text-muted-foreground">Loading routes…</div>
      )}
      {!error && data && (tab === "neighbours"
        ? <NeighboursTable data={data} />
        : <DestinationsTable data={data} navigate={navigate} />
      )}
    </Page>
  );
}

function NeighboursTable({ data }: { data: NetRomRoutingSnapshot }) {
  return (
    <Card className="overflow-hidden p-0">
      <div className="overflow-x-auto">
        <table className="w-full border-collapse">
          <thead>
            <tr className="border-b border-border">
              <Th>Neighbour</Th>
              <Th>Alias</Th>
              <Th>Port</Th>
              <Th>
                <span className="inline-flex items-center gap-1">
                  Path quality
                  <InfoHint text="Link quality to this directly-heard neighbour, 0–255. Higher is better — a blend of how reliably and directly you hear each other." />
                </span>
              </Th>
              <Th className="text-right">Last heard</Th>
              <Th className="w-px" />
            </tr>
          </thead>
          <tbody>
            {data.neighbours.map((n) => (
              <tr key={n.neighbour} className="border-b border-border/60 hover:bg-accent/40">
                <Td className="font-mono font-semibold">{n.neighbour}</Td>
                <Td className="font-mono text-xs text-muted-foreground">{n.alias}</Td>
                <Td><Badge variant="muted">{n.portId}</Badge></Td>
                <Td><QualityBar value={n.pathQuality} /></Td>
                <Td className="text-right font-mono text-xs text-muted-foreground">{n.lastHeard}</Td>
                <Td>
                  <div className="flex items-center justify-end">
                    <PingButton station={n.neighbour} portId={n.portId} />
                  </div>
                </Td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}

function DestinationsTable({ data, navigate }: {
  data: NetRomRoutingSnapshot;
  navigate: (to: string) => void;
}) {
  // Resolve the port the best route's neighbour is heard on (fall back to the
  // first neighbour's port) — used for the per-row Ping + Connect hand-off.
  const portOfNeighbour = (neighbour: string): string => {
    const nb = data.neighbours.find((x) => x.neighbour === neighbour);
    return nb?.portId ?? data.neighbours[0]?.portId ?? "";
  };

  return (
    <Card className="overflow-hidden p-0">
      <div className="overflow-x-auto">
        <table className="w-full border-collapse">
          <thead>
            <tr className="border-b border-border">
              <Th>Destination</Th>
              <Th>Alias</Th>
              <Th>Best via</Th>
              <Th>
                <span className="inline-flex items-center gap-1">
                  Quality
                  <InfoHint text="NET/ROM path quality, 0–255 (higher is better) — how good this route is judged to be overall. Routes below the node's minimum quality are ignored." />
                </span>
              </Th>
              <Th className="text-right">
                <span className="inline-flex items-center justify-end gap-1">
                  Obsol.
                  <InfoHint text="Obsolescence — a freshness countdown. Each routing sweep that doesn't re-hear the route ticks it down; hearing it again refreshes it to the top. At zero the route is dropped. High = recently confirmed, low = going stale." />
                </span>
              </Th>
              <Th className="text-right">
                <span className="inline-flex items-center justify-end gap-1">
                  INP3 time
                  <InfoHint text="INP3's actual measured round-trip time to the destination, in milliseconds. Unlike quality (a static score), this is timed live — so pdn can prefer the genuinely faster route when it's available." />
                </span>
              </Th>
              <Th className="hidden text-right sm:table-cell">
                <span className="inline-flex items-center justify-end gap-1">
                  Hops
                  <InfoHint text="How many NET/ROM nodes the traffic crosses to reach the destination (reported by INP3)." />
                </span>
              </Th>
              <Th className="w-px" />
            </tr>
          </thead>
          <tbody>
            {data.destinations.map((d) => {
              const r = d.routes[d.bestRoute];
              const viaPort = portOfNeighbour(r.neighbour);
              const altCount = d.routes.length - 1;
              return (
                <tr key={d.destination} className="border-b border-border/60 hover:bg-accent/40">
                  <Td className="font-mono font-semibold">{d.destination}</Td>
                  <Td className="font-mono text-xs text-muted-foreground">{d.alias}</Td>
                  <Td className="font-mono text-xs">
                    {r.neighbour}
                    {altCount > 0 && <span className="ml-1 text-muted-foreground">+{altCount}</span>}
                  </Td>
                  <Td><QualityBar value={r.quality} /></Td>
                  <Td className="tnum text-right font-mono text-xs text-muted-foreground">{r.obsolescence}</Td>
                  <Td className="text-right font-mono text-xs">
                    {r.inp3
                      ? <span className="text-primary">{r.inp3.targetTimeMs}ms</span>
                      : <span className="text-muted-foreground/50">—</span>}
                  </Td>
                  <Td className="hidden text-right font-mono text-xs text-muted-foreground sm:table-cell">
                    {r.inp3 ? `${r.inp3.hopCount}h` : "—"}
                  </Td>
                  <Td>
                    <div className="flex items-center justify-end gap-1">
                      <PingButton station={r.neighbour} portId={viaPort} />
                      <Button
                        variant="ghost"
                        size="xs"
                        onClick={() => navigate(`/sessions?connect=${encodeURIComponent(d.destination)}&port=${encodeURIComponent(viaPort)}`)}
                      >
                        <Icon name="link" size={13} /> Connect
                      </Button>
                    </div>
                  </Td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <div className="flex flex-wrap items-center gap-4 border-t border-border bg-muted/20 px-4 py-2.5 text-[11px] text-muted-foreground">
        <span className="flex items-center gap-1.5"><span className="h-2 w-3 rounded-sm bg-success" />quality ≥180 (good)</span>
        <span className="flex items-center gap-1.5"><span className="h-2 w-3 rounded-sm bg-warning" />100–179 (ok)</span>
        <span className="flex items-center gap-1.5"><span className="h-2 w-3 rounded-sm bg-danger" />&lt;100 (poor)</span>
        <span className="ml-auto flex items-center gap-1.5">
          <span className="font-mono text-primary">INP3 time</span> = measured round-trip target (preferred when present)
        </span>
      </div>
    </Card>
  );
}
