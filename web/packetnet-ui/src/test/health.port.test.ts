import { describe, expect, it } from "vitest";
import { portDotState, portHealth, portIsServing } from "@/lib/health";
import type { LinkStats, PortStatus } from "@/lib/types";

// The port badge is the SERVER's verdict now (packet-net/packet.net#722): the node keeps a
// state model per port and says degraded / faulted / retrying with the reason. The browser's
// LinkStats heuristic survives only for what the server does not judge - how the links on a
// healthy port are behaving.

const port = (over: Partial<PortStatus> = {}): PortStatus => ({
  id: "vhf", enabled: true, state: "up", sessionCount: 0, lastError: null,
  framesIn: 0, framesOut: 0, degraded: [], since: "2026-08-17T12:00:00+00:00", channelBusy: null,
  ...over,
});

const struggling: LinkStats[] = [
  { portId: "vhf", peer: "M0LTE", smoothedRttMs: 2400, retries: 4, rejCount: 5, srejCount: 1, framesIn: 10, framesOut: 9 },
];

describe("port health comes from the node's state model", () => {
  it("an up port with healthy links is good", () => {
    expect(portHealth(port(), [])).toEqual({ level: "good" });
  });

  it("a degraded port reports the components the node says are missing, not a link guess", () => {
    const h = portHealth(port({ state: "degraded", degraded: ["radio"], lastError: "radio (tait-ccdi): open failed" }), []);
    expect(h.level).toBe("degraded");
    expect(h.reason).toContain("open failed");
  });

  it("a faulted port carries the node's own reason - lastError is populated now", () => {
    const h = portHealth(port({ state: "faulted", lastError: "serial: /dev/ttyUSB1 not present" }), []);
    expect(h).toEqual({ level: "faulted", reason: "serial: /dev/ttyUSB1 not present" });
  });

  it("a retrying port is faulted-level: it is not on the air", () => {
    expect(portHealth(port({ state: "retrying", lastError: "device busy" }), []).level).toBe("faulted");
  });

  it("keeps the link heuristic for a serving port whose links are struggling", () => {
    const h = portHealth(port(), struggling);
    expect(h.level).toBe("degraded");
    expect(h.reason).toContain("M0LTE");
  });

  it("does not let the link heuristic downgrade a port the node calls faulted", () => {
    const h = portHealth(port({ state: "faulted", lastError: "device busy" }), struggling);
    expect(h.reason).toBe("device busy");
  });

  it("knows which states are serving, and paints them", () => {
    expect(portIsServing("up")).toBe(true);
    expect(portIsServing("degraded")).toBe(true);
    expect(portIsServing("retrying")).toBe(false);
    expect(portIsServing("disabled")).toBe(false);

    expect(portDotState("up")).toBe("up");
    expect(portDotState("degraded")).toBe("faulted");
    expect(portDotState("faulted")).toBe("error");
    expect(portDotState("configured")).toBe("down");
  });
});
