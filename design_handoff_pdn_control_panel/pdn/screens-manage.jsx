// ============================================================
// pdn — Ports (4.4) + Config editor (4.8) + Users (4.9)
// ============================================================

// ---------- 4.4 Ports ---------------------------------------
function transportDesc(t) {
  if (t.kind==="kiss-tcp") return `${t.host}:${t.port}`;
  if (t.kind==="serial-kiss") return `${t.device} @ ${t.baud}`;
  if (t.kind==="nino-tnc") { const m = NINO_MODES.find(x=>x.mode===t.mode); return `${t.device} · ${m?m.label:('mode '+t.mode)}`; }
  if (t.kind==="axudp") return `${t.host}:${t.port}`;
  return "";
}
function setupSummary(id) {
  const s = PORT_SETUP[id]; if (!s) return "Custom";
  if (s.custom) return "Custom parameters";
  const r = RADIO_PROFILES.find(x=>x.id===s.radio);
  const ch = CHANNEL_MODES.find(x=>x.id===s.channel);
  const d = LINK_DIFFICULTY.find(x=>x.id===s.difficulty);
  return [r&&r.name, ch&&ch.name, d&&d.name].filter(Boolean).join(" · ");
}

function Ports({ onTune }) {
  const [ports, setPorts] = useState(NODE_CONFIG.ports);
  const [edit, setEdit] = useState(null);
  const [testDismissed, setTestDismissed] = useState(false);

  const newPort = { id:"", enabled:true, transport:{kind:"kiss-tcp",host:"127.0.0.1",port:8001}, ax25:{...AX25_DEFAULTS}, kiss:{...KISS_DEFAULTS}, setup:{radio:RADIO_PROFILES[0].id,channel:"shared",difficulty:"moderate",custom:false}, _new:true };

  return (
    <div>
      <PageHeader title="Ports" subtitle="Each RF or network port pdn talks through" actions={
        <div className="flex items-center gap-2">
          <PingButton variant="outline" portId={PORTS_LIST[0]}>AX.25 ping</PingButton>
          <Button size="sm" onClick={()=>setEdit(newPort)}><Icon name="plus" size={14}/> Add port</Button>
        </div>
      } />

      {!testDismissed && <NinoTestFlash test={NINO_TEST} onDismiss={()=>setTestDismissed(true)} onConfigure={()=>{ const p = ports.find(x=>x.id===NINO_TEST.portId); if(p) setEdit(p); }} />}

      <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
        {ports.map(p => {
          const st = PORT_STATUS[p.id];
          const h = portHealth(p.id);
          const accent = h.level==="faulted" ? "border-danger/40" : h.level==="degraded" ? "border-warning/40" : "border-border";
          return (
            <Card key={p.id} className={cn("p-4", accent)}>
              <div className="flex items-start justify-between">
                <div className="flex items-center gap-2.5">
                  <StatusDot state={st.state} live={st.state==='up'} />
                  <div>
                    <div className="flex items-center gap-2">
                      <span className="font-mono text-sm font-semibold">{p.id}</span>
                      {!p.enabled && <Badge variant="muted">disabled</Badge>}
                      {h.level==="faulted" && <Badge variant="danger">faulted</Badge>}
                      {h.level==="degraded" && <Tooltip text={h.reason}><Badge variant="warning">needs attention</Badge></Tooltip>}
                    </div>
                    <p className="mt-0.5 font-mono text-xs text-muted-foreground">{transportDesc(p.transport)}</p>
                  </div>
                </div>
                <Badge variant="secondary">{KIND_LABEL[p.transport.kind]}</Badge>
              </div>

              {h.level==="degraded" && <div className="mt-3 flex items-center gap-2 rounded-md bg-warning/10 px-2.5 py-1.5 text-xs text-warning"><Icon name="alert" size={13}/> {h.reason}</div>}
              {st.lastError && <div className="mt-3 flex items-center gap-2 rounded-md bg-danger/10 px-2.5 py-1.5 text-xs text-danger"><Icon name="alert" size={13}/> {st.lastError}</div>}

              <div className="mt-3 rounded-md bg-muted/40 px-2.5 py-2 text-xs">
                <span className="text-muted-foreground">Setup </span>
                <span className="font-medium text-foreground">{setupSummary(p.id)}</span>
              </div>

              <div className="mt-3 grid grid-cols-3 gap-2 border-t border-border pt-3 text-xs">
                <div><p className="text-muted-foreground">Sessions</p><p className="tnum mt-0.5 font-mono font-semibold">{st.sessionCount}</p></div>
                <div><p className="text-muted-foreground">Frames ↓</p><p className="tnum mt-0.5 font-mono font-semibold">{st.framesIn.toLocaleString()}</p></div>
                <div><p className="text-muted-foreground">Frames ↑</p><p className="tnum mt-0.5 font-mono font-semibold">{st.framesOut.toLocaleString()}</p></div>
              </div>

              <div className="mt-3 flex flex-wrap items-center gap-2">
                <Button variant="outline" size="sm" onClick={()=>setEdit(p)}>Edit</Button>
                {onTune && <Button variant="ghost" size="sm" onClick={()=>onTune(p.id)} title="Tune this link with a partner"><Icon name="signal" size={14}/> Tune link</Button>}
                {st.state==='up'
                  ? <Button variant="ghost" size="sm" title="Restart port"><Icon name="restart" size={14}/> Restart</Button>
                  : <Button variant="ghost" size="sm" title="Bring up"><Icon name="power" size={14}/> Bring up</Button>}
                {st.state==='up' && <Button variant="ghost" size="sm" className="text-muted-foreground" title="Take down"><Icon name="power" size={14}/> Down</Button>}
              </div>
            </Card>
          );
        })}
      </div>

      <PortEditor port={edit} onClose={()=>setEdit(null)} onSave={(p)=>{
        setPorts(prev => {
          const exists = prev.find(x=>x.id===p.id);
          if (exists) return prev.map(x=>x.id===p.id?p:x);
          return [...prev, p];
        });
        setEdit(null);
      }} />
    </div>
  );
}

// NinoTNC hardware "test button" decode, flashed on the Ports screen
function NinoTestFlash({ test, onDismiss, onConfigure }) {
  return (
    <Card className={cn("mb-4 p-0 overflow-hidden", test.softwareControl ? "border-success/40" : "border-primary/40")}>
      <div className="flex items-start gap-3 p-4">
        <div className={cn("mt-0.5 grid h-9 w-9 shrink-0 place-items-center rounded-md", test.softwareControl ? "bg-success/15 text-success" : "bg-primary/15 text-primary")}>
          <Icon name="radio" size={18}/>
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-sm font-semibold">NinoTNC test frame received</span>
            <Badge variant="muted">{test.portId}</Badge>
            <span className="text-xs text-muted-foreground">{test.receivedAt}</span>
          </div>
          <p className="mt-1 text-xs text-muted-foreground">
            <span className="font-mono text-foreground/80">{test.firmware}</span> · mode {test.mode} <span className="text-foreground/80">{test.modeLabel}</span> · RSSI {test.rssiDbm} dBm · CRC {test.crcOk ? "OK" : "FAIL"}
          </p>
          {!test.softwareControl && (
            <div className="mt-2.5 flex items-start gap-2 rounded-md bg-primary/10 px-2.5 py-2 text-xs text-primary">
              <Icon name="info" size={14} className="mt-px shrink-0"/>
              <span>This modem is in <strong>hardware control</strong> — TX delay and mode are set by its DIP switches. Switch it to <strong>software-control mode</strong> so pdn can set them remotely (the two parameters that matter most for a healthy link).</span>
            </div>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-1">
          {!test.softwareControl && <Button variant="outline" size="sm" onClick={onConfigure}>How to enable</Button>}
          <Button variant="ghost" size="iconSm" onClick={onDismiss}><Icon name="x" size={15}/></Button>
        </div>
      </div>
    </Card>
  );
}

// transport + parameter editor: profile-first, with a custom escape hatch
function PortEditor({ port, onClose, onSave }) {
  const seed = (p) => p ? { ...p, ax25:{...AX25_DEFAULTS,...(p.ax25||{})}, kiss:{...KISS_DEFAULTS,...(p.kiss||{})}, setup: p.setup || PORT_SETUP[p.id] || {radio:RADIO_PROFILES[0].id,channel:"shared",difficulty:"moderate",custom:true} } : p;
  const [draft, setDraft] = useState(seed(port));
  const [srcId, setSrcId] = useState(port ? port.id : undefined);
  const [confirm, setConfirm] = useState(false);
  if (port && port.id !== srcId) { setSrcId(port.id); setDraft(seed(port)); }
  if (!port) return null;
  const model = draft || seed(port);
  const t = model.transport;
  const setup = model.setup;
  const usesKiss = KIND_USES_KISS[t.kind];

  const setT = (patch) => setDraft(d => ({ ...d, transport: { ...d.transport, ...patch } }));
  const setKind = (kind) => {
    const defaults = {
      "kiss-tcp": { kind, host:"127.0.0.1", port:8001 },
      "serial-kiss": { kind, device:"/dev/ttyUSB0", baud:38400 },
      "nino-tnc": { kind, device:"/dev/ttyACM0", mode:4 },   // wire baud fixed at 57600
      "axudp": { kind, host:"44.0.0.1", port:10093, localPort:10093 },
    };
    setDraft(d => ({ ...d, transport: defaults[kind] }));
  };
  const setSetup = (patch) => setDraft(d => ({ ...d, setup: { ...d.setup, ...patch } }));
  const setAx = (k,v) => setDraft(d => ({ ...d, ax25: { ...d.ax25, [k]: v } }));
  const setKiss = (k,v) => setDraft(d => ({ ...d, kiss: { ...d.kiss, [k]: v } }));

  const profile = RADIO_PROFILES.find(r=>r.id===setup.radio);
  const baseline = profile ? profile.baseline : { ...AX25_DEFAULTS, ...KISS_DEFAULTS };
  const applyProfile = (radioId) => {
    const r = RADIO_PROFILES.find(x=>x.id===radioId);
    setDraft(d => {
      const next = { ...d, setup:{...d.setup, radio:radioId, custom:false} };
      if (r) {
        next.ax25 = { ...d.ax25, t1Ms:r.baseline.t1Ms, t2Ms:r.baseline.t2Ms, t3Ms:r.baseline.t3Ms, n2:r.baseline.n2, windowSize:r.baseline.windowSize };
        next.kiss = { ...d.kiss, txDelay:r.baseline.txDelay, slotTime:r.baseline.slotTime, txTail:r.baseline.txTail, persistence:r.baseline.persistence };
        if (next.transport.kind==="nino-tnc") next.transport = { ...next.transport, mode:r.ninoMode };
      }
      return next;
    });
  };
  const resetToProfile = () => applyProfile(setup.radio);

  // disruption summary for the save confirmation (plain language)
  const transportChanged = JSON.stringify(model.transport) !== JSON.stringify(port.transport || {});
  const idChanged = !port._new && model.id !== port.id;
  const enabledChanged = !port._new && model.enabled !== port.enabled;
  const sessions = PORT_STATUS[port.id] ? PORT_STATUS[port.id].sessionCount : 0;
  let disrupt;
  if (port._new) disrupt = { tone:"success", text:`Port ${model.id||"(new)"} will be created and brought up.` };
  else if (idChanged) disrupt = { tone:"danger", text:`Renaming a port restarts the node — every session on every port drops.` };
  else if (transportChanged || enabledChanged) disrupt = { tone:"warning", text:`Port ${port.id} will restart.${sessions>0?` ${sessions} session${sessions>1?"s":""} on this port will drop.`:" No sessions are connected."}` };
  else disrupt = { tone:"success", text:`Modem parameters apply live to ${port.id}. No sessions drop.` };

  return (
    <Sheet open={!!port} onClose={onClose}
      title={port._new ? "Add port" : `Edit port — ${port.id}`}
      subtitle="Pick a profile, or open the parameters to fine-tune"
      footer={<>
        <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
        <Button size="sm" onClick={()=>setConfirm(true)}><Icon name="check" size={14}/> Save changes</Button>
      </>}>
      <div className="space-y-5">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Port id" info="A short name you choose for this port (e.g. vhf-1). Used in logs, sessions, and the monitor.">
            <Input value={model.id} onChange={e=>setDraft(d=>({...d,id:e.target.value}))} className="font-mono" placeholder="vhf-1"/>
          </Field>
          <Field label="Enabled" info="Whether pdn brings this port up. Disabling it takes the port down.">
            <div className="flex h-9 items-center"><Switch checked={model.enabled} onChange={v=>setDraft(d=>({...d,enabled:v}))}/></div>
          </Field>
        </div>

        {/* transport */}
        <div className="rounded-lg border border-border p-3">
          <Label className="text-foreground">Connection</Label>
          <div className="mt-3">
            <Field label="Type" info="How pdn reaches the modem: a KISS TNC over TCP or serial, a NinoTNC, or an AXUDP network link.">
              <Select value={t.kind} onChange={e=>setKind(e.target.value)}>
                <option value="kiss-tcp">{KIND_LABEL["kiss-tcp"]} — KISS TNC over TCP</option>
                <option value="serial-kiss">{KIND_LABEL["serial-kiss"]} — KISS TNC over serial</option>
                <option value="nino-tnc">{KIND_LABEL["nino-tnc"]} — NinoTNC</option>
                <option value="axudp">{KIND_LABEL["axudp"]} — AXUDP network link</option>
              </Select>
            </Field>
          </div>
          <div className="mt-3 grid grid-cols-2 gap-3">
            {t.kind==="kiss-tcp" && <>
              <Field label="Host"><Input value={t.host} onChange={e=>setT({host:e.target.value})} className="font-mono"/></Field>
              <Field label="TCP port"><Input type="number" value={t.port} onChange={e=>setT({port:+e.target.value})} className="font-mono"/></Field>
            </>}
            {t.kind==="serial-kiss" && <>
              <Field label="Serial device"><Input value={t.device} onChange={e=>setT({device:e.target.value})} className="font-mono"/></Field>
              <Field label="Baud"><Input type="number" value={t.baud} onChange={e=>setT({baud:+e.target.value})} className="font-mono"/></Field>
            </>}
            {t.kind==="nino-tnc" && <>
              <Field label="Serial device"><Input value={t.device} onChange={e=>setT({device:e.target.value})} className="font-mono"/></Field>
              <Field label="USB wire speed" info="A NinoTNC always runs at 57600 baud on the USB-serial wire. The radio-side speed is set by the modem mode below, not here.">
                <div className="flex h-9 items-center rounded-md border border-input bg-muted/40 px-3 font-mono text-sm text-muted-foreground">57600 <span className="ml-1.5 text-[11px]">fixed</span></div>
              </Field>
              <Field label="Modem mode" info="The radio-side modulation and speed. Served by the node from the NinoTNC firmware's table. In software-control mode pdn can set this remotely." className="col-span-2">
                <Select value={t.mode} onChange={e=>setT({mode:+e.target.value})}>
                  {NINO_MODES.map(m=><option key={m.mode} value={m.mode}>mode {m.mode} — {m.label}</option>)}
                </Select>
              </Field>
            </>}
            {t.kind==="axudp" && <>
              <Field label="Peer host"><Input value={t.host} onChange={e=>setT({host:e.target.value})} className="font-mono"/></Field>
              <Field label="Peer port"><Input type="number" value={t.port} onChange={e=>setT({port:+e.target.value})} className="font-mono"/></Field>
              <Field label="Local port"><Input type="number" value={t.localPort} onChange={e=>setT({localPort:+e.target.value})} className="font-mono"/></Field>
            </>}
          </div>
        </div>

        {/* profile-first setup */}
        <div className="rounded-lg border border-border p-3">
          <div className="mb-3 flex items-center justify-between">
            <Label className="text-foreground">Profile</Label>
            {setup.custom && <Badge variant="warning">customised</Badge>}
          </div>
          <div className="space-y-3">
            <Field label="Radio profile" info="A starting point for this kind of radio and speed. pdn fills in sensible timing and modem parameters; you can fine-tune below.">
              <Select value={setup.radio||""} onChange={e=>applyProfile(e.target.value)}>
                {RADIO_PROFILES.map(r=><option key={r.id} value={r.id}>{r.name}</option>)}
              </Select>
            </Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Channel use" info={CHANNEL_MODES.map(c=>`${c.name}: ${c.help}`).join("  ")}>
                <SegMode options={CHANNEL_MODES} value={setup.channel} onChange={v=>setSetup({channel:v})}/>
              </Field>
              <Field label="Link difficulty" info={LINK_DIFFICULTY.map(d=>`${d.name}: ${d.help}`).join("  ")}>
                <SegMode options={LINK_DIFFICULTY} value={setup.difficulty} onChange={v=>setSetup({difficulty:v})}/>
              </Field>
            </div>
          </div>
        </div>

        {/* advanced parameters */}
        <details className="rounded-lg border border-border" open={setup.custom}>
          <summary onClick={(e)=>{ e.preventDefault(); setSetup({custom:!setup.custom}); }} className="flex cursor-pointer list-none items-center justify-between p-3 text-sm font-medium text-foreground">
            <span className="flex items-center gap-2"><Icon name="config" size={14} className="text-muted-foreground"/> Advanced parameters</span>
            <span className="flex items-center gap-2">
              {setup.custom && <button onClick={(e)=>{ e.preventDefault(); e.stopPropagation(); resetToProfile(); }} className="text-xs font-normal text-muted-foreground hover:text-primary">Reset to profile</button>}
              <Icon name="chevDown" size={15} className={cn("text-muted-foreground transition-transform", setup.custom && "rotate-180")}/>
            </span>
          </summary>
          {setup.custom && (
            <div className="space-y-4 border-t border-border p-3">
              <div>
                <p className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Link timing</p>
                <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
                  <ParamField k="t1Ms" value={model.ax25.t1Ms} base={baseline.t1Ms} onChange={v=>setAx('t1Ms',v)}/>
                  <ParamField k="t2Ms" value={model.ax25.t2Ms} base={baseline.t2Ms} onChange={v=>setAx('t2Ms',v)}/>
                  <ParamField k="t3Ms" value={model.ax25.t3Ms} base={baseline.t3Ms} onChange={v=>setAx('t3Ms',v)}/>
                  <ParamField k="n2" value={model.ax25.n2} base={baseline.n2} onChange={v=>setAx('n2',v)}/>
                  <ParamField k="windowSize" value={model.ax25.windowSize} base={baseline.windowSize} onChange={v=>setAx('windowSize',v)}/>
                </div>
              </div>
              {usesKiss && (
                <div>
                  <p className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Modem keying</p>
                  <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
                    <ParamField k="txDelay" value={model.kiss.txDelay} base={baseline.txDelay} onChange={v=>setKiss('txDelay',v)}/>
                    <ParamField k="txTail" value={model.kiss.txTail} base={baseline.txTail} onChange={v=>setKiss('txTail',v)}/>
                    <ParamField k="slotTime" value={model.kiss.slotTime} base={baseline.slotTime} onChange={v=>setKiss('slotTime',v)}/>
                  </div>
                  <div className="mt-3">
                    <PersistenceField value={model.kiss.persistence} base={baseline.persistence} onChange={v=>setKiss('persistence',v)}/>
                  </div>
                </div>
              )}
            </div>
          )}
        </details>
      </div>

      <Modal open={confirm} onClose={()=>setConfirm(false)} title="Apply changes?" width="max-w-md" footer={<>
        <Button variant="outline" size="sm" onClick={()=>setConfirm(false)}>Cancel</Button>
        <Button size="sm" className={disrupt.tone==="danger"?"bg-danger hover:bg-danger/90 text-danger-foreground":disrupt.tone==="warning"?"bg-warning hover:bg-warning/90 text-warning-foreground":""} onClick={()=>{ setConfirm(false); onSave(model); }}>
          {disrupt.tone==="success" ? <><Icon name="check" size={14}/> Apply</> : <><Icon name="alert" size={14}/> Apply anyway</>}
        </Button>
      </>}>
        <div className={cn("flex items-start gap-3 rounded-lg border p-3 text-sm",
          disrupt.tone==="danger"?"border-danger/30 bg-danger/5 text-danger":disrupt.tone==="warning"?"border-warning/30 bg-warning/5 text-warning":"border-success/30 bg-success/5 text-success")}>
          <Icon name={disrupt.tone==="success"?"check":"alert"} size={16} className="mt-0.5 shrink-0"/>
          <span>{disrupt.text}</span>
        </div>
      </Modal>
    </Sheet>
  );
}

// segmented control for short option sets, with per-option tooltip
function SegMode({ options, value, onChange }) {
  return (
    <div className="inline-flex w-full rounded-md border border-input p-0.5">
      {options.map(o=>(
        <Tooltip key={o.id} text={o.help} className="flex-1">
          <button onClick={()=>onChange(o.id)} className={cn("w-full rounded px-2 py-1.5 text-xs font-medium transition-colors", value===o.id ? "bg-primary text-primary-foreground" : "text-muted-foreground hover:text-foreground")}>{o.name}</button>
        </Tooltip>
      ))}
    </div>
  );
}

// a single tuneable: friendly label + help + unit + "modified" marker
function ParamField({ k, value, base, onChange }) {
  const meta = PARAM_HELP[k];
  const modified = base !== undefined && value !== base;
  return (
    <Field label={meta.label} info={meta.help} badge={modified ? <Tooltip text={`Default for this profile: ${base}${meta.unit?(' '+meta.unit):''}`}><Badge variant="warning">modified</Badge></Tooltip> : null}>
      <div className="relative">
        <Input type="number" value={value??''} onChange={e=>onChange(+e.target.value)} className={cn("font-mono", meta.unit && "pr-12", modified && "border-warning/60")}/>
        {meta.unit && <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-[11px] text-muted-foreground">{meta.unit}</span>}
      </div>
    </Field>
  );
}

// persistence as a 0–100% slider (stored as a 0–255 byte)
function PersistenceField({ value, base, onChange }) {
  const meta = PARAM_HELP.persistence;
  const pct = persistPct(value);
  const modified = base !== undefined && value !== base;
  return (
    <Field label={meta.label} info={meta.help} badge={modified ? <Tooltip text={`Default for this profile: ${persistPct(base)}%`}><Badge variant="warning">modified</Badge></Tooltip> : null}>
      <div className="flex items-center gap-3">
        <Slider value={pct} min={0} max={100} onChange={p=>onChange(pctToPersist(p))}/>
        <span className="tnum w-20 shrink-0 text-right font-mono text-xs text-muted-foreground">{pct}% <span className="text-muted-foreground/50">({value})</span></span>
      </div>
    </Field>
  );
}

// ---------- 4.8 Config editor -------------------------------
function ConfigEditor({ onNavigate }) {
  const [tab, setTab] = useState("identity");
  const [cfg, setCfg] = useState(NODE_CONFIG);
  const [dirty, setDirty] = useState([]); // list of {path,impact}
  const [showReconcile, setShowReconcile] = useState(false);
  const [mode, setMode] = useState("forms"); // forms | raw

  const touch = (path, impact) => setDirty(d => d.find(x=>x.path===path) ? d : [...d, { path, impact }]);
  const set = (path, val, impact) => {
    touch(path, impact);
    setCfg(c => {
      const next = JSON.parse(JSON.stringify(c));
      const keys = path.split('.'); let o = next;
      for (let i=0;i<keys.length-1;i++) o = o[keys[i]];
      o[keys[keys.length-1]] = val;
      return next;
    });
  };

  const tabs = [
    { id:"identity", label:"Identity" },
    { id:"services", label:"Services" },
    { id:"management", label:"Management" },
    { id:"netrom", label:"NET/ROM + INP3" },
    { id:"beacons", label:"Beacons" },
  ];

  return (
    <div>
      <PageHeader title="Config" subtitle="Edit the whole NodeConfig — validate before apply, every write through the reconcile path" actions={
        <div className="flex items-center gap-2">
          <Tabs active={mode} onChange={setMode} tabs={[{id:"forms",label:"Forms"},{id:"raw",label:"Raw YAML"}]} />
          {dirty.length>0 && <Button size="sm" onClick={()=>setShowReconcile(true)}><Icon name="check" size={14}/> Review & apply <Badge variant="secondary" className="ml-1">{dirty.length}</Badge></Button>}
        </div>
      } />

      {mode==="forms" ? (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-[200px_1fr]">
          <div className="flex gap-1 overflow-x-auto lg:flex-col">
            {tabs.map(t=>(
              <button key={t.id} onClick={()=>setTab(t.id)} className={cn("whitespace-nowrap rounded-md px-3 py-2 text-left text-sm font-medium transition-colors", tab===t.id?"bg-primary/10 text-primary":"text-muted-foreground hover:bg-accent hover:text-foreground")}>{t.label}</button>
            ))}
            <button onClick={()=>onNavigate && onNavigate("ports")} className="flex items-center justify-between gap-2 whitespace-nowrap rounded-md px-3 py-2 text-left text-sm font-medium text-muted-foreground hover:bg-accent hover:text-foreground" title="Ports are edited on the Ports screen">Ports <Icon name="external" size={13}/></button>
          </div>

          <Card className="p-5">
            {tab==="identity" && <section className="max-w-md space-y-4">
              <Field label="Callsign (required)" impact="node-reset" hint="Changing identity resets the node.">
                <Input value={cfg.identity.callsign} onChange={e=>set('identity.callsign', e.target.value, 'node-reset')} className="font-mono"/>
              </Field>
              <Field label="Alias" impact="node-reset"><Input value={cfg.identity.alias} onChange={e=>set('identity.alias', e.target.value, 'node-reset')} className="font-mono"/></Field>
              <Field label="Locator (grid)" impact="live"><Input value={cfg.identity.grid} onChange={e=>set('identity.grid', e.target.value, 'live')} className="font-mono"/></Field>
            </section>}

            {tab==="services" && <section className="max-w-xl space-y-4">
              <Field label="Banner" hint="{node} and {call} are templated." impact="live"><Input value={cfg.services.banner} onChange={e=>set('services.banner', e.target.value, 'live')} className="font-mono text-xs"/></Field>
              <Field label="Prompt" impact="live"><Input value={cfg.services.prompt} onChange={e=>set('services.prompt', e.target.value, 'live')} className="font-mono"/></Field>
            </section>}

            {tab==="management" && <section className="max-w-xl space-y-5">
              <div className="rounded-lg border border-border p-3">
                <div className="mb-3 flex items-center justify-between"><Label className="text-foreground">HTTP (this UI)</Label><ImpactBadge impact="node-reset"/></div>
                <div className="grid grid-cols-2 gap-3">
                  <Field label="Bind"><Input value={cfg.management.http.bind} onChange={e=>set('management.http.bind', e.target.value, 'node-reset')} className="font-mono"/></Field>
                  <Field label="Port"><Input type="number" value={cfg.management.http.port} onChange={e=>set('management.http.port', +e.target.value, 'node-reset')} className="font-mono"/></Field>
                </div>
              </div>
              <div className="rounded-lg border border-border p-3">
                <div className="mb-3 flex items-center justify-between"><Label className="text-foreground">Telnet console</Label><ImpactBadge impact="port-restart"/></div>
                <div className="grid grid-cols-3 gap-3">
                  <Field label="Enabled"><div className="flex h-9 items-center"><Switch checked={cfg.management.telnet.enabled} onChange={v=>set('management.telnet.enabled', v, 'port-restart')}/></div></Field>
                  <Field label="Bind"><Input value={cfg.management.telnet.bind} onChange={e=>set('management.telnet.bind', e.target.value, 'port-restart')} className="font-mono"/></Field>
                  <Field label="Port"><Input type="number" value={cfg.management.telnet.port} onChange={e=>set('management.telnet.port', +e.target.value, 'port-restart')} className="font-mono"/></Field>
                </div>
              </div>
            </section>}

            {tab==="netrom" && <section className="space-y-5">
              <p className="max-w-2xl text-sm text-muted-foreground">NET/ROM is the layer that turns your node from a single radio link into part of a routed network — it learns which stations are reachable and relays traffic between them. The switches below decide how much your node takes part.</p>

              <div className="space-y-2">
                {["enabled","broadcast","connect","forward"].map(k=>(
                  <ToggleRow key={k} label={NETROM_TOGGLE_HELP[k].label} desc={NETROM_TOGGLE_HELP[k].desc} checked={cfg.netRom[k]} onChange={v=>set('netRom.'+k, v, 'live')}/>
                ))}
              </div>

              <div className="max-w-xs">
                <Field label={NETROM_FIELD_HELP.alias.label} info={NETROM_FIELD_HELP.alias.help}>
                  <Input value={cfg.netRom.alias} onChange={e=>set('netRom.alias', e.target.value, 'live')} className="font-mono"/>
                </Field>
              </div>

              <AdvancedDetails title="Advanced routing tuning">
                <p className="mb-3 text-xs text-muted-foreground">Most nodes never touch these — the defaults are sensible. Adjust only if you understand the trade-off.</p>
                <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
                  {["defaultNeighbourQuality","minQuality","sweepIntervalSeconds","timeToLive","window"].map(k=>(
                    <GuidedNum key={k} meta={NETROM_FIELD_HELP[k]} value={cfg.netRom[k]} onChange={v=>set('netRom.'+k, v, 'live')}/>
                  ))}
                </div>
              </AdvancedDetails>

              <div className="rounded-lg border border-primary/30 bg-primary/5 p-4">
                <Label className="flex items-center gap-1.5 text-primary"><Icon name="signal" size={14}/> INP3 time-routing</Label>
                <p className="mt-1.5 max-w-2xl text-xs text-muted-foreground">An overlay that measures the <strong>actual round-trip time</strong> to each destination. Plain NET/ROM picks routes by a static quality score; INP3 lets the node prefer the route that's genuinely fastest right now.</p>
                <div className="mt-3 space-y-2">
                  <ToggleRow label="Use INP3 time-routing" desc="Measure and track real path times across the network." checked={cfg.netRom.inp3.enabled} onChange={v=>set('netRom.inp3.enabled', v, 'live')}/>
                  <ToggleRow label="Prefer the faster route" desc="When a measured time is available, choose routes by speed ahead of the static quality score." checked={cfg.netRom.inp3.preferInp3Routes} onChange={v=>set('netRom.inp3.preferInp3Routes', v, 'live')}/>
                </div>
                <div className="mt-3">
                  <AdvancedDetails title="INP3 timing intervals">
                    <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                      {["l3RttInterval","l3RttResetWindow","rifInterval","positiveDebounce"].map(k=>(
                        <GuidedNum key={k} meta={INP3_FIELD_HELP[k]} value={cfg.netRom.inp3[k]} onChange={v=>set('netRom.inp3.'+k, v, 'live')}/>
                      ))}
                    </div>
                  </AdvancedDetails>
                </div>
              </div>
            </section>}

            {tab==="beacons" && <BeaconsSection/>}
          </Card>
        </div>
      ) : (
        <RawYaml cfg={cfg} onValidate={()=>setShowReconcile(true)} dirtyCount={dirty.length} />
      )}

      <ReconcilePreview open={showReconcile} dirty={dirty} onClose={()=>setShowReconcile(false)} onApply={()=>{ setShowReconcile(false); setDirty([]); }} />
    </div>
  );
}

function ToggleField({ label, checked, onChange }) {
  return <div className="flex items-center justify-between gap-3"><Label className="text-foreground">{label}</Label><Switch checked={checked} onChange={onChange}/></div>;
}

// labelled on/off choice with a one-line plain-English description
function ToggleRow({ label, desc, checked, onChange }) {
  return (
    <div className="flex items-start justify-between gap-4 rounded-lg border border-border p-3">
      <div className="min-w-0">
        <p className="text-sm font-medium text-foreground">{label}</p>
        {desc && <p className="mt-0.5 text-xs leading-snug text-muted-foreground">{desc}</p>}
      </div>
      <div className="pt-0.5 shrink-0"><Switch checked={checked} onChange={onChange}/></div>
    </div>
  );
}

// numeric field driven by a {label, unit, help} descriptor (tooltip + unit suffix)
function GuidedNum({ meta, value, onChange }) {
  const suffix = meta.unit && !meta.unit.includes("–") ? meta.unit : null;
  return (
    <Field label={meta.label} info={meta.help}>
      <div className="relative">
        <Input type="number" value={value} onChange={e=>onChange(+e.target.value)} className={cn("font-mono", suffix && "pr-16")}/>
        {suffix && <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-[11px] text-muted-foreground">{suffix}</span>}
      </div>
    </Field>
  );
}

// collapsible "advanced" panel (closed by default)
function AdvancedDetails({ title, children }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="rounded-lg border border-border">
      <button onClick={()=>setOpen(o=>!o)} className="flex w-full items-center justify-between p-3 text-sm font-medium text-foreground">
        <span className="flex items-center gap-2"><Icon name="config" size={14} className="text-muted-foreground"/> {title}</span>
        <Icon name="chevDown" size={15} className={cn("text-muted-foreground transition-transform", open && "rotate-180")}/>
      </button>
      {open && <div className="border-t border-border p-3">{children}</div>}
    </div>
  );
}

// ID beacons — system default + per-port overrides
function BeaconsSection() {
  const [def, setDef] = useState(BEACON_DEFAULT);
  const [ports, setPorts] = useState(PORT_BEACONS);
  const setPort = (id, patch) => setPorts(p => ({ ...p, [id]: { ...p[id], ...patch } }));

  return (
    <section className="space-y-5">
      <div className="rounded-lg border border-border p-4">
        <div className="mb-3 flex items-center gap-1.5">
          <Label className="text-foreground">System default beacon</Label>
          <InfoHint text="The ID beacon pdn sends on a port unless that port overrides it. {node} and {call} are filled in automatically."/>
        </div>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-[160px_1fr]">
          <Field label="Every" info="How often the ID beacon is transmitted.">
            <div className="relative">
              <Input type="number" value={def.intervalMinutes} onChange={e=>setDef(d=>({...d,intervalMinutes:+e.target.value}))} className="font-mono pr-16"/>
              <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-[11px] text-muted-foreground">minutes</span>
            </div>
          </Field>
          <Field label="Text"><Input value={def.text} onChange={e=>setDef(d=>({...d,text:e.target.value}))} className="font-mono text-xs"/></Field>
        </div>
      </div>

      <div>
        <p className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Per-port</p>
        <div className="space-y-2">
          {Object.keys(ports).map(id => {
            const b = ports[id];
            const overriding = b.text != null;
            return (
              <div key={id} className="rounded-lg border border-border p-3">
                <div className="flex items-center justify-between">
                  <span className="flex items-center gap-2.5">
                    <Switch checked={b.enabled} onChange={v=>setPort(id,{enabled:v})}/>
                    <span className="font-mono text-sm font-semibold">{id}</span>
                    {!b.enabled && <Badge variant="muted">no beacon</Badge>}
                  </span>
                  {b.enabled && <span className="flex items-center gap-2 text-xs text-muted-foreground">
                    every
                    <Input type="number" value={b.intervalMinutes} onChange={e=>setPort(id,{intervalMinutes:+e.target.value})} className="h-7 w-16 font-mono text-xs"/>
                    min
                  </span>}
                </div>
                {b.enabled && (
                  <div className="mt-3">
                    {overriding ? (
                      <Field label="Custom text" badge={<button onClick={()=>setPort(id,{text:null})} className="text-[11px] text-muted-foreground hover:text-primary">use default</button>}>
                        <Input value={b.text} onChange={e=>setPort(id,{text:e.target.value})} className="font-mono text-xs"/>
                      </Field>
                    ) : (
                      <div className="flex items-center justify-between rounded-md bg-muted/40 px-2.5 py-2 text-xs">
                        <span className="text-muted-foreground">Uses default — <span className="font-mono text-foreground/70">{def.text}</span></span>
                        <button onClick={()=>setPort(id,{text:def.text})} className="shrink-0 font-medium text-primary hover:underline">Override</button>
                      </div>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>
    </section>
  );
}

function RawYaml({ cfg, onValidate, dirtyCount }) {
  const yaml = toYaml(cfg);
  return (
    <Card className="overflow-hidden p-0">
      <div className="flex items-center justify-between border-b border-border bg-muted/30 px-4 py-2">
        <span className="flex items-center gap-2 text-xs text-muted-foreground"><Icon name="config" size={13}/> node-config.yaml · advanced</span>
        <div className="flex items-center gap-2">
          <span className="flex items-center gap-1.5 text-xs text-success"><Icon name="check" size={13}/> valid</span>
          <Button variant="outline" size="xs" onClick={onValidate}>Validate & preview</Button>
        </div>
      </div>
      <textarea spellCheck={false} defaultValue={yaml} className="h-[calc(100vh-20rem)] w-full resize-none bg-background/40 p-4 font-mono text-xs leading-relaxed text-foreground/90 focus:outline-none"/>
    </Card>
  );
}

// reconcile preview modal — the safety story
function ReconcilePreview({ open, dirty, onClose, onApply }) {
  const live = dirty.filter(d=>d.impact==='live');
  const restart = dirty.filter(d=>d.impact==='port-restart');
  const reset = dirty.filter(d=>d.impact==='node-reset');
  return (
    <Modal open={open} onClose={onClose} width="max-w-lg" title="Reconcile preview" footer={<>
      <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
      <Button size="sm" onClick={onApply} className={reset.length?"bg-danger hover:bg-danger/90 text-danger-foreground":""}>
        {reset.length ? <><Icon name="alert" size={14}/> Apply — node reset</> : <><Icon name="check" size={14}/> Apply atomically</>}
      </Button>
    </>}>
      <div className="space-y-3">
        <p className="text-sm text-muted-foreground">Your changes are checked before anything is applied — a bad edit never reaches the running node. Valid changes are applied all at once.</p>
        <ReconcileGroup variant="success" icon="check" title={`${live.length} apply live`} items={live} desc="hot-applied, no disruption"/>
        <ReconcileGroup variant="warning" icon="restart" title={`${restart.length} restart a port`} items={restart} desc="single-port bounce, sessions on that port drop"/>
        <ReconcileGroup variant="danger" icon="alert" title={`${reset.length} reset the node`} items={reset} desc="node-wide restart, all sessions drop"/>
      </div>
    </Modal>
  );
}
function ReconcileGroup({ variant, icon, title, items, desc }) {
  if (items.length===0) return null;
  const c = { success:"border-success/30 bg-success/5 text-success", warning:"border-warning/30 bg-warning/5 text-warning", danger:"border-danger/30 bg-danger/5 text-danger" }[variant];
  return (
    <div className={cn("rounded-lg border p-3", c)}>
      <p className="flex items-center gap-2 text-sm font-semibold"><Icon name={icon} size={14}/> {title}</p>
      <p className="mt-0.5 text-xs opacity-80">{desc}</p>
      <ul className="mt-2 space-y-0.5">
        {items.map(i=><li key={i.path} className="font-mono text-[11px] text-foreground/70">· {i.path}</li>)}
      </ul>
    </div>
  );
}

// minimal YAML serializer for the raw view
function toYaml(obj, indent=0) {
  const pad = "  ".repeat(indent);
  let out = "";
  for (const [k,v] of Object.entries(obj)) {
    if (v===null || v===undefined) { out += `${pad}${k}: null\n`; }
    else if (Array.isArray(v)) {
      out += `${pad}${k}:\n`;
      v.forEach(item => {
        if (typeof item==='object') { out += `${pad}  - ` + toYaml(item, indent+2).replace(/^\s+/, "").replace(/\n {0,}(\S)/g, (m,c,o)=> o===0?m:`\n${pad}    ${c}`); }
        else out += `${pad}  - ${item}\n`;
      });
    }
    else if (typeof v==='object') { out += `${pad}${k}:\n` + toYaml(v, indent+1); }
    else out += `${pad}${k}: ${v}\n`;
  }
  return out;
}

// ---------- 4.9 Users ---------------------------------------
function Users() {
  const [users, setUsers] = useState(USERS.map(u=>({ ...u, totpEnrolled: !!u.totpEnrolled })));
  const [enroll, setEnroll] = useState(null);
  const setTotp = (name, val) => setUsers(us=>us.map(u=>u.name===name?{...u, totpEnrolled:val}:u));

  return (
    <div>
      <PageHeader title="Users" subtitle="Operators & access — single-admin in this slice" actions={
        <Button size="sm" variant="outline" disabled title="Multi-user is a later extension"><Icon name="plus" size={14}/> Add user</Button>
      } />

      <div className="space-y-3">
        {users.map(u=>(
          <Card key={u.name} className="p-4">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="flex items-center gap-3">
                <span className="grid h-9 w-9 place-items-center rounded-full bg-primary/15 text-sm font-semibold text-primary">{u.name.slice(0,2)}</span>
                <div>
                  <div className="flex items-center gap-2"><span className="font-medium">{u.name}</span><Badge variant="default">{u.role}</Badge></div>
                  <p className="mt-0.5 text-xs text-muted-foreground">last login {u.lastLogin}</p>
                </div>
              </div>
              <div className="flex gap-1">{u.scopes.map(s=><Badge key={s} variant="muted">{s}</Badge>)}</div>
            </div>

            <div className="mt-4 space-y-2 border-t border-border pt-4">
              <p className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Web login</p>
              <AuthMethod icon="key" title="Password" sub="Argon2id" enabled action={<Button variant="ghost" size="xs">Reset</Button>}/>
              <AuthMethod icon="fingerprint" title="Passkeys" sub={u.passkeys>0?`${u.passkeys} enrolled`:"none"} enabled={u.passkeys>0} action={<Button variant="ghost" size="xs"><Icon name="plus" size={12}/> Add passkey</Button>}/>

              <p className="pt-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">On-air auth</p>
              <AuthMethod icon="signal" title="Authenticator (TOTP)" sub={u.totpEnrolled?"6-digit code, enrolled":"prove identity over a packet session"} enabled={u.totpEnrolled}
                action={u.totpEnrolled
                  ? <div className="flex items-center gap-1"><Button variant="ghost" size="xs" onClick={()=>setEnroll(u.name)}>Re-enrol</Button><Button variant="ghost" size="xs" className="text-danger" onClick={()=>setTotp(u.name,false)}>Remove</Button></div>
                  : <Button size="xs" onClick={()=>setEnroll(u.name)}><Icon name="plus" size={12}/> Enrol</Button>}/>
            </div>
          </Card>
        ))}
      </div>

      <div className="mt-4 flex items-start gap-2 rounded-lg border border-border bg-muted/30 p-4 text-sm text-muted-foreground">
        <Icon name="info" size={16} className="mt-0.5 shrink-0"/>
        <div>
          <p className="font-medium text-foreground">Two worlds of auth</p>
          <p className="mt-0.5 text-xs"><strong className="text-foreground/80">Web login</strong> uses passkeys / password over HTTPS. <strong className="text-foreground/80">On-air auth</strong> uses a TOTP code, because when someone reaches the node over a plain packet session there's no browser — just a 6-digit code they can type. Full multi-user CRUD is a later extension.</p>
        </div>
      </div>

      <TotpEnroll userName={enroll} onClose={()=>setEnroll(null)} onDone={()=>{ setTotp(enroll, true); setEnroll(null); }} />
    </div>
  );
}

// one authentication method row
function AuthMethod({ icon, title, sub, enabled, action }) {
  return (
    <div className="flex items-center justify-between gap-3 rounded-lg border border-border p-3">
      <div className="flex min-w-0 items-center gap-3">
        <span className={cn("grid h-8 w-8 shrink-0 place-items-center rounded-md", enabled?"bg-success/15 text-success":"bg-muted text-muted-foreground")}><Icon name={icon} size={16}/></span>
        <div className="min-w-0">
          <p className="flex items-center gap-2 text-sm font-medium">{title} {enabled ? <Badge variant="success">enabled</Badge> : <Badge variant="muted">not set</Badge>}</p>
          <p className="truncate text-xs text-muted-foreground">{sub}</p>
        </div>
      </div>
      <div className="shrink-0">{action}</div>
    </div>
  );
}

// TOTP enrollment flow — scan → verify → recovery codes
const TOTP_SECRET = "KZXW6YTB OI6Q SESH";
function TotpEnroll({ userName, onClose, onDone }) {
  const [step, setStep] = useState(0);
  const [code, setCode] = useState("");
  useEffect(()=>{ if(userName){ setStep(0); setCode(""); } }, [userName]);
  if (!userName) return null;
  const secret = TOTP_SECRET.replace(/ /g,"");
  const uri = `otpauth://totp/pdn:${userName}@GB7RDG?secret=${secret}&issuer=pdn%20GB7RDG&digits=6&period=30`;
  const recovery = ["8FK2-QP4M","ZT9X-7HRN","LM3C-W0AE","V6BD-1KXP"];

  return (
    <Modal open={true} onClose={onClose} width="max-w-md" title={`On-air authenticator — ${userName}`} footer={
      step===0 ? <>
        <Button variant="outline" size="sm" onClick={onClose}>Cancel</Button>
        <Button size="sm" onClick={()=>setStep(1)}>I've added it <Icon name="chevRight" size={14}/></Button>
      </> : step===1 ? <>
        <Button variant="outline" size="sm" onClick={()=>setStep(0)}>Back</Button>
        <Button size="sm" disabled={code.replace(/\D/g,"").length!==6} onClick={()=>setStep(2)}><Icon name="check" size={14}/> Verify & enable</Button>
      </> : <>
        <Button size="sm" onClick={onDone}><Icon name="check" size={14}/> Done</Button>
      </>
    }>
      {step===0 && (
        <div className="space-y-4">
          <p className="text-sm text-muted-foreground">Add this node to an authenticator app (Aegis, 1Password, Google Authenticator…). When this user connects to the node <strong className="text-foreground">on the air</strong>, the node asks for the current 6-digit code.</p>
          <div className="flex gap-4">
            <div className="grid h-32 w-32 shrink-0 place-items-center rounded-lg border border-border bg-muted/40 text-center">
              <div className="text-muted-foreground"><Icon name="radio" size={24} className="mx-auto"/><p className="mt-1 px-2 text-[10px] leading-tight">otpauth QR<br/><span className="text-muted-foreground/60">scan in app</span></p></div>
            </div>
            <div className="min-w-0 flex-1 space-y-2">
              <div>
                <Label>Manual key</Label>
                <div className="mt-1 flex items-center gap-2 rounded-md border border-border bg-background/60 px-2.5 py-1.5">
                  <span className="flex-1 font-mono text-sm tracking-wider">{TOTP_SECRET}</span>
                  <button className="text-muted-foreground hover:text-foreground" title="Copy"><Icon name="copy" size={14}/></button>
                </div>
              </div>
              <p className="break-all font-mono text-[10px] leading-snug text-muted-foreground/70">{uri}</p>
            </div>
          </div>
        </div>
      )}
      {step===1 && (
        <div className="space-y-4">
          <p className="text-sm text-muted-foreground">Enter the current 6-digit code from your app to confirm it's set up correctly.</p>
          <Input value={code} onChange={e=>setCode(e.target.value.replace(/\D/g,"").slice(0,6))} placeholder="123456" inputMode="numeric"
            className="text-center font-mono text-2xl tracking-[0.4em]" autoFocus/>
        </div>
      )}
      {step===2 && (
        <div className="space-y-4">
          <div className="flex items-center gap-2 rounded-md bg-success/10 px-3 py-2.5 text-sm text-success"><Icon name="check" size={16}/> On-air authenticator enabled for {userName}.</div>
          <div>
            <Label>Recovery codes</Label>
            <p className="mb-2 mt-0.5 text-xs text-muted-foreground">Store these safely — each can be used once if the authenticator is unavailable.</p>
            <div className="grid grid-cols-2 gap-2 rounded-md border border-border bg-muted/30 p-3 font-mono text-sm">
              {recovery.map(c=><span key={c}>{c}</span>)}
            </div>
          </div>
        </div>
      )}
    </Modal>
  );
}

Object.assign(window, { Ports, ConfigEditor, Users });
