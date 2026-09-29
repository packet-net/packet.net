using System.Globalization;
using System.Text;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Explores the space of two stations dialling each other at once (packet.net#875).
/// Runs on <see cref="TwoStationHarness"/> with a scheduled channel: every frame a
/// station sends joins a per-direction FIFO, and the scenario chooses, step by step,
/// which direction's head frame lands next, whether it is lost or duplicated, or
/// whether the next timer fires first. A small layer 3 sits on each station and
/// does what a node's client does: dial, and send its data the moment the link is
/// up. The prefix of choices is enumerated or drawn from a seeded generator, then
/// the run is completed on a fair lossless channel and judged.
/// </summary>
public sealed class CrossedDialExplorer
{
    /// <summary>One crossing scenario.</summary>
    /// <param name="Extended">Modulo 128 (SABME) rather than modulo 8 (SABM).</param>
    /// <param name="Probe">Each dial runs the pre-connect XID probe before its SABM(E).</param>
    /// <param name="DataA">Payloads A submits as soon as its link is up.</param>
    /// <param name="DataB">Payloads B submits as soon as its link is up.</param>
    /// <param name="DropBudget">How many frames the scenario may lose.</param>
    /// <param name="DupBudget">How many frames the scenario may deliver twice.</param>
    public sealed record Config(
        bool Extended,
        bool Probe,
        int DataA = 1,
        int DataB = 1,
        int DropBudget = 0,
        int DupBudget = 0,
        int T1Ms = TwoStationHarness.DefaultT1Ms,
        int N2 = 10,
        Ax25SessionQuirks? Quirks = null,
        Ax25SessionQuirks? QuirksB = null,
        bool SendAfterReset = false,
        bool DialOnLiveLink = false)
    {
        /// <summary>B's quirks: <see cref="QuirksB"/> if set, else the same as A's, so a sweep can
        /// pit this runtime against a peer that follows the figures as drawn.</summary>
        public Ax25SessionQuirks? EffectiveQuirksB => QuirksB ?? Quirks;

        public override string ToString() =>
            $"{(Extended ? "mod128" : "mod8")}{(Probe ? "+probe" : "")} data={DataA}/{DataB} drops<={DropBudget} dups<={DupBudget}"
            + (QuirksB is null ? "" : " (B on its own quirks)") + (SendAfterReset ? " +sendAfterReset" : "") + (DialOnLiveLink ? " +dialOnLiveLink" : "");
    }

    public enum Choice
    {
        DeliverAB,
        DeliverBA,
        DropAB,
        DropBA,
        DupAB,
        DupBA,
        Timer,
    }

    /// <summary>What a completed run looked like.</summary>
    public sealed record Outcome(
        Config Config,
        IReadOnlyList<Choice> Prefix,
        IReadOnlyList<string> Trace,
        IReadOnlyList<string> Violations,
        int Resets,
        int SettleSteps,
        string StateA,
        string StateB)
    {
        /// <summary>The harness the run ended on, for a test that wants to look at an end.</summary>
        public TwoStationHarness Harness { get; init; } = null!;

        public bool Ok => Violations.Count == 0;

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{Config}; prefix [{string.Join(' ', Prefix)}]; end A={StateA} B={StateB}; resets={Resets}; settle={SettleSteps}");
            foreach (var v in Violations)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  !! {v}");
            }

            foreach (var t in Trace)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {t}");
            }

            return sb.ToString();
        }
    }

    // ─── One run ─────────────────────────────────────────────────────────

    /// <summary>A single run under construction: the harness, the in-flight queues,
    /// the layer-3 models and the trace.</summary>
    public sealed class Run
    {
        private const int SettleBound = 400;
        private const int MaxTimerChoices = 3;
        private int timerChoices;

        private readonly Config cfg;
        private readonly TwoStationHarness h;
        private readonly Queue<TwoStationHarness.InFlightFrame> ab = new();
        private readonly Queue<TwoStationHarness.InFlightFrame> ba = new();
        private readonly L3 l3A;
        private readonly L3 l3B;
        private readonly List<string> trace = new();
        private readonly List<Choice> prefix = new();
        private readonly List<string> violations = new();
        private int dropsLeft;
        private int dupsLeft;
        private bool dead;

        public Run(Config cfg)
        {
            this.cfg = cfg;
            dropsLeft = cfg.DropBudget;
            dupsLeft = cfg.DupBudget;
            h = TwoStationHarness.Build(
                extended: cfg.Extended, t1Ms: cfg.T1Ms, n2: cfg.N2, quirks: cfg.Quirks, quirksB: cfg.EffectiveQuirksB,
                srej: false, k: 4);
            h.CheckAfterEachStep = false;
            h.A.Session.TransitionFired += (_, t) => CountReset("A", t);
            h.B.Session.TransitionFired += (_, t) => CountReset("B", t);
            h.Link.Schedule = f =>
            {
                var q = f.From.Equals(h.A.Context.Local) ? ab : ba;
                q.Enqueue(f);
                Log($"{Dir(f.From)} tx  {Describe(f.Frame)}");
            };
            l3A = new L3(h.A, cfg.DataA, cfg.Probe, cfg.SendAfterReset, cfg.DialOnLiveLink, this);
            l3B = new L3(h.B, cfg.DataB, cfg.Probe, cfg.SendAfterReset, cfg.DialOnLiveLink, this);

            // Both dial. A first, then B; the scheduled channel makes the order of
            // what follows the scenario's choice, so this is not a bias.
            l3A.Start();
            l3B.Start();
            Pump();
        }

        public TwoStationHarness Harness => h;
        public IReadOnlyList<string> Trace => trace;
        public bool Dead => dead;

        /// <summary>Resets seen on the wire, from the figure arms that run them: a SABM(E)
        /// received on an up link (figc4.4 t14 / t15, figc4.5 t13 / t14, either branch), and
        /// any arm that takes an up link back to establishment (a UA or FRMR received, an N(R)
        /// error, a DL-CONNECT request). Counted here rather than from DL-CONNECT signals, which
        /// the V(s) = V(a) branches do not raise.</summary>
        public int ResetsOnTheWire { get; private set; }

        private void CountReset(string owner, Packet.Ax25.Sdl.TransitionSpec t)
        {
            bool up = t.From is "Connected" or "TimerRecovery";
            bool peerSabm = up && t.Id.Contains("sabm", StringComparison.Ordinal);
            bool reestablish = up && t.Next is "AwaitingConnection" or "AwaitingV22Connection";
            if (peerSabm || reestablish)
            {
                ResetsOnTheWire++;
                Log($"--- {owner}: reset ({t.From} {t.Id})");
            }
        }

        /// <summary>The choices open at this point.</summary>
        public IEnumerable<Choice> Available()
        {
            if (dead)
            {
                yield break;
            }

            if (ab.Count > 0)
            {
                yield return Choice.DeliverAB;
                if (dropsLeft > 0)
                {
                    yield return Choice.DropAB;
                }
                if (dupsLeft > 0)
                {
                    yield return Choice.DupAB;
                }
            }

            if (ba.Count > 0)
            {
                yield return Choice.DeliverBA;
                if (dropsLeft > 0)
                {
                    yield return Choice.DropBA;
                }
                if (dupsLeft > 0)
                {
                    yield return Choice.DupBA;
                }
            }

            // A timer may fire ahead of frames in flight only a few times per
            // scenario: that is a slow channel, which is the crossing space. A
            // channel that stalls for N2 whole T1 periods is a dead link, which is
            // not, and the fair settle covers timers on an empty channel anyway.
            if (NextTimer() is not null && timerChoices < MaxTimerChoices)
            {
                yield return Choice.Timer;
            }
        }

        public bool Quiescent => ab.Count == 0 && ba.Count == 0 && NextTimer() is null;

        /// <summary>Apply <paramref name="c"/> if it is open at this point; false if it is not
        /// (a prefix replayed under other quirks may reach a different tree).</summary>
        public bool TryApply(Choice c)
        {
            if (!Available().Contains(c))
            {
                return false;
            }

            Apply(c);
            return true;
        }

        public void Apply(Choice c)
        {
            prefix.Add(c);
            if (c == Choice.Timer)
            {
                timerChoices++;
            }

            Step(c);
        }

        private void Step(Choice c)
        {
            if (dead)
            {
                return;
            }

            switch (c)
            {
                case Choice.DeliverAB: Land(ab.Dequeue(), times: 1); break;
                case Choice.DeliverBA: Land(ba.Dequeue(), times: 1); break;
                case Choice.DropAB: dropsLeft--; Lose(ab.Dequeue()); break;
                case Choice.DropBA: dropsLeft--; Lose(ba.Dequeue()); break;
                case Choice.DupAB: dupsLeft--; Land(ab.Dequeue(), times: 2); break;
                case Choice.DupBA: dupsLeft--; Land(ba.Dequeue(), times: 2); break;
                case Choice.Timer: FireNextTimer(); break;
            }
        }

        private void Land(TwoStationHarness.InFlightFrame f, int times)
        {
            for (int i = 0; i < times; i++)
            {
                Log($"{Dir(f.From)} rx  {Describe(f.Frame)}{(i > 0 ? " (duplicate)" : "")}");
                f.Deliver();
                Pump();
            }
        }

        private void Lose(TwoStationHarness.InFlightFrame f)
        {
            Log($"{Dir(f.From)} LOST {Describe(f.Frame)}");
        }

        private (string Owner, string Name, TimeSpan Remaining)? NextTimer()
        {
            (string, string, TimeSpan)? best = null;
            foreach (var (owner, ep) in new[] { ("A", h.A), ("B", h.B) })
            {
                foreach (var t in ep.Scheduler.CaptureState())
                {
                    // T3 is the idle-link keep-alive (minutes). It never orders a
                    // handshake, and firing it on every quiescent link would only
                    // pad the runs.
                    if (t.Name == "T3")
                    {
                        continue;
                    }

                    if (best is null || t.Remaining < best.Value.Item3)
                    {
                        best = (owner, t.Name, t.Remaining);
                    }
                }
            }

            return best;
        }

        private void FireNextTimer()
        {
            if (NextTimer() is not { } t)
            {
                return;
            }

            var due = string.Join(", ", new[] { ("A", h.A), ("B", h.B) }
                .SelectMany(o => o.Item2.Scheduler.CaptureState().Where(x => x.Name != "T3" && x.Remaining <= t.Remaining).Select(x => $"{o.Item1} {x.Name}")));
            Log($"--- timers expire (+{t.Remaining.TotalMilliseconds:0} ms): {due}");
            var step = t.Remaining > TimeSpan.Zero ? t.Remaining : TimeSpan.FromMilliseconds(1);
            h.Time.Advance(step);
            Pump();
        }

        /// <summary>Drain the harness's local queues (LM-SEIZE confirms, MDL work
        /// routed by a delivery), let layer 3 react to any new signals, and check
        /// safety. Loops because a layer-3 reaction can raise more signals.</summary>
        private void Pump()
        {
            for (int i = 0; i < 16; i++)
            {
                h.Settle();
                var acted = l3A.React() | l3B.React();
                if (!acted)
                {
                    break;
                }
            }

            try
            {
                // Not InvariantChecker.CheckSafety: its delivery check wants an exact prefix,
                // and a layer 3 that sends again after a reported reset (SendAfterReset)
                // legitimately has a gap before its later payloads.
                InvariantChecker.CheckStateAndSequenceSanity(h);
                CheckDelivery(h.A, l3B);
                CheckDelivery(h.B, l3A);
            }
            catch (InvariantViolationException ex)
            {
                Fail("safety: " + ex.Message);
            }
        }

        private void Fail(string what)
        {
            violations.Add(what);
            Log($"!! {what}");
            dead = true;
        }

        /// <summary>Complete the run on a fair lossless channel: land everything in
        /// flight, alternating directions, and fire timers only when nothing is in
        /// flight. Then judge it.</summary>
        public Outcome Finish()
        {
            int steps = 0;
            Log("--- settle");
            while (!dead && !Quiescent)
            {
                if (++steps > SettleBound)
                {
                    Fail($"hang: not quiescent after {SettleBound} settle steps (A={h.A.State} B={h.B.State}, in flight ab={ab.Count} ba={ba.Count}, next timer={NextTimer()?.Name})");
                    break;
                }

                if (ab.Count > 0)

                {

                    Step(Choice.DeliverAB);

                }
                if (ba.Count > 0)
                {
                    Step(Choice.DeliverBA);
                }
                if (ab.Count == 0 && ba.Count == 0)
                {
                    Step(Choice.Timer);
                }
            }

            if (!dead)
            {
                Judge();
            }

            return new Outcome(cfg, prefix.ToArray(), trace.ToArray(), violations.ToArray(),
                ResetsOnTheWire, steps, h.A.State, h.B.State) { Harness = h };
        }

        private void Judge()
        {
            var a = h.A.State;
            var b = h.B.State;

            if (!(a == "Connected" && b == "Connected") && !(a == "Disconnected" && b == "Disconnected"))
            {
                // A station left in Timer Recovery with nothing outstanding and no T1
                // is ax25sdl's known figure defect H1 (figc4.5 completes a recovery
                // on an I frame's N(R) with Stop T1 and no way out; see
                // TimerRecoveryStuckAfterIFrameAckTests). Named so the crossing
                // findings are not buried under it.
                bool h1 = (a, h.A) is ("TimerRecovery", var ea) && IsH1(ea)
                    || (b, h.B) is ("TimerRecovery", var eb) && IsH1(eb);
                violations.Add(h1
                    ? $"liveness (known, ax25sdl H1): A={a} B={b} at quiescence with Timer Recovery's T1 stopped"
                    : $"state: A={a} B={b} at quiescence");
            }

            JudgeDirection(l3A, h.B);
            JudgeDirection(l3B, h.A);
            JudgeKnowledge(l3A);
            JudgeKnowledge(l3B);
        }

        /// <summary>What <paramref name="receiver"/> delivered must be, in order, some of what
        /// <paramref name="sender"/> submitted: nothing invented, nothing twice, nothing out of
        /// order. Payloads are unique per station, so this is a subsequence check.</summary>
        private static void CheckDelivery(TwoStationHarness.Endpoint receiver, L3 sender)
        {
            var submitted = sender.Endpoint.Submitted;
            int next = 0;
            foreach (var payload in receiver.Delivered)
            {
                int at = -1;
                for (int i = next; i < submitted.Count; i++)
                {
                    if (submitted[i].AsSpan().SequenceEqual(payload))
                    {
                        at = i;
                        break;
                    }
                }

                if (at < 0)
                {
                    bool earlier = submitted.Take(next).Any(p => p.AsSpan().SequenceEqual(payload));
                    throw new InvariantViolationException(earlier
                        ? $"[{receiver.Name}] delivered [{Convert.ToHexString(payload)}] from [{sender.Endpoint.Name}] twice or out of order"
                        : $"[{receiver.Name}] delivered [{Convert.ToHexString(payload)}] which [{sender.Endpoint.Name}] never submitted");
                }

                next = at + 1;
            }
        }

        private static bool IsH1(TwoStationHarness.Endpoint e) =>
            e.Context.VS == e.Context.VA && !e.Scheduler.IsRunning("T1");

        // Everything the sender queued reached the receiver exactly once, or the
        // sender was told its link broke after it queued them.
        private void JudgeDirection(L3 sender, TwoStationHarness.Endpoint receiver)
        {
            var submitted = sender.Endpoint.Submitted;
            var delivered = receiver.Delivered;
            // Payloads queued before the last break the sender heard of may be gone; anything
            // queued after it must arrive (ordering and duplicates are checked after every step).
            for (int i = sender.SubmittedAtLastBreak; i < submitted.Count; i++)
            {
                if (!delivered.Any(d => d.AsSpan().SequenceEqual(submitted[i])))
                {
                    violations.Add(sender.SubmittedAtLastBreak == 0
                        ? $"data: {receiver.Name} never delivered [{Convert.ToHexString(submitted[i])}] from {sender.Endpoint.Name} ({delivered.Count} of {submitted.Count} arrived) and {sender.Endpoint.Name}'s layer 3 was never told the link broke"
                        : $"data: {receiver.Name} never delivered [{Convert.ToHexString(submitted[i])}] from {sender.Endpoint.Name}, queued after the last break {sender.Endpoint.Name}'s layer 3 heard of ({delivered.Count} of {submitted.Count} arrived)");
                    return;
                }
            }
        }

        // What layer 3 believes matches the link.
        private void JudgeKnowledge(L3 l3)
        {
            var state = l3.Endpoint.State;
            if (state == "Connected" && !l3.BelievesConnected)
            {
                violations.Add($"result: {l3.Endpoint.Name} is Connected but its layer 3 never heard a DL-CONNECT confirm or indication");
            }

            if (state == "Disconnected" && l3.BelievesConnected)
            {
                violations.Add($"result: {l3.Endpoint.Name} is Disconnected but its layer 3 still believes the link is up");
            }

            if (state == "Disconnected" && !l3.EverConnected && !l3.DialFailed && l3.Dialled)
            {
                violations.Add($"result: {l3.Endpoint.Name}'s dial neither confirmed nor failed");
            }
        }

        private void Log(string line) => trace.Add(line);

        private string Dir(Callsign from) => from.Equals(h.A.Context.Local) ? "A>B" : "B>A";

        internal static string Describe(Ax25Frame f)
        {
            var cr = f.IsCommand ? "C" : "R";
            var pf = f.PollFinal ? (f.IsCommand ? " P" : " F") : "";
            return f.FrameType switch
            {
                Ax25FrameType.I => $"I {cr}{pf} S{f.Ns} R{f.Nr}" + (f.Info.Length > 0 ? $" [{Convert.ToHexString(f.Info.Span)}]" : ""),
                Ax25FrameType.Rr or Ax25FrameType.Rnr or Ax25FrameType.Rej or Ax25FrameType.Srej
                    => $"{f.FrameType.ToString().ToUpperInvariant()} {cr}{pf} R{f.Nr}",
                _ => $"{f.FrameType.ToString().ToUpperInvariant()} {cr}{pf}",
            };
        }

        // ─── Layer 3 model ──────────────────────────────────────────────

        /// <summary>What a node's client does on one station: run the XID probe if
        /// asked, dial, and hand over its data as soon as the link is up. Records
        /// what it was told so the judge can compare belief with the link.</summary>
        private sealed class L3
        {
            private readonly int data;
            private readonly bool probe;
            private readonly bool sendAfterReset;
            private readonly bool dialOnLiveLink;
            private readonly Run run;
            private int signalCursor;
            private int mdlCursor;
            private bool dataSubmitted;
            private int sent;

            public L3(TwoStationHarness.Endpoint endpoint, int data, bool probe, bool sendAfterReset, bool dialOnLiveLink, Run run)
            {
                Endpoint = endpoint;
                this.data = data;
                this.probe = probe;
                this.sendAfterReset = sendAfterReset;
                this.dialOnLiveLink = dialOnLiveLink;
                this.run = run;
            }

            public TwoStationHarness.Endpoint Endpoint { get; }
            public bool Dialled { get; private set; }
            public bool BelievesConnected { get; private set; }
            public bool EverConnected { get; private set; }
            public bool DialFailed { get; private set; }
            /// <summary>How many payloads had been queued when this layer 3 last heard the link
            /// broke (a reset or a disconnect); 0 if it never has.</summary>
            public int SubmittedAtLastBreak { get; private set; }
            public int Resets { get; private set; }

            public void Start()
            {
                if (probe)
                {
                    run.Log($"--- {Endpoint.Name}: probe");
                    Endpoint.Mdl.Negotiate();
                }
                else
                {
                    Dial();
                }
            }

            private void Dial()
            {
                Dialled = true;
                if (Endpoint.State is "Connected" or "TimerRecovery" && !dialOnLiveLink)
                {
                    // The peer's call brought the link up during our probe. A dial
                    // on a live link would re-establish it (figc4.4 / figc4.5
                    // DL-CONNECT request), which is packet.net#862; the listener
                    // keeps the live link instead (#854, IsLinkUp), and so does
                    // this model.
                    run.Log($"--- {Endpoint.Name}: dial finds the link up, keeps it");
                    return;
                }

                // With DialOnLiveLink, the node's race (packet.net#862): the peer's call
                // brought the link up between the probe ending and the dial posting, and the
                // DL-CONNECT request lands on the live link (figc4.4 t07 re-establishes it).
                run.Log($"--- {Endpoint.Name}: DL-CONNECT request" + (Endpoint.State is "Connected" or "TimerRecovery" ? " on the live link" : ""));
                Endpoint.Session.PostEvent(new DlConnectRequest());
            }

            /// <summary>Act on any signal not yet seen. Returns true if it did anything.</summary>
            public bool React()
            {
                bool acted = false;

                var mdl = Endpoint.MdlSignals.ToArray();
                for (; mdlCursor < mdl.Length; mdlCursor++)
                {
                    if (!Dialled && probe && mdl[mdlCursor] is MdlNegotiateConfirmSignal or MdlErrorIndicateSignal)
                    {
                        run.Log($"--- {Endpoint.Name}: probe ends ({mdl[mdlCursor].Name})");
                        Dial();
                        acted = true;
                    }
                }

                var signals = Endpoint.Signals.ToArray();
                for (; signalCursor < signals.Length; signalCursor++)
                {
                    switch (signals[signalCursor])
                    {
                        case DataLinkConnectConfirm or DataLinkConnectIndication:
                            var name = signals[signalCursor] is DataLinkConnectConfirm ? "DL-CONNECT confirm" : "DL-CONNECT indication";
                            if (BelievesConnected)
                            {
                                // The link was re-established under us: the figure's
                                // §6.5 reset. Whatever was queued is gone. (A second
                                // confirm rather than an indication means this end's
                                // own dial was still counted as layer-3 initiated.)
                                Resets++;
                                run.Log($"--- {Endpoint.Name}: {name} on a live link (reset)");
                                SubmittedAtLastBreak = Endpoint.Submitted.Count;
                                if (sendAfterReset)
                                {
                                    // What a client does once told: send again on the new link.
                                    Send(1);
                                }
                            }
                            else
                            {
                                run.Log($"--- {Endpoint.Name}: {name}");
                                OnUp();
                            }

                            acted = true;
                            break;
                        case DataLinkErrorIndication err:
                            run.Log($"--- {Endpoint.Name}: DL-ERROR indication {err.Code}");
                            break;
                        case DataLinkDisconnectIndication or DataLinkDisconnectConfirm:
                            run.Log($"--- {Endpoint.Name}: {signals[signalCursor].Name}");
                            if (BelievesConnected)
                            {
                                SubmittedAtLastBreak = Endpoint.Submitted.Count;
                                BelievesConnected = false;
                            }
                            else
                            {
                                DialFailed = true;
                            }

                            acted = true;
                            break;
                    }
                }

                return acted;
            }

            private void OnUp()
            {
                BelievesConnected = true;
                EverConnected = true;
                if (dataSubmitted)
                {
                    return;
                }

                dataSubmitted = true;
                Send(data);
            }

            private void Send(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    var payload = new[] { (byte)(Endpoint.Name[^1]), (byte)(0x30 + sent++) };
                    run.Log($"--- {Endpoint.Name}: DL-DATA request [{Convert.ToHexString(payload)}]");
                    Endpoint.Submitted.Add(payload);
                    Endpoint.Session.PostEvent(new DlDataRequest(payload));
                }
            }
        }
    }

    // ─── Search ──────────────────────────────────────────────────────────

    /// <summary>Enumerate every choice prefix up to <paramref name="depth"/> (each
    /// completed on the fair channel), stopping after <paramref name="maxRuns"/>.
    /// Returns every outcome.</summary>
    public static List<Outcome> Exhaustive(Config cfg, int depth, int maxRuns = 200_000)
    {
        var outcomes = new List<Outcome>();
        var stack = new Stack<List<Choice>>();
        stack.Push(new List<Choice>());
        while (stack.Count > 0 && outcomes.Count < maxRuns)
        {
            var prefix = stack.Pop();
            var run = Replay(cfg, prefix);
            var next = prefix.Count < depth && !run.Dead ? run.Available().ToList() : new List<Choice>();
            outcomes.Add(run.Finish());
            // Push in reverse so DeliverAB explores first (natural order in the log).
            for (int i = next.Count - 1; i >= 0; i--)
            {
                var child = new List<Choice>(prefix) { next[i] };
                stack.Push(child);
            }
        }

        return outcomes;
    }

    /// <summary>One seeded random walk of up to <paramref name="steps"/> choices,
    /// completed on the fair channel.</summary>
    public static Outcome RandomWalk(Config cfg, int seed, int steps)
    {
        var rng = new Random(seed);
        var run = new Run(cfg);
        for (int i = 0; i < steps; i++)
        {
            var options = run.Available().ToList();
            if (options.Count == 0)
            {
                break;
            }

            run.Apply(options[rng.Next(options.Count)]);
        }

        return run.Finish();
    }

    public static Run Replay(Config cfg, IEnumerable<Choice> prefix)
    {
        var run = new Run(cfg);
        foreach (var c in prefix)
        {
            run.Apply(c);
        }

        return run;
    }

    /// <summary>Replay as far as the prefix stays open under <paramref name="cfg"/>; null if it
    /// diverges before the end (the two configurations took different trees there).</summary>
    public static Outcome? ReplayIfOpen(Config cfg, IEnumerable<Choice> prefix)
    {
        var run = new Run(cfg);
        foreach (var c in prefix)
        {
            if (!run.TryApply(c))
            {
                return null;
            }
        }

        return run.Finish();
    }

    /// <summary>Group outcomes by their first violation, keeping the shortest
    /// prefix for each.</summary>
    public static string Report(IEnumerable<Outcome> outcomes)
    {
        var all = outcomes.ToList();
        var sb = new StringBuilder();
        var bad = all.Where(o => !o.Ok).ToList();
        var resets = all.Count(o => o.Ok && o.Resets > 0);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{all.Count} runs, {bad.Count} violating, {resets} clean but with a reset");
        foreach (var g in bad.GroupBy(o => Kind(o.Violations[0])).OrderByDescending(g => g.Count()))
        {
            var shortest = g.OrderBy(o => o.Prefix.Count).ThenBy(o => o.Trace.Count).First();
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"== {g.Count()} x {g.Key}");
            sb.Append(shortest.Describe());
        }

        if (resets > 0)
        {
            var shortest = all.Where(o => o.Ok && o.Resets > 0).OrderBy(o => o.Prefix.Count).First();
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"== {resets} x clean run with a reset (shortest)");
            sb.Append(shortest.Describe());
        }

        return sb.ToString();
    }

    private static string Kind(string violation)
    {
        // Strip the numbers so the same shape groups together.
        var i = violation.IndexOf(':');
        var head = i < 0 ? violation : violation[..i];
        var rest = i < 0 ? "" : violation[(i + 1)..];
        rest = new string(rest.Where(ch => !char.IsDigit(ch)).ToArray());
        return head + ":" + rest;
    }
}
