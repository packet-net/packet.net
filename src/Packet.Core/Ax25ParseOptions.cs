namespace Packet.Core;

/// <summary>
/// Per-call configuration for the AX.25 wire-parse paths
/// (<see cref="Ax25Address.Read(System.ReadOnlySpan{byte})"/>,
/// <c>Ax25Frame.TryParse</c>). Each pragmatic accommodation beyond
/// strict AX.25 v2.2 compliance is a named, individually-toggleable
/// flag — see <c>docs/strict-vs-pragmatic-audit.md</c> for the
/// inventory.
/// </summary>
/// <remarks>
/// <para>
/// Spec philosophy: Packet.NET is spec-compliant by default. The
/// parameterless decoder overloads use <see cref="Lenient"/> (kitchen-
/// sink accept-everything mode) to preserve current behaviour without
/// breaking callers. Callers who want strict spec adherence should
/// pass <see cref="Strict"/>; callers who know they're talking to a
/// specific peer can pass that peer's named preset
/// (<see cref="Bpq"/>, <see cref="Xrouter"/>, <see cref="Direwolf"/>).
/// </para>
/// <para>
/// When you discover a new real-world quirk, add a named flag here
/// (defaulted to keep current behaviour), surface it in the preset(s)
/// it belongs to, and update <c>docs/strict-vs-pragmatic-audit.md</c>.
/// Do not silently widen an existing parser to accept new garbage.
/// </para>
/// </remarks>
public sealed record Ax25ParseOptions
{
    /// <summary>
    /// Accept address slots with an empty callsign (all six callsign
    /// bytes are <c>0x40</c>, i.e. ASCII space shifted left 1).
    /// </summary>
    /// <remarks>
    /// Strict §3.12: "The call sign is made up of upper-case alpha and
    /// numeric ASCII characters only" — singular *characters*, plural,
    /// implying ≥ 1. §6.1.1 acknowledges non-callsign destinations
    /// exist but classes them as "a subject for further study".
    /// Driver: BPQ's <c>&gt;IS</c> ID beacons + PD4R-12 QRV broadcasts.
    /// </remarks>
    public bool AllowEmptyCallsignBase { get; init; } = true;

    /// <summary>
    /// Capture trailing bytes as the frame's <c>Info</c> on S frames.
    /// Strict §3.5: only I, UI, FRMR, XID and TEST carry information
    /// fields; S frames do not.
    /// </summary>
    /// <remarks>
    /// Pragmatic in two ways: (a) sidesteps having to enumerate which
    /// U-frames legitimately carry info (FRMR/XID/TEST do; UA/DM/DISC/
    /// SABM/SABME don't); (b) tolerates corrupted S frames with
    /// trailing bytes from a noisy RF link.
    /// </remarks>
    public bool AllowInfoOnSupervisoryFrames { get; init; } = true;

    /// <summary>
    /// Strict AX.25 v2.2 — all pragmatic accommodations disabled.
    /// </summary>
    public static Ax25ParseOptions Strict { get; } = new()
    {
        AllowEmptyCallsignBase = false,
        AllowInfoOnSupervisoryFrames = false,
    };

    /// <summary>
    /// Accept-everything mode (the kitchen sink). All currently known
    /// pragmatic flags enabled. Used by the parameterless decoder
    /// overloads to preserve historical behaviour for callers that
    /// pre-date the <c>…ParseOptions</c> introduction.
    /// </summary>
    public static Ax25ParseOptions Lenient { get; } = new();

    /// <summary>
    /// BPQ-flavoured leniency (G8BPQ / LinBPQ). Today this is the
    /// same instance as <see cref="Lenient"/>; it may diverge as we
    /// learn more about what BPQ specifically does or doesn't do.
    /// </summary>
    public static Ax25ParseOptions Bpq { get; } = Lenient;

    /// <summary>
    /// Xrouter-flavoured leniency. Today identical to <see cref="Strict"/>
    /// — no Xrouter-specific quirks observed yet. Will be populated as
    /// the interop test stack surfaces specific accommodations.
    /// </summary>
    public static Ax25ParseOptions Xrouter { get; } = Strict;

    /// <summary>
    /// Direwolf-as-AX.25-stack leniency. Today identical to
    /// <see cref="Lenient"/>; may diverge.
    /// </summary>
    public static Ax25ParseOptions Direwolf { get; } = Lenient;
}
