using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Transport;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Tests.Transport;

/// <summary>
/// Deterministic tests for <see cref="Inp3UpdateScheduler"/> (the INP3 triggered-update
/// timing state machine, slice I-4) driven by a <see cref="FakeTimeProvider"/>:
/// negative-fires-immediately, positive-is-debounced-then-fires, periodic-fires-on-interval,
/// and negative-preempts-a-pending-positive-batch - plus the locked monotonic-class
/// (upgrade-not-downgrade) and debounce-coalescing invariants (design §3.3 / §4 Storm).
/// </summary>
public sealed class Inp3UpdateSchedulerTests
{
    private static readonly Callsign N1 = new("GB7AAA", 0);
    private static readonly Callsign N2 = new("GB7BBB", 0);
    private static readonly Callsign N3 = new("GB7CCC", 0);

    private static readonly Callsign DestA = new("M0AAA", 0);
    private static readonly Callsign DestB = new("M0BBB", 0);

    private static Inp3UpdateScheduler NewScheduler(
        FakeTimeProvider clock,
        out List<Inp3AdvertiseIntent> intents,
        NetRomInp3Options? options = null,
        IReadOnlyCollection<Callsign>? neighbours = null)
    {
        var captured = new List<Inp3AdvertiseIntent>();
        intents = captured;
        var opts = options ?? new NetRomInp3Options
        {
            RifInterval = TimeSpan.FromSeconds(300),
            PositiveDebounce = TimeSpan.FromSeconds(5),
        };
        var scheduler = new Inp3UpdateScheduler(opts, clock)
        {
            Advertise = i => captured.Add(i),
        };
        scheduler.SetTargetNeighbours(neighbours ?? new[] { N1, N2 });
        return scheduler;
    }

    [Fact]
    public void Negative_fires_immediately_on_next_tick_for_every_neighbour()
    {
        var clock = new FakeTimeProvider();
        using var scheduler = NewScheduler(clock, out var intents, neighbours: new[] { N1, N2, N3 });

        scheduler.MarkWithdrawn(DestA);   // a loss is always NEGATIVE

        // No debounce on NEGATIVE: the very next tick (no clock advance) fans out.
        scheduler.Tick();

        intents.Should().HaveCount(3, "a NEGATIVE change fans out to every INP3-capable neighbour at once");
        intents.Select(i => i.Neighbour).Should().BeEquivalentTo(new[] { N1, N2, N3 });
        intents.Should().OnlyContain(i => i.Reason == Inp3AdvertiseReason.Triggered);

        // The dirty flag cleared - a follow-up tick with no new change is silent.
        intents.Clear();
        scheduler.Tick();
        intents.Should().BeEmpty("the NEGATIVE fan-out cleared the dirty flag");
    }

    [Fact]
    public void Positive_is_debounced_then_fires()
    {
        var clock = new FakeTimeProvider();
        using var scheduler = NewScheduler(clock, out var intents);   // N1, N2

        scheduler.MarkDirty(DestA, Inp3UpdateClass.Positive);

        // Before the 5 s debounce elapses, ticking does NOT fan out the positive.
        scheduler.Tick();
        intents.Should().BeEmpty("a POSITIVE change is held until the debounce elapses");

        clock.Advance(TimeSpan.FromSeconds(4));
        scheduler.Tick();
        intents.Should().BeEmpty("still inside the 5 s debounce window");

        // Crossing the debounce boundary fans out once, to every neighbour.
        clock.Advance(TimeSpan.FromSeconds(1));   // t = 5 s == PositiveDebounce
        scheduler.Tick();
        intents.Should().HaveCount(2, "the debounced positive drains to both neighbours once the window elapses");
        intents.Select(i => i.Neighbour).Should().BeEquivalentTo(new[] { N1, N2 });
        intents.Should().OnlyContain(i => i.Reason == Inp3AdvertiseReason.Triggered);

        // Drained - no repeat.
        intents.Clear();
        clock.Advance(TimeSpan.FromSeconds(10));
        scheduler.Tick();
        intents.Should().BeEmpty("the positive batch drained exactly once");
    }

    [Fact]
    public void Positive_burst_within_the_window_coalesces_to_one_fan_out()
    {
        var clock = new FakeTimeProvider();
        using var scheduler = NewScheduler(clock, out var intents);   // N1, N2

        // Two positives marked at different times inside one debounce window.
        scheduler.MarkDirty(DestA, Inp3UpdateClass.Positive);
        clock.Advance(TimeSpan.FromSeconds(2));
        scheduler.MarkDirty(DestB, Inp3UpdateClass.Positive);

        // The debounce is anchored on the EARLIEST mark (DestA at t=0), so it drains
        // at t=5 (one window after the first), not t=7.
        clock.Advance(TimeSpan.FromSeconds(3));   // t = 5 s
        scheduler.Tick();

        intents.Should().HaveCount(2, "a burst of positives coalesces into ONE fan-out per neighbour");
        intents.Select(i => i.Neighbour).Should().BeEquivalentTo(new[] { N1, N2 });
    }

    [Fact]
    public void Periodic_fires_on_interval_regardless_of_dirty_state()
    {
        var clock = new FakeTimeProvider();
        var opts = new NetRomInp3Options
        {
            RifInterval = TimeSpan.FromSeconds(300),
            PositiveDebounce = TimeSpan.FromSeconds(5),
        };
        using var scheduler = NewScheduler(clock, out var intents, opts);   // N1, N2

        // No dirty state at all. Nothing fires before the interval.
        clock.Advance(TimeSpan.FromSeconds(299));
        scheduler.Tick();
        intents.Should().BeEmpty("the periodic refresh has not yet reached its interval");

        // Crossing the 300 s interval fires a Periodic fan-out to every neighbour.
        clock.Advance(TimeSpan.FromSeconds(1));   // t = 300 s
        scheduler.Tick();
        intents.Should().HaveCount(2, "the periodic full RIF fans out to every neighbour on the interval");
        intents.Select(i => i.Neighbour).Should().BeEquivalentTo(new[] { N1, N2 });
        intents.Should().OnlyContain(i => i.Reason == Inp3AdvertiseReason.Periodic);

        // And again one interval later - it re-anchors each time.
        intents.Clear();
        clock.Advance(TimeSpan.FromSeconds(300));
        scheduler.Tick();
        intents.Should().HaveCount(2, "the periodic refresh re-fires every interval");
        intents.Should().OnlyContain(i => i.Reason == Inp3AdvertiseReason.Periodic);
    }

    [Fact]
    public void Periodic_subsumes_a_pending_positive_batch_and_resets_the_debounce()
    {
        var clock = new FakeTimeProvider();
        var opts = new NetRomInp3Options
        {
            RifInterval = TimeSpan.FromSeconds(10),
            PositiveDebounce = TimeSpan.FromSeconds(5),
        };
        using var scheduler = NewScheduler(clock, out var intents, opts);   // N1, N2

        // Mark a positive at t=6 s (after the first debounce boundary would have been,
        // had it been marked at t=0) - but here we mark it freshly so it would drain at
        // t=11 s. The periodic at t=10 s pre-empts it as a single Periodic fan-out.
        clock.Advance(TimeSpan.FromSeconds(6));
        scheduler.MarkDirty(DestA, Inp3UpdateClass.Positive);

        clock.Advance(TimeSpan.FromSeconds(4));   // t = 10 s == RifInterval
        scheduler.Tick();

        intents.Should().HaveCount(2, "the periodic emit fans out once per neighbour");
        intents.Should().OnlyContain(i => i.Reason == Inp3AdvertiseReason.Periodic,
            "a periodic emit subsumes the pending positive batch (full RIF) — it is not a second Triggered fan-out");

        // The pending positive was cleared by the periodic; it does NOT re-drain later.
        intents.Clear();
        clock.Advance(TimeSpan.FromSeconds(5));   // would have been the old debounce boundary
        scheduler.Tick();
        intents.Should().BeEmpty("the periodic emit cleared the pending positive and reset the debounce");
    }

    [Fact]
    public void Negative_preempts_a_pending_positive_batch()
    {
        var clock = new FakeTimeProvider();
        using var scheduler = NewScheduler(clock, out var intents);   // N1, N2

        // A positive is sitting in the debounce window...
        scheduler.MarkDirty(DestA, Inp3UpdateClass.Positive);
        clock.Advance(TimeSpan.FromSeconds(2));   // 2 s into the 5 s window

        // ...then a NEGATIVE arrives for a different destination. The next tick fans
        // out IMMEDIATELY (no waiting out the positive's debounce) as Triggered, and
        // clears BOTH the negative and the still-pending positive (full RIF subsumes).
        scheduler.MarkWithdrawn(DestB);
        scheduler.Tick();

        intents.Should().HaveCount(2, "the NEGATIVE pre-empts and fans out immediately to every neighbour");
        intents.Should().OnlyContain(i => i.Reason == Inp3AdvertiseReason.Triggered);

        // The previously-pending positive was subsumed - nothing left to drain at the
        // old debounce boundary.
        intents.Clear();
        clock.Advance(TimeSpan.FromSeconds(10));   // well past the old positive's 5 s boundary
        scheduler.Tick();
        intents.Should().BeEmpty("the NEGATIVE fan-out subsumed the pending positive batch (full RIF)");
    }

    [Fact]
    public void Negative_upgrade_within_window_is_immediate_and_does_not_downgrade()
    {
        var clock = new FakeTimeProvider();
        using var scheduler = NewScheduler(clock, out var intents);   // N1, N2

        // POSITIVE then upgraded to NEGATIVE for the SAME destination → NEGATIVE wins.
        scheduler.MarkDirty(DestA, Inp3UpdateClass.Positive);
        scheduler.MarkDirty(DestA, Inp3UpdateClass.Negative);   // upgrade

        scheduler.Status.NegativeDirty.Should().Be(1, "POSITIVE→NEGATIVE upgrades the class");
        scheduler.Status.PositiveDirty.Should().Be(0);

        scheduler.Tick();
        intents.Should().HaveCount(2, "the upgraded-to-NEGATIVE destination fans out immediately");
        intents.Should().OnlyContain(i => i.Reason == Inp3AdvertiseReason.Triggered);

        // The reverse: NEGATIVE then POSITIVE for the same dest must NOT downgrade.
        intents.Clear();
        scheduler.MarkDirty(DestB, Inp3UpdateClass.Negative);
        scheduler.MarkDirty(DestB, Inp3UpdateClass.Positive);   // must NOT downgrade
        scheduler.Status.NegativeDirty.Should().Be(1, "NEGATIVE→POSITIVE must not downgrade — a loss cannot be demoted to a batched positive");
        scheduler.Tick();
        intents.Should().HaveCount(2, "the still-NEGATIVE destination fans out immediately, not after a debounce");
    }

    [Fact]
    public void No_neighbours_means_no_intents_even_when_dirty()
    {
        var clock = new FakeTimeProvider();
        using var scheduler = NewScheduler(clock, out var intents, neighbours: Array.Empty<Callsign>());

        scheduler.MarkWithdrawn(DestA);
        scheduler.Tick();
        intents.Should().BeEmpty("with no target neighbours there is no one to advertise to");

        // Adding neighbours later does not resurrect the already-cleared dirty flag -
        // the NEGATIVE was consumed by the (empty) fan-out on the previous tick.
        scheduler.SetTargetNeighbours(new[] { N1 });
        scheduler.Tick();
        intents.Should().BeEmpty("the NEGATIVE dirty was cleared by the prior (empty) fan-out");
    }

    [Fact]
    public void Duplicate_neighbours_are_de_duplicated()
    {
        var clock = new FakeTimeProvider();
        using var scheduler = NewScheduler(clock, out var intents, neighbours: new[] { N1, N1, N2 });

        scheduler.MarkWithdrawn(DestA);
        scheduler.Tick();

        intents.Select(i => i.Neighbour).Should().BeEquivalentTo(new[] { N1, N2 },
            "a duplicate in the host neighbour set must not double-advertise to one neighbour");
    }

    [Fact]
    public void First_tick_does_not_fire_a_periodic_immediately()
    {
        var clock = new FakeTimeProvider();
        using var scheduler = NewScheduler(clock, out var intents);

        // A brand-new scheduler ticked at t=0 must NOT fire a periodic - it waits one
        // full interval (the periodic anchor is set to "now" on the first tick).
        scheduler.Tick();
        intents.Should().BeEmpty("the periodic refresh waits one full interval after the first tick");
    }
}
