// The soundmodem waterfall's PAINT path (#692 C033). Everything below the "if (!ctx) return"
// guard in screens/waterfall.tsx used to be dead under vitest: jsdom has no canvas, getContext
// returned null, and the screen quietly drew nothing while the suite still passed. src/test/setup.ts
// now installs a recording 2D context, so the drawing calls themselves are the assertion.
//
// The contract this pins is the scrolling spectrogram: on every spectrum line, scroll the raster up
// by one row (drawImage of the canvas onto itself, shifted), build a 1-pixel-tall row the width of
// the canvas, and put it at the bottom. One line in, one row out - a regression that dropped the
// scroll, painted the wrong row, or stopped resampling the bins would show here.
//
// Time is faked throughout: the mock feed (lib/mock.ts driveSpectrumStream) is a 330 ms
// window.setInterval, so the timers must be faked BEFORE the mount that opens it, and the whole
// test then costs no wall-clock time at all.
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, act } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "@/app/auth";
import { Waterfall } from "@/screens/waterfall";
import type { Recording2DContext, RecordedImageData } from "./setup";

// The mock backend's own constants - deterministic, which is the point of driving them by hand.
const MOCK_GET_MS = 60;            // api.ts's mock `get` sleep; what GET /config costs here
const SPECTRUM_INTERVAL_MS = 330;  // mock.driveSpectrumStream's setInterval cadence

// The canvas is authored at a fixed raster size in waterfall.tsx (CSS scales it).
const CANVAS_W = 1024;
const CANVAS_H = 320;

function mountWaterfall(): void {
  render(
    <MemoryRouter initialEntries={["/tools/waterfall"]}>
      <AuthProvider><Waterfall /></AuthProvider>
    </MemoryRouter>,
  );
}

// The recorder the screen drew into. Same object for the life of the canvas (see setup.ts), so
// the clear from the subscribe effect and the rows from the feed share one call log.
function recorder(): Recording2DContext {
  const canvas = document.querySelector("canvas");
  expect(canvas, "the waterfall renders a canvas").not.toBeNull();
  return canvas!.getContext("2d") as unknown as Recording2DContext;
}

// Let the mock GET /config land, which seeds the port id, which runs the subscribe effect.
async function mountAndSettle(): Promise<Recording2DContext> {
  vi.useFakeTimers();
  mountWaterfall();
  await act(async () => { await vi.advanceTimersByTimeAsync(MOCK_GET_MS); });
  return recorder();
}

async function feedLines(n: number): Promise<void> {
  await act(async () => { await vi.advanceTimersByTimeAsync(SPECTRUM_INTERVAL_MS * n); });
}

afterEach(() => { vi.useRealTimers(); });

describe("Waterfall paints the spectrum", () => {
  it("clears the raster to the dark background when it subscribes, before any line arrives", async () => {
    const ctx = await mountAndSettle();

    // The screen's own background colour, then a full-canvas clear - a stale raster from the
    // previously selected port must not survive a port change.
    expect(ctx.__calls[0]).toEqual({ op: "fillStyle", args: ["#0a0e14"] });
    expect(ctx.__calls[1]).toEqual({ op: "fillRect", args: [0, 0, CANVAS_W, CANVAS_H] });
    // Nothing is painted speculatively: no row exists until the feed produces one.
    expect(ctx.__calls.filter((c) => c.op === "putImageData")).toHaveLength(0);
    expect(screen.getByText(/connecting/i)).toBeInTheDocument();
  });

  it("paints exactly one bottom row per spectrum line, scrolling the raster up first", async () => {
    const ctx = await mountAndSettle();
    const LINES = 4;
    await feedLines(LINES);

    // The whole call log after the two-call clear: scroll, build a row, put it down - once per line.
    expect(ctx.__calls.slice(2).map((c) => c.op)).toEqual(
      Array.from({ length: LINES }, () => ["drawImage", "createImageData", "putImageData"]).flat(),
    );

    for (const call of ctx.__calls.filter((c) => c.op === "drawImage")) {
      // Source is the canvas one row down; destination is the top-left, one row shorter: the
      // whole raster shifted up by a row, which is what makes it a waterfall.
      expect(call.args[0]).toBe(document.querySelector("canvas"));
      expect(call.args.slice(1)).toEqual([0, 1, CANVAS_W, CANVAS_H - 1, 0, 0, CANVAS_W, CANVAS_H - 1]);
    }

    for (const call of ctx.__calls.filter((c) => c.op === "putImageData")) {
      const [image, dx, dy] = call.args as [RecordedImageData, number, number];
      // One pixel tall, the full width of the raster, laid along the bottom edge.
      expect(image.width).toBe(CANVAS_W);
      expect(image.height).toBe(1);
      expect(dx).toBe(0);
      expect(dy).toBe(CANVAS_H - 1);
    }

    // And the screen stops saying "connecting": drawLine is what flips the status to live and
    // publishes the bin width the axis is drawn from (12000/4096 Hz).
    expect(screen.getByText(/2\.93 Hz\/bin/)).toBeInTheDocument();
  });

  it("resamples the bins onto the row: opaque pixels, the mock's carrier brighter than the floor", async () => {
    const ctx = await mountAndSettle();
    await feedLines(1);

    const [row] = ctx.__calls.find((c) => c.op === "putImageData")!.args as [RecordedImageData];
    expect(row.data).toHaveLength(CANVAS_W * 4);

    // Every pixel is written, not just the ones the palette happens to light up - an unwritten
    // pixel is transparent black, which would punch a hole through the row.
    const alphaEverywhere = Array.from({ length: CANVAS_W }, (_, x) => row.data[x * 4 + 3]);
    expect(alphaEverywhere.every((a) => a === 255)).toBe(true);

    // The mock feed puts a strong carrier at ~2.2 kHz over a 30-55 noise floor, and the palette
    // is monotonic in red, so the resampled row must show that contrast. A row painted from a
    // constant (or from the wrong end of the bins) would flatten it.
    const reds = Array.from({ length: CANVAS_W }, (_, x) => row.data[x * 4]);
    expect(Math.max(...reds)).toBeGreaterThan(200);
    expect(reds[0]).toBeLessThan(80);
  });
});
