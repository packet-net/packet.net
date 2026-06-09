// ============================================================
// pdn — Sessions (4.6) + NET/ROM Routes (4.7)
// ============================================================

// ---------- 4.6 Sessions ------------------------------------
function Sessions({ initialConnect }) {
  const [sessions, setSessions] = useState(SESSIONS);
  const [openSession, setOpenSession] = useState(null);
  const [connectOpen, setConnectOpen] = useState(!!initialConnect);

  const drop = (id) => setSessions(s => s.filter(x => x.id !== id));

  const roleBadge = { console: "default", interlink: "secondary", bridge: "muted" };
  const stateBadge = (st) => st==="Connected" ? "success" : st==="TimerRecovery" ? "warning" : "muted";

  return (
    <div>
      <PageHeader title="Sessions" subtitle="Active AX.25 L2 + NET/ROM L4 circuits" actions={
        <Button size="sm" onClick={()=>setConnectOpen(true)}><Icon name="plus" size={14}/> Connect out</Button>
      } />

      <Card className="overflow-hidden p-0">
        <div className="overflow-x-auto">
          <table className="w-full border-collapse">
            <thead>
              <tr className="border-b border-border">
                <Th>Peer</Th><Th>Port</Th><Th>Role</Th><Th>State</Th>
                <Th className="text-right">V(S)/V(R)</Th><Th className="text-right">Win</Th>
                <Th className="hidden text-right md:table-cell">Uptime</Th>
                <Th className="hidden text-right md:table-cell">Bytes ↓/↑</Th>
                <Th className="hidden text-right lg:table-cell">Last</Th>
                <Th className="w-px"></Th>
              </tr>
            </thead>
            <tbody>
              {sessions.length===0 && <tr><td colSpan={10}><div className="py-10"><EmptyState icon="sessions" title="No active sessions" body="Connect out to a station or alias to start one."/></div></td></tr>}
              {sessions.map(s => (
                <tr key={s.id} className="border-b border-border/60 hover:bg-accent/40">
                  <Td><button onClick={()=>setOpenSession(s)} className="font-mono font-semibold hover:text-primary">{s.peer}</button></Td>
                  <Td className="font-mono text-xs text-muted-foreground">{s.portId}</Td>
                  <Td><Badge variant={roleBadge[s.role]}>{s.role}</Badge></Td>
                  <Td><span className="flex items-center gap-1.5"><StatusDot state={s.state==='Connected'?'up':s.state==='TimerRecovery'?'faulted':'down'}/><Badge variant={stateBadge(s.state)}>{s.state}</Badge></span></Td>
                  <Td className="tnum text-right font-mono text-xs">{s.vs}/{s.vr}</Td>
                  <Td className="tnum text-right font-mono text-xs text-muted-foreground">{s.window}</Td>
                  <Td className="hidden text-right text-xs text-muted-foreground md:table-cell">{fmtUptime(s.uptimeSeconds)}</Td>
                  <Td className="hidden text-right font-mono text-xs text-muted-foreground md:table-cell">{fmtBytes(s.bytesIn)} / {fmtBytes(s.bytesOut)}</Td>
                  <Td className="hidden text-right font-mono text-xs text-muted-foreground lg:table-cell">{s.lastActivity}</Td>
                  <Td>
                    <div className="flex items-center justify-end gap-1">
                      <Button variant="ghost" size="iconSm" title="Open" onClick={()=>setOpenSession(s)}><Icon name="external" size={14}/></Button>
                      <Button variant="ghost" size="iconSm" title="Disconnect" onClick={()=>drop(s.id)}><Icon name="x" size={15} className="text-danger"/></Button>
                    </div>
                  </Td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      <SessionConsole session={openSession} onClose={()=>setOpenSession(null)} onDrop={(id)=>{drop(id); setOpenSession(null);}} />
      <ConnectOut open={connectOpen} initialCall={initialConnect&&initialConnect.call} initialPort={initialConnect&&initialConnect.portId} onClose={()=>setConnectOpen(false)} onConnect={(call,port)=>{
        const id = "s"+Date.now();
        const sess = { id, portId:port, peer:call, role:"console", state:"Connected", vs:0,vr:0,window:4,uptimeSeconds:0,bytesIn:0,bytesOut:0,lastActivity:"0:00:00" };
        setSessions(s=>[...s, sess]);
        setConnectOpen(false);
        setOpenSession(sess);   // sysop interactive connect — drop straight into the terminal
      }} />
    </div>
  );
}

// session detail drawer with a minimal send-into-session affordance
function SessionConsole({ session, onClose, onDrop }) {
  const [lines, setLines] = useState([]);
  const [draft, setDraft] = useState("");
  const scrollRef = useRef(null);

  useEffect(() => {
    if (session) {
      setLines([
        { dir: "in", t: "from peer", text: `${session.peer} connected to GB7RDG` },
        { dir: "in", t: "from peer", text: "Welcome to GB7RDG — Reading & District packet gateway" },
        { dir: "in", t: "from peer", text: "Type ? for help" },
      ]);
      setDraft("");
    }
  }, [session]);

  const send = () => {
    if (!draft.trim()) return;
    setLines(l => [...l, { dir: "out", t: "sent", text: draft }]);
    const echo = draft;
    setDraft("");
    setTimeout(() => setLines(l => [...l, { dir: "in", t: "from peer", text: `ack: ${echo.slice(0,40)}` }]), 600);
  };

  return (
    <Sheet open={!!session} onClose={onClose}
      title={session ? `Session — ${session.peer}` : ""}
      subtitle={session ? `${session.portId} · ${session.role} · ${session.state}` : ""}
      width="max-w-2xl"
      footer={session && <>
        <Button variant="destructive" size="sm" onClick={()=>onDrop(session.id)}><Icon name="x" size={14}/> Disconnect</Button>
        <Button variant="outline" size="sm" onClick={onClose}>Close</Button>
      </>}>
      {session && (
        <div className="space-y-4">
          <div className="grid grid-cols-3 gap-2">
            <Stat k="V(S)/V(R)" v={`${session.vs}/${session.vr}`} />
            <Stat k="Window" v={session.window} />
            <Stat k="Uptime" v={fmtUptime(session.uptimeSeconds)} />
            <Stat k="Bytes in" v={fmtBytes(session.bytesIn)} />
            <Stat k="Bytes out" v={fmtBytes(session.bytesOut)} />
            <Stat k="Last activity" v={session.lastActivity} />
          </div>

          <div>
            <Label>Session stream</Label>
            <div ref={scrollRef} className="mt-1.5 h-64 overflow-y-auto rounded-md border border-border bg-background/60 p-3 font-mono text-xs">
              {lines.map((l,i)=>(
                <div key={i} className={cn("flex gap-2 py-0.5", l.dir==='out' && "text-primary")}>
                  <span className="shrink-0 text-muted-foreground/60">{l.dir==='out'?'»':'«'}</span>
                  <span className="whitespace-pre-wrap break-all">{l.text}</span>
                </div>
              ))}
            </div>
            <div className="mt-2 flex items-center gap-2">
              <Input value={draft} onChange={e=>setDraft(e.target.value)} onKeyDown={e=>e.key==='Enter'&&send()} placeholder="send a line into the session…" className="font-mono text-xs"/>
              <Button size="sm" onClick={send}><Icon name="send" size={14}/> Send</Button>
            </div>
            <p className="mt-1.5 text-[11px] text-muted-foreground">Minimal v1 affordance — pushes one line of text into the connected-mode session.</p>
          </div>
        </div>
      )}
    </Sheet>
  );
}
function Stat({ k, v }) {
  return <div className="rounded-md border border-border bg-muted/30 p-2.5"><p className="text-[11px] text-muted-foreground">{k}</p><p className="mt-0.5 font-mono text-sm font-semibold">{v}</p></div>;
}

// connect-out modal with alias autocomplete from the routes list
function ConnectOut({ open, onClose, onConnect, initialCall, initialPort }) {
  const [target, setTarget] = useState("");
  const [port, setPort] = useState("vhf-1");
  const suggestions = NETROM.destinations
    .map(d => ({ call: d.destination, alias: d.alias }))
    .filter(d => target && (d.call.includes(target.toUpperCase()) || d.alias.includes(target.toUpperCase())));

  useEffect(() => { if (open) { setTarget(initialCall || ""); setPort(initialPort || "vhf-1"); } }, [open, initialCall, initialPort]);

  return (
    <Modal open={open} onClose={onClose} title="Connect out" footer={<>
      <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
      <Button size="sm" disabled={!target} onClick={()=>onConnect(target.toUpperCase(), port)}><Icon name="link" size={14}/> Connect</Button>
    </>}>
      <div className="space-y-4">
        <Field label="Callsign or NET/ROM alias" hint="You're opening an interactive session from this node to the station — the same as a sysop Connect.">
          <div className="relative">
            <Input value={target} onChange={e=>setTarget(e.target.value)} placeholder="e.g. GB7CIP or CIPGW" className="font-mono" autoFocus/>
            {suggestions.length>0 && (
              <div className="absolute z-10 mt-1 w-full overflow-hidden rounded-md border border-border bg-popover shadow-lg">
                {suggestions.map(s=>(
                  <button key={s.call} onClick={()=>setTarget(s.call)} className="flex w-full items-center justify-between px-3 py-2 text-left text-sm hover:bg-accent">
                    <span className="font-mono font-semibold">{s.call}</span>
                    <span className="font-mono text-xs text-muted-foreground">{s.alias}</span>
                  </button>
                ))}
              </div>
            )}
          </div>
        </Field>
        <Field label="Via port">
          <Select value={port} onChange={e=>setPort(e.target.value)}>
            {PORTS_LIST.map(p=><option key={p} value={p}>{p}</option>)}
          </Select>
        </Field>
      </div>
    </Modal>
  );
}

// ---------- 4.7 NET/ROM Routes ------------------------------
function Routes({ onConnect }) {
  const [tab, setTab] = useState("destinations");
  return (
    <div>
      <PageHeader title="NET/ROM routes" subtitle={`The network view — quality and INP3 time, side by side · updated ${new Date(NETROM.generatedAt).toLocaleTimeString('en-GB',{hour12:false})}`} actions={
        <Tabs active={tab} onChange={setTab} tabs={[{id:"destinations",label:`Destinations · ${NETROM.destinations.length}`},{id:"neighbours",label:`Neighbours · ${NETROM.neighbours.length}`}]} />
      } />

      {tab==="neighbours" ? (
        <Card className="overflow-hidden p-0">
          <div className="overflow-x-auto">
            <table className="w-full border-collapse">
              <thead><tr className="border-b border-border">
                <Th>Neighbour</Th><Th>Alias</Th><Th>Port</Th>
                <Th><span className="inline-flex items-center gap-1">Path quality <InfoHint text="Link quality to this directly-heard neighbour, 0–255. Higher is better — a blend of how reliably and directly you hear each other."/></span></Th>
                <Th className="text-right">Last heard</Th><Th className="w-px"></Th>
              </tr></thead>
              <tbody>
                {NETROM.neighbours.map(n=>(
                  <tr key={n.neighbour} className="border-b border-border/60 hover:bg-accent/40">
                    <Td className="font-mono font-semibold">{n.neighbour}</Td>
                    <Td className="font-mono text-xs text-muted-foreground">{n.alias}</Td>
                    <Td><Badge variant="muted">{n.portId}</Badge></Td>
                    <Td><QualityBar value={n.pathQuality}/></Td>
                    <Td className="text-right font-mono text-xs text-muted-foreground">{n.lastHeard}</Td>
                    <Td><PingButton call={n.neighbour} portId={n.portId} size="xs"/></Td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      ) : (
        <Card className="overflow-hidden p-0">
          <div className="overflow-x-auto">
            <table className="w-full border-collapse">
              <thead><tr className="border-b border-border">
                <Th>Destination</Th><Th>Alias</Th><Th>Best via</Th>
                <Th><span className="inline-flex items-center gap-1">Quality <InfoHint text="NET/ROM path quality, 0–255 (higher is better) — how good this route is judged to be overall. Routes below the node's minimum quality are ignored."/></span></Th>
                <Th className="text-right"><span className="inline-flex items-center justify-end gap-1">Obsol. <InfoHint text="Obsolescence — a freshness countdown. Each routing sweep that doesn't re-hear the route ticks it down; hearing it again refreshes it to the top. At zero the route is dropped. High = recently confirmed, low = going stale."/></span></Th>
                <Th className="text-right"><span className="inline-flex items-center justify-end gap-1">INP3 time <InfoHint text="INP3's actual measured round-trip time to the destination, in milliseconds. Unlike quality (a static score), this is timed live — so pdn can prefer the genuinely faster route when it's available."/></span></Th>
                <Th className="hidden text-right sm:table-cell"><span className="inline-flex items-center justify-end gap-1">Hops <InfoHint text="How many NET/ROM nodes the traffic crosses to reach the destination (reported by INP3)."/></span></Th>
                <Th className="w-px"></Th>
              </tr></thead>
              <tbody>
                {NETROM.destinations.map(d=>{
                  const r = d.routes[d.bestRoute];
                  const viaNb = NETROM.neighbours.find(nb=>nb.neighbour===r.neighbour);
                  const viaPort = viaNb ? viaNb.portId : null;
                  return (
                    <tr key={d.destination} className="border-b border-border/60 hover:bg-accent/40">
                      <Td className="font-mono font-semibold">{d.destination}</Td>
                      <Td className="font-mono text-xs text-muted-foreground">{d.alias}</Td>
                      <Td className="font-mono text-xs">{r.neighbour}{d.routes.length>1 && <span className="ml-1 text-muted-foreground">+{d.routes.length-1}</span>}</Td>
                      <Td><QualityBar value={r.quality}/></Td>
                      <Td className="tnum text-right font-mono text-xs text-muted-foreground">{r.obsolescence}</Td>
                      <Td className="text-right font-mono text-xs">
                        {r.inp3 ? <span className="text-primary">{r.inp3.targetTimeMs}ms</span> : <span className="text-muted-foreground/50">—</span>}
                      </Td>
                      <Td className="hidden text-right font-mono text-xs text-muted-foreground sm:table-cell">{r.inp3 ? r.inp3.hopCount : '—'}</Td>
                      <Td>
                        <div className="flex items-center justify-end gap-1">
                          <PingButton call={r.neighbour} portId={viaPort} size="xs"/>
                          <Button variant="ghost" size="xs" onClick={()=>onConnect(d.destination, viaPort)}><Icon name="link" size={13}/> Connect</Button>
                        </div>
                      </Td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <div className="flex items-center gap-4 border-t border-border bg-muted/20 px-4 py-2.5 text-[11px] text-muted-foreground">
            <span className="flex items-center gap-1.5"><span className="h-2 w-3 rounded-sm bg-success"/>quality ≥180</span>
            <span className="flex items-center gap-1.5"><span className="h-2 w-3 rounded-sm bg-warning"/>100–179</span>
            <span className="flex items-center gap-1.5"><span className="h-2 w-3 rounded-sm bg-danger"/>&lt;100</span>
            <span className="ml-auto flex items-center gap-1.5"><span className="font-mono text-primary">INP3 time</span> = measured round-trip target (preferred when present)</span>
          </div>
        </Card>
      )}
    </div>
  );
}

Object.assign(window, { Sessions, Routes });
