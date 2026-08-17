// KISS TXDELAY / TXTAIL / SLOTTIME are single BYTES in units of 10 ms on the wire — the KISS
// protocol's own units, and `byte?` on the server (NodeConfig.Ports.cs). The panel showed and
// stored MILLISECONDS: its preset baselines and the old KISS_DEFAULTS carried 300/150/100/50, its
// field labels said "ms", and nothing converted. So a port created with the stock defaults - or
// from the "VHF FM · 1200 AFSK" preset - POSTed `txDelay: 300` into a byte and the request failed
// model binding with an opaque 400, before any validator could say why. The sub-256 presets bound
// fine but meant 10x what the label promised (txDelay 150 = 1.5 s, not 150 ms).
//
// These tests pin the contract on both sides of the conversion: the catalogue is stored in wire
// units, and the editor field converts to/from ms for display.
import { describe, it, expect } from "vitest";
import { RADIO_PROFILES, msToTenMs, tenMsToMs } from "@/lib/catalogue";
import { ENGINE_AX25_DEFAULTS, ENGINE_KISS_DEFAULTS } from "@/screens/ports";

const TIMERS = ["txDelay", "slotTime", "txTail"] as const;

describe("KISS timing params are stored in wire units, not milliseconds", () => {
  it("the engine defaults the editor shows as placeholders match the engine", () => {
    // What the node does with a null block (PortSupervisor.MapAx25Params + the implicit TXTAIL 0
    // of ApplyKissParamsToModemAsync; Ax25SessionParameters' own null semantics). The editor shows
    // these rather than WRITING a set of UI-invented values over a port that had none (#690 C005).
    expect(ENGINE_AX25_DEFAULTS).toEqual({ t1Ms: 6000, t2Ms: 3000, t3Ms: 30000, n2: 10, windowSize: 4, n1: 256 });
    // TXDELAY / PERSIST / SLOTTIME are opt-in - unset leaves the modem on its own default - while
    // TXTAIL has an implicit 0 sent on every apply.
    expect(ENGINE_KISS_DEFAULTS.txTail).toBe("0");
    for (const k of ["txDelay", "slotTime", "persistence"] as const) {
      expect(ENGINE_KISS_DEFAULTS[k], `ENGINE_KISS_DEFAULTS.${k}`).toBe("modem default");
    }
  });

  it("every radio preset baseline fits the byte the wire carries", () => {
    for (const profile of RADIO_PROFILES) {
      for (const k of TIMERS) {
        const v = profile.baseline[k];
        expect(v, `${profile.id}.${k}`).toBeGreaterThanOrEqual(0);
        expect(v, `${profile.id}.${k}`).toBeLessThanOrEqual(255);
      }
    }
    // The preset that broke it: VHF FM 1200 wants a 300 ms TX delay = 30 units.
    expect(RADIO_PROFILES.find((p) => p.id === "vhf-fm-1200")!.baseline.txDelay).toBe(30);
  });

  it("converts between the operator's milliseconds and the wire's 10 ms units", () => {
    expect(msToTenMs(300)).toBe(30);
    expect(tenMsToMs(30)).toBe(300);
    // Round-trips for every value the field can hold.
    for (let units = 0; units <= 255; units++) {
      expect(msToTenMs(tenMsToMs(units))).toBe(units);
    }
  });

  it("clamps to the range KISS can express rather than posting an out-of-range byte", () => {
    // 3000 ms is longer than TXDELAY can say (255 units = 2.55 s) — clamp, don't overflow,
    // because overflowing is what produced the unexplained 400.
    expect(msToTenMs(3000)).toBe(255);
    expect(msToTenMs(-50)).toBe(0);
    expect(msToTenMs(304)).toBe(30);   // nearest unit
    expect(msToTenMs(305)).toBe(31);
  });
});
