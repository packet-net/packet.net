// ============================================================
// pdn — Observe screens: Dashboard, Monitor, Sessions, Routes
// ============================================================

// ---------- 4.3 Dashboard -----------------------------------
function Dashboard({ onNavigate }) {
  const s = NODE_STATUS;
  const [fps, setFps] = useState(7.4);
  const [sess, setSess] = useState(s.sessionCount);
  useEffect(() => {
    const t = setInterval(() => {
      setFps(+(4 + Math.random()*9).toFixed(1));
    }, 1400);
    return () => clearInterval(t);
  }, []);

  const ports = Object.values(PORT_STATUS);
  return (
    <div>
      <PageHeader title="Dashboard" subtitle="Health at a glance" actions={
        <Button variant="outline" size="sm" onClick={()=>onNavigate("config")}><Icon name="config" size={14}/> Configure</Button>
      } />

      {/* metric strip */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Metric label="Status" value={<span className="flex items-center gap-2"><StatusDot state="up" live/> Operational</span>} sub={`up ${fmtUptime(s.uptimeSeconds)}`} />
        <Metric label="Ports up" value={<span className="tnum">{s.portsUp}<span className="text-muted-foreground">/{s.portsTotal}</span></span>} sub="1 faulted" subVariant="warning" onClick={()=>onNavigate("ports")} />
        <Metric label="Active sessions" value={<span className="tnum">{sess}</span>} sub="2 console · 1 bridge · 1 interlink" onClick={()=>onNavigate("sessions")} />
        <Metric label="Frames/sec" value={<span className="tnum">{fps}</span>} sub={<span className="flex items-center gap-1"><span className="h-1.5 w-1.5 rounded-full bg-success live-dot"/> live</span>} onClick={()=>onNavigate("monitor")} />
      </div>

      <div className="mt-5 grid grid-cols-1 gap-4 lg:grid-cols-3">
        {/* identity */}
        <Card>
          <CardHeader><CardTitle className="flex items-center gap-2"><Icon name="radio" size={15} className="text-muted-foreground"/> Station</CardTitle></CardHeader>
          <CardContent className="space-y-2.5">
            <Row k="Callsign" v={<span className="font-mono font-semibold">{s.callsign}</span>} />
            <Row k="Alias" v={<span className="font-mono">{s.alias}</span>} />
            <Row k="Locator" v={<span className="font-mono">{s.grid}</span>} />
            <Row k="Version" v={<span className="font-mono text-xs">{s.version}</span>} />
            <Row k="Uptime" v={fmtUptime(s.uptimeSeconds)} />
          </CardContent>
        </Card>

        {/* ports */}
        <Card>
          <div className="flex items-center justify-between p-4 pb-2">
            <CardTitle className="flex items-center gap-2"><Icon name="ports" size={15} className="text-muted-foreground"/> Ports</CardTitle>
            <Button variant="ghost" size="xs" onClick={()=>onNavigate("ports")}>All <Icon name="chevRight" size={13}/></Button>
          </div>
          <CardContent className="space-y-1">
            {ports.map(p => {
              const cfg = NODE_CONFIG.ports.find(x=>x.id===p.id);
              const h = portHealth(p.id);
              return (
                <button key={p.id} onClick={()=>onNavigate("ports")} className="flex w-full items-center justify-between rounded-md px-2 py-1.5 text-sm hover:bg-accent">
                  <span className="flex items-center gap-2"><StatusDot state={h.level==='degraded'?'faulted':p.state} live={p.state==='up'&&h.level==='good'}/> <span className="font-mono">{p.id}</span></span>
                  <span className="flex items-center gap-2 text-xs text-muted-foreground">
                    {h.level==='degraded' && <Tooltip text={h.reason}><Badge variant="warning">attention</Badge></Tooltip>}
                    {h.level==='faulted' && <Badge variant="danger">faulted</Badge>}
                    <Badge variant="muted">{KIND_LABEL[cfg.transport.kind]}</Badge>
                  </span>
                </button>
              );
            })}
          </CardContent>
        </Card>

        {/* netrom */}
        <Card>
          <div className="flex items-center justify-between p-4 pb-2">
            <CardTitle className="flex items-center gap-2"><Icon name="routes" size={15} className="text-muted-foreground"/> NET/ROM</CardTitle>
            <Button variant="ghost" size="xs" onClick={()=>onNavigate("routes")}>Routes <Icon name="chevRight" size={13}/></Button>
          </div>
          <CardContent className="space-y-2.5">
            <Row k="Neighbours" v={<span className="tnum font-semibold">{s.netrom.neighbours}</span>} />
            <Row k="Destinations" v={<span className="tnum font-semibold">{s.netrom.destinations}</span>} />
            <Row k="Forwarding" v={<Badge variant="success">PerFlow</Badge>} />
            <Row k="INP3 overlay" v={s.netrom.inp3Enabled ? <Badge variant="success">on</Badge> : <Badge variant="muted">off</Badge>} />
          </CardContent>
        </Card>
      </div>

      {/* log tail */}
      <Card className="mt-4">
        <div className="flex items-center justify-between p-4 pb-2">
          <CardTitle className="flex items-center gap-2"><Icon name="signal" size={15} className="text-muted-foreground"/> Recent activity</CardTitle>
          <Badge variant="muted">journald tail</Badge>
        </div>
        <CardContent>
          <div className="space-y-0.5 font-mono text-xs">
            {LOG_TAIL.map((l,i)=>(
              <div key={i} className="flex gap-3 rounded px-1.5 py-1 hover:bg-accent/60">
                <span className="shrink-0 text-muted-foreground">{l.t}</span>
                <span className={cn("shrink-0 w-10 font-semibold uppercase", l.lvl==='error'?'text-danger':l.lvl==='warn'?'text-warning':'text-muted-foreground')}>{l.lvl}</span>
                <span className="text-foreground/90">{l.msg}</span>
              </div>
            ))}
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

function Metric({ label, value, sub, subVariant, onClick }) {
  return (
    <Card className={cn("p-4", onClick && "cursor-pointer transition-colors hover:border-primary/40")} onClick={onClick}>
      <p className="text-xs font-medium text-muted-foreground">{label}</p>
      <div className="mt-1.5 text-2xl font-semibold tracking-tight">{value}</div>
      <p className={cn("mt-1 text-xs", subVariant==='warning'?'text-warning':'text-muted-foreground')}>{sub}</p>
    </Card>
  );
}
function Row({ k, v }) {
  return <div className="flex items-center justify-between text-sm"><span className="text-muted-foreground">{k}</span><span>{v}</span></div>;
}

// ---------- 4.5 Live Monitor --------------------------------
function Monitor() {
  const [frames, setFrames] = useState(() => seedFrames(40));
  const [paused, setPaused] = useState(false);
  const [expanded, setExpanded] = useState(null);
  const [fPort, setFPort] = useState("all");
  const [fType, setFType] = useState("all");
  const [fCall, setFCall] = useState("");
  const pausedRef = useRef(paused);
  pausedRef.current = paused;

  // smooth-prepend: glide new rows in at the top, preserve position when scrolled
  const scrollRef = useRef(null);
  const prevHeightRef = useRef(0);
  const followRef = useRef(true);     // are we auto-following the top of the stream?
  const firstRef = useRef(true);
  const tweenRef = useRef(0);
  const onScroll = () => {
    const el = scrollRef.current; if (!el) return;
    if (el.scrollTop < 8) followRef.current = true;        // back at the top → follow
    else if (el.scrollTop > 140) followRef.current = false; // scrolled away to read → hold
  };
  // custom eased glide to the top (smoother + steadier than native smooth-scroll at this cadence)
  const glideToTop = (el) => {
    cancelAnimationFrame(tweenRef.current);
    const start = el.scrollTop, startT = performance.now(), dur = 520;
    const ease = t => 1 - Math.pow(1 - t, 3);   // easeOutCubic
    const step = (now) => {
      const t = Math.min(1, (now - startT) / dur);
      el.scrollTop = Math.round(start * (1 - ease(t)));
      if (t < 1) tweenRef.current = requestAnimationFrame(step);
    };
    tweenRef.current = requestAnimationFrame(step);
  };
  React.useLayoutEffect(() => {
    const el = scrollRef.current; if (!el) return;
    if (firstRef.current) { firstRef.current = false; prevHeightRef.current = el.scrollHeight; return; }
    const added = el.scrollHeight - prevHeightRef.current;
    if (added > 0) {
      if (followRef.current) { el.scrollTop = el.scrollTop + added; glideToTop(el); } // hold visual, then ease up
      else { el.scrollTop = el.scrollTop + added; }                                    // keep the row you're reading still
    }
    prevHeightRef.current = el.scrollHeight;
  }, [frames]);
  React.useEffect(() => () => cancelAnimationFrame(tweenRef.current), []);
  // re-baseline height on filter/expand changes so the next frame doesn't lurch
  React.useLayoutEffect(() => { const el = scrollRef.current; if (el) prevHeightRef.current = el.scrollHeight; }, [fPort, fType, fCall, expanded]);

  useEffect(() => {
    const t = setInterval(() => {
      if (pausedRef.current) return;
      const n = 1 + Math.floor(Math.random()*2);
      setFrames(prev => {
        const add = [];
        for (let i=0;i<n;i++) add.push(makeFrame(new Date()));
        return [...add.reverse(), ...prev].slice(0, 300);
      });
    }, 850);
    return () => clearInterval(t);
  }, []);

  const filtered = frames.filter(f =>
    (fPort==="all" || f.portId===fPort) &&
    (fType==="all" || f.type===fType) &&
    (!fCall || f.source.includes(fCall.toUpperCase()) || f.dest.includes(fCall.toUpperCase()))
  );

  return (
    <div>
      <PageHeader title="Live monitor" subtitle="Frames on the air — every port, pre-address-filter" actions={
        <div className="flex items-center gap-2">
          <Button variant={paused?"default":"outline"} size="sm" onClick={()=>setPaused(p=>!p)}>
            <Icon name={paused?"play":"pause"} size={14}/>{paused?"Resume":"Pause"}
          </Button>
          <Button variant="outline" size="sm" onClick={()=>setFrames([])}><Icon name="trash" size={14}/>Clear</Button>
        </div>
      } />

      {/* link stats strip */}
      <div className="mb-4 grid grid-cols-2 gap-2 sm:grid-cols-4">
        {LINK_STATS.map((l,i)=>(
          <div key={i} className="rounded-lg border border-border bg-card p-3">
            <div className="flex items-center justify-between">
              <span className="font-mono text-xs font-semibold">{l.peer}</span>
              <Badge variant="muted">{l.portId}</Badge>
            </div>
            <div className="mt-2 grid grid-cols-2 gap-y-1 text-[11px] text-muted-foreground">
              <span>RTT</span><span className={cn("tnum text-right font-mono", l.smoothedRttMs>1500?'text-warning':'text-foreground')}>{l.smoothedRttMs}ms</span>
              <span>Retries</span><span className={cn("tnum text-right font-mono", l.retries>0?'text-warning':'text-foreground')}>{l.retries}</span>
              <span>REJ/SREJ</span><span className={cn("tnum text-right font-mono", (l.rejCount+l.srejCount)>0?'text-danger':'text-foreground')}>{l.rejCount}/{l.srejCount}</span>
            </div>
          </div>
        ))}
      </div>

      {/* filters */}
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <div className="relative">
          <Icon name="search" size={14} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-muted-foreground"/>
          <Input value={fCall} onChange={e=>setFCall(e.target.value)} placeholder="callsign…" className="h-8 w-36 pl-8 font-mono text-xs"/>
        </div>
        <Select value={fPort} onChange={e=>setFPort(e.target.value)} className="h-8 w-32 text-xs">
          <option value="all">All ports</option>
          {PORTS_LIST.map(p=><option key={p} value={p}>{p}</option>)}
        </Select>
        <Select value={fType} onChange={e=>setFType(e.target.value)} className="h-8 w-32 text-xs">
          <option value="all">All types</option>
          {FRAME_TYPES.map(t=><option key={t} value={t}>{t}</option>)}
        </Select>
        <div className="ml-auto flex items-center gap-2 text-xs text-muted-foreground">
          {paused ? <Badge variant="warning">paused</Badge> : <span className="flex items-center gap-1.5"><span className="h-1.5 w-1.5 rounded-full bg-success live-dot"/>streaming</span>}
          <span className="tnum">{filtered.length} frames</span>
        </div>
      </div>

      {/* frame table */}
      <Card className="overflow-hidden p-0">
        <div ref={scrollRef} onScroll={onScroll} className="max-h-[calc(100vh-22rem)] overflow-y-auto">
          <table className="w-full border-collapse">
            <thead className="sticky top-0 z-10 bg-card/95 backdrop-blur">
              <tr className="border-b border-border">
                <Th className="w-24">Time</Th>
                <Th className="w-16">Port</Th>
                <Th className="w-8"></Th>
                <Th>Source → Dest</Th>
                <Th className="w-16">Type</Th>
                <Th className="hidden w-20 md:table-cell">PID</Th>
                <Th className="w-14 text-right">Len</Th>
                <Th className="hidden lg:table-cell">Summary</Th>
              </tr>
            </thead>
            <tbody>
              {filtered.length===0 && (
                <tr><td colSpan={8}><div className="py-10"><EmptyState icon="filter" title="No frames match" body="Adjust the filters, or resume the stream."/></div></td></tr>
              )}
              {filtered.map((f) => (
                <React.Fragment key={f.seq}>
                  <tr onClick={()=>setExpanded(expanded===f.seq?null:f.seq)}
                    className={cn("cursor-pointer border-b border-border/60 font-mono text-xs hover:bg-accent/50", expanded===f.seq && "bg-accent/60", f.seq===frames[0]?.seq && !paused && "row-flash")}>
                    <Td className="text-muted-foreground">{f.timestamp.toLocaleTimeString('en-GB',{hour12:false})}.{String(f.timestamp.getMilliseconds()).padStart(3,'0')}</Td>
                    <Td><span className="text-muted-foreground">{f.portId}</span></Td>
                    <Td>
                      {f.direction==='in'
                        ? <Icon name="arrowDown" size={13} className="text-emerald-500"/>
                        : <Icon name="arrowUp" size={13} className="text-sky-500"/>}
                    </Td>
                    <Td><span className="font-semibold">{f.source}</span><span className="text-muted-foreground"> → {f.dest}</span>{f.path.length>0 && <span className="text-muted-foreground/60"> v {f.path.join(',')}</span>}</Td>
                    <Td><FrameBadge type={f.type} classKind={f.classKind}/></Td>
                    <Td className="hidden text-muted-foreground md:table-cell">{f.pid||'—'}</Td>
                    <Td className="tnum text-right text-muted-foreground">{f.length}</Td>
                    <Td className="hidden text-muted-foreground lg:table-cell">{f.summary}</Td>
                  </tr>
                  {expanded===f.seq && (
                    <tr className="border-b border-border bg-muted/30">
                      <td colSpan={8} className="p-0"><FrameDecode f={f}/></td>
                    </tr>
                  )}
                </React.Fragment>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
}

// Full Wireshark-style frame decode
function FrameDecode({ f }) {
  return (
    <div className="grid grid-cols-1 gap-4 p-4 lg:grid-cols-2">
      {/* decoded fields */}
      <div>
        <p className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">AX.25 frame</p>
        <div className="space-y-px rounded-md border border-border font-mono text-xs">
          <DecRow k="Direction" v={f.direction==='in'?'RX (received)':'TX (transmitted)'} />
          <DecRow k="Port" v={f.portId} />
          <DecRow k="Source SSID" v={f.source} />
          <DecRow k="Destination SSID" v={f.dest} />
          {f.path.length>0 && <DecRow k="Digipeater path" v={f.path.join(', ')} />}
          <DecRow k="Frame type" v={<FrameBadge type={f.type} classKind={f.classKind}/>} />
          <DecRow k="Class" v={`${f.classKind}-frame`} />
          <DecRow k="Command/Response" v={f.command?'Command':'Response'} />
          {f.ns!=null && <DecRow k="N(S) send seq" v={f.ns} />}
          {f.nr!=null && <DecRow k="N(R) recv seq" v={f.nr} />}
          <DecRow k="Poll/Final" v={f.pf?'1':'0'} />
          {f.pid && <DecRow k="PID" v={`${f.pid} — ${f.pidName}`} />}
          <DecRow k="Length" v={`${f.length} bytes`} />
        </div>
      </div>
      {/* hex dump */}
      <div>
        <p className="mb-2 flex items-center justify-between text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
          <span>Raw octets</span>
          <button className="flex items-center gap-1 normal-case text-muted-foreground hover:text-foreground"><Icon name="copy" size={12}/> copy hex</button>
        </p>
        <div className="rounded-md border border-border bg-background/60 p-3 font-mono text-xs leading-relaxed">
          {Array.from({length: Math.ceil(f.raw.length/8)}).map((_,row)=>{
            const slice = f.raw.slice(row*8,row*8+8);
            return (
              <div key={row} className="flex gap-4">
                <span className="text-muted-foreground/60">{hex(row*8,4)}</span>
                <span className="text-foreground/90">{slice.map(b=>hex(b)).join(' ')}</span>
                <span className="text-muted-foreground">{slice.map(b=> (b>=32&&b<127)?String.fromCharCode(b):'·').join('')}</span>
              </div>
            );
          })}
        </div>
        <p className="mt-2 text-[11px] text-muted-foreground">Decoded summary: <span className="font-mono text-foreground/80">{f.summary}</span></p>
      </div>
    </div>
  );
}
function DecRow({ k, v }) {
  return <div className="flex items-center justify-between px-3 py-1.5 odd:bg-muted/30"><span className="text-muted-foreground">{k}</span><span className="text-right text-foreground/90">{v}</span></div>;
}

Object.assign(window, { Dashboard, Monitor });
