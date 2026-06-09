// ============================================================
// pdn — port health derivation (pure).
// The dashboard + ports screens roll a port's PortStatus together with its
// LinkStats into a single good/degraded/faulted verdict. Kept pure (data in,
// verdict out) so it works identically against mock or live API data — the
// caller supplies the live PortStatus (api.ports) + LinkStats (api.linkStats).
// ============================================================
import type { PortStatus, LinkStats, PortHealth } from "./types";

export function portHealth(status: PortStatus | undefined, links: LinkStats[]): PortHealth {
  if (!status) return { level: "good" };
  if (status.state === "faulted") return { level: "faulted", reason: status.lastError || "port faulted" };
  const portLinks = links.filter((l) => l.portId === status.id);
  const bad = portLinks.find((l) => l.retries > 2 || l.rejCount + l.srejCount > 3 || l.smoothedRttMs > 1500);
  if (bad) return { level: "degraded", reason: `link to ${bad.peer} struggling — RTT ${bad.smoothedRttMs}ms, ${bad.retries} retries, ${bad.rejCount + bad.srejCount} REJ` };
  return { level: "good" };
}
