// ============================================================
// pdn — shadcn-flavoured UI primitives + app shell
// ============================================================
const { useState, useEffect, useRef, useCallback, createContext, useContext } = React;

function cn(...a){ return a.filter(Boolean).join(" "); }

// ---- Icons (Lucide-style line glyphs) ----------------------
const ICON = {
  dashboard: "M3 13h8V3H3v10Zm0 8h8v-6H3v6Zm10 0h8V11h-8v10Zm0-18v6h8V3h-8Z",
  monitor: "M2 12h4l3 8 4-16 3 8h6",
  sessions: "M8 3 4 7l4 4M4 7h11a5 5 0 0 1 5 5v0M16 21l4-4-4-4m4 4H9a5 5 0 0 1-5-5v0",
  routes: "M6 3v12M6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM18 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm0 0v6a3 3 0 0 1-3 3H9",
  ports: "M4 4h16v6H4zM4 14h16v6H4M8 7h.01M8 17h.01",
  config: "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2Z",
  configGear2: "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z",
  users: "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8ZM22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75",
  sun: "M12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10ZM12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42",
  moon: "M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79Z",
  chevDown: "m6 9 6 6 6-6",
  chevRight: "m9 18 6-6-6-6",
  x: "M18 6 6 18M6 6l12 12",
  plus: "M5 12h14M12 5v14",
  search: "M21 21l-4.35-4.35M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16Z",
  pause: "M6 4h4v16H6zM14 4h4v16h-4z",
  play: "m5 3 14 9-14 9V3z",
  trash: "M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2",
  power: "M12 2v10M18.36 6.64a9 9 0 1 1-12.73 0",
  restart: "M3 12a9 9 0 1 0 9-9 9 9 0 0 0-6.36 2.64L3 8M3 3v5h5",
  arrowDown: "M12 5v14M19 12l-7 7-7-7",
  arrowUp: "M12 19V5M5 12l7-7 7 7",
  alert: "M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0ZM12 9v4M12 17h.01",
  check: "M20 6 9 17l-5-5",
  link: "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71",
  radio: "M4.93 19.07a10 10 0 0 1 0-14.14M7.76 16.24a6 6 0 0 1 0-8.48M16.24 7.76a6 6 0 0 1 0 8.48M19.07 4.93a10 10 0 0 1 0 14.14M12 13a1 1 0 1 0 0-2 1 1 0 0 0 0 2Z",
  send: "m22 2-7 20-4-9-9-4 20-7Z",
  copy: "M20 9H11a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h9a2 2 0 0 0 2-2v-9a2 2 0 0 0-2-2ZM5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1",
  filter: "M22 3H2l8 9.46V19l4 2v-8.54L22 3Z",
  menu: "M3 12h18M3 6h18M3 18h18",
  key: "M21 2l-2 2m-3.5 3.5a5 5 0 1 1-7 7 5 5 0 0 1 7-7Zm0 0L19 4m-1 1 2 2",
  fingerprint: "M12 10a2 2 0 0 0-2 2c0 1.02-.1 2.51-.26 4M4 13a8 8 0 0 1 8-8c2.5 0 4.5 1 6 2.5M2 16c.5-1 1-2 1-4M14 13.12c0 2.38 0 6.38-1 8.88M17.29 21.02c.12-.6.43-2.3.5-3.02M12 12c0 2.4 0 6.4-2 9",
  download: "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3",
  external: "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6M15 3h6v6M10 14 21 3",
  info: "M12 16v-4M12 8h.01M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0Z",
  signal: "M2 20h.01M7 20v-4M12 20v-8M17 20V8M22 4v16",
};

function Icon({ name, className, size = 16, fill }) {
  const d = ICON[name];
  if (!d) return null;
  const paths = d.split("M").filter(Boolean).map((seg,i)=>(
    <path key={i} d={"M"+seg} />
  ));
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill={fill||"none"} stroke="currentColor"
      strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round"
      className={className} aria-hidden="true">{paths}</svg>
  );
}

// ---- Button ------------------------------------------------
function Button({ variant = "default", size = "default", className, children, ...rest }) {
  const variants = {
    default: "bg-primary text-primary-foreground hover:bg-primary/90 shadow-sm",
    secondary: "bg-secondary text-secondary-foreground hover:bg-secondary/80",
    outline: "border border-input bg-transparent hover:bg-accent hover:text-accent-foreground",
    ghost: "hover:bg-accent hover:text-accent-foreground",
    destructive: "bg-danger text-danger-foreground hover:bg-danger/90 shadow-sm",
    link: "text-primary underline-offset-4 hover:underline",
  };
  const sizes = {
    default: "h-9 px-4 py-2 text-sm",
    sm: "h-8 px-3 text-xs",
    xs: "h-7 px-2 text-xs",
    lg: "h-10 px-6 text-sm",
    icon: "h-9 w-9",
    iconSm: "h-8 w-8",
  };
  return (
    <button className={cn("inline-flex items-center justify-center gap-1.5 whitespace-nowrap rounded-md font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1 focus-visible:ring-offset-background disabled:pointer-events-none disabled:opacity-50", variants[variant], sizes[size], className)} {...rest}>{children}</button>
  );
}

// ---- Badge -------------------------------------------------
function Badge({ variant = "default", className, children }) {
  const variants = {
    default: "border-transparent bg-primary/15 text-primary",
    secondary: "border-transparent bg-secondary text-secondary-foreground",
    outline: "text-foreground",
    muted: "border-transparent bg-muted text-muted-foreground",
    success: "border-transparent bg-success/15 text-success",
    warning: "border-transparent bg-warning/15 text-warning",
    danger: "border-transparent bg-danger/15 text-danger",
  };
  return <span className={cn("inline-flex items-center rounded-md border px-1.5 py-0.5 text-[11px] font-semibold leading-none", variants[variant], className)}>{children}</span>;
}

// frame-type → badge color by AX.25 class
function FrameBadge({ type, classKind }) {
  const map = { I: "bg-primary/15 text-primary", U: "bg-violet-500/15 text-violet-500 dark:text-violet-400", S: "bg-amber-500/15 text-amber-600 dark:text-amber-400" };
  const special = { UI: "bg-emerald-500/15 text-emerald-600 dark:text-emerald-400", FRMR: "bg-danger/15 text-danger", REJ: "bg-danger/15 text-danger", SREJ: "bg-danger/15 text-danger" };
  const c = special[type] || map[classKind] || "bg-muted text-muted-foreground";
  return <span className={cn("inline-flex min-w-[42px] justify-center rounded px-1.5 py-0.5 font-mono text-[11px] font-semibold leading-none", c)}>{type}</span>;
}

// ---- Card --------------------------------------------------
function Card({ className, children, ...rest }) {
  return <div className={cn("rounded-lg border border-border bg-card text-card-foreground shadow-sm", className)} {...rest}>{children}</div>;
}
function CardHeader({ className, children }) { return <div className={cn("flex flex-col space-y-1 p-4 pb-2", className)}>{children}</div>; }
function CardTitle({ className, children }) { return <h3 className={cn("text-sm font-semibold leading-none tracking-tight", className)}>{children}</h3>; }
function CardContent({ className, children }) { return <div className={cn("p-4 pt-2", className)}>{children}</div>; }

// ---- Status dot --------------------------------------------
function StatusDot({ state, live }) {
  const c = { up: "bg-success", down: "bg-muted-foreground", faulted: "bg-warning", error: "bg-danger" }[state] || "bg-muted-foreground";
  return <span className={cn("inline-block h-2 w-2 rounded-full", c, live && state==='up' && "live-dot")} />;
}

// ---- Inputs ------------------------------------------------
function Input({ className, ...rest }) {
  return <input className={cn("flex h-9 w-full rounded-md border border-input bg-background/60 px-3 py-1 text-sm shadow-sm transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:border-ring disabled:cursor-not-allowed disabled:opacity-50", className)} {...rest} />;
}
function Label({ className, children, ...rest }) {
  return <label className={cn("text-xs font-medium text-muted-foreground", className)} {...rest}>{children}</label>;
}
function Field({ label, hint, impact, info, badge, className, children }) {
  return (
    <div className={cn("space-y-1.5", className)}>
      <div className="flex items-center justify-between gap-2">
        <span className="flex items-center gap-1.5">
          <Label>{label}</Label>
          {info && <InfoHint text={info} />}
        </span>
        {badge ? badge : (impact && <ImpactBadge impact={impact} />)}
      </div>
      {children}
      {hint && <p className="text-[11px] text-muted-foreground">{hint}</p>}
    </div>
  );
}

// hover tooltip (also shows on focus); CSS-driven so it survives screenshots
function Tooltip({ text, children, className }) {
  return (
    <span className={cn("group/tip relative inline-flex", className)} tabIndex={0}>
      {children}
      <span role="tooltip" className="pointer-events-none absolute bottom-full left-1/2 z-50 mb-1.5 w-64 -translate-x-1/2 rounded-md border border-border bg-popover px-2.5 py-1.5 text-[11px] font-normal leading-snug text-popover-foreground opacity-0 shadow-lg transition-opacity duration-150 group-hover/tip:opacity-100 group-focus/tip:opacity-100">{text}</span>
    </span>
  );
}
function InfoHint({ text }) {
  return <Tooltip text={text}><Icon name="info" size={13} className="cursor-help text-muted-foreground/60 hover:text-foreground" /></Tooltip>;
}

// range slider with a filled track
function Slider({ value, min = 0, max = 255, step = 1, onChange }) {
  const pct = Math.round(((value - min) / (max - min)) * 100);
  return (
    <input type="range" min={min} max={max} step={step} value={value} onChange={e=>onChange(+e.target.value)}
      className="h-2 w-full cursor-pointer appearance-none rounded-full outline-none"
      style={{ accentColor: "hsl(var(--primary))", background: `linear-gradient(to right, hsl(var(--primary)) ${pct}%, hsl(var(--muted)) ${pct}%)` }} />
  );
}
function Select({ className, children, ...rest }) {
  return (
    <div className="relative">
      <select className={cn("flex h-9 w-full appearance-none rounded-md border border-input bg-background/60 pl-3 pr-8 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring", className)} {...rest}>{children}</select>
      <Icon name="chevDown" size={14} className="pointer-events-none absolute right-2.5 top-1/2 -translate-y-1/2 text-muted-foreground" />
    </div>
  );
}
function Switch({ checked, onChange }) {
  return (
    <button type="button" onClick={()=>onChange(!checked)} role="switch" aria-checked={checked}
      className={cn("relative inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring", checked ? "bg-primary" : "bg-input")}>
      <span className={cn("inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform", checked ? "translate-x-4" : "translate-x-0.5")} />
    </button>
  );
}

// impact badge: live / port-restart / node-reset
function ImpactBadge({ impact }) {
  const map = {
    "live": { v: "success", label: "live" },
    "port-restart": { v: "warning", label: "port restart" },
    "node-reset": { v: "danger", label: "node reset" },
  };
  const m = map[impact]; if (!m) return null;
  return <Badge variant={m.v}>{m.label}</Badge>;
}

// ---- Quality bar (NET/ROM 0..255) --------------------------
function QualityBar({ value }) {
  const pct = Math.round((value/255)*100);
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
function Th({ className, children }) { return <th className={cn("h-9 px-3 text-left align-middle text-[11px] font-semibold uppercase tracking-wide text-muted-foreground", className)}>{children}</th>; }
function Td({ className, children, ...rest }) { return <td className={cn("px-3 py-2 align-middle text-sm", className)} {...rest}>{children}</td>; }

// ---- Tabs --------------------------------------------------
function Tabs({ tabs, active, onChange, className }) {
  return (
    <div className={cn("inline-flex h-9 items-center gap-1 rounded-lg bg-muted p-1", className)}>
      {tabs.map(t => (
        <button key={t.id} onClick={()=>onChange(t.id)}
          className={cn("inline-flex h-7 items-center rounded-md px-3 text-xs font-medium transition-colors", active===t.id ? "bg-background text-foreground shadow-sm" : "text-muted-foreground hover:text-foreground")}>{t.label}</button>
      ))}
    </div>
  );
}

// ---- Sheet / Drawer (right side) ---------------------------
function Sheet({ open, onClose, title, subtitle, children, footer, width = "max-w-xl" }) {
  useEffect(() => {
    const h = (e) => { if (e.key === "Escape") onClose(); };
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
function Modal({ open, onClose, title, children, footer, width = "max-w-md" }) {
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

// ---- Logo --------------------------------------------------
function Logo({ size = 28 }) {
  return (
    <div className="flex items-center gap-2">
      <div className="grid place-items-center rounded-md bg-primary text-primary-foreground" style={{ width: size, height: size }}>
        <Icon name="radio" size={size*0.62} />
      </div>
      <span className="font-mono text-[15px] font-semibold tracking-tight">pdn</span>
    </div>
  );
}

// ---- Theme toggle ------------------------------------------
function ThemeToggle() {
  const [dark, setDark] = useState(() => document.documentElement.classList.contains("dark"));
  const toggle = () => {
    const next = !dark;
    document.documentElement.classList.toggle("dark", next);
    setDark(next);
  };
  return (
    <Button variant="ghost" size="iconSm" onClick={toggle} title="Toggle theme">
      <Icon name={dark ? "sun" : "moon"} size={16} />
    </Button>
  );
}

// ---- App shell (sidebar + topbar) --------------------------
const NAV = [
  { id: "dashboard", label: "Dashboard", icon: "dashboard" },
  { id: "monitor", label: "Monitor", icon: "monitor" },
  { id: "sessions", label: "Sessions", icon: "sessions" },
  { id: "routes", label: "Routes", icon: "routes" },
  { id: "ports", label: "Ports", icon: "ports" },
  { id: "config", label: "Config", icon: "config" },
  { id: "users", label: "Users", icon: "users" },
];

function Shell({ route, onNavigate, onLogout, children }) {
  const [mobileNav, setMobileNav] = useState(false);
  const s = NODE_STATUS;
  return (
    <div className="flex h-screen w-full overflow-hidden bg-background">
      {/* sidebar */}
      <aside className={cn("absolute z-40 flex h-full w-60 flex-col border-r border-border bg-card transition-transform md:static md:translate-x-0", mobileNav ? "translate-x-0" : "-translate-x-full")}>
        <div className="flex h-14 items-center justify-between border-b border-border px-4">
          <Logo />
          <Button variant="ghost" size="iconSm" className="md:hidden" onClick={()=>setMobileNav(false)}><Icon name="x" /></Button>
        </div>
        <nav className="flex-1 space-y-0.5 overflow-y-auto p-2">
          {NAV.map(item => (
            <button key={item.id} onClick={()=>{ onNavigate(item.id); setMobileNav(false); }}
              className={cn("flex w-full items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors", route===item.id ? "bg-primary/10 text-primary" : "text-muted-foreground hover:bg-accent hover:text-foreground")}>
              <Icon name={item.icon} size={17} />
              {item.label}
            </button>
          ))}
        </nav>
        <div className="border-t border-border p-3">
          <div className="flex items-center gap-2 rounded-md px-2 py-1.5 text-xs text-muted-foreground">
            <Icon name="info" size={14} />
            <span className="font-mono">{s.version}</span>
          </div>
        </div>
      </aside>

      {/* main column */}
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-14 shrink-0 items-center justify-between border-b border-border bg-card/60 px-4 backdrop-blur">
          <div className="flex items-center gap-3">
            <Button variant="ghost" size="iconSm" className="md:hidden" onClick={()=>setMobileNav(true)}><Icon name="menu" /></Button>
            <div className="flex items-center gap-2">
              <StatusDot state="up" live />
              <span className="font-mono text-sm font-semibold">{s.callsign}</span>
              <span className="hidden text-xs text-muted-foreground sm:inline">· {s.alias} · {s.grid}</span>
            </div>
          </div>
          <div className="flex items-center gap-1">
            <div className="mr-2 hidden items-center gap-1.5 text-xs text-muted-foreground sm:flex">
              <span className="h-1.5 w-1.5 rounded-full bg-success live-dot" />
              up {fmtUptime(s.uptimeSeconds)}
            </div>
            <ThemeToggle />
            <UserMenu onLogout={onLogout} />
          </div>
        </header>
        <main className="flex-1 overflow-y-auto">
          <div key={route} data-screen className="mx-auto max-w-[1400px] p-4 sm:p-6">{children}</div>
        </main>
      </div>
    </div>
  );
}

function UserMenu({ onLogout }) {
  const [open, setOpen] = useState(false);
  const ref = useRef(null);
  useEffect(() => {
    const h = (e) => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
    document.addEventListener("mousedown", h);
    return () => document.removeEventListener("mousedown", h);
  }, []);
  return (
    <div className="relative" ref={ref}>
      <button onClick={()=>setOpen(o=>!o)} className="grid h-8 w-8 place-items-center rounded-full bg-primary/15 text-xs font-semibold text-primary">to</button>
      {open && (
        <div className="absolute right-0 top-10 z-50 w-48 rounded-lg border border-border bg-popover p-1 shadow-lg">
          <div className="px-3 py-2">
            <p className="text-sm font-medium">tom</p>
            <p className="text-xs text-muted-foreground">admin</p>
          </div>
          <div className="my-1 h-px bg-border" />
          <button onClick={onLogout} className="flex w-full items-center gap-2 rounded-md px-3 py-1.5 text-sm text-muted-foreground hover:bg-accent hover:text-foreground"><Icon name="power" size={14} /> Sign out</button>
        </div>
      )}
    </div>
  );
}

// page header used by screens
function PageHeader({ title, subtitle, actions }) {
  return (
    <div className="mb-5 flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
        {subtitle && <p className="mt-1 text-sm text-muted-foreground">{subtitle}</p>}
      </div>
      {actions && <div className="flex items-center gap-2">{actions}</div>}
    </div>
  );
}

function EmptyState({ icon, title, body }) {
  return (
    <div className="flex flex-col items-center justify-center rounded-lg border border-dashed border-border py-12 text-center">
      <div className="mb-3 grid h-10 w-10 place-items-center rounded-full bg-muted text-muted-foreground"><Icon name={icon||"info"} /></div>
      <p className="text-sm font-medium">{title}</p>
      {body && <p className="mt-1 max-w-xs text-xs text-muted-foreground">{body}</p>}
    </div>
  );
}

Object.assign(window, {
  cn, Icon, Button, Badge, FrameBadge, Card, CardHeader, CardTitle, CardContent,
  StatusDot, Input, Label, Field, Select, Switch, ImpactBadge, QualityBar,
  Th, Td, Tabs, Sheet, Modal, Logo, ThemeToggle, Shell, PageHeader, EmptyState, NAV,
  Tooltip, InfoHint, Slider,
});
