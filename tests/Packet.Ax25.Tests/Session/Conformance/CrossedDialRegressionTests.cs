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
    private static readonly Ax25SessionQuirks NoUaQuirk = Ax25SessionQuirks.Default with { UnexpectedUaIgnored = false };
    private static readonly Ax25SessionQuirks NoSabmQuirk = Ax25SessionQuirks.Default with { RepeatedConnectSabmReacknowledged = false };

    [Fact]
    public void A_T1_expiry_during_the_crossing_makes_each_side_answer_two_SABMs_and_the_second_UA_must_not_reset()
    {
        // packet.net#874 in its simplest form: A's SABM lands and is answered, then both T1s fire
        // before B's SABM lands. Each side now sees two SABMs and answers both; each dial gets two
        // UAs, the second after the peer's first data. figc4.4 t17 read that UA as unexpected.
        var cfg = new Config(Extended: false, Probe: false);
        var prefix = new[] { Choice.DeliverAB, Choice.Timer };

        Expect(Replay(cfg, prefix).Finish(), resets: 0);
        Expect(Replay(cfg with { Quirks = NoUaQuirk }, prefix).Finish(), resets: 1);
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
        Expect(Replay(cfg with { Quirks = NoSabmQuirk }, prefix).Finish(), resets: 1);
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
