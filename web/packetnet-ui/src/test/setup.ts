import "@testing-library/jest-dom/vitest";
import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";

afterEach(() => cleanup());

// jsdom lacks these; the monitor (smooth-prepend rAF tween) and the session
// console (auto-scroll) touch them. Polyfill as harmless no-ops/timers.
if (typeof globalThis.requestAnimationFrame !== "function") {
  globalThis.requestAnimationFrame = (cb: FrameRequestCallback): number =>
    setTimeout(() => cb(performance.now()), 0) as unknown as number;
  globalThis.cancelAnimationFrame = (id: number): void => clearTimeout(id);
}
if (!Element.prototype.scrollTo) {
  Element.prototype.scrollTo = () => {};
}
if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = () => {};
}

// jsdom lacks ResizeObserver; the Console screen observes its terminal host to refit on
// resize. Polyfill a no-op so the screen mounts (no layout to observe in jsdom anyway).
if (typeof globalThis.ResizeObserver !== "function") {
  globalThis.ResizeObserver = class {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  } as unknown as typeof globalThis.ResizeObserver;
}

// jsdom lacks matchMedia; xterm.js (the Console screen's terminal) reads it on open to
// track device-pixel-ratio. Polyfill a never-matching stub so the terminal mounts in tests.
if (typeof globalThis.matchMedia !== "function") {
  globalThis.matchMedia = ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof globalThis.matchMedia;
}

// ---- recording 2D canvas context ----------------------------------------------------
// jsdom ships no canvas implementation at all: getContext logs a "Not implemented" stack
// trace and returns null, so screens/waterfall.tsx bails at `if (!ctx) return` and its whole
// paint path (clear, scroll, resample, putImageData) went untested while every waterfall
// mount spewed that trace. The stub below is deliberately a RECORDER, not a renderer: there
// are no pixels to look at in jsdom, so what a test can meaningfully assert is the sequence
// of drawing calls the screen made - see src/test/waterfall.paint.test.tsx. Only "2d" is
// answered; a caller asking for "webgl" gets the honest null a browser without it would give.
export type Recorded2DCall = { op: string; args: unknown[] };
export type RecordedImageData = { data: Uint8ClampedArray; width: number; height: number };

// The subset of CanvasRenderingContext2D the waterfall uses, plus the call log.
export interface Recording2DContext {
  readonly __calls: Recorded2DCall[];
  fillStyle: string;
  fillRect(x: number, y: number, w: number, h: number): void;
  drawImage(...args: unknown[]): void;
  createImageData(w: number, h: number): RecordedImageData;
  putImageData(image: RecordedImageData, dx: number, dy: number): void;
}

function recording2DContext(): Recording2DContext {
  const calls: Recorded2DCall[] = [];
  let fill = "#000000";
  return {
    __calls: calls,
    get fillStyle(): string { return fill; },
    set fillStyle(v: string) { fill = v; calls.push({ op: "fillStyle", args: [v] }); },
    fillRect(x, y, w, h) { calls.push({ op: "fillRect", args: [x, y, w, h] }); },
    drawImage(...args) { calls.push({ op: "drawImage", args }); },
    createImageData(w, h) {
      calls.push({ op: "createImageData", args: [w, h] });
      return { data: new Uint8ClampedArray(w * h * 4), width: w, height: h };
    },
    putImageData(image, dx, dy) { calls.push({ op: "putImageData", args: [image, dx, dy] }); },
  };
}

if (typeof HTMLCanvasElement !== "undefined") {
  // One context per canvas, as a browser does: the waterfall asks for the context once to
  // clear and again on every spectrum line, and a test reading __calls must see all of them
  // in order rather than a fresh empty log each time.
  const contexts = new WeakMap<HTMLCanvasElement, Recording2DContext>();
  HTMLCanvasElement.prototype.getContext = function (this: HTMLCanvasElement, id: string) {
    if (id !== "2d") return null;
    let ctx = contexts.get(this);
    if (!ctx) { ctx = recording2DContext(); contexts.set(this, ctx); }
    return ctx;
  } as typeof HTMLCanvasElement.prototype.getContext;
}
