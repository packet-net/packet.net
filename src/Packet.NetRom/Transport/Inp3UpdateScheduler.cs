using Packet.Core;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Transport;

/// <summary>
/// The host-free INP3 <em>triggered-update timing</em> state machine (slice I-4,
/// design §3): it answers <b>"when do we emit a RIF, and toward whom?"</b> - never
/// <em>what</em> the RIF contains (that is <c>NetRomRoutingTable.BuildRif</c>, the
/// content half). It consumes per-destination <em>dirty signals</em> (the table /
/// ingestion path tells it a destination changed, and how -
/// <see cref="MarkDirty"/> / <see cref="MarkWithdrawn"/>), consumes a
/// <see cref="System.TimeProvider"/>-driven <see cref="Tick"/>, and emits
/// <see cref="Inp3AdvertiseIntent">advertise intents</see> ("advertise to neighbour
/// X now") through the <see cref="Advertise"/> sink. The host turns each intent into
/// <c>table.BuildRif(myCall, X, preferInp3Routes)</c> + a send over X's interlink.
/// </summary>
/// <remarks>
/// <para>
/// <b>Host-free + intent-emitting.</b> Like <see cref="Inp3Engine"/> and
/// <see cref="CircuitManager"/>, the scheduler owns no I/O, no routing table, and no
/// AX.25 session - it speaks only <see cref="Callsign"/> in and
/// <see cref="Inp3AdvertiseIntent"/> out. It is a pure function of (dirty signals,
/// clock) → intents. The split keeps each piece pure: <c>BuildRif</c> is a pure read
/// of table state; the scheduler is pure timing; the host is the only stateful glue
/// (design §3.1).
/// </para>
/// <para>
/// <b>Monotonic clock.</b> Like <see cref="Inp3Engine"/>, it times the debounce and
/// the periodic interval off the injected provider's <em>monotonic</em> source
/// (<see cref="TimeProvider.GetTimestamp"/> / <see cref="TimeProvider.GetElapsedTime(long)"/>),
/// never wall-clock - an NTP / DST step can never fire or suppress a debounce
/// (design §3.1). Deterministic under <c>FakeTimeProvider</c>: advance the clock,
/// call <see cref="Tick"/>, assert the intents drained.
/// </para>
/// <para>
/// <b>Per-destination dirty, per-neighbour fan-out.</b> Dirty state is tracked per
/// <em>destination</em> (a single change must reach every INP3-capable neighbour,
/// each with its own poison-reversed RIF at emit time - design §3.2); but the
/// scheduler only tracks <em>which destinations are dirty and at what priority</em>
/// to decide <em>whether / when / at what priority</em> to fan out - it never builds
/// a partial RIF. Every fan-out emits one intent per target neighbour, and the host
/// rebuilds the complete (full) poison-reversed RIF for each (design §3.3, "full
/// RIF"): a NEGATIVE fan-out therefore naturally carries the changed destination's
/// new/withdrawn state and subsumes any pending POSITIVE batch.
/// </para>
/// <para>
/// <b>Totality.</b> Marking a destination dirty never throws; <see cref="Tick"/>
/// with no neighbours, no dirty state, and no <see cref="Advertise"/> sink is a
/// no-op. The recently-withdrawn set is <em>not</em> held here - it is table state
/// (design AMBIGUITY-I4-5: <c>BuildRif</c> consumes-and-clears it); a withdrawal here
/// only escalates the destination to NEGATIVE so the fan-out is immediate.
/// </para>
/// </remarks>
public sealed class Inp3UpdateScheduler : IDisposable
{
    /// <summary>Sentinel for <see cref="earliestPositiveMarkMs"/>: "no POSITIVE mark is
    /// pending", distinct from the monotonic clock's legitimate <c>0</c> at construction
    /// (a positive marked at <c>t=0</c> must not read as "none pending").</summary>
    private const long NeverMarked = long.MinValue;

    private readonly NetRomInp3Options options;
    private readonly TimeProvider time;
    private readonly long startTimestamp;
    private readonly long rifIntervalMs;
    private readonly long positiveDebounceMs;
    private readonly object gate = new();

    /// <summary>Per-destination dirty class. A destination is in at most one class at
    /// a time (design §3.2). Absent ⇒ clean.</summary>
    private readonly Dictionary<Callsign, Inp3UpdateClass> dirty = new();

    /// <summary>The INP3-capable neighbour set to fan out to - host-supplied
    /// (<see cref="SetTargetNeighbours"/>); the scheduler never discovers neighbours
    /// (host-free, design §3.2/§3.6). Stored ordered-by-callsign so a fan-out emits
    /// intents in a deterministic order.</summary>
    private Callsign[] targetNeighbours = Array.Empty<Callsign>();

    /// <summary>Monotonic ms of the <em>earliest still-pending</em> POSITIVE mark -
    /// the debounce anchor (design §3.3 rule 2: a steady positive drip drains within
    /// one <see cref="positiveDebounceMs"/> of the first, not perpetually deferred).
    /// <see cref="NeverMarked"/> when no POSITIVE is pending.</summary>
    private long earliestPositiveMarkMs = NeverMarked;

    /// <summary>Monotonic ms of the last periodic fan-out (design §3.3 rule 3),
    /// anchored at construction (monotonic <c>0</c>) so the first baseline refresh
    /// fires exactly one <see cref="rifIntervalMs"/> after the scheduler is built -
    /// timing depends only on the injected clock, not on when ticking begins.</summary>
    private long lastPeriodicMs;

    private readonly ITimer? tickTimer;
    private int disposed;

    /// <summary>
    /// The intent sink the host wires: for each fan-out the scheduler invokes this
    /// once per target neighbour with "(re)build <c>BuildRif(myCall, neighbour,
    /// prefer)</c> and send it over <paramref>neighbour</paramref>'s interlink now".
    /// The intent carries the <see cref="Inp3AdvertiseReason"/> for observability.
    /// Invoked <em>after</em> the internal lock is released (the
    /// <see cref="Inp3Engine.Tick"/> discipline - a re-entrant host handler that
    /// marks dirty / re-ticks cannot deadlock).
    /// </summary>
    public Action<Inp3AdvertiseIntent>? Advertise { get; set; }

    /// <summary>
    /// Construct the scheduler. Pass <paramref name="tickInterval"/> to self-drive
    /// <see cref="Tick"/> off the time provider (production); pass <c>null</c> to
    /// drive <see cref="Tick"/> manually after advancing a <c>FakeTimeProvider</c>
    /// (the deterministic-test path). Identical to <see cref="Inp3Engine"/>'s
    /// <c>tickInterval</c> semantics.
    /// </summary>
    /// <param name="options">Timing knobs - <see cref="NetRomInp3Options.RifInterval"/>
    /// (periodic cadence) and <see cref="NetRomInp3Options.PositiveDebounce"/> (the
    /// positive-update coalescing window). Defaults to
    /// <see cref="NetRomInp3Options.Default"/>; validated.</param>
    /// <param name="time">Injected clock (a monotonic source is used). Defaults to
    /// <see cref="TimeProvider.System"/>.</param>
    /// <param name="tickInterval">Self-drive <see cref="Tick"/> off the clock, or
    /// <c>null</c> for manual ticking. Choose a value &lt;= <see cref="NetRomInp3Options.PositiveDebounce"/>
    /// in production so the debounce resolves promptly.</param>
    public Inp3UpdateScheduler(
        NetRomInp3Options? options = null,
        TimeProvider? time = null,
        TimeSpan? tickInterval = null)
    {
        this.options = options ?? NetRomInp3Options.Default;
        this.options.Validate();
        this.time = time ?? TimeProvider.System;
        this.startTimestamp = this.time.GetTimestamp();
        this.rifIntervalMs = (long)this.options.RifInterval.TotalMilliseconds;
        this.positiveDebounceMs = (long)this.options.PositiveDebounce.TotalMilliseconds;

        if (tickInterval is { } interval && interval > TimeSpan.Zero)
        {
            tickTimer = this.time.CreateTimer(_ => Tick(), state: null, dueTime: interval, period: interval);
        }
    }

    /// <summary>
    /// Set the INP3-capable neighbour set to fan out to. Host-supplied (e.g. from
    /// <see cref="Inp3Engine.Neighbours"/> filtered to <c>Inp3Capable</c>); the
    /// scheduler never discovers neighbours. Replaces the previous set wholesale.
    /// Takes a defensive, callsign-ordered copy so a later mutation of the caller's
    /// collection cannot change which neighbours a fan-out targets and the order is
    /// deterministic. Removing a neighbour here simply stops it receiving future
    /// fan-outs; it does not clear any dirty state (the next fan-out reaches whatever
    /// set is current at that <see cref="Tick"/>).
    /// </summary>
    public void SetTargetNeighbours(IReadOnlyCollection<Callsign> capableNeighbours)
    {
        ArgumentNullException.ThrowIfNull(capableNeighbours);
        // Distinct + ordered: a duplicate in the host set must not double-advertise to
        // one neighbour, and a stable order keeps a fan-out's intents deterministic
        // (the NetRomRoutingTable.Snapshot / Inp3Engine.Neighbours ordering discipline).
        var snapshot = capableNeighbours
            .Distinct()
            .OrderBy(c => c.ToString(), StringComparer.Ordinal)
            .ToArray();
        lock (gate)
        {
            targetNeighbours = snapshot;
        }
    }

    /// <summary>
    /// Mark a destination dirty with a change class (design §3.2). The table /
    /// ingestion path computes the class: NEGATIVE for a selected route worsened by
    /// &gt;= <see cref="NetRomInp3Options.WorsenThresholdMs"/>; POSITIVE for a
    /// new / improved / faster-next-hop route or a sub-threshold worsening. The class
    /// is <b>monotonic within the debounce window</b>: a POSITIVE destination
    /// re-marked NEGATIVE is <em>upgraded</em> to NEGATIVE (a loss must not be held
    /// back by a coincident positive); a NEGATIVE destination re-marked POSITIVE is
    /// <b>not</b> downgraded. Never throws; the actual fan-out happens on the next
    /// <see cref="Tick"/> (NEGATIVE immediately, POSITIVE after the debounce).
    /// </summary>
    /// <param name="destination">The destination whose selected INP3 route changed.</param>
    /// <param name="cls">The change class (see <see cref="Inp3UpdateClass"/>).</param>
    public void MarkDirty(Callsign destination, Inp3UpdateClass cls)
    {
        long now = NowMs();
        lock (gate)
        {
            MarkLocked(destination, cls, now);
        }
    }

    /// <summary>
    /// Mark a destination's selected INP3 route <em>withdrawn</em> (fully lost - no
    /// selected INP3 route remains). A withdrawal is <b>always NEGATIVE</b> regardless
    /// of any threshold (design §3.2: it is a removal, not a worsening) so it fans out
    /// on the next <see cref="Tick"/> immediately. The explicit one-shot horizon
    /// withdrawal RIP itself is emitted by <c>BuildRif</c> from the <em>table's</em>
    /// recently-withdrawn set (design AMBIGUITY-I4-5) - the scheduler only escalates
    /// the timing here; it does not hold the withdrawn set.
    /// </summary>
    public void MarkWithdrawn(Callsign destination)
    {
        long now = NowMs();
        lock (gate)
        {
            MarkLocked(destination, Inp3UpdateClass.Negative, now);
        }
    }

    /// <summary>
    /// Advance the clock-driven state machine and fan out any updates now due. On
    /// each tick (design §3.3), in precedence:
    /// <list type="number">
    /// <item><b>Any NEGATIVE dirty → immediate, prioritised.</b> Emit an
    /// <see cref="Inp3AdvertiseReason.Triggered"/> intent for <em>every</em> target
    /// neighbour now and clear <b>all</b> dirty (the full poison-reversed RIF the host
    /// rebuilds subsumes pending positives too). No debounce.</item>
    /// <item><b>Else POSITIVE dirty and debounce elapsed → batched.</b> If any
    /// destination is POSITIVE and <c>now - earliestPositiveMark &gt;= PositiveDebounce</c>,
    /// emit a <see cref="Inp3AdvertiseReason.Triggered"/> intent for every neighbour
    /// and clear the POSITIVE dirty. The debounce coalesces a burst of positives into
    /// one fan-out.</item>
    /// <item><b>Independently, periodic interval elapsed → full RIF regardless.</b> If
    /// <c>now - lastPeriodicEmit &gt;= RifInterval</c>, emit a
    /// <see cref="Inp3AdvertiseReason.Periodic"/> intent for every neighbour, stamp
    /// the periodic anchor, clear all dirty, and reset the debounce.</item>
    /// </list>
    /// Intents are collected under the lock and invoked <em>after</em> it is released
    /// (the <see cref="Inp3Engine.Tick"/> snapshot-then-act discipline - a re-entrant
    /// host handler cannot deadlock). Drive it from the internal timer (production) or
    /// manually after advancing a <c>FakeTimeProvider</c> (tests).
    /// </summary>
    public void Tick()
    {
        long now = NowMs();
        List<Inp3AdvertiseIntent>? toRaise = null;

        lock (gate)
        {
            // The periodic anchor was seeded to 0 (monotonic construction time), so the
            // first baseline refresh is due exactly one RifInterval after construction -
            // timing depends only on the injected clock, not on when ticking began.
            bool periodicDue = now - lastPeriodicMs >= rifIntervalMs;

            bool negativeDue = false;
            bool positiveDue = false;
            if (!periodicDue)
            {
                foreach (var cls in dirty.Values)
                {
                    if (cls == Inp3UpdateClass.Negative)
                    {
                        negativeDue = true;
                        break;   // NEGATIVE dominates - no need to scan further.
                    }
                }
                if (!negativeDue
                    && earliestPositiveMarkMs != NeverMarked
                    && now - earliestPositiveMarkMs >= positiveDebounceMs)
                {
                    positiveDue = true;
                }
            }

            // A periodic emit subsumes everything (full RIF) and takes the Periodic
            // reason; otherwise a NEGATIVE (immediate) or a debounced POSITIVE fans out
            // as Triggered. At most one fan-out per tick - the rebuilt full RIF carries
            // all current state, so there is never a reason to fan out twice.
            Inp3AdvertiseReason? reason =
                periodicDue ? Inp3AdvertiseReason.Periodic
                : (negativeDue || positiveDue) ? Inp3AdvertiseReason.Triggered
                : null;

            if (reason is { } r)
            {
                foreach (var neighbour in targetNeighbours)
                {
                    (toRaise ??= new()).Add(new Inp3AdvertiseIntent(neighbour, r));
                }

                // Clearing semantics (design §3.3):
                //  - Periodic and NEGATIVE both clear ALL dirty (the full RIF subsumes
                //    every pending change) and reset the debounce anchor.
                //  - A pure debounced-POSITIVE fan-out clears only POSITIVE dirty (there
                //    are no NEGATIVEs by construction - rule 1 would have won) which, in
                //    practice, is also all dirty; either way the debounce anchor resets.
                dirty.Clear();
                earliestPositiveMarkMs = NeverMarked;

                if (periodicDue)
                {
                    lastPeriodicMs = now;
                }
            }
        }

        if (toRaise is not null)
        {
            var sink = Advertise;
            if (sink is not null)
            {
                foreach (var intent in toRaise)
                {
                    sink(intent);
                }
            }
        }
    }

    /// <summary>
    /// A point-in-time snapshot of pending dirty state, for surfacing / tests: how
    /// many destinations are dirty NEGATIVE vs POSITIVE, and the current neighbour
    /// fan-out count. A pure read.
    /// </summary>
    public Inp3SchedulerStatus Status
    {
        get
        {
            lock (gate)
            {
                int negative = 0, positive = 0;
                foreach (var cls in dirty.Values)
                {
                    if (cls == Inp3UpdateClass.Negative)
                    {
                        negative++;
                    }
                    else
                    {
                        positive++;
                    }
                }
                return new Inp3SchedulerStatus(negative, positive, targetNeighbours.Length);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        tickTimer?.Dispose();
        lock (gate)
        {
            dirty.Clear();
            targetNeighbours = Array.Empty<Callsign>();
        }
    }

    // ─── Internals ──────────────────────────────────────────────────────

    /// <summary>Monotonic milliseconds since construction (not wall-clock - design
    /// §3.1, the <see cref="Inp3Engine"/> clock pattern).</summary>
    private long NowMs() => (long)time.GetElapsedTime(startTimestamp).TotalMilliseconds;

    /// <summary>
    /// Apply a dirty mark under the lock with the monotonic-within-window rule:
    /// POSITIVE→NEGATIVE upgrades; NEGATIVE→POSITIVE does not downgrade; a fresh
    /// POSITIVE anchors the debounce window if none is pending. Must hold <see cref="gate"/>.
    /// </summary>
    private void MarkLocked(Callsign destination, Inp3UpdateClass cls, long now)
    {
        if (dirty.TryGetValue(destination, out var existing))
        {
            // Upgrade-only: NEGATIVE dominates, so only POSITIVE→NEGATIVE changes the
            // stored class. NEGATIVE→POSITIVE (and same-class) leave it untouched.
            if (existing == Inp3UpdateClass.Positive && cls == Inp3UpdateClass.Negative)
            {
                dirty[destination] = Inp3UpdateClass.Negative;
            }
            return;
        }

        dirty[destination] = cls;
        if (cls == Inp3UpdateClass.Positive && earliestPositiveMarkMs == NeverMarked)
        {
            // Anchor the debounce on the EARLIEST still-pending positive so a steady
            // drip drains within one window of the first mark, not perpetually deferred.
            earliestPositiveMarkMs = now;
        }
    }
}

/// <summary>
/// The change class of a destination's selected INP3 route, set by whoever marks it
/// dirty (the table / ingestion path - design §3.2). NEGATIVE is immediate +
/// prioritised; POSITIVE is debounced + batched.
/// </summary>
public enum Inp3UpdateClass
{
    /// <summary>A new / improved / faster-next-hop route, or a sub-threshold
    /// worsening (routine SNTT jitter). Batched behind
    /// <see cref="NetRomInp3Options.PositiveDebounce"/>.</summary>
    Positive,

    /// <summary>A route lost (withdrawal / <c>MarkNeighbourDown</c> / aged out) or a
    /// selected target time worsened by &gt;= <see cref="NetRomInp3Options.WorsenThresholdMs"/>.
    /// Fans out immediately on the next <see cref="Inp3UpdateScheduler.Tick"/>, ahead
    /// of any pending positive batch.</summary>
    Negative,
}

/// <summary>
/// Why a fan-out fired, carried on each <see cref="Inp3AdvertiseIntent"/> for
/// observability (design §3.6).
/// </summary>
public enum Inp3AdvertiseReason
{
    /// <summary>A dirty-driven fan-out - a NEGATIVE change (immediate) or a debounced
    /// batch of POSITIVE changes.</summary>
    Triggered,

    /// <summary>The baseline periodic full-RIF refresh on
    /// <see cref="NetRomInp3Options.RifInterval"/>, regardless of dirty state.</summary>
    Periodic,
}

/// <summary>
/// One "advertise to neighbour X now" intent the scheduler emits through
/// <see cref="Inp3UpdateScheduler.Advertise"/>. The host turns it into
/// <c>table.BuildRif(myCall, <see cref="Neighbour"/>, preferInp3Routes)</c> (the full,
/// poison-reversed RIF) and a send over the neighbour's interlink session.
/// </summary>
/// <param name="Neighbour">The INP3-capable neighbour to (re)advertise toward.</param>
/// <param name="Reason">Why this fan-out fired (triggered vs periodic).</param>
public readonly record struct Inp3AdvertiseIntent(Callsign Neighbour, Inp3AdvertiseReason Reason);

/// <summary>
/// An immutable snapshot of the scheduler's pending dirty state, for surfacing /
/// tests (the <see cref="Inp3UpdateScheduler.Status"/> projection).
/// </summary>
/// <param name="NegativeDirty">Destinations pending an immediate (NEGATIVE) fan-out.</param>
/// <param name="PositiveDirty">Destinations pending a debounced (POSITIVE) fan-out.</param>
/// <param name="TargetNeighbours">The current INP3-capable fan-out target count.</param>
public readonly record struct Inp3SchedulerStatus(int NegativeDirty, int PositiveDirty, int TargetNeighbours);
