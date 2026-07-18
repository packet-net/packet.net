// ============================================================
// Soundmodem waterfall (/tools/waterfall) — the modem setup/tuning view for
// `kind: soundmodem` ports: a scrolling spectrogram of the port's receive audio
// (SSE from /ports/{id}/spectrum/events), with a frequency axis and the classic
// use-case in mind: set audio levels and centre the signal by eye. Reads
// ?port=<id> from the URL; ports of other kinds 404 and show a hint instead.
// ============================================================
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Page, PageHeader } from "@/components/layout/shell";
import { Card, Field, Select } from "@/components/ui";
import { api, useQuery, subscribeSpectrum } from "@/lib/api";
import type { SoundModemQualitySnapshot } from "@/lib/types";

// Perceptual-ish blue→yellow→white ramp, dark-background native (the classic
// waterfall look); computed once into a 256-entry RGB LUT.
const PALETTE: [number, number, number][] = (() => {
  const lut: [number, number, number][] = [];
  for (let i = 0; i < 256; i++) {
    const x = i / 255;
    const r = Math.min(255, Math.floor(255 * Math.pow(x, 0.9) * 1.4 - 40));
    const g = Math.min(255, Math.floor(255 * Math.pow(x, 1.3)));
    const b = Math.floor(60 + 195 * Math.pow(1 - Math.abs(x - 0.35) / 0.65, 2));
    lut.push([Math.max(0, r), Math.max(0, g), Math.max(20, Math.min(255, b))]);
  }
  return lut;
})();

export function Waterfall() {
  const [searchParams] = useSearchParams();
  const { data: config } = useQuery(api.config, []);
  const ports = useMemo(() => config?.ports ?? [], [config]);
  const soundmodemPorts = useMemo(
    () => ports.filter((p) => (p.transport as { kind?: string } | undefined)?.kind === "soundmodem"),
    [ports],
  );
  const urlPort = searchParams.get("port");

  const [portId, setPortId] = useState("");
  const [binHz, setBinHz] = useState(12000 / 4096);
  const [status, setStatus] = useState<"connecting" | "live" | "unavailable">("connecting");
  const [quality, setQuality] = useState<SoundModemQualitySnapshot | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);

  // Poll the selected port's rolling receive-quality snapshot (a plain GET, unlike the spectrum SSE).
  // A 404 — the port isn't a running soundmodem — clears it, so the FrameQuality readout hides for a
  // non-soundmodem port exactly as the spectrum feed goes "unavailable". Cleared on unmount / port
  // change (the RemoteAccessSection poll-while-mounted pattern). 2 s cadence: frames arrive slowly and
  // the snapshot is cumulative, so a gentle poll keeps the FEC counters fresh without churn.
  useEffect(() => {
    if (!portId) { setQuality(null); return; }
    let alive = true;
    const tick = () => api.portQuality(portId)
      .then((q) => { if (alive) setQuality(q); })
      .catch(() => { if (alive) setQuality(null); });
    tick();
    const t = setInterval(tick, 2000);
    return () => { alive = false; clearInterval(t); };
  }, [portId]);

  useEffect(() => {
    if (portId) return;
    const candidates = soundmodemPorts.length > 0 ? soundmodemPorts : ports;
    const first = candidates[0]?.id ?? "";
    setPortId(urlPort && ports.some((p) => p.id === urlPort) ? urlPort : first);
  }, [ports, soundmodemPorts, urlPort, portId]);

  const drawLine = useCallback((bins: Uint8Array, hz: number) => {
    setBinHz(hz);
    setStatus("live");
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    // Scroll up one row, then paint the new line along the bottom, resampling the
    // bins onto the canvas width.
    const { width, height } = canvas;
    ctx.drawImage(canvas, 0, 1, width, height - 1, 0, 0, width, height - 1);
    const row = ctx.createImageData(width, 1);
    for (let x = 0; x < width; x++) {
      const bin = Math.min(bins.length - 1, Math.floor((x / width) * bins.length));
      const [r, g, b] = PALETTE[bins[bin]];
      row.data[x * 4] = r;
      row.data[x * 4 + 1] = g;
      row.data[x * 4 + 2] = b;
      row.data[x * 4 + 3] = 255;
    }
    ctx.putImageData(row, 0, height - 1);
  }, []);

  useEffect(() => {
    if (!portId) return;
    setStatus("connecting");
    const canvas = canvasRef.current;
    if (canvas) {
      const ctx = canvas.getContext("2d");
      if (ctx) {
        ctx.fillStyle = "#0a0e14";
        ctx.fillRect(0, 0, canvas.width, canvas.height);
      }
    }
    return subscribeSpectrum(portId, drawLine, () => setStatus("unavailable"));
  }, [portId, drawLine]);

  const spanHz = 2048 * binHz;
  const ticks = useMemo(() => {
    const list: { hz: number; left: string }[] = [];
    for (let hz = 500; hz < spanHz; hz += 500) {
      list.push({ hz, left: `${((hz / spanHz) * 100).toFixed(2)}%` });
    }
    return list;
  }, [spanHz]);

  return (
    <Page>
      <PageHeader
        title="Waterfall"
        subtitle="Live receive spectrum of a soundmodem port — set levels and centre the signal by eye."
      />
      <Card className="space-y-3 p-4">
        <div className="flex flex-wrap items-end gap-3">
          <Field label="Port">
            <Select value={portId} onChange={(e) => setPortId(e.target.value)}>
              {ports.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.id}
                  {(p.transport as { kind?: string } | undefined)?.kind === "soundmodem" ? "" : " (not a soundmodem)"}
                </option>
              ))}
            </Select>
          </Field>
          <div className="pb-2 text-xs text-muted-foreground">
            {status === "live" && `live · ${binHz.toFixed(2)} Hz/bin · 0–${Math.round(spanHz)} Hz`}
            {status === "connecting" && "connecting…"}
            {status === "unavailable" &&
              "no spectrum feed — this port is not a running soundmodem port"}
          </div>
        </div>

        <FrameQuality q={quality} />

        <div className="relative w-full overflow-hidden rounded-md border border-border">
          <canvas
            ref={canvasRef}
            width={1024}
            height={320}
            className="block h-80 w-full"
            style={{ imageRendering: "pixelated" }}
          />
          {/* Frequency grid + labels over the raster. */}
          <div className="pointer-events-none absolute inset-0">
            {ticks.map((t) => (
              <div key={t.hz} className="absolute inset-y-0" style={{ left: t.left }}>
                <div className="h-full w-px bg-white/10" />
                <span className="absolute top-0.5 left-1 text-[10px] leading-none text-white/50">
                  {t.hz >= 1000 ? `${(t.hz / 1000).toFixed(1)}k` : t.hz}
                </span>
              </div>
            ))}
          </div>
        </div>

        <p className="text-xs text-muted-foreground">
          Lines arrive at the modem&rsquo;s FFT cadence (≈3/s). Brightness is receive level
          (dB scale); a healthy packet signal shows as bright bursts inside the modem&rsquo;s
          passband with visible noise floor elsewhere — no floor means the RX level is too
          low, a solid white band means it&rsquo;s clipping.
        </p>
      </Card>
    </Page>
  );
}

// Compact per-frame receive-quality readout for a soundmodem port (GET /ports/{id}/quality, #635):
// frames decoded, FEC-corrected frames + the cumulative corrected-byte count (the early-warning
// number — persistently climbing means the link is spending its FEC budget before frames drop), and
// the most-recent frame's mode / frequency offset / emphasis. Renders nothing when there's no
// snapshot (non-soundmodem / not-running port, cleared on a 404) and a muted "no frames yet" when the
// port is up but nothing has decoded. Frequency offset / emphasis are only shown when present (a
// multi-decoder bank's winning branch; null for single decoders).
function FrameQuality({ q }: { q: SoundModemQualitySnapshot | null }) {
  if (!q) return null;
  const last = q.recent[0] ?? null;
  const sign = (n: number) => (n > 0 ? "+" : "");
  return (
    <div className="flex flex-wrap items-center gap-x-5 gap-y-1.5 rounded-md border border-border bg-muted/30 px-3 py-2 text-xs">
      <span className="font-semibold text-foreground">Frame quality</span>
      {q.frames === 0 ? (
        <span className="text-muted-foreground/70">no frames yet</span>
      ) : (
        <>
          <QStat label="decoded" value={q.frames.toLocaleString()} />
          <QStat label="FEC-corrected" value={`${q.framesWithCorrections.toLocaleString()} frames · ${q.cumulativeCorrectedBytes.toLocaleString()} B`} />
          {last && (
            <span className="flex items-center gap-1.5">
              <span className="text-muted-foreground">last</span>
              <span className="font-mono text-foreground">{last.mode}</span>
              {last.frequencyOffsetHz != null && (
                <span className="font-mono text-muted-foreground">{sign(last.frequencyOffsetHz)}{last.frequencyOffsetHz.toFixed(1)} Hz</span>
              )}
              {last.emphasisDb != null && (
                <span className="font-mono text-muted-foreground">{sign(last.emphasisDb)}{last.emphasisDb} dB</span>
              )}
            </span>
          )}
        </>
      )}
    </div>
  );
}

function QStat({ label, value }: { label: string; value: string }) {
  return (
    <span className="flex items-center gap-1.5">
      <span className="text-muted-foreground">{label}</span>
      <span className="tnum font-mono font-semibold text-foreground">{value}</span>
    </span>
  );
}
