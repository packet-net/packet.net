using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Transport;

/// <summary>
/// The host-free INP3 <em>link-timing</em> engine: it owns the per-neighbour INP3
/// state, probes each interlink neighbour with L3RTT datagrams on a cadence, times
/// the reflections (RTT ÷ 2 → the <see cref="Inp3Sntt">SNTT smoother</see>),
/// reflects a peer's probes back verbatim, learns INP3 capability from the
/// <c>$N</c> / <c>$IX</c> flags, and raises <see cref="NeighbourDown"/> when a
/// previously-capable neighbour stops reflecting for the reset window (default
/// 180 s). This is INP3 slice I-2 - link timing only; it produces the SNTT value
/// the route layer (I-3) consumes but does not itself touch the routing table
/// beyond signalling a down neighbour.
/// </summary>
/// <remarks>
/// <para>
/// <b>Host-free.</b> Like <see cref="CircuitManager"/>, the engine has no AX.25 /
/// node-host / routing-table dependency - it speaks only <see cref="Callsign"/> +
/// <see cref="Inp3L3RttFrame"/> / <see cref="NetRomPacket"/> in and out. The host
/// supplies a <see cref="SendL3Rtt"/> sink (wrap the frame in a PID-0xCF I-frame on
/// the neighbour's interlink session) and subscribes <see cref="NeighbourDown"/>
/// (wired to <c>NetRomRoutingTable.MarkNeighbourDown</c> + a DISC / re-establish).
/// </para>
/// <para>
/// <b>Monotonic clock.</b> Unlike <see cref="CircuitManager"/> (which uses
/// <c>GetUtcNow</c> for circuit timers), the INP3 engine is RTT-centric, so it uses
/// the injected <see cref="System.TimeProvider"/>'s <em>monotonic</em> source
/// (<see cref="TimeProvider.GetTimestamp"/> / <see cref="TimeProvider.GetElapsedTime(long)"/>)
/// converted to milliseconds - never wall-clock - so an NTP / DST step cannot
/// corrupt an RTT or fire / suppress the 180 s reset. This is the one intentional
/// divergence from the <see cref="CircuitManager"/> clock pattern (design §2.1,
/// AMBIGUITY-I2-5).
/// </para>
/// <para>
/// <b>Totality.</b> The engine never throws on any inbound frame (the I-1 totality
/// contract extends here): a negative / stale RTT, an unsolicited reflection, a
/// reflection from an unknown neighbour, or a non-L3RTT packet are all handled
/// without corrupting the metric.
/// </para>
/// </remarks>
public sealed class Inp3Engine : IDisposable
{
    /// <summary>Sentinel for <see cref="Inp3NeighbourState.LastL3RttSentMs"/> meaning
    /// "no probe ever sent" - distinct from the monotonic clock's legitimate <c>0</c>
    /// at engine start (a probe genuinely sent at <c>t=0</c> must not read as
    /// never-sent, or the cadence gate re-fires it every tick).</summary>
    private const long NeverProbed = long.MinValue;

    private Callsign localNode;
    private readonly NetRomInp3Options options;
    private readonly TimeProvider time;
    private readonly long startTimestamp;
    private readonly long cadenceMs;
    private readonly long resetWindowMs;
    private readonly object gate = new();

    private readonly Dictionary<Callsign, Inp3NeighbourState> neighbours = new();

    private readonly ITimer? tickTimer;
    private int disposed;

    /// <summary>
    /// The sink the host wires to ship an L3RTT datagram onto the interlink toward
    /// <paramref>neighbour</paramref>. The host wraps <c>frame.ToBytes()</c> in a
    /// PID-0xCF I-frame on that neighbour's interlink session. Must be set before
    /// any probe is due. (Mirrors <see cref="CircuitManager.SendPacket"/>.)
    /// </summary>
    public Action<Callsign, Inp3L3RttFrame>? SendL3Rtt { get; set; }

    /// <summary>
    /// Raised when a previously-INP3-capable neighbour has not reflected within the
    /// reset window (design §3). The host wires this to
    /// <c>NetRomRoutingTable.MarkNeighbourDown(neighbour)</c> and to DISC /
    /// re-establish the interlink. The engine has already reset (removed) that
    /// neighbour's INP3 state by the time this fires, and it is raised outside the
    /// internal lock so a re-entrant handler cannot deadlock. (Mirrors the
    /// <see cref="CircuitManager.IncomingCircuit"/> failover seam.)
    /// </summary>
    public event EventHandler<Inp3NeighbourDownEventArgs>? NeighbourDown;

    /// <summary>
    /// Construct the engine for a node. Pass <paramref name="tickInterval"/> to
    /// self-drive <see cref="Tick"/> off the time provider (production); pass
    /// <c>null</c> to drive <see cref="Tick"/> manually after advancing a
    /// <c>FakeTimeProvider</c> (the deterministic-test path). Identical to
    /// <see cref="CircuitManager"/>'s <c>tickInterval</c> semantics.
    /// </summary>
    /// <param name="localNode">Our own L3 callsign - the origin we stamp into probes
    /// and the <see cref="Inp3L3RttFrame.IsReflectionOf"/> self-test target.
    /// Settable later via <see cref="SetLocalNode"/>.</param>
    /// <param name="options">Cadence, reset window, SNTT gain, advertised
    /// capability. Defaults to <see cref="NetRomInp3Options.Default"/>; validated.</param>
    /// <param name="time">Injected clock (a monotonic source is used). Defaults to
    /// <see cref="TimeProvider.System"/>.</param>
    /// <param name="tickInterval">Self-drive <see cref="Tick"/> off the clock, or
    /// <c>null</c> for manual ticking.</param>
    public Inp3Engine(
        Callsign localNode,
        NetRomInp3Options? options = null,
        TimeProvider? time = null,
        TimeSpan? tickInterval = null)
    {
        this.localNode = localNode;
        this.options = options ?? NetRomInp3Options.Default;
        this.options.Validate();
        this.time = time ?? TimeProvider.System;
        this.startTimestamp = this.time.GetTimestamp();
        this.cadenceMs = (long)this.options.L3RttInterval.TotalMilliseconds;
        this.resetWindowMs = (long)this.options.L3RttResetWindow.TotalMilliseconds;

        if (tickInterval is { } interval && interval > TimeSpan.Zero)
        {
            tickTimer = this.time.CreateTimer(_ => Tick(), state: null, dueTime: interval, period: interval);
        }
    }

    /// <summary>
    /// Set the local node callsign stamped into the L3 origin of probes this engine
    /// builds, and the target of the reflection self-test. The node host calls this
    /// once the node identity is known (at first port attach). Affects probes built
    /// <em>after</em> the call. (Mirrors <see cref="CircuitManager.SetLocalNode"/>.)
    /// </summary>
    public void SetLocalNode(Callsign node)
    {
        lock (gate)
        {
            localNode = node;
        }
    }

    /// <summary>
    /// Register / refresh awareness of an interlink neighbour (e.g. when an
    /// interlink session is established, or a NODES neighbour is learned). Creates
    /// the per-neighbour state with a fresh reset window if new; a no-op refresh if
    /// already known. Probing then begins on the next due <see cref="Tick"/> (once
    /// the neighbour is known INP3-capable, or immediately if
    /// <see cref="NetRomInp3Options.ProbeUnknownCapability"/>).
    /// </summary>
    public void ObserveNeighbour(Callsign neighbour)
    {
        long now = NowMs();
        lock (gate)
        {
            EnsureNeighbour(neighbour, now);
        }
    }

    /// <summary>
    /// Drop a neighbour the host knows is gone (interlink torn down for non-INP3
    /// reasons). Removes its INP3 state; <see cref="NeighbourDown"/> is <em>not</em>
    /// raised (the host already knows). Idempotent - dropping an unknown neighbour
    /// is a no-op, so a re-entrant call from a <see cref="NeighbourDown"/> handler
    /// is safe.
    /// </summary>
    public void RemoveNeighbour(Callsign neighbour)
    {
        lock (gate)
        {
            neighbours.Remove(neighbour);
        }
    }

    /// <summary>
    /// Advance the engine by one tick: (a) for each neighbour due a probe
    /// (capability-permitted, not awaiting a reflection, and cadence elapsed since
    /// the last send) emit a <see cref="SendL3Rtt"/> and stamp the send; (b) for
    /// each neighbour silent past the reset window, reset it - raising
    /// <see cref="NeighbourDown"/> only if it was INP3-capable (the AMBIGUITY-I2-3
    /// guard: a never-capable vanilla neighbour is dropped from probing silently,
    /// never routing-torn-down). Sends and callbacks are invoked <em>after</em> the
    /// lock is released. Drive it from the internal timer (production) or manually
    /// after advancing a <c>FakeTimeProvider</c> (tests). Mirrors
    /// <see cref="CircuitManager.Tick"/>.
    /// </summary>
    public void Tick()
    {
        long now = NowMs();

        // Collected under the lock, invoked after release (the snapshot-then-act
        // pattern of CircuitManager.Tick - a re-entrant host handler cannot deadlock).
        List<(Callsign Neighbour, Inp3L3RttFrame Frame)>? toSend = null;
        List<Inp3NeighbourDownEventArgs>? toRaise = null;

        lock (gate)
        {
            // Snapshot so we can mutate the dictionary (reset removes entries) while
            // iterating.
            foreach (var (call, n) in neighbours.ToArray())
            {
                // Reset wins over probe for the same neighbour in the same tick:
                // evaluate it first and skip the probe branch if it fires.
                if (now - n.LastReflectionMs > resetWindowMs)
                {
                    neighbours.Remove(call);
                    if (n.Inp3Capable)
                    {
                        (toRaise ??= new()).Add(new Inp3NeighbourDownEventArgs(call, now - n.LastReflectionMs));
                    }
                    // else: a never-capable neighbour that never reflected our
                    // optimistic probes - drop it silently, NO NeighbourDown (the
                    // guard: a vanilla peer is reachable by NODES, it just doesn't
                    // speak L3RTT - we must not feed its silence into routing).
                    continue;
                }

                bool mayProbe = n.Inp3Capable || options.ProbeUnknownCapability;
                bool cadenceElapsed = n.LastL3RttSentMs == NeverProbed || now - n.LastL3RttSentMs >= cadenceMs;
                if (mayProbe && !n.AwaitingReflection && cadenceElapsed)
                {
                    var frame = Inp3L3RttFrame.Build(
                        localNode,
                        ipAccept: options.AdvertiseIpAccept,
                        capabilityTextWidth: options.CapabilityTextWidth);
                    n.LastL3RttSentMs = now;
                    n.AwaitingReflection = true;
                    (toSend ??= new()).Add((call, frame));
                }
            }
        }

        if (toSend is not null)
        {
            foreach (var (neighbour, frame) in toSend)
            {
                SendL3Rtt?.Invoke(neighbour, frame);
            }
        }
        if (toRaise is not null)
        {
            foreach (var args in toRaise)
            {
                NeighbourDown?.Invoke(this, args);
            }
        }
    }

    /// <summary>
    /// Feed an inbound L3RTT frame received from <paramref name="neighbour"/> on the
    /// interlink (the caller already recognised it as L3RTT). Two cases:
    /// <list type="bullet">
    /// <item>If it is a reflection of <em>our</em> probe
    /// (<see cref="Inp3L3RttFrame.IsReflectionOf"/> with our local node, and we were
    /// awaiting one from this neighbour): compute RTT, feed RTT/2 to the SNTT
    /// smoother, stamp the reflection, clear the outstanding-probe flag, and learn
    /// the (echoed) capability.</item>
    /// <item>Otherwise it is a peer's probe to us: reflect it verbatim via
    /// <see cref="SendL3Rtt"/>, and learn the peer's <c>$N</c> / <c>$IX</c>
    /// capability from it.</item>
    /// </list>
    /// Never throws. (Mirrors <see cref="CircuitManager.OnPacket(NetRomPacket)"/>.)
    /// </summary>
    public void OnL3Rtt(Callsign neighbour, Inp3L3RttFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        long now = NowMs();

        Inp3L3RttFrame? reflectFrame = null;
        lock (gate)
        {
            var n = EnsureNeighbour(neighbour, now);

            // Learn capability from whatever flags the frame carries (both
            // directions advertise capability; design §2.3).
            if (frame.Inp3Capable)
            {
                n.Inp3Capable = true;
            }
            if (frame.IpAccept is int ip)
            {
                n.IpAccept = (byte)ip;
            }

            if (frame.IsReflectionOf(localNode) && n.AwaitingReflection)
            {
                // Our probe came back. The reflection itself proves liveness.
                long rtt = now - n.LastL3RttSentMs;
                n.AwaitingReflection = false;
                n.LastReflectionMs = now;

                // A negative / stale RTT (clock went backwards, or a 0-stamp edge)
                // updates liveness but contributes NO sample - never feed the filter
                // a negative value (design §2.4). A non-negative sample (= RTT/2) is
                // clamped to the INP3 horizon and seeded / smoothed inside Smooth
                // (design §0.2-0.3).
                if (rtt >= 0)
                {
                    // Clamp on the long before narrowing: a pathological RTT whose
                    // RTT/2 exceeds uint range would otherwise wrap mod 2^32 and
                    // present as a small sample (under-reporting the link). Clamp to
                    // the horizon first so the narrowing is always lossless.
                    uint sample = (uint)Math.Min(rtt / 2, (long)Inp3Sntt.SampleMaxMs);
                    n.SnttMs = Inp3Sntt.Smooth(n.SnttMs, sample, options.SnttGainShift);
                }
            }
            else
            {
                // A peer's probe to us (origin != us, or we weren't awaiting a
                // reflection - an unsolicited / duplicate reflection is treated as a
                // peer probe, never as a metric sample). Reflect it verbatim
                // (i1-wire-spec §1.4 locked byte-for-byte echo).
                reflectFrame = frame;
            }
        }

        if (reflectFrame is not null)
        {
            SendL3Rtt?.Invoke(neighbour, reflectFrame);
        }
    }

    /// <summary>
    /// Feed a raw <see cref="NetRomPacket"/> received from <paramref name="neighbour"/>:
    /// if it is an L3RTT frame the engine recognises and processes it (as
    /// <see cref="OnL3Rtt(Callsign, Inp3L3RttFrame)"/>) and returns <c>true</c>;
    /// otherwise it returns <c>false</c> with no state change (the packet is
    /// something else the caller should route elsewhere). Never throws.
    /// </summary>
    public bool OnL3Rtt(Callsign neighbour, NetRomPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!Inp3L3RttFrame.TryFrom(packet, out var frame))
        {
            return false;
        }
        OnL3Rtt(neighbour, frame);
        return true;
    }

    /// <summary>
    /// Immutable snapshot of per-neighbour timing state, for the console / MCP /
    /// tests. Stable ordering by callsign (the <c>NetRomRoutingTable.Snapshot</c>
    /// discipline) so the surfaced output is deterministic.
    /// </summary>
    public IReadOnlyList<Inp3NeighbourTiming> Neighbours
    {
        get
        {
            long now = NowMs();
            lock (gate)
            {
                return neighbours
                    .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                    .Select(kv => new Inp3NeighbourTiming(
                        kv.Key,
                        kv.Value.SnttInitialised ? kv.Value.SnttMs : null,
                        kv.Value.Inp3Capable,
                        kv.Value.IpAccept,
                        now - kv.Value.LastReflectionMs,
                        kv.Value.AwaitingReflection))
                    .ToList();
            }
        }
    }

    /// <summary>
    /// The smoothed neighbour transport time (ms) the route layer (I-3) reads for a
    /// neighbour; <c>null</c> if the neighbour is unknown or has no measurement yet
    /// (still <see cref="Inp3Sntt.SnttUnset"/>). A pure read.
    /// </summary>
    public uint? SnttMs(Callsign neighbour)
    {
        lock (gate)
        {
            if (neighbours.TryGetValue(neighbour, out var n) && n.SnttInitialised)
            {
                return n.SnttMs;
            }
            return null;
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
            neighbours.Clear();
        }
    }

    // ─── Internals ──────────────────────────────────────────────────────

    /// <summary>Monotonic milliseconds since construction (not wall-clock - design §2.1).</summary>
    private long NowMs() => (long)time.GetElapsedTime(startTimestamp).TotalMilliseconds;

    /// <summary>
    /// Get-or-create a neighbour's state. A fresh entry seeds
    /// <c>LastReflectionMs = now</c> (a full reset window before it can be torn down)
    /// and <c>SnttMs = SnttUnset</c> (no measurement). Must be called under the lock.
    /// </summary>
    private Inp3NeighbourState EnsureNeighbour(Callsign neighbour, long now)
    {
        if (!neighbours.TryGetValue(neighbour, out var n))
        {
            n = new Inp3NeighbourState
            {
                SnttMs = Inp3Sntt.SnttUnset,
                LastL3RttSentMs = NeverProbed,
                LastReflectionMs = now,
                Inp3Capable = false,
                IpAccept = null,
                AwaitingReflection = false,
            };
            neighbours[neighbour] = n;
        }
        return n;
    }

    /// <summary>
    /// The per-neighbour INP3 link-timing state (design §1 / plan §5.1). A mutable
    /// internal class, like <c>NetRomRoutingTable.NeighbourState</c>; all timestamps
    /// are monotonic ms from the injected clock.
    /// </summary>
    private sealed class Inp3NeighbourState
    {
        /// <summary>Smoothed neighbour transport time (the link metric);
        /// <see cref="Inp3Sntt.SnttUnset"/> until the first reflection.</summary>
        public uint SnttMs;

        /// <summary>Monotonic ms when we last SENT a probe; <see cref="NeverProbed"/> = never probed.</summary>
        public long LastL3RttSentMs;

        /// <summary>Monotonic ms when this neighbour last reflected our probe
        /// (drives the reset timer); seeded to "now" at add-time.</summary>
        public long LastReflectionMs;

        /// <summary>Learned from the peer's <c>$N</c> flag.</summary>
        public bool Inp3Capable;

        /// <summary>From <c>$IX</c>, if advertised; else null.</summary>
        public byte? IpAccept;

        /// <summary>A probe is outstanding (sent, not yet reflected). At most one in
        /// flight per neighbour - bounds state and makes "is this reflection ours?"
        /// unambiguous.</summary>
        public bool AwaitingReflection;

        /// <summary>Whether a valid SNTT measurement exists yet.</summary>
        public bool SnttInitialised => SnttMs != Inp3Sntt.SnttUnset;
    }
}

/// <summary>
/// Carries an INP3 link-down signal to a <see cref="Inp3Engine.NeighbourDown"/>
/// handler: a previously-INP3-capable neighbour went silent past the reset window.
/// The handler wires it to <c>NetRomRoutingTable.MarkNeighbourDown</c> + a DISC /
/// re-establish of the interlink.
/// </summary>
public sealed class Inp3NeighbourDownEventArgs : EventArgs
{
    internal Inp3NeighbourDownEventArgs(Callsign neighbour, long silentForMs)
    {
        Neighbour = neighbour;
        SilentForMs = silentForMs;
    }

    /// <summary>The neighbour to <c>MarkNeighbourDown</c>.</summary>
    public Callsign Neighbour { get; }

    /// <summary>How long since its last reflection (≥ the reset window), in ms.</summary>
    public long SilentForMs { get; }
}

/// <summary>
/// An immutable snapshot of one neighbour's INP3 link-timing state, for surfacing /
/// tests (the <c>Inp3Engine.Neighbours</c> projection).
/// </summary>
/// <param name="Neighbour">The neighbour callsign.</param>
/// <param name="SnttMs">The smoothed neighbour transport time (ms), or <c>null</c>
/// if no measurement yet.</param>
/// <param name="Inp3Capable">Whether the neighbour has advertised INP3 capability.</param>
/// <param name="IpAccept">The IP version the neighbour accepts (from <c>$IX</c>), or
/// <c>null</c>.</param>
/// <param name="LastReflectionAgeMs">Monotonic ms since the neighbour last reflected
/// (or since it was registered, if it never has).</param>
/// <param name="AwaitingReflection">Whether a probe is currently outstanding.</param>
public readonly record struct Inp3NeighbourTiming(
    Callsign Neighbour,
    uint? SnttMs,
    bool Inp3Capable,
    byte? IpAccept,
    long LastReflectionAgeMs,
    bool AwaitingReflection);
