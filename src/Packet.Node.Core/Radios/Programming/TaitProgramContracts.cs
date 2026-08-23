namespace Packet.Node.Core.Radios.Programming;

/// <summary>
/// The settings a "Program radio" run writes into an attached Tait TM8100 / TM8200: one channel,
/// plus an optional PDN upgrade profile. Frequencies are in hertz (the codeplug's own unit) so
/// nothing rides on a decimal MHz round-trip; the web UI types MHz and converts.
/// </summary>
/// <param name="RxFrequencyHz">Receive frequency, Hz. Required.</param>
/// <param name="TxFrequencyHz">Transmit frequency, Hz. Omit (or repeat the RX frequency) for a
/// simplex channel.</param>
/// <param name="Bandwidth">Channel bandwidth: <c>narrow</c> (12.5 kHz), <c>medium</c> (20 kHz) or
/// <c>wide</c> (25 kHz). Required.</param>
/// <param name="Power">Transmit power step: <c>verylow</c>, <c>low</c>, <c>medium</c> or
/// <c>high</c>. Required. (The codeplug's <c>off</c> step is deliberately not offered - a packet
/// channel that cannot transmit is a support call, not a setting.)</param>
/// <param name="Profile">The PDN upgrade profile to apply on top: <c>none</c>, <c>pdn-basic</c> or
/// <c>pdn-extra</c>. Null or omitted means <c>none</c>.</param>
public sealed record TaitProgramRequest(
    long? RxFrequencyHz,
    long? TxFrequencyHz,
    string? Bandwidth,
    string? Power,
    string? Profile);

/// <summary>Which PDN upgrade profile a programming run lays on top of the channel it writes.</summary>
public enum TaitPdnProfile
{
    /// <summary>Leave the radio's data / signalling configuration exactly as it is.</summary>
    None,

    /// <summary>`pdn-basic` - the CCDI command channel PDN's telemetry and control ride on.</summary>
    Basic,

    /// <summary>`pdn-extra` - `pdn-basic` plus the internal FFSK packet modem and the SDM side
    /// channel.</summary>
    Extra,
}

/// <summary>Where a programming run has got to. The wire spelling is
/// <see cref="TaitProgramStates.ToWire"/>.</summary>
public enum TaitProgramState
{
    /// <summary>Accepted; the port is being taken out of service.</summary>
    Starting,

    /// <summary>The programmer is probing for the radio's boot banner - <b>this</b> is the moment
    /// the operator has to power-cycle the radio.</summary>
    PowerCycle,

    /// <summary>The radio answered and the codeplug is being read.</summary>
    Reading,

    /// <summary>The codeplug is being written back.</summary>
    Writing,

    /// <summary>The radio is done with; the port is being brought back into service.</summary>
    Restoring,

    /// <summary>Written and committed, port back up. Terminal.</summary>
    Done,

    /// <summary>The run failed; <c>error</c> says how. The port is back up regardless. Terminal.</summary>
    Failed,

    /// <summary>The operator (or a node shutdown) abandoned the run. Terminal.</summary>
    Cancelled,
}

/// <summary>Wire spellings for <see cref="TaitProgramState"/> - one place, so the API, the SSE feed
/// and the web UI cannot drift.</summary>
public static class TaitProgramStates
{
    /// <summary>The wire spelling of a state.</summary>
    public static string ToWire(TaitProgramState state) => state switch
    {
        TaitProgramState.Starting => "starting",
        TaitProgramState.PowerCycle => "power-cycle",
        TaitProgramState.Reading => "reading",
        TaitProgramState.Writing => "writing",
        TaitProgramState.Restoring => "restoring",
        TaitProgramState.Done => "done",
        TaitProgramState.Failed => "failed",
        TaitProgramState.Cancelled => "cancelled",
        _ => "unknown",
    };

    /// <summary>Whether a state is terminal (nothing more will happen on this run).</summary>
    public static bool IsTerminal(TaitProgramState state) =>
        state is TaitProgramState.Done or TaitProgramState.Failed or TaitProgramState.Cancelled;
}

/// <summary>The settings a run resolved to, echoed back so the operator sees exactly what was
/// written rather than what was typed.</summary>
/// <param name="RxFrequencyHz">Receive frequency, Hz.</param>
/// <param name="TxFrequencyHz">Transmit frequency, Hz (equal to RX on a simplex channel).</param>
/// <param name="Bandwidth">Bandwidth: <c>narrow</c> / <c>medium</c> / <c>wide</c>.</param>
/// <param name="Power">Power step: <c>verylow</c> / <c>low</c> / <c>medium</c> / <c>high</c>.</param>
/// <param name="Profile">Profile applied: <c>none</c> / <c>pdn-basic</c> / <c>pdn-extra</c>.</param>
public sealed record TaitProgramPlanInfo(
    long RxFrequencyHz,
    long TxFrequencyHz,
    string Bandwidth,
    string Power,
    string Profile);

/// <summary>
/// A programming run as the API projects it: the POST's response body, and what a caller sees for
/// the run still on the port (live or just finished).
/// </summary>
/// <param name="PortId">The port whose radio is (or was) being programmed.</param>
/// <param name="State">The run's state - see <see cref="TaitProgramStates.ToWire"/>.</param>
/// <param name="StartedAt">When the run was accepted.</param>
/// <param name="FinishedAt">When it reached a terminal state, or null while it is live.</param>
/// <param name="DevicePath">The serial device the programmer drove, once it is known. Null
/// while a serial-bound radio on a stopped port is still being located.</param>
/// <param name="Plan">The settings written.</param>
/// <param name="RadioModel">The radio's product code, once the interrogate has read it.</param>
/// <param name="RadioSerial">The radio's serial number, once the interrogate has read it.</param>
/// <param name="BackupPath">Where the pre-change codeplug was snapshotted, once it has been.</param>
/// <param name="Error">The failure reason on a failed run, else null.</param>
public sealed record TaitProgramInfo(
    string PortId,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? DevicePath,
    TaitProgramPlanInfo Plan,
    string? RadioModel,
    string? RadioSerial,
    string? BackupPath,
    string? Error);

/// <summary>
/// One line of a run's live feed (<c>GET /api/v1/ports/{id}/radio/program/events</c>, SSE
/// <c>event: program</c>). A <c>state</c> event marks a transition, a <c>progress</c> event carries
/// a fraction within the current state, and a <c>log</c> event is a plain note.
/// </summary>
/// <param name="Kind"><c>state</c>, <c>progress</c> or <c>log</c>.</param>
/// <param name="At">When it happened.</param>
/// <param name="State">The run's state as of this event.</param>
/// <param name="Message">Operator-facing text, when there is any.</param>
/// <param name="Fraction">0..1 within the current state, when it is known.</param>
/// <param name="Error">Set on the terminal event of a failed run.</param>
public sealed record TaitProgramEvent(
    string Kind,
    DateTimeOffset At,
    string State,
    string? Message,
    double? Fraction,
    string? Error);

/// <summary>The caveat every start response carries: what a programming run costs the operator
/// while it is happening. Mirrors the RF caveats on the transmitting endpoints - the action is
/// consequential and the response says so in words, not just in a status code.</summary>
public static class TaitProgramCaveat
{
    /// <summary>The caveat text.</summary>
    public const string Text =
        "This TAKES THE PORT OFF THE AIR for the whole run (a few minutes) and REWRITES the radio's " +
        "codeplug: its channel table is replaced by the single channel given here, and any CTCSS/DCS " +
        "on that channel is cleared. The radio must be POWER-CYCLED when the run asks for it. The " +
        "pre-change codeplug is snapshotted to a .m8p file on the node first, and the port is brought " +
        "back into service whether the run succeeds or fails.";
}
