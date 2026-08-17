// ============================================================
// pdn — Console: a browser xterm.js terminal wired to the node's OWN sysop command
// console (NodeCommandService) — the telnet-equivalent shell where you type node
// commands (ports, nodes, connect, …). This is NOT the per-AX.25-session console the
// Sessions screen exposes; it's the node's command processor, admin-gated.
//
// Lifecycle: on mount POST /api/v1/console → id; create an xterm Terminal (FitAddon,
// dark theme); subscribe GET /console/{id}/stream (EventSource) → write output into the
// terminal; on terminal onData → POST /console/{id}/input. On unmount: DELETE the
// console + close the stream + dispose the terminal. A closed stream shows a reconnect state.
// ============================================================
import { useEffect, useRef, useState, type CSSProperties } from "react";
import "@xterm/xterm/css/xterm.css";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { Page } from "@/components/layout/shell";
import { Button, Card, Icon } from "@/components/ui";
import { api, subscribeConsoleOutput } from "@/lib/api";
import { useAuth } from "@/app/auth";

// xterm wants concrete colours (not CSS hsl() vars), so mirror the app's dark theme
// (src/index.css .dark) as hex. Kept deliberately close to the panel chrome so the
// terminal reads as part of the surface, not a foreign black box.
const TERMINAL_THEME = {
  background: "#0d121c", // --card 220 23% 9.5%
  foreground: "#dce4ee", // --foreground 210 22% 92%
  cursor: "#38bdf8", // --primary 199 89% 52%
  cursorAccent: "#0d121c",
  selectionBackground: "#1d2735",
  black: "#0d121c",
  brightBlack: "#5a6678",
  red: "#e15252",
  brightRed: "#f08a8a",
  green: "#3fc77f",
  brightGreen: "#6fdca0",
  yellow: "#f0a92e",
  brightYellow: "#f7c668",
  blue: "#38bdf8",
  brightBlue: "#7dd3fc",
  magenta: "#c084fc",
  brightMagenta: "#d8b4fe",
  cyan: "#22d3ee",
  brightCyan: "#67e8f9",
  white: "#dce4ee",
  brightWhite: "#ffffff",
} as const;

type Phase = "connecting" | "open" | "closed" | "error";

export function Console() {
  const { has } = useAuth();
  const canOpen = has("admin"); // the node command console is admin-gated server-side
  const containerRef = useRef<HTMLDivElement>(null);
  // The live terminal, so a tap on the host can refocus it (iOS won't raise the soft
  // keyboard — and won't deliver keystrokes — unless xterm's input textarea has focus).
  const termRef = useRef<Terminal | null>(null);
  const [phase, setPhase] = useState<Phase>("connecting");
  const [error, setError] = useState<string | null>(null);
  // Bumped to force a fresh session (the Reconnect button), re-running the effect.
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!canOpen) { setPhase("error"); setError("The node command console requires the admin scope."); return; }
    const host = containerRef.current;
    if (!host) return;

    setPhase("connecting");
    setError(null);

    // Build the terminal up front so output can flow the instant the stream opens.
    const term = new Terminal({
      convertEol: false, // the node emits CR-LF (telnet transport) — let xterm handle it natively
      cursorBlink: true,
      fontFamily:
        'ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, "Liberation Mono", monospace',
      fontSize: 12,
      theme: TERMINAL_THEME,
      scrollback: 10000, // generous history; scroll the viewport (touch-drag on mobile) to review
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(host);
    termRef.current = term;

    // Size the terminal's columns to the actual host width so long lines WRAP instead of
    // running off the edge. A bad fit (too many cols) makes lines overflow the card (which
    // clips them) rather than wrap — the iPhone "lines cut off" symptom, where iOS text
    // auto-sizing also inflated the glyphs past what xterm measured (see the host's
    // text-size-adjust pin).
    const refit = () => { try { fit.fit(); } catch { /* zero-size / mid-teardown */ } };

    // Fill the terminal down to the visible bottom — and on iOS shrink it above the on-screen
    // keyboard. On iOS (Safari AND Chrome — both WebKit) the layout viewport doesn't change when
    // the keyboard opens; only window.visualViewport shrinks. So we measure the visible bottom
    // from it and set the host height to what's left below the terminal's top (down to a usable
    // minimum), reclaiming the now-trimmed header space and keeping the input line above the
    // keyboard. NOTE: this runs from window/visualViewport events, NOT the ResizeObserver —
    // setting the host height from inside the RO callback would feed back into the observer
    // (resize loop). The RO only refits (width/cols), never sets height.
    const MIN_H = 140;        // keep a few lines + the input visible even with the keyboard up
    const BOTTOM_GAP = 16;    // breathing room above the visible bottom / keyboard (covers page padding)
    const resize = () => {
      const vv = window.visualViewport;
      const top = host.getBoundingClientRect().top;
      const visibleBottom = vv ? vv.offsetTop + vv.height : window.innerHeight;
      const avail = visibleBottom - top - BOTTOM_GAP;
      host.style.height = `${Math.max(MIN_H, avail)}px`;
      refit();
    };
    resize();
    requestAnimationFrame(resize); // let the layout settle before the first real measure

    // Refit on every box change: container resize (panel/layout) refits only; window + iOS
    // visual-viewport (keyboard) resizes also re-evaluate the height.
    const ro = new ResizeObserver(refit);
    ro.observe(host);
    window.addEventListener("resize", resize);
    window.visualViewport?.addEventListener("resize", resize);
    window.visualViewport?.addEventListener("scroll", resize);

    let id: string | null = null;
    let unsubscribe: (() => void) | null = null;
    let disposed = false;

    void (async () => {
      try {
        id = await api.openConsole();
        if (disposed) { void api.closeConsole(id); return; }
        setPhase("open");
        term.focus();

        // Stream the console's output (banner/prompt/responses) into the terminal. The
        // node JSON-encodes each chunk so embedded CR/LF survive — subscribeConsoleOutput
        // hands us the decoded text; write it verbatim (xterm renders the CR-LF).
        //
        // The node replays the console's whole backlog on EVERY subscription, including the
        // silent reconnect the stream helper performs after a token rotation or a node
        // restart. It arrives as a `backlog` event, and onReset fires just before it is
        // written: reset the terminal so the replay REPLACES the history instead of
        // appending a second copy of it (review item C045, #689).
        unsubscribe = subscribeConsoleOutput(
          id,
          (chunk) => term.write(chunk),
          () => { if (!disposed) setPhase("closed"); },
          { onReset: () => term.reset() },
        );

        // Forward every keystroke to the node verbatim (its LineAssembler splits on the CR
        // xterm sends on Enter). A failed send (the session was closed) flips to the closed
        // state.
        //
        // LOCAL ECHO. The node does NOT echo typed characters: NodeCommandService
        // line-assembles and only replies to whole lines — a real telnet/RF client supplies
        // its own local echo, and xterm does none by default. So we echo here, or nothing the
        // user types is visible (the command still ran, just invisibly — this is what made the
        // console look dead on iPhone). Purely cosmetic: the node stays the source of truth for
        // the line. We do NOT echo server-side, which would double-echo every real telnet/RF
        // sysop. `col` bounds Backspace so it can't erase back into the prompt.
        //
        // ORDERING. Each keystroke is its own POST, and POST /console/{id}/input answers 202
        // "queued" - nothing on the wire or on the server sequences two in-flight requests. Typed
        // at speed the node received "OPRTS" for "PORTS" and answered "Unknown command" (found by
        // the live-smoke driver, #692). The sends are chained through one promise per console so
        // the next only leaves after the previous has been accepted; a keystroke is small and the
        // node answers immediately, so this costs nothing a typist can feel.
        let col = 0;
        let sendChain: Promise<void> = Promise.resolve();
        term.onData((data) => {
          if (!id) return;
          const consoleId = id;   // the narrowing does not survive into the deferred send
          sendChain = sendChain
            .then(() => api.consoleInput(consoleId, data))
            .catch(() => { if (!disposed) setPhase("closed"); });
          for (const ch of data) {
            if (ch === "\r" || ch === "\n") { term.write("\r\n"); col = 0; }
            else if (ch === "\x7f" || ch === "\b") { if (col > 0) { term.write("\b \b"); col -= 1; } }
            else if (ch === "\x03") { term.write("^C\r\n"); col = 0; } // Ctrl-C: visible + fresh line
            else if (ch >= " ") { term.write(ch); col += 1; }          // printable (control seqs are sent, not echoed)
          }
        });
      } catch (e) {
        if (disposed) return;
        setPhase("error");
        setError(String((e as Error)?.message ?? e) || "Could not open the console.");
      }
    })();

    return () => {
      disposed = true;
      ro.disconnect();
      window.removeEventListener("resize", resize);
      window.visualViewport?.removeEventListener("resize", resize);
      window.visualViewport?.removeEventListener("scroll", resize);
      unsubscribe?.();
      if (id) void api.closeConsole(id);
      termRef.current = null;
      term.dispose();
    };
  }, [attempt, canOpen]);

  return (
    <Page>
      {error && (
        <div className="mb-2 flex items-start gap-2 rounded-md border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">
          <Icon name="alert" size={15} className="mt-px shrink-0" />
          <span className="flex-1">{error}</span>
        </div>
      )}

      {/* Dense layout: no page title/blurb (the nav already says Console). One thin strip carries
          the status + a Reconnect (only when there's nothing to type into), then the terminal fills
          the rest of the screen. */}
      <Card className="overflow-hidden p-0">
        <div className="flex items-center justify-between border-b border-border bg-muted/30 px-2.5 py-1">
          <span className="flex items-center gap-1.5 font-mono text-[11px] leading-none text-muted-foreground">
            <Icon name="console" size={12} />
            {phase === "connecting" && "connecting…"}
            {phase === "open" && <span className="text-success">connected</span>}
            {phase === "closed" && <span className="text-warning">closed</span>}
            {phase === "error" && <span className="text-danger">unavailable</span>}
          </span>
          {(phase === "closed" || phase === "error") && (
            <Button size="sm" className="h-6 px-2 text-xs" onClick={() => setAttempt((a) => a + 1)} disabled={!canOpen}>
              <Icon name="restart" size={12} /> Reconnect
            </Button>
          )}
        </div>
        {/* The xterm host. Its height is set imperatively (see resize()) so it fills to the visible
            bottom / shrinks above the iOS keyboard; the terminal owns its own scrollback. */}
        <div
          ref={containerRef}
          data-testid="console-terminal"
          className="h-[28rem] w-full overflow-hidden bg-[#0d121c] p-1.5"
          // textSizeAdjust: stop iOS Safari inflating the monospace glyphs (it otherwise
          // renders them wider than xterm measured, so FitAddon picks too many columns and
          // long lines overflow/clip instead of wrapping). Inherited, so the host covers the
          // xterm DOM beneath it.
          style={{ textSizeAdjust: "100%", WebkitTextSizeAdjust: "100%" } as CSSProperties}
          // Tap-to-focus: on iOS the soft keyboard only appears (and keystrokes only
          // arrive) when xterm's hidden textarea has focus; a tap on the host refocuses it.
          onPointerDown={() => termRef.current?.focus()}
        />
      </Card>
    </Page>
  );
}
