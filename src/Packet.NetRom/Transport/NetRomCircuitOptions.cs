using Packet.NetRom.Wire;

namespace Packet.NetRom.Transport;

/// <summary>
/// The tunable knobs of the NET/ROM L4 transport (the circuit layer). As with
/// <see cref="Packet.NetRom.Routing.NetRomRoutingOptions"/>, NET/ROM has <b>no
/// single normative standard</b> for these — the canonical appendix names a few
/// (OBSINIT-style defaults), but the timers and window come from the de-facto
/// reference (BPQ's <c>L4*</c> knobs / the Linux <c>transport_*</c> tunables).
/// Per CLAUDE.md every divergence is a named knob defaulted to a widely
/// interoperable value, never a silent BPQ-ism baked into the state machine.
/// </summary>
/// <remarks>
/// All durations are driven by an injected <see cref="System.TimeProvider"/>
/// (§2.7) — no wall-clock anywhere in the circuit layer.
/// </remarks>
public sealed record NetRomCircuitOptions
{
    /// <summary>
    /// The send-window size this node <em>proposes</em> in a Connect Request and
    /// the maximum it will <em>accept</em> in a Connect Acknowledge. NET/ROM
    /// negotiates the window down (the accepted size is ≤ both ends' proposals).
    /// Canonical / BPQ default <b>4</b> (<c>L4WINDOW</c>); the 8-bit sequence space
    /// allows up to 127.
    /// </summary>
    public int WindowSize { get; init; } = 4;

    /// <summary>
    /// The transport retransmit timeout (BPQ <c>L4TIMEOUT</c> / the Linux
    /// <c>transport_timeout</c>): how long to wait for an ack before retransmitting
    /// the oldest unacknowledged Information message. Default <b>5 s</b>.
    /// </summary>
    public TimeSpan RetransmitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum retransmit attempts for a Connect / Disconnect / Information message
    /// before the circuit is declared failed (BPQ <c>L4RETRIES</c> / the Linux
    /// <c>transport_maximum_tries</c>). Default <b>3</b>.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// The initial time-to-live stamped into the L3 network header of datagrams
    /// this circuit originates (<see cref="NetRomNetworkHeader.DefaultTimeToLive"/>).
    /// </summary>
    public byte TimeToLive { get; init; } = NetRomNetworkHeader.DefaultTimeToLive;

    /// <summary>
    /// Maximum bytes of user data per Information datagram — the fragment size. A
    /// logical send larger than this is split across several Information messages
    /// with the more-follows flag set on all but the last. Canonical maximum
    /// (and default) is <see cref="NetRomPacket.MaxPayload"/> (236).
    /// </summary>
    public int FragmentSize { get; init; } = NetRomPacket.MaxPayload;

    /// <summary>
    /// The number of queued-but-undelivered received Information messages at which
    /// this node asserts <em>choke</em> (tells the peer to stop sending) — the
    /// receive-side flow-control high-water mark. Choke is released once the
    /// backlog drains below it. Default <b>0</b> meaning the receiver never
    /// self-chokes (it always drains promptly — the node bridge does); a host that
    /// can stall its reader sets this so backpressure reaches the wire.
    /// </summary>
    public int ChokeThreshold { get; init; }

    /// <summary>The canonical / widely-interoperable defaults.</summary>
    public static NetRomCircuitOptions Default { get; } = new();

    /// <summary>
    /// BPQ / LinBPQ-flavoured defaults (the de-facto reference). Today identical to
    /// <see cref="Default"/> (BPQ's <c>L4WINDOW=4</c>, <c>L4TIMEOUT=60</c> is the
    /// idle-circuit timeout not the per-message one, <c>L4RETRIES=3</c>) — kept
    /// named so a future BPQ-specific accommodation lands here without churning
    /// call sites.
    /// </summary>
    public static NetRomCircuitOptions Bpq { get; } = Default;
}
