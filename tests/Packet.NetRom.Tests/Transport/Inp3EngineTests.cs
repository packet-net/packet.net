using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Transport;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Tests.Transport;

/// <summary>
/// Deterministic tests for <see cref="Inp3Engine"/> (the INP3 link-timing engine,
/// slice I-2) driven by a <see cref="FakeTimeProvider"/>: probe-fires-on-cadence,
/// reflection-updates-SNTT, peer-probe-is-reflected, capability-learned, and the
/// 180 s no-reflection reset fires <see cref="Inp3Engine.NeighbourDown"/> for an
/// INP3-capable neighbour (and resets it) - plus the AMBIGUITY-I2-3 guard that a
/// never-capable vanilla neighbour is dropped silently.
/// </summary>
public sealed class Inp3EngineTests
{
    private static readonly Callsign Local = new("GB7PDN", 0);
    private static readonly Callsign Peer = new("GB7RDG", 0);

    private static Inp3Engine NewEngine(
        FakeTimeProvider clock,
        NetRomInp3Options? options,
        out List<(Callsign Neighbour, Inp3L3RttFrame Frame)> sent)
    {
        var captured = new List<(Callsign, Inp3L3RttFrame)>();
        sent = captured;
        var engine = new Inp3Engine(Local, options ?? NetRomInp3Options.Default, clock)
        {
            SendL3Rtt = (n, f) => captured.Add((n, f)),
        };
        return engine;
    }

    [Fact]
    public void Probe_fires_on_cadence_and_not_before()
    {
        var clock = new FakeTimeProvider();
        var opts = new NetRomInp3Options { L3RttInterval = TimeSpan.FromSeconds(60), L3RttResetWindow = TimeSpan.FromSeconds(180) };
        using var engine = NewEngine(clock, opts, out var sent);

        engine.ObserveNeighbour(Peer);

        // First tick: never-probed neighbour is immediately due (LastSent == 0).
        engine.Tick();
        sent.Should().ContainSingle("a freshly-observed neighbour is probed on the first tick");
        sent[0].Neighbour.Should().Be(Peer);
        sent[0].Frame.Packet.Network.Origin.Should().Be(Local, "the probe carries our node as L3 origin");
        sent[0].Frame.Packet.Network.Destination.Base.Should().Be(Inp3L3RttFrame.L3RttBase);

        // A probe is outstanding (AwaitingReflection) - no re-probe even past cadence.
        sent.Clear();
        clock.Advance(TimeSpan.FromSeconds(120));
        engine.Tick();
        sent.Should().BeEmpty("a neighbour with a probe in flight is never re-probed");
    }

    [Fact]
    public void Probe_does_not_re_fire_within_cadence_after_reflection()
    {
        var clock = new FakeTimeProvider();
        var opts = new NetRomInp3Options { L3RttInterval = TimeSpan.FromSeconds(60), L3RttResetWindow = TimeSpan.FromSeconds(180) };
        using var engine = NewEngine(clock, opts, out var sent);

        engine.ObserveNeighbour(Peer);
        engine.Tick();                 // probe #1 at t=0
        sent.Should().ContainSingle();

        // Reflect it 1 s later so the outstanding-probe flag clears.
        clock.Advance(TimeSpan.FromSeconds(1));
        engine.OnL3Rtt(Peer, sent[0].Frame);   // our own probe echoed back
        sent.Clear();

        // 30 s after probe #1 (< 60 s cadence) → no new probe.
        clock.Advance(TimeSpan.FromSeconds(29));
        engine.Tick();
        sent.Should().BeEmpty("cadence has not elapsed since the last send");

        // Past the 60 s mark since probe #1 → probe #2 fires.
        clock.Advance(TimeSpan.FromSeconds(31));
        engine.Tick();
        sent.Should().ContainSingle("the next probe fires once the cadence has elapsed");
    }

    [Fact]
    public void Reflection_of_our_probe_updates_SNTT_with_half_the_round_trip()
    {
        var clock = new FakeTimeProvider();
        var opts = new NetRomInp3Options { L3RttInterval = TimeSpan.FromSeconds(60), L3RttResetWindow = TimeSpan.FromSeconds(180) };
        using var engine = NewEngine(clock, opts, out var sent);

        engine.ObserveNeighbour(Peer);
        engine.Tick();                          // probe at t=0
        var ourProbe = sent[0].Frame;

        engine.SnttMs(Peer).Should().BeNull("no measurement before the first reflection");

        // Reflection arrives 400 ms later → RTT = 400, sample = RTT/2 = 200; the
        // first sample seeds the filter directly (SRT/Karn cold-start).
        clock.Advance(TimeSpan.FromMilliseconds(400));
        engine.OnL3Rtt(Peer, ourProbe);

        engine.SnttMs(Peer).Should().Be(200u, "first reflection seeds SNTT = RTT/2");

        var timing = engine.Neighbours.Should().ContainSingle().Subject;
        timing.Neighbour.Should().Be(Peer);
        timing.SnttMs.Should().Be(200u);
        timing.AwaitingReflection.Should().BeFalse("the outstanding-probe flag cleared on reflection");
    }

    [Fact]
    public void A_peer_probe_is_reflected_verbatim()
    {
        var clock = new FakeTimeProvider();
        using var engine = NewEngine(clock, NetRomInp3Options.Default, out var sent);

        // A probe ORIGINATED BY THE PEER (its origin is the peer, not us) - we must
        // echo it back byte-for-byte, not treat it as a reflection / SNTT sample.
        var peerProbe = Inp3L3RttFrame.Build(Peer);
        engine.OnL3Rtt(Peer, peerProbe);

        sent.Should().ContainSingle("a peer's probe is reflected back to it");
        sent[0].Neighbour.Should().Be(Peer);
        sent[0].Frame.Should().BeSameAs(peerProbe, "reflection is verbatim — the same frame goes back unchanged");
        sent[0].Frame.Packet.Network.Origin.Should().Be(Peer, "verbatim echo keeps the peer as the origin");

        engine.SnttMs(Peer).Should().BeNull("reflecting a peer's probe is not a measurement of our own RTT");
    }

    [Fact]
    public void Capability_is_learned_from_a_peer_probe()
    {
        var clock = new FakeTimeProvider();
        using var engine = NewEngine(clock, NetRomInp3Options.Default, out _);

        // The peer probes us with $N and $I4 - we learn it speaks INP3 and accepts IPv4.
        var peerProbe = Inp3L3RttFrame.Build(Peer, ipAccept: 4);
        peerProbe.Inp3Capable.Should().BeTrue();
        peerProbe.IpAccept.Should().Be(4);

        engine.OnL3Rtt(Peer, peerProbe);

        var timing = engine.Neighbours.Should().ContainSingle().Subject;
        timing.Inp3Capable.Should().BeTrue("a peer's $N probe proves it speaks INP3");
        timing.IpAccept.Should().Be((byte)4, "its $I4 token advertises IPv4 acceptance");
    }

    [Fact]
    public void Reset_window_with_no_reflection_fires_NeighbourDown_for_a_capable_neighbour_and_resets_it()
    {
        var clock = new FakeTimeProvider();
        var opts = new NetRomInp3Options { L3RttInterval = TimeSpan.FromSeconds(60), L3RttResetWindow = TimeSpan.FromSeconds(180) };
        using var engine = NewEngine(clock, opts, out var sent);

        var downEvents = new List<Inp3NeighbourDownEventArgs>();
        engine.NeighbourDown += (_, e) => downEvents.Add(e);

        // The peer proves it speaks INP3 (so the 180 s reset is allowed to raise
        // NeighbourDown - the AMBIGUITY-I2-3 guard).
        engine.OnL3Rtt(Peer, Inp3L3RttFrame.Build(Peer));
        engine.Neighbours.Should().ContainSingle().Which.Inp3Capable.Should().BeTrue();
        sent.Clear();   // discard the reflection we sent

        // It then goes silent. Probes keep firing but nothing reflects. Just under
        // the window → no reset yet.
        clock.Advance(TimeSpan.FromSeconds(179));
        engine.Tick();
        downEvents.Should().BeEmpty("179 s of silence is within the 180 s reset window");
        engine.Neighbours.Should().ContainSingle("the neighbour is still tracked");

        // Past the window → NeighbourDown fires and the state is reset (removed).
        clock.Advance(TimeSpan.FromSeconds(2));   // t = 181 s of silence
        engine.Tick();

        downEvents.Should().ContainSingle("an INP3-capable neighbour that went silent raises NeighbourDown");
        downEvents[0].Neighbour.Should().Be(Peer);
        downEvents[0].SilentForMs.Should().BeGreaterThanOrEqualTo(180_000, "it was silent at least the reset window");

        engine.Neighbours.Should().BeEmpty("the neighbour's INP3 state is reset (removed) on teardown");
        engine.SnttMs(Peer).Should().BeNull("a reset neighbour has no SNTT");
    }

    [Fact]
    public void A_never_capable_vanilla_neighbour_is_dropped_silently_without_NeighbourDown()
    {
        // The AMBIGUITY-I2-3 guard: a neighbour that never reflects our optimistic
        // probes (never proven INP3-capable) must NOT trigger a routing teardown -
        // it is reachable by vanilla NODES, it just doesn't speak L3RTT. After the
        // reset window it is dropped from probing silently, no callback.
        var clock = new FakeTimeProvider();
        var opts = new NetRomInp3Options
        {
            L3RttInterval = TimeSpan.FromSeconds(60),
            L3RttResetWindow = TimeSpan.FromSeconds(180),
            ProbeUnknownCapability = true,
        };
        using var engine = NewEngine(clock, opts, out var sent);

        var downEvents = new List<Inp3NeighbourDownEventArgs>();
        engine.NeighbourDown += (_, e) => downEvents.Add(e);

        engine.ObserveNeighbour(Peer);
        engine.Tick();   // optimistic probe fires (capability unknown)
        sent.Should().ContainSingle("ProbeUnknownCapability probes a not-yet-known neighbour");
        engine.Neighbours.Should().ContainSingle().Which.Inp3Capable.Should().BeFalse();

        // It never reflects. Past the reset window it is dropped - silently.
        clock.Advance(TimeSpan.FromSeconds(181));
        engine.Tick();

        downEvents.Should().BeEmpty("a never-capable vanilla neighbour is never MarkNeighbourDown'd");
        engine.Neighbours.Should().BeEmpty("but it is dropped from probing so we don't probe a vanilla peer forever");
    }

    [Fact]
    public void Conservative_policy_does_not_probe_an_unknown_capability_neighbour()
    {
        var clock = new FakeTimeProvider();
        var opts = new NetRomInp3Options
        {
            L3RttInterval = TimeSpan.FromSeconds(60),
            L3RttResetWindow = TimeSpan.FromSeconds(180),
            ProbeUnknownCapability = false,
        };
        using var engine = NewEngine(clock, opts, out var sent);

        engine.ObserveNeighbour(Peer);
        engine.Tick();
        sent.Should().BeEmpty("with ProbeUnknownCapability=false we wait to be probed first");

        // Once the peer probes us (proving capability), we start probing it.
        engine.OnL3Rtt(Peer, Inp3L3RttFrame.Build(Peer));
        sent.Clear();   // discard the reflection
        engine.Tick();
        sent.Should().ContainSingle("a now-known-capable neighbour is probed");
    }

    [Fact]
    public void Reflection_smoothing_follows_the_one_eighth_gain_IIR()
    {
        // Drive a sequence of reflections and assert the SNTT trajectory matches the
        // design §0.5 Example C (steady 200 ms RTT with one 2000 ms spike): the
        // first sample seeds, then SNTT' = (7*SNTT + sample + 4)/8.
        var clock = new FakeTimeProvider();
        var opts = new NetRomInp3Options { L3RttInterval = TimeSpan.FromSeconds(60), L3RttResetWindow = TimeSpan.FromSeconds(600) };
        using var engine = NewEngine(clock, opts, out var sent);
        engine.ObserveNeighbour(Peer);

        // RTT in ms for each probe; sample = RTT/2. Expected SNTT after each.
        var steps = new (int RttMs, uint ExpectedSntt)[]
        {
            (200, 100),    // seed = 100
            (200, 100),    // (7*100+100+4)/8 = 100
            (2000, 213),   // (7*100+1000+4)/8 = 213  (the spike)
            (200, 199),    // (7*213+100+4)/8 = 199
            (200, 187),    // (7*199+100+4)/8 = 187   (walking the outlier back)
        };

        foreach (var (rttMs, expected) in steps)
        {
            engine.Tick();                      // emit a probe (cadence has elapsed each loop)
            var probe = sent[^1].Frame;
            clock.Advance(TimeSpan.FromMilliseconds(rttMs));
            engine.OnL3Rtt(Peer, probe);
            engine.SnttMs(Peer).Should().Be(expected, $"RTT {rttMs} ms ⇒ sample {rttMs / 2} smoothed");
            // Advance past the cadence so the next loop's Tick probes again.
            clock.Advance(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public void OnL3Rtt_with_a_non_L3RTT_packet_returns_false_and_does_nothing()
    {
        var clock = new FakeTimeProvider();
        using var engine = NewEngine(clock, NetRomInp3Options.Default, out var sent);

        var notL3Rtt = new NetRomPacket
        {
            Network = new NetRomNetworkHeader { Origin = Peer, Destination = Local, TimeToLive = 10 },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 1,
                CircuitId = 1,
                TxSequence = 0,
                RxSequence = 0,
                Opcode = NetRomOpcode.Information,
                Flags = NetRomTransportFlags.None,
            },
            Payload = new byte[] { 1, 2, 3 },
        };

        engine.OnL3Rtt(Peer, notL3Rtt).Should().BeFalse("a non-L3RTT packet is not ours to handle");
        sent.Should().BeEmpty();
        engine.Neighbours.Should().BeEmpty("a non-L3RTT packet creates no neighbour state");
    }

    [Fact]
    public void RemoveNeighbour_inside_a_NeighbourDown_handler_is_safe()
    {
        // The callback fires outside the lock; a handler that re-enters the engine
        // (RemoveNeighbour for the same neighbour, already removed) must not deadlock
        // or throw.
        var clock = new FakeTimeProvider();
        var opts = new NetRomInp3Options { L3RttInterval = TimeSpan.FromSeconds(60), L3RttResetWindow = TimeSpan.FromSeconds(180) };
        using var engine = NewEngine(clock, opts, out _);

        engine.NeighbourDown += (_, e) => engine.RemoveNeighbour(e.Neighbour);

        engine.OnL3Rtt(Peer, Inp3L3RttFrame.Build(Peer));   // capable

        clock.Advance(TimeSpan.FromSeconds(181));
        var act = () => engine.Tick();
        act.Should().NotThrow();
        engine.Neighbours.Should().BeEmpty();
    }
}
