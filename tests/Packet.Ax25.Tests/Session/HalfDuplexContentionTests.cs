using System.Collections.Concurrent;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;
using Ax25Event = Packet.Ax25.Session.Ax25Event;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Deterministic reproduction of the issue #292 connected-mode stall over a
/// <b>two-station half-duplex</b> channel: a "node" accepts a connect, sends its
/// banner + prompt, and a "peer" sends a console command (an I-frame) that must
/// reach the node. No network, no live box, no wall-clock - two real
/// <see cref="Ax25Session"/>s over a synchronous, turn-based half-duplex medium
/// driven by a shared <see cref="FakeTimeProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Half-duplex model.</b> The medium is turn-based: everything a station emits
/// inside one pump turn is "its transmission for that turn." If <em>both</em>
/// stations transmit in the same turn, both rigs were keyed at once → both are
/// deaf → <b>both frames are lost</b> (there is no third listener, so capture is
/// irrelevant - exactly the issue's regime). A station also never hears its own
/// transmission. Otherwise the lone transmitter's frames are delivered to the
/// peer's inbound queue for the next turn.
/// </para>
/// <para>
/// <b>Why this reproduces the bug.</b> #292's key lead is that the per-port
/// <c>t1Ms</c> lever did nothing - the node sat at the same spec-default T1 (6 s)
/// as the peer. Two equal, phase-locked T1 timers fire together every period, so
/// both rigs key up at once and every transmission collides; the peer's command
/// never lands and the link times out to DM. Once the configured T1 is actually
/// honoured (this PR's engine fix), an asymmetric node/peer T1 drifts in and out of
/// phase, transmissions find clear air, and the exchange converges. These tests pin
/// both ends of that: phase-locked equal T1 stalls; honoured asymmetric T1
/// completes.
/// </para>
/// <para>
/// <b>Scope.</b> The turn-based medium collapses everything a station emits in one
/// turn into a single "transmission," so it models the <em>timer-phase</em>
/// dynamics faithfully but does not, on its own, isolate the secondary
/// air-occupancy effect of the node bursting banner+prompt as two I-frames. That
/// banner+prompt combine is covered as a node-behaviour change in
/// <c>Ax25ConsoleIntegrationTests</c>, not here.
/// </para>
/// </remarks>
public sealed class HalfDuplexContentionTests
{
    private static readonly Callsign NodeCall = new("M9YYY", 0);
    private static readonly Callsign PeerCall = new("M0LTE", 0);

    [Fact]
    public void Phase_locked_equal_T1_perpetually_collides_and_the_command_never_lands()
    {
        // The #292 stall, distilled: the configured per-port t1Ms had NO effect, so
        // the node sat at the SAME spec-default T1 (6 s) as the peer's retransmit
        // timer. Two equal, phase-locked timers fire together every period → both
        // rigs key up at once → every transmission collides → the peer's `info`
        // command never reaches the node and the link times out to DM.
        var link = new HalfDuplexLink(t1Ms: 6000, peerT1Ms: 6000);
        link.Connect();

        link.Node.Submit("BANNER\rPROMPT> ");
        link.Peer.Submit("info");
        link.RunRounds(40);

        link.CollisionCount.Should().BeGreaterThan(0, "the phase-locked timers must actually be colliding");
        link.Node.DeliveredText.Should().NotContain("info",
            "phase-locked equal T1 keeps colliding — the command never reaches the node (the #292 stall)");
    }

    [Fact]
    public void An_honoured_asymmetric_T1_breaks_the_lock_and_the_QSO_completes()
    {
        // The fix in effect: because the node's configured 10 s T1 now actually
        // reaches its T1 timer (it no longer gets reset to 6 s on connect), the
        // node's poll cadence drifts relative to the peer's 3 s timer. They fall in
        // and out of phase, so transmissions find clear air and the exchange
        // converges - the node hears `info`, the peer hears the banner, both windows
        // drain.
        var link = new HalfDuplexLink(t1Ms: 10000, peerT1Ms: 3000);
        link.Connect();

        link.Node.Submit("BANNER\rPROMPT> ");
        link.Peer.Submit("info");
        link.RunRounds(40);

        link.Node.DeliveredText.Should().Contain("info",
            "with an honoured, asymmetric T1 the node's quiet periods leave clear air for the peer's command");
        link.Peer.DeliveredText.Should().Contain("BANNER", "the node's banner reaches the peer");
        link.Node.Context.VS.Should().Be(link.Node.Context.VA, "the node's I-frames are all acknowledged");
        link.Peer.Context.VS.Should().Be(link.Peer.Context.VA, "the peer's I-frame is acknowledged");
    }

    [Fact]
    public void A_long_T1_is_actually_in_effect_after_the_accept_handshake()
    {
        // Guards the #292 root cause directly at the session level: the configured
        // T1V must survive the SABM/UA establishment path on BOTH the accepting and
        // initiating sessions (the figc4.1 path runs `SRT := Initial Default;
        // T1V := 2 * SRT`, which without the InitialSrt wiring resets it to 6 s).
        var link = new HalfDuplexLink(t1Ms: 10000, peerT1Ms: 3000);
        link.Connect();

        link.Node.Context.T1V.Should().Be(TimeSpan.FromMilliseconds(10000),
            "the node's configured T1V survives the inbound-accept handshake");
        link.Peer.Context.T1V.Should().Be(TimeSpan.FromMilliseconds(3000),
            "the initiator's configured T1V survives its own connect handshake");
    }

    // ─── Harness ────────────────────────────────────────────────────────

    /// <summary>Two real sessions on a synchronous, turn-based half-duplex medium
    /// with a shared FakeTimeProvider.</summary>
    private sealed class HalfDuplexLink
    {
        public Station Node { get; }
        public Station Peer { get; }
        private readonly FakeTimeProvider time = new();

        public HalfDuplexLink(int t1Ms, int peerT1Ms)
        {
            Node = new Station("NODE", NodeCall, PeerCall, time, t1Ms);
            Peer = new Station("PEER", PeerCall, NodeCall, time, peerT1Ms);
            Node.Peer = Peer;
            Peer.Peer = Node;
        }

        /// <summary>Establish the link from the peer (the inbound caller), pumping
        /// the half-duplex medium until both sides are Connected.</summary>
        public void Connect()
        {
            Peer.Session.PostEvent(new DlConnectRequest());
            // SABM → UA handshake. Drive turns (with the odd clock nudge for any
            // armed connect-timer) until both reach Connected.
            for (int i = 0; i < 50 && !(Node.State == "Connected" && Peer.State == "Connected"); i++)
            {
                Transmit();
                if (i % 2 == 1)
                {
                    time.Advance(TimeSpan.FromMilliseconds(50));
                }
            }
            if (Node.State != "Connected" || Peer.State != "Connected")
            {
                throw new InvalidOperationException($"connect failed: node={Node.State} peer={Peer.State}");
            }

            Node.DrainSignals();
            Peer.DrainSignals();
        }

        /// <summary>Advance virtual time in FINE steps (settling the medium after
        /// each), so each station's T1 fires at its <em>own</em> cadence rather than
        /// both firing together on a coarse jump. This is what lets two different
        /// T1 values drift in and out of phase - the realistic regime in which a
        /// quiet (long-T1) node eventually leaves clear air for the peer. The total
        /// span is <paramref name="rounds"/> × the smaller T1 (plenty of retries).</summary>
        public void RunRounds(int rounds)
        {
            var smaller = Node.Context.T1V < Peer.Context.T1V ? Node.Context.T1V : Peer.Context.T1V;
            // Sub-T1 granularity so a due timer fires in the turn it is due, not
            // lumped with the other station's.
            var step = smaller / 4;
            if (step < TimeSpan.FromMilliseconds(50))
            {
                step = TimeSpan.FromMilliseconds(50);
            }

            var total = TimeSpan.FromTicks(smaller.Ticks * rounds);
            SettleTransmissions();
            for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
            {
                time.Advance(step);
                SettleTransmissions();
            }
        }

        // Pump the medium until neither station has anything to transmit and no
        // inbound work remains, WITHOUT advancing the clock.
        private void SettleTransmissions()
        {
            for (int i = 0; i < 256; i++)
            {
                if (!Transmit())
                {
                    return;
                }
            }
            throw new InvalidOperationException("half-duplex medium did not settle — possible livelock");
        }

        // One turn of the half-duplex medium. Returns true if anything moved.
        // Collision rule: if BOTH stations transmitted this turn, both lose
        // everything (mutual deafness); else the lone transmitter's frames reach
        // the peer. Then both stations process their inbound queue (which may
        // enqueue new transmissions for the next turn).
        private bool Transmit()
        {
            var nodeTx = Node.TakeOutbox();
            var peerTx = Peer.TakeOutbox();

            bool moved = false;
            if (nodeTx.Count > 0 && peerTx.Count > 0)
            {
                // Both keyed at once → collision → both transmissions lost.
                CollisionCount += nodeTx.Count + peerTx.Count;
                moved = true;
            }
            else if (nodeTx.Count > 0)
            {
                foreach (var f in nodeTx)
                {
                    Peer.DeliverFromAir(f);
                }

                moved = true;
            }
            else if (peerTx.Count > 0)
            {
                foreach (var f in peerTx)
                {
                    Node.DeliverFromAir(f);
                }

                moved = true;
            }

            // Process any delivered frames; emissions land in the outbox for the
            // next turn (faithful: a response is a separate transmission).
            moved |= Node.ProcessInbound();
            moved |= Peer.ProcessInbound();
            return moved;
        }

        public int CollisionCount { get; private set; }
    }

    /// <summary>One station: a real <see cref="Ax25Session"/> whose wire sink fills
    /// an outbox (this turn's transmission) and whose inbound queue is fed by the
    /// medium.</summary>
    private sealed class Station
    {
        public string Name { get; }
        public Ax25Session Session { get; private set; } = null!;
        public Ax25SessionContext Context { get; }
        public string State => Session.CurrentState;

        public Station? Peer { get; set; }

        private readonly List<byte[]> outbox = new();
        private readonly Queue<Ax25Frame> inbound = new();
        private readonly ConcurrentQueue<DataLinkSignal> signals = new();
        private readonly StringBuilder delivered = new();

        public Station(string name, Callsign local, Callsign remote, FakeTimeProvider time, int t1Ms)
        {
            Name = name;
            var scheduler = new SystemTimerScheduler(time);
            Context = new Ax25SessionContext
            {
                Local = local,
                Remote = remote,
                T1V = TimeSpan.FromMilliseconds(t1Ms),
                Srt = TimeSpan.FromMilliseconds(t1Ms / 2.0),
            };
            var subroutines = new DefaultSubroutineRegistry();
            Ax25Session? sessionRef = null;

            void SendBytes(ReadOnlyMemory<byte> bytes) => outbox.Add(bytes.ToArray());

            void SendUpward(DataLinkSignal sig)
            {
                signals.Enqueue(sig);
                if (sig is DataLinkDataIndication di)
                {
                    delivered.Append(Encoding.UTF8.GetString(di.Info.Span));
                }
            }

            var dispatcher = new ActionDispatcher(
                onTimerExpiry: n => sessionRef!.PostEvent(TimerExpiry(n)),
                sendSFrame: s => SendBytes(s.ToAx25Frame(Context).ToBytes()),
                sendUFrame: s => SendBytes(s.ToAx25Frame(Context).ToBytes()),
                sendUiFrame: s => SendBytes(s.ToAx25Frame(Context).ToBytes()),
                sendIFrame: s => SendBytes(s.ToAx25Frame(Context).ToBytes()),
                sendUpward: SendUpward,
                // Contention-free LM is the spec's autonomous-ack path; the node
                // listener stubs it (see Ax25Listener), so model it the same way -
                // acks ride piggyback or the T1 poll, never an LM-SEIZE flush.
                sendLinkMux: _ => { },
                sendInternal: _ => { },
                subroutines: subroutines)
            {
                // The configured T1V seeds the establishment path so it is actually
                // honoured (the whole point of #292): InitialSrt = T1V/2 makes the
                // figc4.1 `T1V := 2 * SRT` reproduce the configured value.
                InitialSrt = TimeSpan.FromMilliseconds(t1Ms / 2.0),
            };

            var guards = new GuardEvaluator(
                Ax25SessionBindings.CreateDefault(Context, scheduler, () => sessionRef?.CurrentTrigger));
            subroutines.Wire(dispatcher, guards);

            Session = new Ax25Session(Context, scheduler, dispatcher, guards, TransitionMap(), "Disconnected");
            sessionRef = Session;
        }

        /// <summary>Submit a console payload for transmission to the peer.</summary>
        public void Submit(string text) =>
            Session.PostEvent(new DlDataRequest(Encoding.UTF8.GetBytes(text)));

        /// <summary>Take (and clear) everything this station put on the air this
        /// turn, re-parsed to frames at mod-8.</summary>
        public List<Ax25Frame> TakeOutbox()
        {
            var frames = new List<Ax25Frame>(outbox.Count);
            foreach (var bytes in outbox)
            {
                if (Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, extended: false, out var f))
                {
                    frames.Add(f);
                }
            }
            outbox.Clear();
            return frames;
        }

        /// <summary>A frame survived the air and arrives at this station.</summary>
        public void DeliverFromAir(Ax25Frame frame) => inbound.Enqueue(frame);

        /// <summary>Feed every queued inbound frame into the session. Returns true
        /// if anything was processed.</summary>
        public bool ProcessInbound()
        {
            if (inbound.Count == 0)
            {
                return false;
            }

            while (inbound.TryDequeue(out var frame))
            {
                Session.PostEvent(Ax25FrameClassifier.Classify(frame));
            }

            return true;
        }

        public string DeliveredText => delivered.ToString();
        public void DrainSignals() { while (signals.TryDequeue(out _)) { } }

        private static Ax25Event TimerExpiry(string name) => name switch
        {
            "T1" => new T1Expiry(),
            "T2" => new T2Expiry(),
            "T3" => new T3Expiry(),
            _ => throw new InvalidOperationException($"unexpected timer '{name}'"),
        };
    }

    private static Dictionary<string, IReadOnlyList<TransitionSpec>> TransitionMap() => new()
    {
        ["Disconnected"] = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"] = DataLink_AwaitingConnection.Transitions,
        ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
        ["Connected"] = DataLink_Connected.Transitions,
        ["AwaitingRelease"] = DataLink_AwaitingRelease.Transitions,
        ["TimerRecovery"] = DataLink_TimerRecovery.Transitions,
    };
}
