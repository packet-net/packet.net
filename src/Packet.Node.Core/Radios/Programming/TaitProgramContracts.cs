namespace Packet.Node.Core.Radios.Programming;

/// <summary>
/// The settings a "Program radio" run writes into an attached Tait TM8100 / TM8200: one channel,
/// plus an optional PDN upgrade profile. Frequencies are in hertz (the codeplug's own unit) so
/// nothing rides on a decimal MHz round-trip; the web UI accepts MHz or Hz and converts.
/// </summary>
/// <param name="RxFrequencyHz">Receive frequency, Hz. Required.</param>
/// <param name="TxFrequencyHz">Transmit frequency, Hz. Omit (or repeat the RX frequency) for a
/// simplex channel, which is what packet always is - the web UI only ever sends one frequency.</param>
/// <param name="Bandwidth">Channel bandwidth: <c>narrow</c> (12.5 kHz), <c>medium</c> (20 kHz) or
/// <c>wide</c> (25 kHz). Required.</param>
/// <param name="Power">Transmit power step: <c>verylow</c>, <c>low</c>, <c>medium</c> or
/// <c>high</c>. Required. (The codeplug's <c>off</c> step is deliberately not offered - a packet
/// channel that cannot transmit is a support call, not a setting.)</param>
/// <param name="Profile">The PDN upgrade profile to apply on top: <c>none</c>, <c>pdn-basic</c> or
/// <c>pdn-extra</c>. Null or omitted means <c>none</c>.</param>
/// <param name="ReplaceChannelTable">Whether to delete the radio's other channels so the one being
/// written is all that is left. Null or omitted means true, which is what
/// packet-net/packet.net#779 asked for. Set false to patch channel 1 in place and leave the rest of
/// the channel table alone - the narrower, better-proven write, and the one to fall back to if a
/// full replacement is refused by the radio.</param>
public sealed record TaitProgramRequest(
    long? RxFrequencyHz,
    long? TxFrequencyHz,
    string? Bandwidth,
    string? Power,
    string? Profile,
    bool? ReplaceChannelTable = null);

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

/// <summary>What a run does to the radio: rewrite its codeplug, or only read it back.</summary>
public enum TaitProgramMode
{
    /// <summary>Read-modify-write: the channel and the profile are written.</summary>
    Program,

    /// <summary>Read-only: the codeplug is read (and snapshotted) and the radio's current settings
    /// are reported. Nothing is written.</summary>
    Read,
}

/// <summary>Wire spellings for <see cref="TaitProgramMode"/>.</summary>
public static class TaitProgramModes
{
    /// <summary>The wire spelling of a mode.</summary>
    public static string ToWire(TaitProgramMode mode) => mode == TaitProgramMode.Read ? "read" : "program";
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
/// written rather than what was typed. Null on a read-only run, which writes nothing.</summary>
/// <param name="RxFrequencyHz">Receive frequency, Hz.</param>
/// <param name="TxFrequencyHz">Transmit frequency, Hz (equal to RX on a simplex channel).</param>
/// <param name="Bandwidth">Bandwidth: <c>narrow</c> / <c>medium</c> / <c>wide</c>.</param>
/// <param name="Power">Power step: <c>verylow</c> / <c>low</c> / <c>medium</c> / <c>high</c>.</param>
/// <param name="Profile">Profile applied: <c>none</c> / <c>pdn-basic</c> / <c>pdn-extra</c>.</param>
/// <param name="ReplaceChannelTable">Whether the radio's other channels were deleted.</param>
public sealed record TaitProgramPlanInfo(
    long RxFrequencyHz,
    long TxFrequencyHz,
    string Bandwidth,
    string Power,
    string Profile,
    bool ReplaceChannelTable);

/// <summary>
/// What the radio's codeplug says <b>right now</b> - read off the radio before anything is written,
/// so a Read run answers "what is this radio set to?" and a Program run records what it replaced.
/// Channel 1 (index 0) is the one reported: the channel a PDN port drives.
/// <para>
/// Everything but <see cref="DatabaseVersion"/> is null when the radio's codeplug database version
/// is not one the field map covers - the read still happened and the version is the answer the
/// operator needs, so it is reported rather than thrown away.
/// </para>
/// </summary>
/// <param name="RxFrequencyHz">Channel 1's receive frequency, Hz.</param>
/// <param name="TxFrequencyHz">Channel 1's transmit frequency, Hz (equal to RX when simplex).</param>
/// <param name="Bandwidth">Channel 1's bandwidth: <c>narrow</c> / <c>medium</c> / <c>wide</c>.</param>
/// <param name="Power">Channel 1's power step: <c>off</c> / <c>verylow</c> / <c>low</c> /
/// <c>medium</c> / <c>high</c>.</param>
/// <param name="Profile">Which PDN profile the radio's data block already matches:
/// <c>pdn-extra</c>, <c>pdn-basic</c>, or <c>none</c> when it matches neither.</param>
/// <param name="ChannelCount">How many channels the codeplug holds.</param>
/// <param name="DatabaseVersion">The codeplug's database version (e.g. <c>0095</c>) - the thing the
/// write path is pinned against, so it is worth showing when a write is refused.</param>
/// <param name="RxTone">Channel 1's receive CTCSS/DCS as text, or <c>none</c>.</param>
/// <param name="TxTone">Channel 1's transmit CTCSS/DCS as text, or <c>none</c>.</param>
public sealed record TaitRadioSettings(
    long? RxFrequencyHz,
    long? TxFrequencyHz,
    string? Bandwidth,
    string? Power,
    string? Profile,
    int? ChannelCount,
    string? DatabaseVersion,
    string? RxTone,
    string? TxTone);

/// <summary>
/// A programming run as the API projects it: the POST's response body, and what a caller sees for
/// the run still on the port (live or just finished).
/// </summary>
/// <param name="PortId">The port whose radio is (or was) being programmed.</param>
/// <param name="Mode"><c>program</c> or <c>read</c> - see <see cref="TaitProgramModes.ToWire"/>.</param>
/// <param name="State">The run's state - see <see cref="TaitProgramStates.ToWire"/>.</param>
/// <param name="StartedAt">When the run was accepted.</param>
/// <param name="FinishedAt">When it reached a terminal state, or null while it is live.</param>
/// <param name="DevicePath">The serial device the programmer drove, once it is known. Null
/// while a serial-bound radio on a stopped port is still being located.</param>
/// <param name="Plan">The settings written, or null on a read-only run.</param>
/// <param name="Current">What the radio's codeplug held when it was read, once it has been.</param>
/// <param name="RadioModel">The radio's product code, once the interrogate has read it.</param>
/// <param name="RadioSerial">The radio's serial number, once the interrogate has read it.</param>
/// <param name="BackupPath">Where the pre-change codeplug was snapshotted, once it has been.</param>
/// <param name="Error">The failure reason on a failed run, else null.</param>
/// <param name="FailedState">Which state the run was in when it failed (<c>power-cycle</c>,
/// <c>reading</c>, <c>writing</c>...) - the single most useful fact about a failure, and not
/// recoverable from <see cref="State"/>, which by then reads <c>failed</c>.</param>
/// <param name="Log">Every operator-facing line the run emitted, oldest first. The panel renders
/// this after the fact, so a failure keeps its context even if nothing was watching the feed.</param>
public sealed record TaitProgramInfo(
    string PortId,
    string Mode,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? DevicePath,
    TaitProgramPlanInfo? Plan,
    TaitRadioSettings? Current,
    string? RadioModel,
    string? RadioSerial,
    string? BackupPath,
    string? Error,
    string? FailedState,
    IReadOnlyList<string> Log);

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
/// <param name="FailedState">Which state the run was in when it failed, on that same event.</param>
public sealed record TaitProgramEvent(
    string Kind,
    DateTimeOffset At,
    string State,
    string? Message,
    double? Fraction,
    string? Error,
    string? FailedState);

/// <summary>The caveat every start response carries: what a run costs the operator while it is
/// happening. Mirrors the RF caveats on the transmitting endpoints - the action is consequential
/// and the response says so in words, not just in a status code.</summary>
public static class TaitProgramCaveat
{
    /// <summary>The caveat on a run that writes.</summary>
    public const string Text =
        "This TAKES THE PORT OFF THE AIR for the whole run (a few minutes) and REWRITES the radio's " +
        "codeplug: channel 1 is replaced by the channel given here, any CTCSS/DCS on it is cleared, " +
        "and unless replaceChannelTable is false the radio's other channels are deleted. The radio " +
        "must be POWER-CYCLED when the run asks for it; it restarts on the new codeplug by itself " +
        "once the write commits. The pre-change codeplug is snapshotted to a .m8p file on the node " +
        "first, and the port is brought back into service whether the run succeeds or fails.";

    /// <summary>The caveat on a read-only run: nothing is written, but the port still goes down and
    /// the radio still has to be power-cycled into programming mode.</summary>
    public const string ReadText =
        "This TAKES THE PORT OFF THE AIR while it runs (up to a couple of minutes) but WRITES " +
        "NOTHING. The radio must be POWER-CYCLED when the run asks for it. The codeplug read is " +
        "snapshotted to a .m8p file on the node, and the port is brought back into service either " +
        "way.";
}
