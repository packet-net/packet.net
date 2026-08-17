using Microsoft.Extensions.Logging;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Transport;
using Packet.NetRom.Wire;

namespace Packet.Node.Core.NetRom;

/// <summary>
/// The INP3 host-integration half of <see cref="NetRomService"/> (the awareness slice):
/// the inbound RIF/L3RTT dispatch tap, the <see cref="Inp3Host"/> nested type that owns the
/// host-free <see cref="Inp3Engine"/> + <see cref="Inp3UpdateScheduler"/> and glues their
/// sinks/events to the interlink send path + routing table, and the deterministic test seams.
/// All of it is dead code when <c>config.Inp3.Enabled</c> is false (the <c>inp3</c> field is
/// null) — the node is byte-for-byte today. Design: <c>docs/netrom-inp3-host-integration-design.md</c>.
/// </summary>
/// <remarks>
/// <b>Scope: AWARENESS ONLY.</b> The node <em>learns and tells</em> the time-space (probe /
/// ingest / advertise / 180 s reset); it does not yet <em>route by</em> it. Feeding
/// <see cref="Inp3RouteSelector"/> into the forward / connect next-hop pick is the named
/// follow-up (design §8.1); <c>PreferInp3Routes</c> is plumbed + validated but inert here.
/// </remarks>
public sealed partial class NetRomService
{
    // ─── INP3: inbound dispatch (the load-bearing precedence, design §3.2) ───

    /// <summary>
    /// The INP3 inbound tap, called from <see cref="OnInterlinkData"/> only when the overlay
    /// is on. Peels a RIF or an L3RTT off the 0xCF interlink stream BEFORE the L4 path so it
    /// can never reach circuits/forwarding. Precedence (a correctness property):
    /// <list type="number">
    /// <item>(A) RIF first — signature-gated on the raw info (one byte). A 0xFF-led frame is
    /// a RIF, never an L4 datagram or an L3RTT; it is consumed regardless of whether it parses
    /// (a malformed 0xFF-led frame is a malformed RIF, dropped — not retried as L4).</item>
    /// <item>(B) L3RTT next — parse as a NetRomPacket, classify by dest/opcode. An L3RTT is
    /// consumed (timed/reflected via the engine); any other NetRomPacket falls through to the
    /// existing L4 dispatch, returned via <paramref name="l4Packet"/> so it is not re-parsed.</item>
    /// </list>
    /// </summary>
    /// <param name="fromNeighbour">The interlink neighbour the frame arrived on.</param>
    /// <param name="info">The 0xCF I-frame info field.</param>
    /// <param name="l4Packet">On return, a parsed non-INP3 <see cref="NetRomPacket"/> the caller
    /// should dispatch to L4 (only when this returns false); null otherwise.</param>
    /// <returns><c>true</c> if the frame was consumed as INP3 (or dropped as a malformed RIF) —
    /// the caller must not touch L4; <c>false</c> if it is not an INP3 frame.</returns>
    private bool DispatchInp3(Callsign fromNeighbour, ReadOnlyMemory<byte> info, out NetRomPacket? l4Packet)
    {
        l4Packet = null;
        var host = inp3;
        if (host is null)
        {
            return false;   // overlay off — never reached (caller guards), but keep total.
        }

        // Any neighbour we hear ANYTHING 0xCF from becomes a probe target (idempotent refresh,
        // cheap). Optimistic probing of unknown-capability neighbours is on by default, so even
        // a neighbour that only ever sent an L4 datagram gets probed (design §3 neighbour-obs).
        host.ObserveNeighbour(fromNeighbour);

        var span = info.Span;

        // (A) RIF? — the 0xFF signature is a single-byte, total, unambiguous discriminator.
        if (span.Length >= 1 && span[0] == Inp3Rif.Signature)
        {
            if (Inp3Rif.TryParse(span, out var rif))
            {
                host.IngestRif(fromNeighbour, rif);
            }
            // Consumed either way: a 0xFF-led-but-unparseable frame is a malformed RIF, dropped
            // — NEVER retried as an L4 datagram (the "never mis-fed" guarantee made total).
            return true;
        }

        // (B) L3RTT? — an L3RTT is a well-formed NetRomPacket to L3RTT-0; classify by dest/opcode.
        if (NetRomPacket.TryParse(span, out var packet))
        {
            if (Inp3L3RttFrame.IsL3Rtt(packet!))
            {
                host.OnL3Rtt(fromNeighbour, packet!);   // times our reflection, or reflects a peer probe
                return true;
            }
            // A normal L4 datagram — hand the already-parsed packet back to the caller for L4.
            l4Packet = packet;
            return false;
        }

        // info didn't parse as a NetRomPacket and wasn't a RIF → drop (today's behaviour for an
        // unparseable interlink frame). Consumed so the caller does not re-attempt the parse.
        return true;
    }

    // ─── INP3: test seams (InternalsVisibleTo Packet.Node.Tests) ───

    /// <summary>True when the INP3 overlay is constructed (config.Inp3.Enabled on a connect node).</summary>
    internal bool Inp3Enabled => inp3 is not null;

    /// <summary>The live INP3 engine (null when the overlay is off) — for a test to read SNTT /
    /// the capable-neighbour set after driving a probe/reflection deterministically.</summary>
    internal Inp3Engine? Inp3EngineForTest => inp3?.Engine;

    /// <summary>The live INP3 scheduler (null when off) — for a test to inspect pending dirty state.</summary>
    internal Inp3UpdateScheduler? Inp3SchedulerForTest => inp3?.Scheduler;

    /// <summary>Feed an inbound interlink 0xCF info field as if it arrived from
    /// <paramref name="fromNeighbour"/> — the session-free path the deterministic node tests use
    /// to exercise <see cref="OnInterlinkData"/>'s dispatch (RIF/L3RTT peel + L4 fallthrough)
    /// without a real Ax25Session. Behaves exactly as the live tap: INP3 on ⇒ DispatchInp3 then
    /// L4; INP3 off ⇒ today's parse-then-DispatchL4.</summary>
    internal void IngestInterlinkForTest(Callsign fromNeighbour, ReadOnlyMemory<byte> info)
    {
        try
        {
            if (inp3 is not null)
            {
                if (DispatchInp3(fromNeighbour, info, out var l4Packet))
                {
                    return;
                }
                if (l4Packet is not null)
                {
                    DispatchL4(l4Packet, fromNeighbour);
                }
                return;
            }
            if (circuits is null || !NetRomPacket.TryParse(info.Span, out var packet))
            {
                return;
            }
            DispatchL4(packet!, fromNeighbour);
        }
        catch (Exception ex)
        {
            LogInterlinkFault(ex, fromNeighbour.ToString());
        }
    }

    /// <summary>Drive one INP3 host tick (engine → scheduler → clear-withdrawn → refresh-capable)
    /// in the locked order (design §6.4), for a FakeTimeProvider-based node test. No-op when off.</summary>
    internal void Inp3TickForTest() => inp3?.TickOnce();

    /// <summary>Register the node callsign for the INP3 engine without standing up a port — the
    /// test analogue of AttachPort's circuits?.SetLocalNode + the INP3 engine's SetLocalNode. The
    /// production node sets this from AttachPort (see SetNodeCallForInp3). No-op when off.</summary>
    internal void SetInp3LocalNodeForTest(Callsign node)
    {
        nodeCall = node;
        nodeCallSet = true;
        circuits?.SetLocalNode(node);
        inp3?.SetLocalNode(node);
    }

    // ─── Inp3Host: the nested glue (design §1) ───

    /// <summary>
    /// Owns the host-free <see cref="Inp3Engine"/> + <see cref="Inp3UpdateScheduler"/> and the glue
    /// that wires their sinks/events to the enclosing <see cref="NetRomService"/>'s interlink send
    /// path + shared routing table. Constructed only when the overlay is on (one field, one
    /// construction site — design §7.1), so when <c>inp3</c> is null none of this exists. Drives a
    /// single 1 s <see cref="TimeProvider"/> timer that ticks engine → drain-recently-withdrawn →
    /// scheduler → refresh-capable in that order, giving the recently-withdrawn set an explicit,
    /// deterministic, atomic round boundary (design §6.4). Does NOT own the routing table
    /// (shared with the vanilla L3/L4 paths — INP3 is a second metric space on the same table) nor
    /// the interlinks map (it calls back into the owner to send).
    /// </summary>
    private sealed class Inp3Host : IDisposable
    {
        // The locked host tick cadence (design §5/§6.4): 1 s — the same cadence CircuitManager
        // uses — checks the 60 s probe / 180 s reset with ≤ 1 s slack and resolves the 5 s
        // positive debounce within 1 s of its deadline.
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

        private readonly NetRomService owner;
        private readonly NetRomInp3Options options;
        private readonly Inp3Engine engine;
        private readonly Inp3UpdateScheduler scheduler;
        private readonly ITimer? timer;
        private int disposed;

        // Serialises a fan-out round (the 1 s timer and the coarse NODESINTERVAL nudge both call
        // TickOnce). Held for the whole round so the drained recently-withdrawn snapshot the
        // Advertise sink reads (currentRoundWithdrawn) belongs to exactly one round at a time.
        private readonly object tickGate = new();

        // The recently-withdrawn snapshot DRAINED at the start of the current fan-out round and
        // handed to every BuildRif the scheduler raises this round (design §6.4). Set under
        // tickGate immediately before scheduler.Tick and reset after; read only by the Advertise
        // sink, which runs synchronously inside scheduler.Tick on the same (tick-holding) thread.
        private IReadOnlyList<Callsign> currentRoundWithdrawn = Array.Empty<Callsign>();

        public Inp3Engine Engine => engine;

        public Inp3UpdateScheduler Scheduler => scheduler;

        public Inp3Host(NetRomService owner, NetRomInp3Options options, TimeProvider time)
        {
            this.owner = owner;
            this.options = options;

            // The engine + scheduler are constructed with tickInterval: null — the host owns the
            // single 1 s timer and drives both Ticks in the locked order so the recently-withdrawn
            // clear lands AFTER the scheduler's whole fan-out round (design §6.4 option (a)).
            engine = new Inp3Engine(owner.nodeCall, options, time, tickInterval: null);
            scheduler = new Inp3UpdateScheduler(options, time, tickInterval: null);

            // L3RTT send: ship the frame's bytes over the neighbour's interlink (0xCF). The engine
            // raises this both to send our probe (on Tick) and to reflect a peer's probe verbatim
            // (on OnL3Rtt) — identical handling. Cold-interlink policy: drop, don't dial (§4.1) —
            // TrySendOverInterlinkBytes returns false when no link is up; we just don't probe.
            engine.SendL3Rtt = (neighbour, frame) =>
            {
                if (!owner.TrySendOverInterlinkBytes(neighbour, frame.ToBytes()))
                {
                    var neighbourText = neighbour.ToString();   // local: not evaluated when the trace is off (CA1873)
                    owner.LogInp3SendDropped(neighbourText, "L3RTT");
                }
            };

            // 180 s reflection-timeout (an INP3-capable neighbour went silent): drop its routes +
            // engine state + escalate the withdrawals. Reuses the shared neighbour-down primitive
            // (the SAME one the L4 dial-failure path calls) rather than inventing a teardown (§4.3).
            engine.NeighbourDown += (_, e) =>
            {
                int dropped = owner.table.MarkNeighbourDown(e.Neighbour);
                var downText = e.Neighbour.ToString();   // local (CA1873)
                owner.LogInp3NeighbourDown(downText, e.SilentForMs, dropped);
                // The engine has already removed the neighbour's state before raising this (and
                // raises it outside its lock), so RemoveNeighbour here is an idempotent belt-and-
                // braces. table.MarkNeighbourDown above populated the recently-withdrawn set; this
                // handler is raised synchronously inside engine.Tick() (the engine self-drives no
                // timer — tickInterval:null), so the very same TickOnce that called engine.Tick
                // drains those withdrawals right after and fans them out this round.
                engine.RemoveNeighbour(e.Neighbour);
                RefreshCapableNeighbours();
            };

            // RIF advertise: turn each per-neighbour intent into a full poison-reversed RIF and
            // send it over that neighbour's interlink (0xFF-led 0xCF I-frame). The withdrawn
            // snapshot is the one TickOnce DRAINED for this round (currentRoundWithdrawn) — passed
            // to BuildRif so every neighbour's RIF carries the same one-shot horizon withdrawals
            // and a concurrent mid-round withdrawal is deferred to the next round (the race fix,
            // design §6.4). Cold-interlink: drop, don't dial (§4.2).
            scheduler.Advertise = intent =>
            {
                var rif = owner.table.BuildRif(owner.nodeCall, intent.Neighbour, currentRoundWithdrawn);
                if (!owner.TrySendOverInterlinkBytes(intent.Neighbour, rif.ToBytes()))
                {
                    var neighbourText = intent.Neighbour.ToString();   // local (CA1873)
                    owner.LogInp3SendDropped(neighbourText, "RIF");
                }
            };

            // The single host-owned 1 s timer (design §6.4). Tests pass through TickOnce instead.
            timer = time.CreateTimer(_ => TickOnce(), state: null, dueTime: TickInterval, period: TickInterval);
        }

        /// <summary>Set the engine's local node callsign (AttachPort pins the node identity).</summary>
        public void SetLocalNode(Callsign node) => engine.SetLocalNode(node);

        /// <summary>Register/refresh awareness of an interlink neighbour so the engine probes it.</summary>
        public void ObserveNeighbour(Callsign neighbour) => engine.ObserveNeighbour(neighbour);

        /// <summary>Ingest a parsed RIF into the shared table (the second metric space), supplying
        /// the engine's measured SNTT for the carrying link. Any destination that lost its last INP3
        /// route during ingest (a horizon RIP withdrew it) lands in the table's recently-withdrawn
        /// set; it is NOT escalated here — the next <see cref="TickOnce"/> (≤ 1 s) DRAINS the set,
        /// marks each NEGATIVE, and fans it out, so this pump-thread path never touches the scheduler
        /// concurrently with a fan-out round (the race fix, design §6.4). An un-probed link
        /// (SNTT Unset) learns nothing and withdraws nothing — IngestRif's documented skip. Positives
        /// ride the periodic RIF this slice (IngestRif returns void, so the host cannot classify
        /// new/improved per-destination — design §6.5 flagged gap; the correctness-critical
        /// NEGATIVE/withdrawal path IS wired, via the drain).</summary>
        public void IngestRif(Callsign from, Inp3Rif rif)
        {
            owner.table.IngestRif(from, owner.nodeCall, engine.SnttMs(from) ?? Inp3Sntt.Unset, rif, options.HopLimit);
        }

        /// <summary>Feed an inbound L3RTT frame to the engine (it reflects a peer probe via the
        /// SendL3Rtt sink, or times our own reflection and folds the RTT/2 into SNTT). A new
        /// reflection may reveal a newly-capable peer — refresh the fan-out set.</summary>
        public void OnL3Rtt(Callsign from, NetRomPacket packet)
        {
            engine.OnL3Rtt(from, packet);
            RefreshCapableNeighbours();
        }

        /// <summary>A neighbour the host knows is gone for non-INP3 reasons (the L4 dial-failure
        /// path). Drop its engine state and refresh the fan-out set. table.MarkNeighbourDown was
        /// already called by the shared helper (populating the recently-withdrawn set); the next
        /// <see cref="TickOnce"/> drains and fans out the withdrawals (the race fix).</summary>
        public void OnNeighbourGone(Callsign neighbour)
        {
            engine.RemoveNeighbour(neighbour);
            RefreshCapableNeighbours();
        }

        /// <summary>Driven from the coarse NODESINTERVAL sweep: the sweep may have aged out the last
        /// INP3 route to a destination (table.Sweep populates recentlyWithdrawn). One host tick
        /// drains those, marks them NEGATIVE, and fans them out — without waiting for the next 1 s
        /// timer firing. (TickOnce serialises against the 1 s timer via tickGate.)</summary>
        public void OnNodesInterval() => TickOnce();

        /// <summary>One host tick in the locked order (design §6.4): engine probes/resets →
        /// DRAIN the recently-withdrawn set (atomic snapshot+clear) → mark each NEGATIVE so the
        /// round fans out immediately → scheduler fans out due intents (each BuildRif gets the
        /// drained snapshot) → reconcile the capable fan-out set. Draining ONCE at the round start
        /// (rather than reading the live set per-neighbour and clearing after) closes the host-thread
        /// race: a concurrent IngestRif/MarkNeighbourDown/Sweep withdrawal landing mid-round is
        /// captured by the NEXT drain, never cleared unadvertised. Serialised via tickGate (the 1 s
        /// timer and the NODESINTERVAL nudge both call here). Never throws (a faulting sink must not
        /// kill the timer).</summary>
        public void TickOnce()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }
            lock (tickGate)
            {
                try
                {
                    RefreshCapableNeighbours();   // keep the fan-out set current before the scheduler reads it
                    engine.Tick();                // may raise NeighbourDown → MarkNeighbourDown → recentlyWithdrawn

                    // Drain AFTER engine.Tick so a 180 s-reset withdrawal raised during this tick is
                    // included; mark each NEGATIVE so the scheduler fans out THIS round (rule 1 beats
                    // any pending positive debounce). The same snapshot feeds every BuildRif via
                    // currentRoundWithdrawn — set just before scheduler.Tick (which invokes Advertise
                    // synchronously on this thread) and reset after, so it belongs to exactly one round.
                    var withdrawn = owner.table.DrainRecentlyWithdrawn();
                    foreach (var dest in withdrawn)
                    {
                        scheduler.MarkWithdrawn(dest);
                    }

                    currentRoundWithdrawn = withdrawn;
                    try
                    {
                        scheduler.Tick();
                    }
                    finally
                    {
                        currentRoundWithdrawn = Array.Empty<Callsign>();
                    }
                }
                catch (Exception ex)
                {
                    owner.LogInp3TickFault(ex);
                }
            }
        }

        // Reconcile the scheduler's fan-out target set from the engine's INP3-capable neighbours.
        // SetTargetNeighbours takes a defensive distinct+ordered copy, so a 1 s reconcile of a
        // handful of neighbours is free (simplicity over event plumbing — design §5).
        private void RefreshCapableNeighbours()
        {
            var capable = engine.Neighbours
                .Where(n => n.Inp3Capable)
                .Select(n => n.Neighbour)
                .ToList();
            scheduler.SetTargetNeighbours(capable);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            timer?.Dispose();
            engine.Dispose();
            scheduler.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "NET/ROM INP3: dropped a {Kind} frame to {Neighbour} - no interlink up (drop, don't dial).")]
    private partial void LogInp3SendDropped(string neighbour, string kind);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "NET/ROM INP3: neighbour {Neighbour} down (silent {SilentForMs} ms past the reset window) - dropped {Dropped} route(s).")]
    private partial void LogInp3NeighbourDown(string neighbour, long silentForMs, int dropped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NET/ROM INP3: host tick faulted (continuing).")]
    private partial void LogInp3TickFault(Exception ex);
}
