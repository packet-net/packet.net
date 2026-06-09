// ============================================================
// pdn UI primitives — TS port of the design handoff's pdn/ui.jsx (shadcn-
// flavoured Tailwind components). Faithful to the target screenshots; tokens
// come from index.css. Icons are lucide via ../icon.
// ============================================================
import { useEffect, useRef, useState, useCallback, type ReactNode, type ButtonHTMLAttributes, type InputHTMLAttributes, type SelectHTMLAttributes, type LabelHTMLAttributes, type TdHTMLAttributes } from "react";
import { createPortal } from "react-dom";
import { cn } from "@/lib/utils";
import { Icon, type IconName } from "@/components/icon";

// ---- Button ------------------------------------------------
export type ButtonVariant = "default" | "secondary" | "outline" | "ghost" | "destructive" | "link";
export type ButtonSize = "default" | "sm" | "xs" | "lg" | "icon" | "iconSm";
export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
}
const BTN_VARIANTS: Record<ButtonVariant, string> = {
  default: "bg-primary text-primary-foreground hover:bg-primary/90 shadow-sm",
  secondary: "bg-secondary text-secondary-foreground hover:bg-secondary/80",
  outline: "border border-input bg-transparent hover:bg-accent hover:text-accent-foreground",
  ghost: "hover:bg-accent hover:text-accent-foreground",
  destructive: "bg-danger text-danger-foreground hover:bg-danger/90 shadow-sm",
  link: "text-primary underline-offset-4 hover:underline",
};
const BTN_SIZES: Record<ButtonSize, string> = {
  default: "h-9 px-4 py-2 text-sm",
  sm: "h-8 px-3 text-xs",
  xs: "h-7 px-2 text-xs",
  lg: "h-10 px-6 text-sm",
  icon: "h-9 w-9",
  iconSm: "h-8 w-8",
};
export function Button({ variant = "default", size = "default", className, children, ...rest }: ButtonProps) {
  return (
    <button
      className={cn(
        "inline-flex items-center justify-center gap-1.5 whitespace-nowrap rounded-md font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1 focus-visible:ring-offset-background disabled:pointer-events-none disabled:opacity-50",
        BTN_VARIANTS[variant], BTN_SIZES[size], className,
      )}
      {...rest}
    >
      {children}
    </button>
  );
}

// ---- Badge -------------------------------------------------
export type BadgeVariant = "default" | "secondary" | "outline" | "muted" | "success" | "warning" | "danger";
const BADGE_VARIANTS: Record<BadgeVariant, string> = {
  default: "border-transparent bg-primary/15 text-primary",
  secondary: "border-transparent bg-secondary text-secondary-foreground",
  outline: "text-foreground",
  muted: "border-transparent bg-muted text-muted-foreground",
  success: "border-transparent bg-success/15 text-success",
  warning: "border-transparent bg-warning/15 text-warning",
  danger: "border-transparent bg-danger/15 text-danger",
};
export function Badge({ variant = "default", className, children }: { variant?: BadgeVariant; className?: string; children: ReactNode }) {
  return <span className={cn("inline-flex items-center rounded-md border px-1.5 py-0.5 text-[11px] font-semibold leading-none", BADGE_VARIANTS[variant], className)}>{children}</span>;
}

// frame-type → colour by AX.25 class
export function FrameBadge({ type, classKind }: { type: string; classKind: "I" | "U" | "S" }) {
  const map: Record<string, string> = {
    I: "bg-primary/15 text-primary",
    U: "bg-violet-500/15 text-violet-500 dark:text-violet-400",
    S: "bg-amber-500/15 text-amber-600 dark:text-amber-400",
  };
  const special: Record<string, string> = {
    UI: "bg-emerald-500/15 text-emerald-600 dark:text-emerald-400",
    FRMR: "bg-danger/15 text-danger", REJ: "bg-danger/15 text-danger", SREJ: "bg-danger/15 text-danger",
  };
  const c = special[type] || map[classKind] || "bg-muted text-muted-foreground";
  return <span className={cn("inline-flex min-w-[42px] justify-center rounded px-1.5 py-0.5 font-mono text-[11px] font-semibold leading-none", c)}>{type}</span>;
}

// ---- Card --------------------------------------------------
export function Card({ className, children, ...rest }: { className?: string; children: ReactNode } & React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("rounded-lg border border-border bg-card text-card-foreground shadow-sm", className)} {...rest}>{children}</div>;
}
export function CardHeader({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn("flex flex-col space-y-1 p-4 pb-2", className)}>{children}</div>;
}
export function CardTitle({ className, children }: { className?: string; children: ReactNode }) {
  return <h3 className={cn("text-sm font-semibold leading-none tracking-tight", className)}>{children}</h3>;
}
export function CardContent({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn("p-4 pt-2", className)}>{children}</div>;
}

// ---- Status dot --------------------------------------------
export function StatusDot({ state, live }: { state: "up" | "down" | "faulted" | "error"; live?: boolean }) {
  const c = { up: "bg-success", down: "bg-muted-foreground", faulted: "bg-warning", error: "bg-danger" }[state] || "bg-muted-foreground";
  return <span className={cn("inline-block h-2 w-2 rounded-full", c, live && state === "up" && "live-dot")} />;
}

// ---- Inputs ------------------------------------------------
export function Input({ className, ...rest }: InputHTMLAttributes<HTMLInputElement>) {
  return <input className={cn("flex h-9 w-full rounded-md border border-input bg-background/60 px-3 py-1 text-sm shadow-sm transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:border-ring disabled:cursor-not-allowed disabled:opacity-50", className)} {...rest} />;
}
export function Label({ className, children, ...rest }: LabelHTMLAttributes<HTMLLabelElement> & { children: ReactNode }) {
  return <label className={cn("text-xs font-medium text-muted-foreground", className)} {...rest}>{children}</label>;
}
export function Field({ label, hint, impact, info, badge, className, children }: {
  label: ReactNode; hint?: ReactNode; impact?: ApplyImpactKind; info?: string; badge?: ReactNode; className?: string; children: ReactNode;
}) {
  return (
    <div className={cn("space-y-1.5", className)}>
      <div className="flex items-center justify-between gap-2">
        <span className="flex items-center gap-1.5">
          <Label>{label}</Label>
          {info && <InfoHint text={info} />}
        </span>
        {badge ? badge : impact ? <ImpactBadge impact={impact} /> : null}
      </div>
      {children}
      {hint && <p className="text-[11px] text-muted-foreground">{hint}</p>}
    </div>
  );
}

// ---- Tooltip (hover/focus; panel PORTALED to <body>) -------
// The panel is rendered into document.body with fixed positioning rather than
// as an absolutely-positioned descendant, so it can't be clipped or stacked
// under an ancestor's overflow / stacking context — e.g. the info hints in a
// table heading, where the table lives in an `overflow-hidden` Card wrapping an
// `overflow-x-auto` scroll box. Shown on hover OR focus (focus = tap on touch).
export function Tooltip({ text, children, className }: { text: ReactNode; children: ReactNode; className?: string }) {
  const ref = useRef<HTMLSpanElement>(null);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);

  const show = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    setPos({ top: r.top, left: r.left + r.width / 2 });
  }, []);
  const hide = useCallback(() => setPos(null), []);

  return (
    <span
      ref={ref}
      className={cn("relative inline-flex", className)}
      tabIndex={0}
      onMouseEnter={show}
      onMouseLeave={hide}
      onFocus={show}
      onBlur={hide}
    >
      {children}
      {pos !== null && createPortal(
        <span
          role="tooltip"
          style={{ position: "fixed", top: pos.top, left: pos.left, transform: "translate(-50%, calc(-100% - 6px))" }}
          className="pointer-events-none z-[100] w-64 max-w-[calc(100vw-1rem)] rounded-md border border-border bg-popover px-2.5 py-1.5 text-[11px] font-normal leading-snug text-popover-foreground shadow-lg"
        >
          {text}
        </span>,
        document.body,
      )}
    </span>
  );
}
export function InfoHint({ text }: { text: ReactNode }) {
  return <Tooltip text={text}><Icon name="info" size={13} className="cursor-help text-muted-foreground/60 hover:text-foreground" /></Tooltip>;
}

// ---- Slider (filled track) ---------------------------------
export function Slider({ value, min = 0, max = 255, step = 1, onChange }: { value: number; min?: number; max?: number; step?: number; onChange: (v: number) => void }) {
  const pct = Math.round(((value - min) / (max - min)) * 100);
  return (
    <input type="range" min={min} max={max} step={step} value={value} onChange={(e) => onChange(+e.target.value)}
      className="h-2 w-full cursor-pointer appearance-none rounded-full outline-none"
      style={{ accentColor: "hsl(var(--primary))", background: `linear-gradient(to right, hsl(var(--primary)) ${pct}%, hsl(var(--muted)) ${pct}%)` }} />
  );
}

// ---- Select (native + chevron) -----------------------------
export function Select({ className, children, ...rest }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <div className="relative">
      <select className={cn("flex h-9 w-full appearance-none rounded-md border border-input bg-background/60 pl-3 pr-8 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring", className)} {...rest}>{children}</select>
      <Icon name="chevDown" size={14} className="pointer-events-none absolute right-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
    </div>
  );
}

// ---- Switch ------------------------------------------------
export function Switch({ checked, onChange }: { checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <button type="button" onClick={() => onChange(!checked)} role="switch" aria-checked={checked}
      className={cn("relative inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring", checked ? "bg-primary" : "bg-input")}>
      <span className={cn("inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform", checked ? "translate-x-4" : "translate-x-0.5")} />
    </button>
  );
}

// ---- Impact badge ------------------------------------------
export type ApplyImpactKind = "live" | "port-restart" | "node-reset";
export function ImpactBadge({ impact }: { impact: ApplyImpactKind }) {
  const map: Record<ApplyImpactKind, { v: BadgeVariant; label: string }> = {
    "live": { v: "success", label: "live" },
    "port-restart": { v: "warning", label: "port restart" },
    "node-reset": { v: "danger", label: "node reset" },
  };
  const m = map[impact];
  if (!m) return null;
  return <Badge variant={m.v}>{m.label}</Badge>;
}

// ---- Quality bar (NET/ROM 0..255) --------------------------
export function QualityBar({ value }: { value: number }) {
  const pct = Math.round((value / 255) * 100);
  const color = value >= 180 ? "bg-success" : value >= 100 ? "bg-warning" : "bg-danger";
  return (
    <div className="flex items-center gap-2">
      <div className="h-1.5 w-16 overflow-hidden rounded-full bg-muted">
        <div className={cn("h-full rounded-full", color)} style={{ width: pct + "%" }} />
      </div>
      <span className="tnum w-7 text-right font-mono text-xs text-muted-foreground">{value}</span>
    </div>
  );
}

// ---- Table helpers -----------------------------------------
export function Th({ className, children }: { className?: string; children?: ReactNode }) {
  return <th className={cn("h-9 px-3 text-left align-middle text-[11px] font-semibold uppercase tracking-wide text-muted-foreground", className)}>{children}</th>;
}
export function Td({ className, children, ...rest }: TdHTMLAttributes<HTMLTableCellElement> & { children?: ReactNode }) {
  return <td className={cn("px-3 py-2 align-middle text-sm", className)} {...rest}>{children}</td>;
}

// ---- Tabs (segmented control) ------------------------------
export function Tabs({ tabs, active, onChange, className }: { tabs: { id: string; label: string }[]; active: string; onChange: (id: string) => void; className?: string }) {
  return (
    <div className={cn("inline-flex h-9 items-center gap-1 rounded-lg bg-muted p-1", className)}>
      {tabs.map((t) => (
        <button key={t.id} onClick={() => onChange(t.id)}
          className={cn("inline-flex h-7 items-center rounded-md px-3 text-xs font-medium transition-colors", active === t.id ? "bg-background text-foreground shadow-sm" : "text-muted-foreground hover:text-foreground")}>{t.label}</button>
      ))}
    </div>
  );
}

// ---- Sheet / Drawer (right side) ---------------------------
export function Sheet({ open, onClose, title, subtitle, children, footer, width = "max-w-xl" }: {
  open: boolean; onClose: () => void; title: ReactNode; subtitle?: ReactNode; children: ReactNode; footer?: ReactNode; width?: string;
}) {
  useEffect(() => {
    const h = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    if (open) window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, [open, onClose]);
  return (
    <div className={cn("fixed inset-0 z-50 transition", open ? "pointer-events-auto" : "pointer-events-none")}>
      <div className={cn("absolute inset-0 bg-black/50 transition-opacity", open ? "opacity-100" : "opacity-0")} onClick={onClose} />
      <div className={cn("absolute right-0 top-0 flex h-full w-full flex-col border-l border-border bg-card shadow-2xl transition-transform duration-300", width, open ? "translate-x-0" : "translate-x-full")}>
        <div className="flex items-start justify-between border-b border-border p-4">
          <div>
            <h2 className="text-base font-semibold">{title}</h2>
            {subtitle && <p className="mt-0.5 text-xs text-muted-foreground">{subtitle}</p>}
          </div>
          <Button variant="ghost" size="iconSm" onClick={onClose}><Icon name="x" /></Button>
        </div>
        <div className="flex-1 overflow-y-auto p-4">{children}</div>
        {footer && <div className="flex items-center justify-end gap-2 border-t border-border bg-muted/30 p-4">{footer}</div>}
      </div>
    </div>
  );
}

// ---- Modal -------------------------------------------------
export function Modal({ open, onClose, title, children, footer, width = "max-w-md" }: {
  open: boolean; onClose: () => void; title: ReactNode; children: ReactNode; footer?: ReactNode; width?: string;
}) {
  useEffect(() => {
    const h = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    if (open) window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, [open, onClose]);
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/50" onClick={onClose} />
      <div className={cn("relative w-full rounded-xl border border-border bg-card shadow-2xl", width)}>
        <div className="flex items-center justify-between border-b border-border p-4">
          <h2 className="text-base font-semibold">{title}</h2>
          <Button variant="ghost" size="iconSm" onClick={onClose}><Icon name="x" /></Button>
        </div>
        <div className="p-4">{children}</div>
        {footer && <div className="flex items-center justify-end gap-2 border-t border-border p-4">{footer}</div>}
      </div>
    </div>
  );
}

// ---- Empty state -------------------------------------------
export function EmptyState({ icon, title, body }: { icon?: IconName; title: ReactNode; body?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center rounded-lg border border-dashed border-border py-12 text-center">
      <div className="mb-3 grid h-10 w-10 place-items-center rounded-full bg-muted text-muted-foreground"><Icon name={icon || "info"} /></div>
      <p className="text-sm font-medium">{title}</p>
      {body && <p className="mt-1 max-w-xs text-xs text-muted-foreground">{body}</p>}
    </div>
  );
}

export { Icon } from "@/components/icon";
