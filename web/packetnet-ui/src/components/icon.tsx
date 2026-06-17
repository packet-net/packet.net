// Maps the design handoff's semantic icon names to lucide-react glyphs, so
// ported screen code keeps using <Icon name="monitor" />. lucide is the
// production icon set the handoff specifies.
import {
  LayoutDashboard, Activity, ArrowLeftRight, Network, Server, Settings, Users,
  Sun, Moon, ChevronDown, ChevronRight, X, Plus, Search, Pause, Play, Trash2,
  Power, RotateCw, ArrowDown, ArrowUp, TriangleAlert, Check, Link as LinkIcon,
  Radio, Send, Copy, Filter, Menu, KeyRound, Fingerprint, Download, ExternalLink,
  Info, Signal, LayoutGrid, AppWindow, SquareTerminal, Pencil, Gauge,
  icons as lucideIcons, type LucideIcon,
} from "lucide-react";

const MAP: Record<string, LucideIcon> = {
  dashboard: LayoutDashboard,
  monitor: Activity,
  sessions: ArrowLeftRight,
  routes: Network,
  ports: Server,
  config: Settings,
  configGear2: Settings,
  users: Users,
  apps: LayoutGrid,
  sun: Sun,
  moon: Moon,
  chevDown: ChevronDown,
  chevRight: ChevronRight,
  x: X,
  plus: Plus,
  search: Search,
  pause: Pause,
  play: Play,
  trash: Trash2,
  power: Power,
  restart: RotateCw,
  arrowDown: ArrowDown,
  arrowUp: ArrowUp,
  alert: TriangleAlert,
  check: Check,
  link: LinkIcon,
  radio: Radio,
  send: Send,
  copy: Copy,
  filter: Filter,
  menu: Menu,
  key: KeyRound,
  fingerprint: Fingerprint,
  download: Download,
  external: ExternalLink,
  info: Info,
  signal: Signal,
  console: SquareTerminal,
  edit: Pencil,
  gauge: Gauge,
};

export interface IconProps {
  name: keyof typeof MAP | string;
  size?: number;
  className?: string;
  fill?: string;
  strokeWidth?: number;
}

export function Icon({ name, size = 16, className, fill, strokeWidth = 1.75 }: IconProps) {
  const Glyph = MAP[name];
  if (!Glyph) return null;
  return <Glyph size={size} className={className} strokeWidth={strokeWidth} fill={fill ?? "none"} aria-hidden />;
}

export type IconName = keyof typeof MAP;

// Resolve a kebab-case lucide-react icon name ("message-square") to its registry
// key ("MessageSquare"). lucide's `icons` registry is keyed by PascalCase.
function kebabToPascal(name: string): string {
  return name.split(/[-_]/).filter(Boolean).map((p) => p.charAt(0).toUpperCase() + p.slice(1)).join("");
}

// Renders a lucide icon chosen at runtime by its (kebab-case) name — the shape the
// app platform's GET /api/v1/apps sends. Unknown / null / absent names fall back to a
// generic app-window glyph, so a registered app always shows *something*. (The fixed
// <Icon> above is for the app's own semantic icon set; this is for arbitrary
// backend-supplied names.)
export function AppIcon({ name, size = 16, className, strokeWidth = 1.75 }: {
  name?: string | null;
  size?: number;
  className?: string;
  strokeWidth?: number;
}) {
  const Glyph = (name ? lucideIcons[kebabToPascal(name) as keyof typeof lucideIcons] : undefined) ?? AppWindow;
  return <Glyph size={size} className={className} strokeWidth={strokeWidth} aria-hidden />;
}
