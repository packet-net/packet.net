using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;

namespace Packet.Tune.Core;

/// <summary>
/// The station-side actions a mode-coordination end must be able to perform on its own
/// hardware: switch the TNC mode, switch the radio channel, and transmit/count the
/// link-verification probe frames. <see cref="NinoTncModeCoordStation"/> is the live
/// NinoTNC + Tait implementation; tests supply fakes.
/// </summary>
public interface IModeCoordStation
{
    /// <summary>Switch the TNC to <paramref name="mode"/> (RAM-only on hardware — never
    /// burn the flash from a negotiation) including whatever settling the TNC needs
    /// before the mode is live (the NinoTNC applies a changed setting from the SECOND
    /// frame after the command).</summary>
    /// <exception cref="ModeCoordException">The mode could not be applied.</exception>
    Task ApplyModeAsync(byte mode, CancellationToken cancellationToken = default);

    /// <summary>Retune the radio to programmed channel <paramref name="channel"/> and
    /// verify it took.</summary>
    /// <exception cref="ModeCoordException">The switch did not verify.</exception>
    Task ApplyChannelAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>Transmit <paramref name="count"/> probe frames tagged
    /// <paramref name="attemptTag"/> over the (possibly just-switched) link under test.
    /// Returns sender-side TX stats; individual probe failures are not fatal (the
    /// receiver's decode count is the verdict that matters).</summary>
    Task<ModeProbeTxStats> TransmitProbesAsync(int attemptTag, int count, CancellationToken cancellationToken = default);

    /// <summary>Start counting inbound probe frames tagged <paramref name="attemptTag"/>.
    /// Dispose to stop counting.</summary>
    IModeProbeCounter BeginProbeCount(int attemptTag);
}

/// <summary>A live probe-frame counter (see <see cref="IModeCoordStation.BeginProbeCount"/>).</summary>
public interface IModeProbeCounter : IDisposable
{
    /// <summary>Probe frames decoded so far.</summary>
    int Count { get; }
}

/// <summary>Sender-side outcome of a probe burst.</summary>
/// <param name="Attempted">Probe frames handed to the TNC.</param>
/// <param name="TxConfirmed">Probes whose TX-completion echo arrived (a missing echo
/// does not mean the frame failed to key — bench-observed sporadic absence).</param>
/// <param name="MeanTxMs">Mean send→TX-complete latency over the confirmed probes
/// (CSMA deferral + TXDELAY + airtime), <c>null</c> when none confirmed.</param>
public sealed record ModeProbeTxStats(int Attempted, int TxConfirmed, double? MeanTxMs);

/// <summary>A station-side mode-coordination failure (mode would not apply, channel
/// switch did not verify…).</summary>
public sealed class ModeCoordException : Exception
{
    /// <summary>Create with a message describing the failure.</summary>
    public ModeCoordException(string message)
        : base(message)
    {
    }

    /// <summary>Create with a message and the underlying failure.</summary>
    public ModeCoordException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Parameterless form (framework convention).</summary>
    public ModeCoordException()
    {
    }
}

/// <summary>
/// The link-verification stimulus of a mode-coordination attempt: short UI frames
/// tagged with the attempt (the commit — or revert — telegram's sequence number), so
/// stale probes from a previous attempt can never satisfy the current one.
/// </summary>
public static class ModeProbe
{
    /// <summary>The info-text marker every probe frame starts with.</summary>
    public const string Marker = "PMODE";

    /// <summary>The destination address probe frames are sent to.</summary>
    public const string Destination = "TUNE";

    /// <summary>Info-field length — short on purpose, long enough to exercise the modem.</summary>
    public const int InfoLength = 40;

    /// <summary>Build probe frame <paramref name="index"/> of <paramref name="count"/>
    /// for attempt <paramref name="attemptTag"/>.</summary>
    public static Ax25Frame BuildFrame(Callsign source, int attemptTag, int index, int count)
    {
        string text = string.Create(
            CultureInfo.InvariantCulture, $"{Marker} a{attemptTag} {index}/{count} ");
        return Ax25Frame.Ui(
            destination: new Callsign(Destination),
            source: source,
            info: Encoding.ASCII.GetBytes(text.PadRight(InfoLength, '.')));
    }

    /// <summary>True when an inbound KISS frame is a probe frame for
    /// <paramref name="attemptTag"/> (a data frame whose payload carries the marker and
    /// the tag).</summary>
    public static bool IsProbeFrame(KissFrame frame, int attemptTag)
    {
        if (frame.Command != KissCommand.Data)
        {
            return false;
        }
        string token = string.Create(CultureInfo.InvariantCulture, $"{Marker} a{attemptTag} ");
        return frame.Payload.AsSpan().IndexOf(Encoding.ASCII.GetBytes(token)) >= 0;
    }
}

/// <summary>Tunables shared by <see cref="ModeCoordinator"/> and <see cref="ModeResponder"/>.
/// The timing defaults encode the bench-measured SDM cadence (a delivered+receipted
/// telegram costs seconds) and the TM8110 auto-ack wedge guards (never key the TNC
/// right after the coordination radio received a telegram).</summary>
public sealed record ModeCoordOptions
{
    /// <summary>The session's home TNC mode — where both ends revert to on any failure,
    /// and where the responder's watchdog takes it back to when the coordinator goes
    /// quiet. Default 6 (1200 AFSK AX.25 — the bench rig's resting mode).</summary>
    public byte HomeMode { get; init; } = 6;

    /// <summary>The session's home radio channel. Default 0.</summary>
    public int HomeChannel { get; init; }

    /// <summary>Probe frames per direction when verifying a switched link. Default 5.</summary>
    public int ProbeFrames { get; init; } = 5;

    /// <summary>Probe frames for the one-way home-link check after a revert. Default 2.</summary>
    public int HomeVerifyProbeFrames { get; init; } = 2;

    /// <summary>Coordinator: wait for confirm/reject after a propose. Default 45 s
    /// (covers the responder's post-receive guard + a full SDM retry cycle).</summary>
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Wait for the peer's probe report after announcing <c>sent</c>. Default 60 s.</summary>
    public TimeSpan ReportTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Coordinator: wait for the responder's reverse probes to be announced
    /// (<c>sent</c>) after its report. Default 120 s (covers 5 probes at 300 baud plus
    /// guards and SDM retries).</summary>
    public TimeSpan PeerProbeTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Pause between committing/reverting and the first probe, so both ends
    /// finish their mode/channel applies (including the settle frame). Default 5 s.</summary>
    public TimeSpan SwitchSettle { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Coordinator: how many times to (re)run the propose→confirm handshake before
    /// giving up. The SDM delivery receipt is unreliable for close bidirectional traffic (the
    /// TM8110 auto-ack refractory — see <see cref="SdmTuningLink"/> and
    /// docs/research/tm8110-sdm-autoack-refractory.md), so reliability is reply-driven: a lost
    /// propose/confirm is recovered by re-proposing with a fresh sequence (the responder
    /// re-confirms idempotently — nothing is committed until the commit phase). Default 3.</summary>
    public int LinkRetryAttempts { get; init; } = 3;

    /// <summary>Minimum gap between receiving a side-channel telegram and keying the
    /// TNC: the coordination radio may still be sending its SDM auto-ack, and a brief gap
    /// avoids keying the TNC over it (half-duplex etiquette; see <see cref="SdmTuningLink"/> —
    /// the auto-ack refractory itself is handled by not depending on the receipt). Default 2.5 s.</summary>
    public TimeSpan PreProbeDelay { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>After a <c>sent</c> announcement arrives, how long to keep the probe
    /// window open before snapshotting the counter (decode + serial trail behind the
    /// sender's TX-complete). Default 1.5 s.</summary>
    public TimeSpan ArrivalGrace { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Responder watchdog: when switched away from home (or mid-attempt) and
    /// nothing has been heard for this long, revert to home unilaterally — SDM loss
    /// (e.g. a split channel after a failed switch) must never strand the responder on
    /// a dead link. Default 150 s (must exceed <see cref="PeerProbeTimeout"/> plus the
    /// coordinator's revert exchange).</summary>
    public TimeSpan ResponderIdleRevert { get; init; } = TimeSpan.FromSeconds(150);
}

/// <summary>How a coordination attempt ended.</summary>
public enum ModeCoordOutcome
{
    /// <summary>Both ends switched and the link verified with probe traffic in both
    /// directions (possibly marginal — see the cells).</summary>
    Switched,

    /// <summary>The responder rejected the proposal; nothing changed.</summary>
    Rejected,

    /// <summary>No confirm arrived; nothing changed at this end (the responder never
    /// switches before the commit).</summary>
    ConfirmTimeout,

    /// <summary>The commit's delivery was not confirmed — ambiguous, so this end stayed
    /// home and sent a best-effort revert (the responder's watchdog covers the rest).</summary>
    CommitUndelivered,

    /// <summary>A local mode/channel apply failed after the commit; both ends reverted.</summary>
    SwitchFailed,

    /// <summary>The switched link carried no probe traffic in at least one direction;
    /// both ends reverted to home over the side channel.</summary>
    ProbeDead,

    /// <summary>The side-channel coordination itself failed mid-attempt; reverted.</summary>
    LinkFailed,
}

/// <summary>One direction's probe result on a switched link.</summary>
/// <param name="Decoded">Probe frames decoded at the receiving end.</param>
/// <param name="Attempts">Probe frames transmitted.</param>
/// <param name="MeanTxMs">Sender-side mean send→TX-complete latency in ms (CSMA +
/// TXDELAY + airtime; the receive-side decode trails it by only tens of ms), <c>null</c>
/// when no TX completion was confirmed.</param>
public sealed record ModeProbeCell(int Decoded, int Attempts, double? MeanTxMs)
{
    /// <summary>Dead / marginal / solid, as in <see cref="ModeSurveyVerdict"/>.</summary>
    public ModeSurveyVerdict Verdict =>
        Decoded <= 0 ? ModeSurveyVerdict.Dead
        : Decoded < Attempts ? ModeSurveyVerdict.Marginal
        : ModeSurveyVerdict.Solid;
}

/// <summary>The full record of one coordination attempt.</summary>
public sealed record ModeCoordAttempt
{
    /// <summary>The proposed NinoTNC mode.</summary>
    public required byte Mode { get; init; }

    /// <summary>Human mode name from the catalog (or <c>mode N</c> when unknown).</summary>
    public required string ModeName { get; init; }

    /// <summary>The channel the attempt switched to, when it included a channel change.</summary>
    public int? Channel { get; init; }

    /// <summary>The radio channel the probes ran on (the attempt's target channel, or
    /// the session's current channel when unchanged).</summary>
    public required int ChannelInEffect { get; init; }

    /// <summary>How the attempt ended.</summary>
    public required ModeCoordOutcome Outcome { get; init; }

    /// <summary>Coordinator→responder probe cell (<c>null</c> when the attempt failed
    /// before probing).</summary>
    public ModeProbeCell? CoordinatorToResponder { get; init; }

    /// <summary>Responder→coordinator probe cell (<c>null</c> when the attempt failed
    /// before the reverse probes).</summary>
    public ModeProbeCell? ResponderToCoordinator { get; init; }

    /// <summary>Whether the attempt ended with both ends sent back to home.</summary>
    public bool Reverted { get; init; }

    /// <summary>After a revert: did the home link verify alive? (<c>null</c> = not
    /// checked / could not be checked.)</summary>
    public bool? HomeLinkAlive { get; init; }

    /// <summary>Free-text detail for the human (failure stage, reasons).</summary>
    public string? Detail { get; init; }

    /// <summary>True when the switch landed and verified.</summary>
    public bool Success => Outcome == ModeCoordOutcome.Switched;
}

/// <summary>Sweep-mode helpers + result-table rendering for the mode-coordination CLI.</summary>
public static class ModeCoordReport
{
    /// <summary>
    /// The modes a <c>--sweep</c> walks: every IL2P+CRC catalog mode
    /// (<see cref="ModeSurvey.SelectIl2pCrcModes"/>), optionally dropping the modes
    /// whose published occupied bandwidth needs a wide (25 kHz) channel when sweeping a
    /// narrow one (<paramref name="strictBandwidth"/>). Lenient (the default CLI
    /// behaviour) tries everything and lets the probe verdicts speak — bench evidence:
    /// mode 2 (9600 GFSK, a 25 kHz mode per the OARC wiki) decodes on this rig's
    /// narrow programmed channel at 38 dB SNR.
    /// </summary>
    public static IReadOnlyList<NinoTncMode> SelectSweepModes(bool strictBandwidth, bool channelIsWide)
    {
        var modes = ModeSurvey.SelectIl2pCrcModes();
        if (!strictBandwidth || channelIsWide)
        {
            return modes;
        }
        return modes.Where(m => !NinoTncCatalog.RequiresWideChannel(m.Mode)).ToList();
    }

    /// <summary>Render attempts as a Markdown table: two direction rows per probed
    /// attempt, one row for attempts that never reached the probes.</summary>
    public static string RenderMarkdown(IReadOnlyList<ModeCoordAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var sb = new StringBuilder();
        sb.AppendLine("| Ch | Mode | Name | Dir | Decoded | Mean TX latency | Outcome |");
        sb.AppendLine("|---:|-----:|------|-----|--------:|----------------:|---------|");
        foreach (var attempt in attempts)
        {
            string outcome = DescribeOutcome(attempt);
            if (attempt.CoordinatorToResponder is null && attempt.ResponderToCoordinator is null)
            {
                AppendRow(sb, attempt, "—", "n/a", "n/a", outcome);
                continue;
            }
            AppendCellRow(sb, attempt, "C→R", attempt.CoordinatorToResponder, outcome);
            AppendCellRow(sb, attempt, "R→C", attempt.ResponderToCoordinator, outcome);
        }
        return sb.ToString();
    }

    /// <summary>One-line human outcome, e.g. <c>switched (solid both ways)</c> or
    /// <c>PROBE DEAD — reverted, home link alive</c>.</summary>
    public static string DescribeOutcome(ModeCoordAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        string reverted = attempt.Reverted
            ? attempt.HomeLinkAlive switch
            {
                true => " — reverted, home link alive",
                false => " — reverted, HOME LINK NOT CONFIRMED",
                null => " — reverted (home link unverified)",
            }
            : string.Empty;
        return attempt.Outcome switch
        {
            ModeCoordOutcome.Switched => "switched" + DescribeCells(attempt),
            ModeCoordOutcome.Rejected => "rejected" + Detail(attempt),
            ModeCoordOutcome.ConfirmTimeout => "no confirm" + Detail(attempt),
            ModeCoordOutcome.CommitUndelivered => "commit undelivered" + reverted,
            ModeCoordOutcome.SwitchFailed => "SWITCH FAILED" + reverted,
            ModeCoordOutcome.ProbeDead => "PROBE DEAD" + reverted,
            ModeCoordOutcome.LinkFailed => "COORDINATION LOST" + reverted,
            _ => attempt.Outcome.ToString(),
        };
    }

    private static string DescribeCells(ModeCoordAttempt attempt)
    {
        if (attempt.CoordinatorToResponder is not { } cToR || attempt.ResponderToCoordinator is not { } rToC)
        {
            return string.Empty;
        }
        return cToR.Verdict == ModeSurveyVerdict.Solid && rToC.Verdict == ModeSurveyVerdict.Solid
            ? " (solid both ways)"
            : " (MARGINAL)";
    }

    private static string Detail(ModeCoordAttempt attempt) =>
        string.IsNullOrEmpty(attempt.Detail) ? string.Empty : $" ({attempt.Detail})";

    private static void AppendCellRow(
        StringBuilder sb, ModeCoordAttempt attempt, string direction, ModeProbeCell? cell, string outcome)
    {
        if (cell is null)
        {
            AppendRow(sb, attempt, direction, "n/a", "n/a", outcome);
            return;
        }
        string decoded = string.Create(CultureInfo.InvariantCulture, $"{cell.Decoded}/{cell.Attempts}");
        string latency = cell.MeanTxMs is { } ms
            ? string.Create(CultureInfo.InvariantCulture, $"{ms:0} ms")
            : "n/a";
        AppendRow(sb, attempt, direction, decoded, latency, outcome);
    }

    private static void AppendRow(
        StringBuilder sb, ModeCoordAttempt attempt, string direction, string decoded, string latency, string outcome)
    {
        sb.Append(CultureInfo.InvariantCulture, $"| {attempt.ChannelInEffect} ");
        sb.Append(CultureInfo.InvariantCulture, $"| {attempt.Mode} ");
        sb.Append(CultureInfo.InvariantCulture, $"| {attempt.ModeName} ");
        sb.Append(CultureInfo.InvariantCulture, $"| {direction} ");
        sb.Append(CultureInfo.InvariantCulture, $"| {decoded} ");
        sb.Append(CultureInfo.InvariantCulture, $"| {latency} ");
        sb.Append(CultureInfo.InvariantCulture, $"| {outcome} |");
        sb.AppendLine();
    }
}
