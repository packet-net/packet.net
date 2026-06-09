// Maps the design handoff's semantic icon names to lucide-react glyphs, so
// ported screen code keeps using <Icon name="monitor" />. lucide is the
// production icon set the handoff specifies.
import {
  LayoutDashboard, Activity, ArrowLeftRight, Network, Server, Settings, Users,
  Sun, Moon, ChevronDown, ChevronRight, X, Plus, Search, Pause, Play, Trash2,
  Power, RotateCw, ArrowDown, ArrowUp, TriangleAlert, Check, Link as LinkIcon,
  Radio, Send, Copy, Filter, Menu, KeyRound, Fingerprint, Download, ExternalLink,
  Info, Signal, type LucideIcon,
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
