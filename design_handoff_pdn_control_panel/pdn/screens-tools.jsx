// ============================================================
// pdn — Partner link tuning (a focused troubleshooting workspace)
// Send numbered frame bursts to a partner, watch how many arrive,
// tweak modem parameters live, compare runs, coordinate over chat,
// and optionally let pdn auto-sweep a parameter.
// ============================================================

function clamp(n,a,b){ return Math.max(a, Math.min(b, n)); }

// model: probability a numbered frame is delivered given the params + path
function deliveryProb({ txDelay, persistence, ackTimeout }, difficulty) {
  const floor = { easy: 0.86, moderate: 0.7, hard: 0.5 }[difficulty] ?? 0.7;
  let p = floor;
  p += (1 - Math.abs(txDelay - 300) / 320) * 0.18;     // txdelay sweet spot ~300ms
  p += (1 - persistence / 255) * 0.08;                  // politer = fewer collisions
  p += (ackTimeout >= 2500 && ackTimeout <= 4500 ? 0.05 : -0.03);
  return clamp(p, 0.25, 0.985);
}

function LinkTuner({ portId, onClose }) {
  const { useState, useRef, useEffect } = React;
  const port = NODE_CONFIG.ports.find(p => p.id === portId) || NODE_CONFIG.ports[0];
  const setup = PORT_SETUP[port.id] || { difficulty: "moderate" };

  const [partner, setPartner] = useState("GB7CIP");
  const [count, setCount] = useState(20);
  const [params, setParams] = useState({
    txDelay: (port.kiss && port.kiss.txDelay) || 300,
    persistence: (port.kiss && port.kiss.persistence) || 63,
    ackTimeout: (port.ax25 && port.ax25.t1Ms) || 3000,
  });
  const [frames, setFrames] = useState([]);     // {n, state: pending|ack|lost}
  const [running, setRunning] = useState(false);
  const [runs, setRuns] = useState([]);          // history of completed runs
  const [autoTuning, setAutoTuning] = useState(false);
  const [chat, setChat] = useState([
    { who: "them", call: "G8PZT", text: "ok ready when you are, watching here" },
    { who: "me", call: "M0LTE", text: "sending a burst of 20 now" },
  ]);
  const [draft, setDraft] = useState("");
  const timer = useRef(null);

  useEffect(() => () => clearInterval(timer.current), []);

  const runBurst = (overrideParams) => new Promise((resolve) => {
    const p = overrideParams || params;
    const prob = deliveryProb(p, setup.difficulty);
    const total = count;
    setFrames(Array.from({length: total}, (_,i)=>({ n:i+1, state:"pending" })));
    setRunning(true);
    let i = 0;
    clearInterval(timer.current);
    timer.current = setInterval(() => {
      setFrames(prev => prev.map((f,idx)=> idx===i ? { ...f, state: Math.random()<prob ? "ack" : "lost" } : f));
      i++;
      if (i >= total) {
        clearInterval(timer.current);
        setTimeout(() => {
          setFrames(prev => {
            const delivered = prev.filter(f=>f.state==="ack").length;
            const run = { id: Date.now(), params: {...p}, delivered, total, pct: Math.round(delivered/total*100), partner };
            setRuns(r => [run, ...r].slice(0,6));
            resolve(run);
            return prev;
          });
          setRunning(false);
        }, 150);
      }
    }, 90);
  });

  const autoTune = async () => {
    setAutoTuning(true);
    const sweep = [120, 200, 300, 420, 550];
    let best = null;
    for (const txDelay of sweep) {
      const trial = { ...params, txDelay };
      setParams(trial);
      // eslint-disable-next-line no-await-in-loop
      const run = await runBurst(trial);
      if (!best || run.pct > best.pct) best = { txDelay, pct: run.pct };
    }
    if (best) setParams(p => ({ ...p, txDelay: best.txDelay }));
    setChat(c => [...c, { who:"me", call:"pdn", text:`auto-sweep done — best TX delay ${best.txDelay}ms (${best.pct}% delivered)` }]);
    setAutoTuning(false);
  };

  const delivered = frames.filter(f=>f.state==="ack").length;
  const done = frames.filter(f=>f.state!=="pending").length;
  const pct = done ? Math.round(delivered/done*100) : 0;
  const sendChat = () => { if(!draft.trim()) return; setChat(c=>[...c,{who:"me",call:"M0LTE",text:draft}]); setDraft(""); };

  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-background" data-screen-label="Link tuning">
      {/* header */}
      <div className="flex h-14 shrink-0 items-center justify-between border-b border-border px-4">
        <div className="flex items-center gap-3">
          <span className="grid h-8 w-8 place-items-center rounded-md bg-primary/15 text-primary"><Icon name="signal" size={17}/></span>
          <div>
            <h2 className="text-sm font-semibold leading-tight">Link tuning</h2>
            <p className="text-xs text-muted-foreground">port <span className="font-mono">{port.id}</span> · {KIND_LABEL[port.transport.kind]} · {setup.difficulty} path</p>
          </div>
        </div>
        <Button variant="ghost" size="iconSm" onClick={onClose}><Icon name="x"/></Button>
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-[1fr_340px]">
        {/* workspace */}
        <div className="min-h-0 overflow-y-auto p-4 sm:p-6">
          <div className="mx-auto max-w-3xl space-y-5">
            {/* partner + burst controls */}
            <div className="flex flex-wrap items-end gap-3">
              <Field label="Partner station" className="w-44">
                <Select value={partner} onChange={e=>setPartner(e.target.value)}>
                  {NETROM.neighbours.map(n=><option key={n.neighbour} value={n.neighbour}>{n.neighbour}</option>)}
                </Select>
              </Field>
              <Field label="Frames per burst" className="w-32">
                <Input type="number" value={count} onChange={e=>setCount(clamp(+e.target.value,5,50))} className="font-mono"/>
              </Field>
              <Button onClick={()=>runBurst()} disabled={running||autoTuning}>
                <Icon name={running?"pause":"play"} size={14}/>{running?"Sending…":"Send burst"}
              </Button>
              <Button variant="outline" onClick={autoTune} disabled={running||autoTuning}>
                <Icon name="restart" size={14}/>{autoTuning?"Auto-tuning…":"Auto-tune TX delay"}
              </Button>
            </div>

            {/* frame grid */}
            <Card className="p-4">
              <div className="mb-3 flex items-center justify-between">
                <span className="text-sm font-semibold">Delivery</span>
                {done>0 && <span className="flex items-center gap-3 text-xs">
                  <span className="text-muted-foreground">delivered <span className={cn("font-mono font-semibold", pct>=90?"text-success":pct>=70?"text-warning":"text-danger")}>{delivered}/{done}</span></span>
                  <span className={cn("rounded px-1.5 py-0.5 font-mono font-semibold", pct>=90?"bg-success/15 text-success":pct>=70?"bg-warning/15 text-warning":"bg-danger/15 text-danger")}>{pct}%</span>
                </span>}
              </div>
              {frames.length===0 ? (
                <div className="py-8 text-center text-xs text-muted-foreground">Send a burst to see how many numbered frames reach <span className="font-mono">{partner}</span>.</div>
              ) : (
                <div className="grid grid-cols-10 gap-1.5">
                  {frames.map(f=>(
                    <div key={f.n} title={`#${f.n} ${f.state}`} className={cn("flex aspect-square items-center justify-center rounded font-mono text-[10px] transition-colors",
                      f.state==="pending"?"bg-muted text-muted-foreground/50":
                      f.state==="ack"?"bg-success/20 text-success":"bg-danger/20 text-danger line-through")}>{f.n}</div>
                  ))}
                </div>
              )}
            </Card>

            {/* live parameter tweaks */}
            <Card className="p-4">
              <p className="mb-3 text-sm font-semibold">Tune while you test <span className="font-normal text-muted-foreground">— applies live to the port</span></p>
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
                <div>
                  <div className="mb-1.5 flex items-center justify-between"><Label>{PARAM_HELP.txDelay.label}</Label><span className="font-mono text-xs text-muted-foreground">{params.txDelay} ms</span></div>
                  <Slider value={params.txDelay} min={50} max={600} step={10} onChange={v=>setParams(p=>({...p,txDelay:v}))}/>
                </div>
                <div>
                  <div className="mb-1.5 flex items-center justify-between"><Label>{PARAM_HELP.persistence.label}</Label><span className="font-mono text-xs text-muted-foreground">{persistPct(params.persistence)}%</span></div>
                  <Slider value={persistPct(params.persistence)} min={0} max={100} onChange={v=>setParams(p=>({...p,persistence:pctToPersist(v)}))}/>
                </div>
                <div>
                  <div className="mb-1.5 flex items-center justify-between"><Label>{PARAM_HELP.t1Ms.label}</Label><span className="font-mono text-xs text-muted-foreground">{params.ackTimeout} ms</span></div>
                  <Slider value={params.ackTimeout} min={1000} max={8000} step={250} onChange={v=>setParams(p=>({...p,ackTimeout:v}))}/>
                </div>
              </div>
            </Card>

            {/* run history (the compare loop) */}
            {runs.length>0 && (
              <Card className="p-4">
                <p className="mb-3 text-sm font-semibold">Runs</p>
                <div className="space-y-1.5">
                  {runs.map(r=>(
                    <div key={r.id} className="flex items-center gap-3 rounded-md bg-muted/40 px-3 py-2 text-xs">
                      <span className={cn("w-12 shrink-0 rounded px-1.5 py-0.5 text-center font-mono font-semibold", r.pct>=90?"bg-success/15 text-success":r.pct>=70?"bg-warning/15 text-warning":"bg-danger/15 text-danger")}>{r.pct}%</span>
                      <span className="font-mono text-muted-foreground">{r.delivered}/{r.total}</span>
                      <span className="ml-auto font-mono text-muted-foreground">TXd {r.params.txDelay}ms · pers {persistPct(r.params.persistence)}% · T1 {r.params.ackTimeout}ms</span>
                    </div>
                  ))}
                </div>
              </Card>
            )}
          </div>
        </div>

        {/* coordination chat */}
        <div className="flex min-h-0 flex-col border-t border-border lg:border-l lg:border-t-0">
          <div className="flex items-center gap-2 border-b border-border px-4 py-3">
            <Icon name="sessions" size={15} className="text-muted-foreground"/>
            <span className="text-sm font-medium">Coordinate</span>
            <span className="ml-auto flex items-center gap-1.5 text-xs text-muted-foreground"><span className="h-1.5 w-1.5 rounded-full bg-success live-dot"/>{partner} op</span>
          </div>
          <div className="min-h-0 flex-1 space-y-2 overflow-y-auto p-3">
            {chat.map((m,i)=>(
              <div key={i} className={cn("flex flex-col", m.who==="me"?"items-end":"items-start")}>
                <span className="mb-0.5 text-[10px] text-muted-foreground">{m.call}</span>
                <span className={cn("max-w-[85%] rounded-lg px-2.5 py-1.5 text-xs", m.who==="me"?"bg-primary text-primary-foreground":"bg-muted text-foreground")}>{m.text}</span>
              </div>
            ))}
          </div>
          <div className="flex items-center gap-2 border-t border-border p-3">
            <Input value={draft} onChange={e=>setDraft(e.target.value)} onKeyDown={e=>e.key==='Enter'&&sendChat()} placeholder="message the other op…" className="text-xs"/>
            <Button size="iconSm" onClick={sendChat}><Icon name="send" size={14}/></Button>
          </div>
        </div>
      </div>
    </div>
  );
}

Object.assign(window, { LinkTuner });

// ============================================================
// AX.25 TEST frame "ping" — connectionless L2 round-trip check
// (the analogue of the Linux `axping` tool). No session required;
// the far station's link layer echoes the TEST frame straight back.
// ============================================================

// plausible round-trip baseline per transport
function pingBaseline(port) {
  const k = port ? port.transport.kind : "kiss-tcp";
  if (k === "axudp") return { rtt: 42, jitter: 14, loss: 0.02 };
  if (k === "serial-kiss") return { rtt: 2600, jitter: 900, loss: 0.18 };  // HF-ish
  return { rtt: 720, jitter: 280, loss: 0.06 };                            // VHF/UHF
}

function Ax25Ping({ defaultCall, defaultPortId, onClose }) {
  const { useState, useRef, useEffect } = React;
  const [call, setCall] = useState(defaultCall || "");
  const [portId, setPortId] = useState(defaultPortId || PORTS_LIST[0]);
  const [count, setCount] = useState(5);
  const [results, setResults] = useState([]);   // {seq, rtt|null}
  const [running, setRunning] = useState(false);
  const timer = useRef(null);
  useEffect(() => () => clearInterval(timer.current), []);

  const port = NODE_CONFIG.ports.find(p => p.id === portId);

  const run = () => {
    if (!call.trim()) return;
    const base = pingBaseline(port);
    setResults([]); setRunning(true);
    let i = 0;
    clearInterval(timer.current);
    timer.current = setInterval(() => {
      const lost = Math.random() < base.loss;
      const rtt = lost ? null : Math.max(20, Math.round(base.rtt + (Math.random()*2-1)*base.jitter));
      setResults(r => [...r, { seq: i+1, rtt }]);
      i++;
      if (i >= count) { clearInterval(timer.current); setTimeout(()=>setRunning(false), 300); }
    }, 700);
  };

  const got = results.filter(r => r.rtt != null);
  const rtts = got.map(r => r.rtt);
  const loss = results.length ? Math.round((results.length - got.length) / results.length * 100) : 0;
  const stat = rtts.length ? { min: Math.min(...rtts), avg: Math.round(rtts.reduce((a,b)=>a+b,0)/rtts.length), max: Math.max(...rtts) } : null;

  return (
    <Modal open={true} onClose={onClose} width="max-w-lg" title="AX.25 ping" footer={<>
      <Button variant="outline" size="sm" onClick={onClose}>Close</Button>
      <Button size="sm" onClick={run} disabled={running||!call.trim()}><Icon name={running?"pause":"signal"} size={14}/>{running?"Pinging…":"Send TEST frames"}</Button>
    </>}>
      <div className="space-y-4">
        <div className="flex items-start gap-2 rounded-md bg-muted/40 px-2.5 py-2 text-[11px] text-muted-foreground">
          <Icon name="info" size={13} className="mt-px shrink-0"/>
          <span>Sends connectionless AX.25 <span className="font-mono">TEST</span> frames — the far station's link layer echoes each one back. No connected session needed (the remote station must support TEST).</span>
        </div>

        <div className="grid grid-cols-[1fr_auto_auto] gap-2">
          <Field label="Station"><Input value={call} onChange={e=>setCall(e.target.value.toUpperCase())} placeholder="GB7CIP" className="font-mono" autoFocus/></Field>
          <Field label="Via port" className="w-32"><Select value={portId} onChange={e=>setPortId(e.target.value)}>{PORTS_LIST.map(p=><option key={p} value={p}>{p}</option>)}</Select></Field>
          <Field label="Count" className="w-20"><Input type="number" value={count} onChange={e=>setCount(Math.max(1,Math.min(20,+e.target.value)))} className="font-mono"/></Field>
        </div>

        {results.length>0 && (
          <div className="rounded-md border border-border bg-background/60 p-3 font-mono text-xs">
            {results.map(r=>(
              <div key={r.seq} className="flex items-center gap-3 py-0.5">
                <span className="w-20 text-muted-foreground">TEST seq={r.seq}</span>
                {r.rtt != null
                  ? <span className="text-success">reply · {r.rtt} ms</span>
                  : <span className="text-danger">no response (timeout)</span>}
              </div>
            ))}
            {!running && stat && (
              <div className="mt-2 border-t border-border pt-2 text-muted-foreground">
                <div>{results.length} sent · {got.length} received · <span className={loss>0?"text-warning":"text-success"}>{loss}% loss</span></div>
                <div>rtt min/avg/max = <span className="text-foreground/80">{stat.min}/{stat.avg}/{stat.max} ms</span></div>
              </div>
            )}
            {!running && !stat && <div className="mt-2 border-t border-border pt-2 text-danger">No replies — station unreachable, or TEST unsupported.</div>}
          </div>
        )}
      </div>
    </Modal>
  );
}

// drop-in trigger: a button that opens the ping modal, optionally pre-targeted
function PingButton({ call, portId, variant="ghost", size="sm", children }) {
  const { useState } = React;
  const [open, setOpen] = useState(false);
  return (
    <React.Fragment>
      <Button variant={variant} size={size} onClick={()=>setOpen(true)} title={call?`AX.25 ping ${call}`:"AX.25 ping a station"}>
        <Icon name="signal" size={14}/>{children!==undefined ? children : "Ping"}
      </Button>
      {open && <Ax25Ping defaultCall={call} defaultPortId={portId} onClose={()=>setOpen(false)} />}
    </React.Fragment>
  );
}

Object.assign(window, { Ax25Ping, PingButton });
