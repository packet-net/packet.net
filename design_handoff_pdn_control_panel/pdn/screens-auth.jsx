// ============================================================
// pdn — Auth: Login (4.2) + First-run setup wizard (4.1)
// ============================================================

function AuthFrame({ children, footer }) {
  return (
    <div className="relative flex min-h-screen items-center justify-center overflow-hidden bg-background p-4">
      {/* quiet technical backdrop */}
      <div className="pointer-events-none absolute inset-0 opacity-[0.18]" style={{
        backgroundImage: "linear-gradient(hsl(var(--border)) 1px, transparent 1px), linear-gradient(90deg, hsl(var(--border)) 1px, transparent 1px)",
        backgroundSize: "44px 44px",
        maskImage: "radial-gradient(ellipse at center, black, transparent 72%)",
        WebkitMaskImage: "radial-gradient(ellipse at center, black, transparent 72%)",
      }} />
      <div className="absolute right-4 top-4"><ThemeToggle /></div>
      <div className="relative w-full max-w-sm">
        <div className="mb-6 flex flex-col items-center text-center">
          <Logo size={40} />
          <p className="mt-3 text-xs text-muted-foreground">amateur packet radio node</p>
        </div>
        {children}
        {footer}
      </div>
    </div>
  );
}

function Login({ onLogin }) {
  const [pw, setPw] = useState("");
  const [busy, setBusy] = useState(false);
  const passkey = () => { setBusy(true); setTimeout(onLogin, 700); };
  const password = () => { if(!pw) return; setBusy(true); setTimeout(onLogin, 500); };
  return (
    <AuthFrame footer={<p className="mt-6 text-center text-[11px] text-muted-foreground">GB7RDG · 127.0.0.1:8080</p>}>
      <Card className="p-6">
        <h1 className="text-lg font-semibold">Sign in</h1>
        <p className="mt-1 text-sm text-muted-foreground">Authenticate to manage this node.</p>

        <Button className="mt-5 w-full" onClick={passkey} disabled={busy}>
          <Icon name="fingerprint" size={16}/> Continue with passkey
        </Button>

        <div className="my-4 flex items-center gap-3 text-[11px] uppercase tracking-wide text-muted-foreground">
          <div className="h-px flex-1 bg-border"/>or password<div className="h-px flex-1 bg-border"/>
        </div>

        <div className="space-y-3">
          <Field label="Username"><Input defaultValue="tom" className="font-mono"/></Field>
          <Field label="Password">
            <Input type="password" value={pw} onChange={e=>setPw(e.target.value)} onKeyDown={e=>e.key==='Enter'&&password()} placeholder="••••••••"/>
          </Field>
          <Button variant="outline" className="w-full" onClick={password} disabled={busy}>Sign in</Button>
        </div>
      </Card>
    </AuthFrame>
  );
}

// 4.1 — 3-step onboarding wizard reached from one-time /setup?token=…
function Setup({ onDone, onSkip }) {
  const [step, setStep] = useState(0);
  const [data, setData] = useState({
    callsign: "", alias: "", grid: "",
    username: "admin", password: "", passkey: false,
    addPort: true, portId: "vhf-1", portKind: "nino-tnc", device: "/dev/ttyACM0", baud: 57600,
  });
  const set = (k,v) => setData(d=>({...d,[k]:v}));
  const steps = ["Station identity", "Admin login", "First port"];
  const canNext = step===0 ? !!data.callsign : step===1 ? (data.password.length>=8 || data.passkey) : true;

  return (
    <AuthFrame>
      <Card className="overflow-hidden p-0">
        {/* stepper */}
        <div className="flex items-center gap-2 border-b border-border bg-muted/30 px-5 py-3">
          {steps.map((s,i)=>(
            <React.Fragment key={s}>
              <div className="flex items-center gap-2">
                <span className={cn("grid h-6 w-6 place-items-center rounded-full text-xs font-semibold", i<step?"bg-success text-success-foreground":i===step?"bg-primary text-primary-foreground":"bg-muted text-muted-foreground")}>
                  {i<step ? <Icon name="check" size={13}/> : i+1}
                </span>
                <span className={cn("hidden text-xs font-medium sm:inline", i===step?"text-foreground":"text-muted-foreground")}>{s}</span>
              </div>
              {i<steps.length-1 && <div className="h-px flex-1 bg-border"/>}
            </React.Fragment>
          ))}
        </div>

        <div className="p-6">
          <p className="mb-4 text-xs text-muted-foreground">First-run setup · reached via a one-time link printed to the node log.</p>

          {step===0 && <div className="space-y-4">
            <Field label="Callsign (required)" hint="Your station's licensed callsign."><Input value={data.callsign} onChange={e=>set('callsign',e.target.value.toUpperCase())} placeholder="GB7RDG" className="font-mono" autoFocus/></Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Alias"><Input value={data.alias} onChange={e=>set('alias',e.target.value.toUpperCase())} placeholder="RDGGW" className="font-mono"/></Field>
              <Field label="Locator"><Input value={data.grid} onChange={e=>set('grid',e.target.value)} placeholder="IO91nl" className="font-mono"/></Field>
            </div>
          </div>}

          {step===1 && <div className="space-y-4">
            <Field label="Admin username"><Input value={data.username} onChange={e=>set('username',e.target.value)} className="font-mono"/></Field>
            <Field label="Password" hint="Min 8 chars · hashed with Argon2id."><Input type="password" value={data.password} onChange={e=>set('password',e.target.value)} placeholder="••••••••"/></Field>
            <button onClick={()=>set('passkey',!data.passkey)} className={cn("flex w-full items-center gap-3 rounded-lg border p-3 text-left transition-colors", data.passkey?"border-primary bg-primary/5":"border-border hover:bg-accent")}>
              <Icon name="fingerprint" size={18} className={data.passkey?"text-primary":"text-muted-foreground"}/>
              <div className="flex-1"><p className="text-sm font-medium">Enrol a passkey</p><p className="text-xs text-muted-foreground">WebAuthn · optional, recommended</p></div>
              <Switch checked={data.passkey} onChange={v=>set('passkey',v)}/>
            </button>
          </div>}

          {step===2 && <div className="space-y-4">
            <button onClick={()=>set('addPort',!data.addPort)} className="flex w-full items-center justify-between rounded-lg border border-border p-3">
              <div className="text-left"><p className="text-sm font-medium">Add a first port now</p><p className="text-xs text-muted-foreground">You can add more later.</p></div>
              <Switch checked={data.addPort} onChange={v=>set('addPort',v)}/>
            </button>
            {data.addPort && <div className="space-y-3 rounded-lg border border-border p-3">
              <div className="grid grid-cols-2 gap-3">
                <Field label="Port id"><Input value={data.portId} onChange={e=>set('portId',e.target.value)} className="font-mono"/></Field>
                <Field label="Transport"><Select value={data.portKind} onChange={e=>set('portKind',e.target.value)}><option>nino-tnc</option><option>kiss-tcp</option><option>serial-kiss</option><option>axudp</option></Select></Field>
              </div>
              <div className="grid grid-cols-2 gap-3">
                <Field label="Device"><Input value={data.device} onChange={e=>set('device',e.target.value)} className="font-mono"/></Field>
                <Field label="Baud"><Input type="number" value={data.baud} onChange={e=>set('baud',+e.target.value)} className="font-mono"/></Field>
              </div>
            </div>}
          </div>}

          <div className="mt-6 flex items-center justify-between">
            <Button variant="ghost" size="sm" onClick={()=> step===0 ? onSkip() : setStep(step-1)}>{step===0?"Skip":"Back"}</Button>
            {step<2
              ? <Button size="sm" disabled={!canNext} onClick={()=>setStep(step+1)}>Continue <Icon name="chevRight" size={14}/></Button>
              : <Button size="sm" onClick={onDone}><Icon name="check" size={14}/> Finish setup</Button>}
          </div>
        </div>
      </Card>
    </AuthFrame>
  );
}

Object.assign(window, { Login, Setup });
