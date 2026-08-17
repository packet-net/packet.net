// ============================================================
// pdn — port health derivation (pure).
// The dashboard + ports screens roll a port's PortStatus together with its
// LinkStats into a single good/degraded/faulted verdict. Kept pure (data in,
// verdict out) so it works identically against mock or live API data — the
// caller supplies the live PortStatus (api.ports) + LinkStats (api.linkStats).
//
// The PORT's own health is the SERVER's, not a browser heuristic: the node runs a
// state model per port (packet-net/packet.net#722) and says whether it is serving,
// degraded (and which component is missing), faulted or retrying - with the reason.
// The LinkStats heuristic below stays, but only for what the server does not judge:
// how the LINKS on a healthy port are behaving (RTT / retries / REJ).
// ============================================================
import type { PortStatus, PortState, LinkStats, PortHealth } from "./types";

/** Whether the port is carrying traffic right now (the server's two serving states). */
export function portIsServing(state: PortState | undefined): boolean {
  return state === "up" || state === "degraded";
}

/** Map a server port state onto the StatusDot's four colours: green = on the air, amber =
 *  serving with a piece missing, red = not on the air and it should be, grey = by design. */
export function portDotState(state: PortState | undefined): "up" | "down" | "faulted" | "error" {
  switch (state) {
    case "up": return "up";
    case "degraded": return "faulted";
    case "faulted":
    case "retrying": return "error";
    default: return "down";   // configured / disabled / starting / stopping
  }
}

export function portHealth(status: PortStatus | undefined, links: LinkStats[]): PortHealth {
  if (!status) return { level: "good" };

  // Not serving at all: faulted / retrying / stopping / starting / configured / disabled.
  // `lastError` is the node's own reason and is populated now, so the fallback text is a
  // genuine last resort rather than the only thing the operator ever saw.
  if (status.state === "faulted" || status.state === "retrying") {
    return { level: "faulted", reason: status.lastError || `port ${status.state}` };
  }

  // Serving with a piece missing (radio / rig / rigctld / transport): the packet channel
  // still carries traffic, so this is degraded, not faulted.
  if (status.state === "degraded") {
    const missing = status.degraded.length > 0 ? status.degraded.join(", ") : "a component";
    return { level: "degraded", reason: status.lastError || `running without ${missing}` };
  }

  const portLinks = links.filter((l) => l.portId === status.id);
  const bad = portLinks.find((l) => l.retries > 2 || l.rejCount + l.srejCount > 3 || l.smoothedRttMs > 1500);
  if (bad) return { level: "degraded", reason: `link to ${bad.peer} struggling — RTT ${bad.smoothedRttMs}ms, ${bad.retries} retries, ${bad.rejCount + bad.srejCount} REJ` };
  return { level: "good" };
}
