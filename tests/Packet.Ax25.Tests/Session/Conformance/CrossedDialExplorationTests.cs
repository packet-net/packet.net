using AwesomeAssertions;
using Packet.Ax25.Session;
using Xunit.Abstractions;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Two stations dialling each other at once, explored systematically
/// (packet.net#875): every ordering of the crossing's frames and timers up to a
/// depth, exhaustively, and seeded random walks beyond it, with up to one lost or
/// duplicated frame, at both moduli, with and without the pre-connect XID probe.
/// See <see cref="CrossedDialExplorer"/> for the model and the checks.
/// </summary>
/// <remarks>
/// A run that ends with a station in Timer Recovery with nothing outstanding and
/// T1 stopped is the known figure defect H1 (figc4.5 has no way out of a recovery
/// completed by an I frame's N(R); <see cref="TimerRecoveryStuckAfterIFrameAckTests"/>),
/// which needs a lost poll and is not a crossing fault. It is reported and
/// tolerated; everything else fails the test.
/// </remarks>
public class CrossedDialExplorationTests
{
    private readonly ITestOutputHelper output;

    public CrossedDialExplorationTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    public static IEnumerable<object[]> Configs()
    {
        foreach (var extended in new[] { false, true })
        {
            foreach (var probe in new[] { false, true })
            {
                yield return new object[] { new CrossedDialExplorer.Config(extended, probe) };
                yield return new object[] { new CrossedDialExplorer.Config(extended, probe, DropBudget: 1) };
                yield return new object[] { new CrossedDialExplorer.Config(extended, probe, DupBudget: 1) };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Every_ordering_to_depth_8_connects_both_ends_and_delivers(CrossedDialExplorer.Config cfg)
    {
        // About 4,000 orderings lossless and 22,000 to 25,000 with a loss or a duplicate
        // (the cap is well above; a capped depth-first search would only ever see prefixes
        // starting with the first choice).
        var outcomes = CrossedDialExplorer.Exhaustive(cfg, depth: 8, maxRuns: 40_000);
        outcomes.Count.Should().BeLessThan(40_000, "the search must finish, not be cut by the cap");
        output.WriteLine(CrossedDialExplorer.Report(outcomes));
        AssertClean(outcomes);
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Random_walks_to_depth_24_connect_both_ends_and_deliver(CrossedDialExplorer.Config cfg)
    {
        var outcomes = new List<CrossedDialExplorer.Outcome>();
        for (int seed = 0; seed < 500; seed++)
        {
            outcomes.Add(CrossedDialExplorer.RandomWalk(cfg, seed, steps: 24));
        }

        output.WriteLine(CrossedDialExplorer.Report(outcomes));
        AssertClean(outcomes);
    }

    [Fact]
    public void Two_losses_still_connect_both_ends_and_deliver()
    {
        var outcomes = new List<CrossedDialExplorer.Outcome>();
        foreach (var extended in new[] { false, true })
        {
            var cfg = new CrossedDialExplorer.Config(extended, Probe: false, DropBudget: 2, DupBudget: 1);
            for (int seed = 0; seed < 500; seed++)
            {
                outcomes.Add(CrossedDialExplorer.RandomWalk(cfg, seed, steps: 30));
            }
        }

        output.WriteLine(CrossedDialExplorer.Report(outcomes));
        AssertClean(outcomes);
    }

    /// <summary>A peer that runs the figures as drawn: it resets on a repeated SABM(E) and on a
    /// UA, as direwolf, rax25 and the Linux kernel do. Not <see cref="Ax25SessionQuirks.StrictlyFaithful"/>,
    /// which also changes how a mod-128 dial routes.</summary>
    private static readonly Ax25SessionQuirks FigurePeer = Ax25SessionQuirks.Default with
    {
        UnexpectedUaIgnored = false,
        RepeatedConnectUaIgnored = false,
        RepeatedConnectSabmReacknowledged = false,
    };

    public static IEnumerable<object[]> MixedConfigs()
    {
        foreach (var extended in new[] { false, true })
        {
            yield return new object[] { new CrossedDialExplorer.Config(extended, Probe: false, QuirksB: FigurePeer, SendAfterReset: true) };
            yield return new object[] { new CrossedDialExplorer.Config(extended, Probe: false, DropBudget: 1, QuirksB: FigurePeer, SendAfterReset: true) };
            yield return new object[] { new CrossedDialExplorer.Config(extended, Probe: true, QuirksB: FigurePeer, SendAfterReset: true) };
        }
    }

    [Theory]
    [MemberData(nameof(MixedConfigs))]
    public void Against_a_peer_that_follows_the_figures_the_quirks_lose_no_more_than_the_figure(CrossedDialExplorer.Config cfg)
    {
        // The figure peer resets on our stale retry, and the figure's reset is lossy by nature:
        // an acknowledgement for the old sequence still in flight is taken for the new one, and
        // its layer 3 is never told. Two figure-faithful stations do that to each other too. What
        // the quirks must not do is add to it: a station that dropped the peer's UA must not take
        // the peer's restarted frames for duplicates (the review of #877 found that hole). The
        // trees differ once the two ends behave differently, so the comparison is over the whole
        // space: the share of orderings that lose data with the quirks on must not exceed the
        // share with them off.
        var withQuirks = Sweep(cfg);
        var asFigure = Sweep(cfg with { Quirks = FigurePeer });

        output.WriteLine("with the quirks on this end:");
        output.WriteLine(CrossedDialExplorer.Report(withQuirks));
        output.WriteLine("as the figure at both ends:");
        output.WriteLine(CrossedDialExplorer.Report(asFigure));

        double lossy = Lossy(withQuirks), figureLossy = Lossy(asFigure);
        output.WriteLine($"lossy share: with the quirks {lossy:P2}, as the figure {figureLossy:P2}");
        // A hair either way is the same mechanism counted on two different trees (one run in
        // four thousand); a real hole shows as points, as the review's did.
        lossy.Should().BeLessThanOrEqualTo(figureLossy + 0.005, "the quirks must not add silent loss against a figure-following peer");
    }

    private static List<CrossedDialExplorer.Outcome> Sweep(CrossedDialExplorer.Config cfg)
    {
        var outcomes = CrossedDialExplorer.Exhaustive(cfg, depth: 8, maxRuns: 40_000);
        for (int seed = 0; seed < 300; seed++)
        {
            outcomes.Add(CrossedDialExplorer.RandomWalk(cfg, seed, steps: 24));
        }

        return outcomes;
    }

    private static double Lossy(List<CrossedDialExplorer.Outcome> outcomes) =>
        outcomes.Count(o => o.Violations.Any(v => v.StartsWith("data:", StringComparison.Ordinal))) / (double)outcomes.Count;

    [Fact]
    public void With_the_crossing_quirks_off_the_figure_resets_on_a_lossless_crossing()
    {
        // Why the quirks exist: the figures as drawn reset the link in most orderings in
        // which one T1 fires before the crossing completes, with no frame lost at all.
        var cfg = new CrossedDialExplorer.Config(Extended: false, Probe: false,
            Quirks: Ax25SessionQuirks.Default with { UnexpectedUaIgnored = false, RepeatedConnectSabmReacknowledged = false });
        var outcomes = CrossedDialExplorer.Exhaustive(cfg, depth: 6, maxRuns: 2000);
        output.WriteLine(CrossedDialExplorer.Report(outcomes));

        outcomes.Count(o => o.Resets > 0).Should().BeGreaterThan(outcomes.Count / 2,
            "the figure's unexpected-UA and SABM-while-connected arms reset the link on a plain crossing");
    }

    private static void AssertClean(IReadOnlyList<CrossedDialExplorer.Outcome> outcomes)
    {
        var bad = outcomes.Where(o => o.Violations.Any(v => !v.StartsWith("liveness (known", StringComparison.Ordinal))).ToList();
        bad.Should().BeEmpty(bad.Count > 0 ? "every run must connect both ends and deliver, or tell the sender, e.g.\n" + bad[0].Describe() : "");

        // A crossing that loses no frame never needs a reset. With a lost SABM(E) one shape
        // remains: the other end connects on its own dial's UA, having answered no call, and
        // the loser's T1 retry is on the wire the same frame as a peer that started over, which
        // the node hands to a fresh owner; the figure's reset runs and the owner is told
        // (CrossedDialRegressionTests pins it). So resets are asserted away only where nothing
        // was lost, and reported otherwise.
        var resets = outcomes.Where(o => o.Resets > 0 && o.Config.DropBudget == 0).ToList();
        resets.Should().BeEmpty(resets.Count > 0 ? "a crossing that loses nothing never needs a reset, e.g.\n" + resets[0].Describe() : "");
    }
}
