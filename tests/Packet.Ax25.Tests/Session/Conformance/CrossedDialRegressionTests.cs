using AwesomeAssertions;
using Packet.Ax25.Session;
using Xunit.Abstractions;
using static Packet.Ax25.Tests.Session.Conformance.CrossedDialExplorer;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// The shortest crossing the explorer found for each fault it turned up
/// (packet.net#875), replayed step by step, with the paired run that shows what the
/// figure does without the quirk that fixes it. Each is a regression test for one
/// shape; the explorer's sweeps guard the rest of the space.
/// </summary>
public class CrossedDialRegressionTests(ITestOutputHelper output)
{
    private static readonly Ax25SessionQuirks NoUaQuirk = Ax25SessionQuirks.Default with { Ax25Spec114UnexpectedUaIgnored = false };
    private static readonly Ax25SessionQuirks NoSabmQuirk = Ax25SessionQuirks.Default with { Ax25Spec50RepeatedConnectSabmReacknowledged = false };

    [Fact]
    public void A_T1_expiry_during_the_crossing_makes_each_side_answer_two_SABMs_and_the_second_UA_must_not_reset()
    {
        // packet.net#874 in its simplest form: A's SABM lands and is answered, then both T1s fire
        // before B's SABM lands. Each side now sees two SABMs and answers both; each dial gets two
        // UAs, the second after the peer's first data. figc4.4 t17 read that UA as unexpected.
        var cfg = new Config(Extended: false, Probe: false);
        var prefix = new[] { Choice.DeliverAB, Choice.Timer };

        Expect(Replay(cfg, prefix).Finish(), resets: 0);
        Replay(cfg with { Quirks = NoUaQuirk }, prefix).Finish().Resets.Should().BeGreaterThan(0, "the figure resets on the second UA");
    }

    [Fact]
    public void The_issues_own_timeline_in_mod_128()
    {
        // packet.net#874's ordering: A's SABME is answered, T1 fires at both ends, B's UA arrives
        // while A's retry is still queued, both send at once, A's retry is re-acknowledged (#857),
        // and that UA reaches A after B's data. On main A reset, B reset on A's third SABME, both
        // queues were discarded and the link went silent with data outstanding.
        var cfg = new Config(Extended: true, Probe: false);
        var prefix = new[] { Choice.DeliverAB, Choice.Timer, Choice.DeliverAB, Choice.DeliverBA, Choice.DeliverBA, Choice.Timer, Choice.Timer };

        Expect(Replay(cfg, prefix).Finish(), resets: 0);
        Replay(cfg with { Quirks = NoUaQuirk }, prefix).Finish().Resets.Should().BeGreaterThan(0, "the figure resets on the retry's UA");
    }

    [Fact]
    public void A_lost_SABM_leaves_one_side_still_dialling_and_its_retry_resets_the_other_which_is_told()
    {
        // The one shape left with a lost frame. A's SABM is lost; A answers B's SABM, so B is up
        // on its own dial's UA and sends; A is still waiting and on T1 sends its SABM again. B
        // never answered a call from A, so the repeated-SABM window is not open, and on the wire
        // A's retry is the same frame as a peer that started over, which the node hands to a
        // fresh owner (Ax25NodeConnectionGracefulCloseTests). The figure's reset runs at B: its
        // data is discarded and its layer 3 hears DL-CONNECT indication. Both ends connect and
        // A's data arrives. Re-acknowledging here instead (LinBPQ's rule) would keep B's data
        // but take that hand-off away; a decision for the node, not the session.
        var cfg = new Config(Extended: false, Probe: false, DropBudget: 1);
        var prefix = new[] { Choice.DropAB };

        var outcome = Replay(cfg, prefix).Finish();
        output.WriteLine(outcome.Describe());
        outcome.Violations.Should().BeEmpty();
        outcome.Resets.Should().Be(1);
        outcome.StateA.Should().Be("Connected");
        outcome.StateB.Should().Be("Connected");
    }

    [Fact]
    public void A_second_XID_response_during_the_crossing_must_not_close_the_repeated_SABM_window()
    {
        // With the pre-connect probe, A's TM201 retry draws a second XID response from B, which
        // reaches A's session (its negotiation is over) between B's SABM and B's retry of it.
        // #857's window closed on any frame but a UA, so the retry reset A. Frames that move no
        // sequence variable leave the re-cut window open.
        var cfg = new Config(Extended: false, Probe: true);
        var prefix = new[]
        {
            Choice.DeliverAB, Choice.DeliverBA, Choice.DeliverAB, Choice.Timer,
            Choice.DeliverAB, Choice.DeliverBA, Choice.DeliverAB, Choice.Timer,
        };

        Expect(Replay(cfg, prefix).Finish(), resets: 0);
        Replay(cfg with { Quirks = NoSabmQuirk }, prefix).Finish().Resets.Should().BeGreaterThan(0, "the figure resets on the repeated SABM");
    }

    /// <summary>A peer that runs the figures as drawn (direwolf, rax25, the Linux kernel).</summary>
    private static readonly Ax25SessionQuirks FigurePeer = Ax25SessionQuirks.Default with
    {
        Ax25Spec114UnexpectedUaIgnored = false,
        Ax25Spec114RepeatedConnectUaIgnored = false,
        Ax25Spec50RepeatedConnectSabmReacknowledged = false,
    };

    [Fact]
    public void A_figure_following_peer_that_resets_on_our_retry_and_sends_again_is_not_taken_for_a_duplicate()
    {
        // The review of #877 found this hole in a plain drop-every-UA: B (the figure) resets on
        // A's stale SABM retry and answers UA; A drops the UA and keeps its link; B's layer 3,
        // told, sends new data as S0; A, with V(r) = 1 from B's earlier frame, took that S0 for a
        // duplicate, discarded it and acknowledged it. Now the dropped UA is remembered, and B's
        // restarted sequence (a 0 with new bytes while V(r) is not 0) dispatches it after all:
        // A resets, both ends are told, and B's data goes on the new link.
        //
        // What this ordering still loses is the figure's own: A's acknowledgement of B's first
        // frame was already on its way when B reset, B takes it for the new frame, and B's
        // second reset (on A's SABM, with nothing outstanding as B sees it) tells nobody. Two
        // figure-faithful stations lose the same frame in the same ordering, which is the
        // baseline below; the quirks add nothing to it.
        var cfg = new Config(Extended: false, Probe: false, QuirksB: FigurePeer, SendAfterReset: true);
        var prefix = new[] { Choice.DeliverAB, Choice.DeliverBA, Choice.DeliverAB, Choice.Timer };

        var outcome = Replay(cfg, prefix).Finish();
        output.WriteLine(outcome.Describe());
        outcome.Trace.Should().Contain(t => t.Contains("A: reset (Connected t17_ua_received", StringComparison.Ordinal),
            "the dropped UA is dispatched once B's sequence restarts, rather than B's new frame being taken for a duplicate");
        outcome.Harness.A.Delivered.Should().NotContain(d => d.SequenceEqual(new byte[] { 0x32, 0x31 }),
            "and A neither delivered it as new nor acknowledged it as a duplicate");

        var baseline = Replay(cfg with { Quirks = FigurePeer }, prefix).Finish();
        output.WriteLine("as the figure at both ends:");
        output.WriteLine(baseline.Describe());
        outcome.Violations.Should().BeEquivalentTo(baseline.Violations, "the quirks lose nothing the figure keeps in this ordering");
    }

    [Fact]
    public void A_duplicated_frame_zero_after_a_dropped_UA_is_still_a_duplicate()
    {
        // The other side of that rule: a copy of the S0 already taken (a duplicating path, or a
        // retransmission) carries the same bytes, and is not a restart. No reset.
        var cfg = new Config(Extended: false, Probe: false, DupBudget: 1);
        var prefix = new[]
        {
            Choice.Timer, Choice.DeliverBA, Choice.DeliverAB, Choice.DeliverAB, Choice.DeliverAB, Choice.DeliverBA,
            Choice.DeliverBA, Choice.DeliverAB, Choice.DupAB,
        };

        Expect(Replay(cfg, prefix).Finish(), resets: 0);
    }

    private void Expect(Outcome outcome, int resets)
    {
        output.WriteLine(outcome.Describe());
        outcome.Violations.Should().BeEmpty();
        outcome.Resets.Should().Be(resets);
        outcome.StateA.Should().Be("Connected");
        outcome.StateB.Should().Be("Connected");
    }
}
