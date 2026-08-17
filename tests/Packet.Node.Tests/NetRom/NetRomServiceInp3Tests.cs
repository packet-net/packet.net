using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.NetRom;

namespace Packet.Node.Tests.NetRom;

/// <summary>
/// Deterministic node-level tests for the INP3 host wiring in <see cref="NetRomService"/>
/// (design <c>docs/netrom-inp3-host-integration-design.md</c>). They drive the host on a
/// <see cref="FakeTimeProvider"/> through the internal test seams
/// (<c>IngestInterlinkForTest</c> / <c>interlinkSendSinkForTest</c> / <c>Inp3TickForTest</c>),
/// so they exercise the real OnInterlinkData dispatch + the real Inp3Engine /
/// Inp3UpdateScheduler / routing-table glue without standing up real AX.25 handshakes.
///
/// The headline assertions: with <c>inp3.enabled=false</c> the node emits ZERO INP3 frames and
/// the L4 dispatch is unchanged; with it on, an inbound RIF is ingested as time-routes, we emit
/// a poison-reversed RIF, and an L3RTT reflection updates SNTT.
/// </summary>
[Trait("Category", "Node")]
public sealed class NetRomServiceInp3Tests
{
    // The port these single-port cases feed interlink frames in on.
    private const string Port = "vhf";

    private static readonly Callsign Me = new("GB7AAA", 0);
    private static readonly Callsign NbrA = new("GB7RDG", 0);   // an interlink neighbour
    private static readonly Callsign NbrB = new("GB7XYZ", 0);   // a second neighbour
    private static readonly Callsign DestSot = new("GB7SOT", 0);
    private static readonly Callsign DestMnc = new("GB7MNC", 0);
    private static readonly DateTimeOffset T0 = new(2026, 6, 8, 12, 0, 0, TimeSpan.Zero);

    // A node config with INP3 either off or on. INP3 rides on Connect (the interlink machinery),
    // so Connect is on in both; only Inp3.Enabled (+ the optional tuned overlay) differs.
    private static NetRomConfig Config(bool inp3Enabled, NetRomInp3Options? overlay = null) => new()
    {
        Enabled = true,
        Connect = true,
        Inp3 = overlay ?? new NetRomInp3Options { Enabled = inp3Enabled },
    };

    // A tuned overlay for the fan-out tests: a SHORT periodic RIF interval (5 s) so a periodic
    // fan-out fires BEFORE the 180 s reflection-timeout would reset a neighbour we keep alive by
    // reflecting its probe — production keeps neighbours alive with the 60 s probe cadence; the
    // test compresses the periodic cadence to do the same deterministically. (Validate only
    // constrains PositiveDebounce < RifInterval and resetWindow > probeInterval — both hold.)
    private static NetRomInp3Options FastRif => new()
    {
        Enabled = true,
        RifInterval = TimeSpan.FromSeconds(5),
        PositiveDebounce = TimeSpan.FromSeconds(1),
    };

    // A captured outbound interlink frame (the test send sink records these instead of touching a
    // real Ax25Session). Kind is derived from the wire bytes exactly as the inbound dispatch keys.
    private sealed record Sent(NeighbourKey Key, byte[] Bytes)
    {
        // The station the frame went to. The adjacency it left on is Key.PortId; with no
        // ports attached in this harness the selected link is unresolved (empty port).
        public Callsign Neighbour => Key.Callsign;

        public bool IsRif => Bytes.Length >= 1 && Bytes[0] == Inp3Rif.Signature;
        public bool IsL3Rtt => !IsRif && NetRomPacket.TryParse(Bytes, out var p) && Inp3L3RttFrame.IsL3Rtt(p!);
    }

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Clock { get; }
        public NetRomService Service { get; }
        public List<Sent> Sent { get; } = new();

        // When true, the send sink reports "an interlink is up" (so cold-interlink drops don't
        // fire). The test owns the "links are up" fiction since there are no real sessions.
        public bool InterlinkUp { get; set; } = true;

        public Harness(bool inp3Enabled, NetRomInp3Options? overlay = null)
        {
            Clock = new FakeTimeProvider(T0);
            Service = new NetRomService(Config(inp3Enabled, overlay), Clock, NullLogger<NetRomService>.Instance);
            Service.interlinkSendSinkForTest = (neighbour, bytes) =>
            {
                if (!InterlinkUp)
                {
                    return false;   // cold interlink — drop, don't dial (the engine just won't probe)
                }
                Sent.Add(new Sent(neighbour, bytes));
                return true;
            };
            Service.SetInp3LocalNodeForTest(Me);
        }

        public IEnumerable<Sent> Rifs => Sent.Where(s => s.IsRif);
        public IEnumerable<Sent> L3Rtts => Sent.Where(s => s.IsL3Rtt);

        public void Dispose() => Service.Dispose();
    }

    // Build a RIF wire body (0xFF-led) advertising one time-route to `dest`.
    private static byte[] RifBytes(Callsign dest, byte hop, int targetTimeMs)
    {
        var rif = new Inp3Rif
        {
            Rips =
            [
                new Inp3Rip { Destination = dest, HopCount = hop, TargetTimeMs = targetTimeMs, Tlvs = [] },
            ],
        };
        return rif.ToBytes();
    }

    // ─── Disabled: zero INP3 frames + behaviour unchanged (design §7.2 headline) ───

    [Fact]
    public void Disabled_overlay_is_not_constructed()
    {
        using var h = new Harness(inp3Enabled: false);
        h.Service.Inp3Enabled.Should().BeFalse("inp3.enabled=false ⇒ no Inp3Host");
        h.Service.Inp3EngineForTest.Should().BeNull();
        h.Service.Inp3SchedulerForTest.Should().BeNull();
    }

    [Fact]
    public void Disabled_node_emits_no_inp3_frames_and_does_not_recognise_a_rif_or_l3rtt()
    {
        using var h = new Harness(inp3Enabled: false);

        // Feed a RIF-shaped (0xFF-led) frame and an L3RTT-shaped NetRomPacket. With INP3 off these
        // get TODAY's generic treatment: the RIF-shaped bytes fail NetRomPacket.TryParse → dropped;
        // the L3RTT packet (dest L3RTT-0 ≠ us, and Forward is on by default under Connect) goes to
        // the forwarder, which finds no route and drops it. NEITHER is recognised as INP3.
        h.Service.IngestInterlinkForTest(Port, NbrA, RifBytes(DestSot, hop: 1, targetTimeMs: 100));
        var probe = Inp3L3RttFrame.Build(NbrA).ToBytes();   // an L3RTT a peer would send us
        h.Service.IngestInterlinkForTest(Port, NbrA, probe);

        // Even after driving the (no-op) interval, nothing INP3 ever reaches the wire.
        h.Clock.Advance(TimeSpan.FromHours(2));

        h.Sent.Should().BeEmpty("a disabled node emits zero L3RTT / RIF frames");
        h.Service.Snapshot().Destinations.Should().NotContain(d => d.Destination == DestSot,
            "a RIF is not ingested as a route when INP3 is off — it was dropped as an unparseable L4 frame");
    }

    [Fact]
    public void Disabled_node_still_dispatches_a_normal_l4_datagram_unchanged()
    {
        using var h = new Harness(inp3Enabled: false);

        // A normal L4 datagram addressed to US must still reach the circuit manager exactly as
        // today (the default-off guarantee at the OnInterlinkData seam). We assert via a marker:
        // an L4 datagram to us doesn't go to the send sink (it terminates locally), and no INP3
        // frame is emitted. (Full L4 termination is covered by the existing L3/L4 integration
        // tests; here we only prove the disabled INP3 branch is the original body.)
        var l4 = new NetRomPacket
        {
            Network = new NetRomNetworkHeader { Origin = NbrA, Destination = Me, TimeToLive = 25 },
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

        h.Service.IngestInterlinkForTest(Port, NbrA, l4.ToBytes());

        h.Sent.Should().BeEmpty("a datagram addressed to us terminates locally; nothing goes back on the wire as INP3");
    }

    // ─── Enabled: inbound RIF ingested as time-routes ───

    [Fact]
    public void Enabled_overlay_is_constructed()
    {
        using var h = new Harness(inp3Enabled: true);
        h.Service.Inp3Enabled.Should().BeTrue();
        h.Service.Inp3EngineForTest.Should().NotBeNull();
        h.Service.Inp3SchedulerForTest.Should().NotBeNull();
    }

    [Fact]
    public void An_l3rtt_reflection_updates_sntt()
    {
        using var h = new Harness(inp3Enabled: true);

        // Observe NbrA + send our probe: feeding any 0xCF frame observes the neighbour; the first
        // tick (LastL3RttSent == NeverProbed) sends our probe (ProbeUnknownCapability default on).
        h.Service.IngestInterlinkForTest(Port, NbrA, RifBytes(DestSot, 1, 100));   // observes NbrA (RIF ignored: link unmeasured)
        h.Service.Inp3TickForTest();

        var ourProbe = h.L3Rtts.Should().ContainSingle("the engine probes NbrA on the first tick").Subject;
        ourProbe.Neighbour.Should().Be(NbrA);

        // 80 ms later, NbrA reflects our probe verbatim (origin still us). RTT = 80 ms → SNTT = 40.
        h.Clock.Advance(TimeSpan.FromMilliseconds(80));
        h.Service.IngestInterlinkForTest(Port, NbrA, ourProbe.Bytes);

        h.Service.Inp3EngineForTest!.SnttMs(NbrA).Should().Be(40u,
            "RTT 80 ms ÷ 2 → the first sample seeds SNTT directly at 40 ms");
    }

    [Fact]
    public void An_inbound_rif_on_a_measured_link_is_ingested_as_a_time_route()
    {
        using var h = new Harness(inp3Enabled: true);

        // First measure the link so the RIF actually learns (an un-probed link learns nothing).
        MeasureLink(h, NbrA, rttMs: 100);   // → SNTT 50

        // Now a RIF advertising SOT (target 100, hop 1) over NbrA. localTargetTime = 100 + 50 + 10.
        h.Service.IngestInterlinkForTest(Port, NbrA, RifBytes(DestSot, hop: 1, targetTimeMs: 100));

        var dest = h.Service.Snapshot().Destinations.SingleOrDefault(d => d.Destination == DestSot);
        dest.Should().NotBeNull("the RIF teaches an INP3 time-route to SOT via NbrA");
        var route = dest!.Routes.Single(r => r.Neighbour == NbrA);
        route.Inp3.Should().NotBeNull("the route carries an INP3 metric");
        route.Inp3!.TargetTimeMs.Should().Be(160, "100 (peer) + 50 (SNTT) + 10 (per-hop)");
        route.Inp3.HopCount.Should().Be(2, "peer hop 1 + 1 through us");
    }

    [Fact]
    public void An_inbound_rif_is_never_fed_to_the_circuit_or_forward_path()
    {
        using var h = new Harness(inp3Enabled: true);
        MeasureLink(h, NbrA, rttMs: 100);
        h.Sent.Clear();   // drop the probe we sent while measuring

        // A RIF to a destination that is NOT us must be peeled off as INP3 — never forwarded.
        h.Service.IngestInterlinkForTest(Port, NbrA, RifBytes(DestSot, hop: 1, targetTimeMs: 100));

        h.Sent.Should().NotContain(s => !s.IsRif && !s.IsL3Rtt,
            "a RIF is ingested, never relayed onward as a forwarded L4 datagram");
        h.Service.Snapshot().Destinations.Should().Contain(d => d.Destination == DestSot, "it was ingested");
    }

    // ─── Enabled: we emit a poison-reversed RIF ───

    [Fact]
    public void After_ingesting_a_rif_a_periodic_fan_out_emits_a_poison_reversed_rif()
    {
        using var h = new Harness(inp3Enabled: true, overlay: FastRif);

        // Measure BOTH neighbours so both are INP3-capable fan-out targets, and so a RIF learns.
        MeasureLink(h, NbrA, rttMs: 100);   // SNTT 50
        MeasureLink(h, NbrB, rttMs: 60);    // SNTT 30
        h.Sent.Clear();

        // Learn SOT via NbrA (160 ms local target time).
        h.Service.IngestInterlinkForTest(Port, NbrA, RifBytes(DestSot, hop: 1, targetTimeMs: 100));

        // Advance past the (compressed 5 s) periodic RIF interval — but under the 180 s reset
        // window, so the neighbours stay alive — and tick → a Periodic fan-out emits one full RIF
        // per capable neighbour. (The capable set is reconciled from the engine.)
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        h.Service.Inp3TickForTest();

        var rifs = h.Rifs.ToList();
        rifs.Should().NotBeEmpty("a periodic fan-out emits a RIF to each capable neighbour");
        rifs.Select(r => r.Neighbour).Should().Contain(NbrA).And.Contain(NbrB);

        // The RIF toward NbrA must POISON SOT (it is reached via NbrA) at the horizon; the RIF
        // toward NbrB must carry SOT FINITE (NbrB is not SOT's next hop).
        var towardA = ParseRif(rifs.First(r => r.Neighbour == NbrA));
        var towardB = ParseRif(rifs.First(r => r.Neighbour == NbrB));

        towardA.Rips.Single(r => r.Destination == DestSot).IsHorizon.Should().BeTrue(
            "SOT is via NbrA → poison-reverse it back to NbrA at the horizon");
        var bSot = towardB.Rips.Single(r => r.Destination == DestSot);
        bSot.IsHorizon.Should().BeFalse("SOT is finite toward a neighbour it is not reached through");
        bSot.TargetTimeMs.Should().Be(160, "100 + 50 (NbrA SNTT) + 10 — our best held target time");

        // The own-node source RIP is present, first, at 0/0, never poisoned.
        towardA.Rips[0].Destination.Should().Be(Me);
        towardA.Rips[0].TargetTimeMs.Should().Be(0);
        towardA.Rips[0].IsHorizon.Should().BeFalse();
    }

    [Fact]
    public void An_inbound_rif_withdrawal_fans_out_to_every_neighbour_then_clears()
    {
        // The NEGATIVE (withdrawal) path is the correctness-critical one (design §6.5). Drive it via
        // an explicit horizon RIP from NbrA (a peer withdrawing SOT) — deterministic, no clock
        // juggling — and assert it reaches EVERY capable neighbour's RIF, then clears after the round.
        using var h = new Harness(inp3Enabled: true, overlay: FastRif);
        MeasureLink(h, NbrA, rttMs: 100);
        MeasureLink(h, NbrB, rttMs: 60);

        // Learn SOT via NbrA, fan it out once (periodic), then clear the wire capture.
        h.Service.IngestInterlinkForTest(Port, NbrA, RifBytes(DestSot, hop: 1, targetTimeMs: 100));
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        h.Service.Inp3TickForTest();
        h.Service.Snapshot().Destinations.Should().Contain(d => d.Destination == DestSot, "SOT learned via NbrA");
        h.Sent.Clear();

        // NbrA now withdraws SOT at the horizon. SOT loses its last INP3 route → recently-withdrawn.
        // The next tick DRAINS the set, marks SOT NEGATIVE, and fans out IMMEDIATELY (no debounce) to
        // every capable neighbour, each RIF carrying the one-shot horizon withdrawal for SOT.
        h.Service.IngestInterlinkForTest(Port, NbrA, RifBytes(DestSot, hop: 1, targetTimeMs: Inp3Rip.HorizonMs));
        h.Service.Inp3TickForTest();   // 0 s later → only the NEGATIVE (immediate) path fires, not periodic

        var rifs = h.Rifs.ToList();
        rifs.Should().NotBeEmpty("the withdrawal fans out immediately (the NEGATIVE path)");
        rifs.Select(r => r.Neighbour).Should().Contain(NbrA).And.Contain(NbrB,
            "the withdrawal must reach EVERY neighbour's RIF (the round-clear correctness, design §6.4)");
        foreach (var r in rifs)
        {
            ParseRif(r).Rips.Single(x => x.Destination == DestSot).IsHorizon.Should().BeTrue(
                $"SOT is withdrawn at the horizon in the RIF toward {r.Neighbour}");
        }

        // The round boundary (TickOnce drains the set atomically before scheduler.Tick) emptied the
        // set, so the next periodic RIF omits SOT entirely.
        h.Sent.Clear();
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        h.Service.Inp3TickForTest();
        foreach (var r in h.Rifs)
        {
            ParseRif(r).Rips.Should().NotContain(x => x.Destination == DestSot,
                "after the round cleared, SOT is absent from every RIF until a fresh RIF re-learns it");
        }
    }

    [Fact]
    public void Withdrawals_accumulated_between_ticks_all_fan_out_on_the_next_drain()
    {
        // The accumulate-then-drain model (the host-thread race fix): the host no longer escalates
        // per-ingest; a withdrawal lands in the table's recently-withdrawn set and the NEXT TickOnce
        // DRAINS the set atomically. Two destinations withdrawn by separate ingests with NO tick
        // between them must therefore BOTH fan out on the single following tick — proving the drain
        // is the one round boundary and an ingest that arrives between rounds is captured, not lost.
        using var h = new Harness(inp3Enabled: true, overlay: FastRif);
        MeasureLink(h, NbrA, rttMs: 100);
        MeasureLink(h, NbrB, rttMs: 60);

        // Learn SOT and MNC via NbrA, fan them out once, then clear the wire capture.
        h.Service.IngestInterlinkForTest(Port, NbrA, RifBytes(DestSot, hop: 1, targetTimeMs: 100));
        h.Service.IngestInterlinkForTest(Port, NbrA, RifBytes(DestMnc, hop: 1, targetTimeMs: 120));
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        h.Service.Inp3TickForTest();
        h.Service.Snapshot().Destinations.Should().Contain(d => d.Destination == DestSot)
            .And.Contain(d => d.Destination == DestMnc, "both learned via NbrA");
        h.Sent.Clear();

        // Withdraw BOTH via two separate ingests, with NO tick in between — they accumulate in the
        // recently-withdrawn set.
        h.Service.IngestInterlinkForTest(Port, NbrA, RifBytes(DestSot, hop: 1, targetTimeMs: Inp3Rip.HorizonMs));
        h.Service.IngestInterlinkForTest(Port, NbrA, RifBytes(DestMnc, hop: 1, targetTimeMs: Inp3Rip.HorizonMs));

        // One tick drains both and fans them out together to every capable neighbour.
        h.Service.Inp3TickForTest();

        var rifs = h.Rifs.ToList();
        rifs.Select(r => r.Neighbour).Should().Contain(NbrA).And.Contain(NbrB);
        foreach (var r in rifs)
        {
            var rif = ParseRif(r);
            rif.Rips.Single(x => x.Destination == DestSot).IsHorizon.Should().BeTrue(
                $"SOT withdrawn in the RIF toward {r.Neighbour}");
            rif.Rips.Single(x => x.Destination == DestMnc).IsHorizon.Should().BeTrue(
                $"MNC withdrawn in the RIF toward {r.Neighbour} — accumulated then drained together");
        }
    }

    // ─── helpers ───

    // Measure a link end-to-end through the host: observe the neighbour, tick to send our probe,
    // advance the clock by rttMs, then feed the probe back as its (verbatim) reflection. Leaves the
    // engine's SNTT(neighbour) = rttMs/2 and the neighbour marked INP3-capable.
    private static void MeasureLink(Harness h, Callsign neighbour, int rttMs)
    {
        // Observe the neighbour (a peer probe TO us both observes it and marks it capable via $N,
        // and is reflected by the engine — but we want OUR probe out so we can reflect it for the
        // RTT sample). Send a peer probe so the neighbour becomes capable, then drive our probe.
        var peerProbe = Inp3L3RttFrame.Build(neighbour).ToBytes();   // origin = neighbour → a peer probe to us
        h.Service.IngestInterlinkForTest(Port, neighbour, peerProbe);      // observes + learns $N capability + reflects

        h.Service.Inp3TickForTest();   // sends OUR probe to the neighbour (cadence elapsed)
        var ourProbe = h.Sent.LastOrDefault(s => s.IsL3Rtt && s.Neighbour.Equals(neighbour)
            && NetRomPacket.TryParse(s.Bytes, out var p) && p!.Network.Origin.Equals(Me));
        ourProbe.Should().NotBeNull($"the engine should probe {neighbour}");

        h.Clock.Advance(TimeSpan.FromMilliseconds(rttMs));
        h.Service.IngestInterlinkForTest(Port, neighbour, ourProbe!.Bytes);   // reflect OUR probe → SNTT sample
    }

    private static Inp3Rif ParseRif(Sent sent)
    {
        Inp3Rif.TryParse(sent.Bytes, out var rif).Should().BeTrue("a sent RIF must round-trip-parse");
        return rif!;
    }
}
