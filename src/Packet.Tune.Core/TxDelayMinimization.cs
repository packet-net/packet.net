using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;

namespace Packet.Tune.Core;

/// <summary>The actions of the TXDELAY-minimisation sub-protocol (the args of a
/// <see cref="TuningVerb.TxDelay"/> telegram).</summary>
public enum TxDelayMinAction
{
    /// <summary><c>propose|&lt;k&gt;|&lt;startMs&gt;|&lt;stepMs&gt;</c> — coordinator → meter:
    /// let's sweep my TXDELAY down from <c>startMs</c> in <c>stepMs</c> decrements,
    /// <c>k</c> separately-keyed probes per step. Doubles as the session handshake.</summary>
    Propose,

    /// <summary><c>confirm|&lt;k&gt;</c> — meter → coordinator: accepted; counting
    /// begins on each <c>step</c>.</summary>
    Confirm,

    /// <summary><c>reject[|&lt;reason&gt;]</c> — meter → coordinator: refused
    /// (bad probe count, local policy…). Nothing has changed anywhere.</summary>
    Reject,

    /// <summary><c>step|&lt;ms&gt;|&lt;k&gt;</c> — coordinator → meter: the next sweep step.
    /// The step telegram's sequence number is the tag every probe keying of this step
    /// carries; the meter opens its counter on receipt.</summary>
    Step,

    /// <summary><c>sent|&lt;ms&gt;|&lt;k&gt;</c> — coordinator → meter: "I have finished the
    /// step's k separate probe keyings". The meter grace-waits, snapshots its counter
    /// and answers with a <c>report</c>.</summary>
    ProbesSent,

    /// <summary><c>report|&lt;ms&gt;|&lt;decoded&gt;/&lt;k&gt;[|&lt;preMs&gt;]</c> — meter →
    /// coordinator: how many of the step's probes decoded, plus (when the meter has a
    /// carrier-sensing radio) the median measured pre-data carrier time in whole ms —
    /// the direct as-heard measurement of the coordinator's effective TXDELAY.</summary>
    StepReport,

    /// <summary><c>apply|&lt;ms&gt;|&lt;k&gt;</c> — coordinator → meter: verification pass at
    /// the recommended TXDELAY (the explicit APPLY, separate from the sweep). Handled
    /// exactly like <see cref="Step"/> at the meter: the telegram's sequence tags the
    /// verify probes.</summary>
    Apply,

    /// <summary><c>done[|&lt;recMs&gt;]</c> — coordinator → meter: session over,
    /// optionally carrying the recommendation for the meter operator's log.</summary>
    Done,

    /// <summary><c>abort[|&lt;reason&gt;]</c> — either direction: the sweep is being
    /// abandoned. The coordinator restores its TXDELAY + channel-access params
    /// regardless of whether this telegram was delivered.</summary>
    Abort,
}

/// <summary>
/// One TXDELAY-minimisation message — the payload of a <see cref="TuningVerb.TxDelay"/>
/// (<c>TXD</c>) telegram. The full wire form is e.g. <c>V1|7|TXD|step|300|5</c>.
/// Every routine form fits the 32-character plain-SDM budget; a worst-case
/// <c>report</c> (4-digit ms + 4-digit pre-data) rides an extended SDM exactly like
/// <c>STAT</c>. Sequence numbering, dedupe and versioning come from the enclosing
/// <see cref="TuningTelegram"/>.
/// </summary>
/// <remarks>
/// Only the <b>coordinator's own TXDELAY</b> is under negotiation — unlike mode
/// coordination nothing at the meter end ever changes, so there is no commit/revert
/// choreography: the coordinator pins its channel-access params, steps its TXDELAY
/// down, and restores on every exit path (see <see cref="TxDelayMinimizer"/>).
/// </remarks>
public sealed record TxDelayMinMessage
{
    /// <summary>The sub-protocol action.</summary>
    public required TxDelayMinAction Action { get; init; }

    /// <summary>The TXDELAY under test in ms (step / sent / report / apply).</summary>
    public int? TxDelayMs { get; init; }

    /// <summary>Probes per step (propose/confirm/step/sent/report/apply).</summary>
    public int? Count { get; init; }

    /// <summary>Probes actually decoded (<see cref="TxDelayMinAction.StepReport"/>).</summary>
    public int? Decoded { get; init; }

    /// <summary>The sweep's starting TXDELAY in ms (<see cref="TxDelayMinAction.Propose"/>).</summary>
    public int? StartTxDelayMs { get; init; }

    /// <summary>The sweep's decrement in ms (<see cref="TxDelayMinAction.Propose"/>).</summary>
    public int? StepMs { get; init; }

    /// <summary>Median measured pre-data carrier across the step's decoded probes, in
    /// whole ms (<see cref="TxDelayMinAction.StepReport"/>; optional — needs a
    /// carrier-sensing radio at the meter).</summary>
    public int? MedianPreDataMs { get; init; }

    /// <summary>The sweep's recommendation in ms (<see cref="TxDelayMinAction.Done"/>; optional).</summary>
    public int? RecommendedTxDelayMs { get; init; }

    /// <summary>Short failure reason (reject/abort; optional). One pipe-free token,
    /// capped at <see cref="ModeCoordMessage.MaxReasonLength"/> like mode-coord's.</summary>
    public string? Reason { get; init; }

    /// <summary>Encode to the telegram-args wire form (see <see cref="TxDelayMinAction"/>
    /// for the per-action shapes).</summary>
    public string ToArgs()
    {
        var sb = new StringBuilder();
        switch (Action)
        {
            case TxDelayMinAction.Propose:
                sb.Append("propose|").Append(RequiredInt(Count, "Count"))
                  .Append('|').Append(RequiredInt(StartTxDelayMs, "StartTxDelayMs"))
                  .Append('|').Append(RequiredInt(StepMs, "StepMs"));
                break;
            case TxDelayMinAction.Confirm:
                sb.Append("confirm|").Append(RequiredInt(Count, "Count"));
                break;
            case TxDelayMinAction.Reject:
                sb.Append("reject");
                AppendReason(sb);
                break;
            case TxDelayMinAction.Step:
            case TxDelayMinAction.ProbesSent:
            case TxDelayMinAction.Apply:
                sb.Append(ActionToken(Action))
                  .Append('|').Append(RequiredInt(TxDelayMs, "TxDelayMs"))
                  .Append('|').Append(RequiredInt(Count, "Count"));
                break;
            case TxDelayMinAction.StepReport:
                sb.Append("report|").Append(RequiredInt(TxDelayMs, "TxDelayMs"))
                  .Append('|').Append(RequiredInt(Decoded, "Decoded"))
                  .Append('/').Append(RequiredInt(Count, "Count"));
                if (MedianPreDataMs is { } pre)
                {
                    sb.Append('|').Append(pre.ToString(CultureInfo.InvariantCulture));
                }
                break;
            case TxDelayMinAction.Done:
                sb.Append("done");
                if (RecommendedTxDelayMs is { } rec)
                {
                    sb.Append('|').Append(rec.ToString(CultureInfo.InvariantCulture));
                }
                break;
            case TxDelayMinAction.Abort:
                sb.Append("abort");
                AppendReason(sb);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Action), Action, "unknown txdelay-min action");
        }
        return sb.ToString();
    }

    /// <summary>Wrap in a <see cref="TuningVerb.TxDelay"/> telegram with the given
    /// sequence number.</summary>
    public TuningTelegram ToTelegram(int sequence) => new(sequence, TuningVerb.TxDelay, ToArgs());

    /// <summary>Try to parse the args of a <c>TXD</c> telegram. Unknown actions and
    /// malformed numbers parse as <c>false</c> (forward compatibility: peers ignore
    /// what they cannot read).</summary>
    public static bool TryParse(string? args, out TxDelayMinMessage? message)
    {
        message = null;
        if (string.IsNullOrEmpty(args))
        {
            return false;
        }
        string[] parts = args.Split('|');
        switch (parts[0])
        {
            case "propose":
            {
                if (parts.Length != 4 ||
                    !TryInt(parts[1], out int k) || !TryInt(parts[2], out int start) || !TryInt(parts[3], out int step))
                {
                    return false;
                }
                message = new TxDelayMinMessage
                {
                    Action = TxDelayMinAction.Propose,
                    Count = k,
                    StartTxDelayMs = start,
                    StepMs = step,
                };
                return true;
            }
            case "confirm":
            {
                if (parts.Length != 2 || !TryInt(parts[1], out int k))
                {
                    return false;
                }
                message = new TxDelayMinMessage { Action = TxDelayMinAction.Confirm, Count = k };
                return true;
            }
            case "reject" or "abort":
            {
                if (parts.Length > 2)
                {
                    return false;
                }
                message = new TxDelayMinMessage
                {
                    Action = parts[0] == "reject" ? TxDelayMinAction.Reject : TxDelayMinAction.Abort,
                    Reason = parts.Length == 2 ? parts[1] : null,
                };
                return true;
            }
            case "step" or "sent" or "apply":
            {
                if (parts.Length != 3 || !TryInt(parts[1], out int ms) || !TryInt(parts[2], out int k))
                {
                    return false;
                }
                message = new TxDelayMinMessage
                {
                    Action = parts[0] switch
                    {
                        "step" => TxDelayMinAction.Step,
                        "sent" => TxDelayMinAction.ProbesSent,
                        _ => TxDelayMinAction.Apply,
                    },
                    TxDelayMs = ms,
                    Count = k,
                };
                return true;
            }
            case "report":
            {
                if (parts.Length is < 3 or > 4 || !TryInt(parts[1], out int ms))
                {
                    return false;
                }
                string[] fraction = parts[2].Split('/');
                if (fraction.Length != 2 || !TryInt(fraction[0], out int decoded) || !TryInt(fraction[1], out int k))
                {
                    return false;
                }
                int? pre = null;
                if (parts.Length == 4)
                {
                    if (!TryInt(parts[3], out int p))
                    {
                        return false;
                    }
                    pre = p;
                }
                message = new TxDelayMinMessage
                {
                    Action = TxDelayMinAction.StepReport,
                    TxDelayMs = ms,
                    Decoded = decoded,
                    Count = k,
                    MedianPreDataMs = pre,
                };
                return true;
            }
            case "done":
            {
                if (parts.Length > 2)
                {
                    return false;
                }
                int? rec = null;
                if (parts.Length == 2)
                {
                    if (!TryInt(parts[1], out int r))
                    {
                        return false;
                    }
                    rec = r;
                }
                message = new TxDelayMinMessage { Action = TxDelayMinAction.Done, RecommendedTxDelayMs = rec };
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Try to extract a TXDELAY-minimisation message from any telegram
    /// (<c>false</c> for non-<c>TXD</c> verbs or unreadable args).</summary>
    public static bool TryFromTelegram(TuningTelegram telegram, out TxDelayMinMessage? message)
    {
        ArgumentNullException.ThrowIfNull(telegram);
        if (telegram.Verb != TuningVerb.TxDelay)
        {
            message = null;
            return false;
        }
        return TryParse(telegram.Args, out message);
    }

    /// <inheritdoc/>
    public override string ToString() => ToArgs();

    private static string ActionToken(TxDelayMinAction action) => action switch
    {
        TxDelayMinAction.Step => "step",
        TxDelayMinAction.ProbesSent => "sent",
        TxDelayMinAction.Apply => "apply",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "not a ms|k action"),
    };

    private void AppendReason(StringBuilder sb)
    {
        if (string.IsNullOrEmpty(Reason))
        {
            return;
        }
        string clean = Reason.Replace('|', '/');
        sb.Append('|').Append(clean.Length <= ModeCoordMessage.MaxReasonLength
            ? clean
            : clean[..ModeCoordMessage.MaxReasonLength]);
    }

    private static string RequiredInt(int? value, string field) =>
        (value ?? throw new InvalidOperationException($"a txdelay-min message needs {field}"))
            .ToString(CultureInfo.InvariantCulture);

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// The stimulus frames of a TXDELAY-minimisation step: short UI frames, each transmitted
/// as its own SEPARATE keying (never a multi-frame train — a train shares one preamble
/// and would measure nothing), tagged with the step (the <c>step</c>/<c>apply</c>
/// telegram's sequence number) so stale probes from a previous step can never satisfy
/// the current one. Mirrors <see cref="ModeProbe"/>.
/// </summary>
public static class TxDelayProbe
{
    /// <summary>The info-text marker every probe frame starts with.</summary>
    public const string Marker = "PTXD";

    /// <summary>The destination address probe frames are sent to.</summary>
    public const string Destination = "TUNE";

    /// <summary>Info-field length — short on purpose (the preamble, not the frame,
    /// is the thing under test) but long enough to exercise the modem.</summary>
    public const int InfoLength = 40;

    /// <summary>Build probe frame <paramref name="index"/> of <paramref name="count"/>
    /// for step <paramref name="stepTag"/>.</summary>
    public static Ax25Frame BuildFrame(Callsign source, int stepTag, int index, int count)
    {
        string text = string.Create(
            CultureInfo.InvariantCulture, $"{Marker} s{stepTag} {index}/{count} ");
        return Ax25Frame.Ui(
            destination: new Callsign(Destination),
            source: source,
            info: Encoding.ASCII.GetBytes(text.PadRight(InfoLength, '.')));
    }

    /// <summary>
    /// The TXDELAY-change throwaway frame. The NinoTNC applies a changed KISS parameter
    /// from the <b>SECOND</b> frame after the command (bench-observed), so one of these
    /// is transmitted — and discarded — after every TXDELAY set, before anything is
    /// probed. Its info text carries no <see cref="Marker"/>, so the meter can never
    /// count it as a probe.
    /// </summary>
    public static Ax25Frame BuildSettleFrame(Callsign source, int txDelayMs) =>
        Ax25Frame.Ui(
            destination: new Callsign("SETTLE"),
            source: source,
            info: Encoding.ASCII.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"PSETTLE txdelay {txDelayMs} throwaway ")
                    .PadRight(InfoLength, '.')));

    /// <summary>True when an inbound KISS frame is a probe frame for step
    /// <paramref name="stepTag"/> (a data frame whose payload carries the marker and
    /// the tag).</summary>
    public static bool IsProbeFrame(KissFrame frame, int stepTag)
    {
        if (frame.Command != KissCommand.Data)
        {
            return false;
        }
        string token = string.Create(CultureInfo.InvariantCulture, $"{Marker} s{stepTag} ");
        return frame.Payload.AsSpan().IndexOf(Encoding.ASCII.GetBytes(token)) >= 0;
    }
}

/// <summary>
/// The station-side actions a TXDELAY-minimisation end must be able to perform on its
/// own hardware. <see cref="NinoTncTxDelayMinStation"/> is the live NinoTNC (+ optional
/// Tait CCDI) implementation; tests supply fakes. The coordinator uses the pin/set/probe
/// members; the meter uses <see cref="BeginProbeCount"/>.
/// </summary>
public interface ITxDelayMinStation
{
    /// <summary>Pin the CSMA channel-access parameters for deterministic keying
    /// (persistence 255 / slottime 0 on a NinoTNC): with the default p-persistence the
    /// TNC defers each transmission a random number of ~100 ms slots, which would swamp
    /// the TXDELAY step under test. Paired with
    /// <see cref="RestoreChannelAccessAsync"/> on every exit path.</summary>
    /// <exception cref="TxDelayMinException">The pin could not be applied.</exception>
    Task PinChannelAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>Restore polite channel-access defaults (KISS has no read-back, so
    /// "restore" means the conventional persistence 63 / slottime 10). Must be
    /// best-effort tolerant — it runs on failure paths.</summary>
    Task RestoreChannelAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>Set the modem's TXDELAY to <paramref name="txDelayMs"/> <b>including
    /// whatever settling the modem needs before the value is live</b> — on a NinoTNC a
    /// changed KISS parameter applies from the SECOND frame after the command, so the
    /// implementation transmits and discards a settle frame, then lets the transmitter
    /// unkey, before returning.</summary>
    /// <exception cref="TxDelayMinException">The set/settle failed.</exception>
    Task SetTxDelayAsync(int txDelayMs, CancellationToken cancellationToken = default);

    /// <summary>Transmit <paramref name="count"/> probe frames tagged
    /// <paramref name="stepTag"/> as <paramref name="count"/> SEPARATE keyings with
    /// unkey gaps between them (back-to-back sends chain into ONE keying train with a
    /// single preamble — which would measure nothing). Returns sender-side TX stats;
    /// individual probe failures are not fatal (the meter's decode count is the
    /// verdict).</summary>
    Task<ModeProbeTxStats> TransmitProbesAsync(int stepTag, int count, CancellationToken cancellationToken = default);

    /// <summary>Start counting inbound probe frames tagged <paramref name="stepTag"/>
    /// (and, when a carrier-sensing radio is attached, measuring each probe's pre-data
    /// carrier time). Dispose to stop counting.</summary>
    ITxDelayProbeCount BeginProbeCount(int stepTag);
}

/// <summary>A live probe counter for one sweep step (see
/// <see cref="ITxDelayMinStation.BeginProbeCount"/>).</summary>
public interface ITxDelayProbeCount : IDisposable
{
    /// <summary>Probe frames decoded so far.</summary>
    int Count { get; }

    /// <summary>Median measured carrier-rise→first-data lead across the decoded probes,
    /// in ms — the direct as-heard measurement of the coordinator's effective TXDELAY
    /// (plus a small constant rig overhead). <c>null</c> when no carrier-sensing radio
    /// is attached, or no probe could be attributed to a carrier window.</summary>
    double? MedianPreDataCarrierMs { get; }
}

/// <summary>A station-side TXDELAY-minimisation failure (the KISS set failed, the pin
/// would not apply…).</summary>
public sealed class TxDelayMinException : Exception
{
    /// <summary>Create with a message describing the failure.</summary>
    public TxDelayMinException(string message)
        : base(message)
    {
    }

    /// <summary>Create with a message and the underlying failure.</summary>
    public TxDelayMinException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Parameterless form (framework convention).</summary>
    public TxDelayMinException()
    {
    }
}

/// <summary>Tunables shared by <see cref="TxDelayMinimizer"/> and
/// <see cref="TxDelayMinResponder"/>. The timing defaults encode the bench-measured SDM
/// cadence and the TM8110 auto-ack wedge guard, mirroring <see cref="ModeCoordOptions"/>.</summary>
public sealed record TxDelayMinOptions
{
    /// <summary>The TXDELAY the sweep starts from — the value the modem is believed to
    /// be configured with (KISS has no read-back). Also what every exit path restores.
    /// Default 500 ms.</summary>
    public int StartTxDelayMs { get; init; } = 500;

    /// <summary>The sweep decrement. Default 40 ms.</summary>
    public int StepMs { get; init; } = 40;

    /// <summary>The sweep floor — never command below this (a 0 TXDELAY keys data
    /// straight into the PA's rise time). Default 20 ms.</summary>
    public int MinTxDelayMs { get; init; } = 20;

    /// <summary>Separately-keyed probes per step. Default 5.</summary>
    public int ProbesPerStep { get; init; } = 5;

    /// <summary>Margin, in steps, added to the knee for the recommendation (the larger
    /// of this many steps and <see cref="MarginFraction"/> of the knee wins). Default 2.</summary>
    public int MarginSteps { get; init; } = 2;

    /// <summary>Margin, as a fraction of the knee, added for the recommendation (the
    /// larger of this and <see cref="MarginSteps"/> wins). Default 0.25.</summary>
    public double MarginFraction { get; init; } = 0.25;

    /// <summary>Coordinator: wait for confirm/reject after the propose. Default 45 s
    /// (covers the meter's post-receive guard + a full SDM retry cycle).</summary>
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Coordinator: wait for the meter's report after announcing
    /// <c>sent</c>. Default 90 s.</summary>
    public TimeSpan ReportTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Minimum gap between a side-channel telegram exchange and keying the
    /// TNC: the coordination radio's SDM auto-ack must clear first (the TM8110
    /// auto-ack wedge — see <see cref="SdmTuningLink"/>). Default 2.5 s.</summary>
    public TimeSpan PreKeyDelay { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Meter: after a <c>sent</c> announcement arrives, how long to keep the
    /// probe window open before snapshotting the counter (decode + serial trail behind
    /// the sender's TX-complete). Default 1.5 s.</summary>
    public TimeSpan ArrivalGrace { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Meter: exit the loop when nothing has been heard for this long — a
    /// silent coordinator must never strand the meter process. The meter changes
    /// nothing locally, so this is an exit, not a revert. Default 10 min.</summary>
    public TimeSpan MeterIdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>How a TXDELAY sweep ended.</summary>
public enum TxDelaySweepOutcome
{
    /// <summary>A knee was found (or the floor was reached still solid) and a
    /// recommendation computed.</summary>
    Complete,

    /// <summary>The very first step (the configured/current TXDELAY) was not solid —
    /// the link itself is marginal, so no step-down carries information. Fix the link
    /// (deviation, SNR) before minimising TXDELAY.</summary>
    NotSolidAtStart,

    /// <summary>The meter rejected the proposal; nothing was transmitted.</summary>
    Rejected,

    /// <summary>No confirm arrived; nothing was transmitted.</summary>
    ConfirmTimeout,

    /// <summary>The side-channel coordination failed mid-sweep; the original TXDELAY +
    /// channel-access params were restored.</summary>
    LinkFailed,

    /// <summary>A local station operation failed mid-sweep; restored.</summary>
    StationFailed,
}

/// <summary>One step of a TXDELAY sweep.</summary>
/// <param name="TxDelayMs">The commanded TXDELAY for this step, in ms.</param>
/// <param name="Decoded">Probes the meter decoded.</param>
/// <param name="Probes">Probes transmitted (separate keyings).</param>
/// <param name="MeanTxMs">Sender-side mean send→TX-complete latency in ms, <c>null</c>
/// when no TX completion was confirmed.</param>
/// <param name="MedianPreDataCarrierMs">The meter's median measured carrier-rise→data
/// lead in ms — the as-heard effective TXDELAY (+ constant rig overhead), the sweep's
/// self-evidencing cross-check. <c>null</c> without a carrier-sensing radio at the
/// meter.</param>
public sealed record TxDelaySweepStep(
    int TxDelayMs,
    int Decoded,
    int Probes,
    double? MeanTxMs,
    double? MedianPreDataCarrierMs)
{
    /// <summary>Dead / marginal / solid, as in <see cref="ModeSurveyVerdict"/>.</summary>
    public ModeSurveyVerdict Verdict =>
        Decoded <= 0 ? ModeSurveyVerdict.Dead
        : Decoded < Probes ? ModeSurveyVerdict.Marginal
        : ModeSurveyVerdict.Solid;
}

/// <summary>The full record of one TXDELAY sweep.</summary>
public sealed record TxDelaySweepResult
{
    /// <summary>How the sweep ended.</summary>
    public required TxDelaySweepOutcome Outcome { get; init; }

    /// <summary>The steps actually measured, in sweep (descending-TXDELAY) order.</summary>
    public required IReadOnlyList<TxDelaySweepStep> Steps { get; init; }

    /// <summary>The knee: the lowest TXDELAY with a full decode where the step below
    /// dropped probes (or the floor, when the floor was still solid). <c>null</c> when
    /// the sweep failed before finding one.</summary>
    public int? KneeMs { get; init; }

    /// <summary>The recommendation: knee + margin, rounded up to the KISS 10 ms unit.
    /// <c>null</c> when there is no knee.</summary>
    public int? RecommendedMs { get; init; }

    /// <summary>True when the sweep hit <see cref="TxDelayMinOptions.MinTxDelayMs"/>
    /// still decoding cleanly — the true knee is at or below the floor.</summary>
    public bool FloorReached { get; init; }

    /// <summary>Whether the exit path restored the original TXDELAY and channel-access
    /// params (false = check the rig by hand).</summary>
    public bool Restored { get; init; }

    /// <summary>The TXDELAY the modem was restored to, in ms.</summary>
    public int RestoredToMs { get; init; }

    /// <summary>Free-text detail for the human (failure stage, reasons).</summary>
    public string? Detail { get; init; }

    /// <summary>True when the sweep produced a recommendation.</summary>
    public bool Success => Outcome == TxDelaySweepOutcome.Complete && RecommendedMs is not null;
}

/// <summary>The outcome of an explicit APPLY (verify at the recommended TXDELAY).</summary>
/// <param name="TxDelayMs">The TXDELAY that was applied and verified, in ms.</param>
/// <param name="Decoded">Verify probes the meter decoded.</param>
/// <param name="Probes">Verify probes transmitted.</param>
/// <param name="MedianPreDataCarrierMs">The meter's as-heard pre-data carrier median
/// during the verify, in ms (<c>null</c> without a carrier-sensing radio).</param>
/// <param name="Verified">True when every verify probe decoded — the applied value
/// stands. False = the modem was restored to the sweep's starting TXDELAY.</param>
/// <param name="Detail">Free-text detail (why a verify failed / could not run).</param>
public sealed record TxDelayApplyResult(
    int TxDelayMs,
    int Decoded,
    int Probes,
    double? MedianPreDataCarrierMs,
    bool Verified,
    string? Detail = null);

/// <summary>Recommendation arithmetic + result-table rendering for the
/// TXDELAY-minimisation CLI and node surfaces.</summary>
public static class TxDelayMinReport
{
    /// <summary>
    /// The recommendation for a knee: knee + margin, where margin is the larger of
    /// <see cref="TxDelayMinOptions.MarginSteps"/> sweep steps and
    /// <see cref="TxDelayMinOptions.MarginFraction"/> of the knee, rounded UP to the
    /// KISS 10 ms unit (the wire encoding cannot express finer).
    /// </summary>
    public static int Recommend(int kneeMs, TxDelayMinOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int margin = Math.Max(
            options.MarginSteps * options.StepMs,
            (int)Math.Ceiling(kneeMs * options.MarginFraction));
        return RoundUpToTen(kneeMs + margin);
    }

    /// <summary>Round up to the KISS TXDELAY wire unit (10 ms).</summary>
    public static int RoundUpToTen(int ms) => checked((ms + 9) / 10 * 10);

    /// <summary>Render the sweep as a Markdown table plus the verdict lines.</summary>
    public static string RenderMarkdown(TxDelaySweepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sb = new StringBuilder();
        sb.AppendLine("| TXDELAY | Decoded | Mean TX latency | Heard pre-data | Verdict |");
        sb.AppendLine("|--------:|--------:|----------------:|---------------:|---------|");
        foreach (var step in result.Steps)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {step.TxDelayMs} ms ");
            sb.Append(CultureInfo.InvariantCulture, $"| {step.Decoded}/{step.Probes} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {FormatMs(step.MeanTxMs)} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {FormatMs(step.MedianPreDataCarrierMs)} ");
            sb.Append(CultureInfo.InvariantCulture, $"| {ModeSurvey.DescribeVerdict(step.Verdict)} |");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine(Describe(result));
        return sb.ToString();
    }

    /// <summary>One-line human verdict for a sweep result.</summary>
    public static string Describe(TxDelaySweepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            TxDelaySweepOutcome.Complete when result.FloorReached =>
                string.Create(CultureInfo.InvariantCulture,
                    $"floor reached still solid — knee ≤ {result.KneeMs} ms; recommend {result.RecommendedMs} ms (knee + margin)"),
            TxDelaySweepOutcome.Complete =>
                string.Create(CultureInfo.InvariantCulture,
                    $"knee {result.KneeMs} ms; recommend {result.RecommendedMs} ms (knee + margin)"),
            TxDelaySweepOutcome.NotSolidAtStart =>
                "NOT SOLID AT START — the link drops probes at the current TXDELAY; fix the link (deviation/SNR) before minimising",
            TxDelaySweepOutcome.Rejected => $"meter rejected the sweep ({result.Detail ?? "no reason"})",
            TxDelaySweepOutcome.ConfirmTimeout => "no confirm from the meter — is the peer process running?",
            TxDelaySweepOutcome.LinkFailed => $"COORDINATION LOST mid-sweep ({result.Detail}) — " + RestoreNote(result),
            TxDelaySweepOutcome.StationFailed => $"STATION FAILED mid-sweep ({result.Detail}) — " + RestoreNote(result),
            _ => result.Outcome.ToString(),
        };
    }

    private static string RestoreNote(TxDelaySweepResult result) => result.Restored
        ? string.Create(CultureInfo.InvariantCulture, $"restored TXDELAY {result.RestoredToMs} ms + channel access")
        : "RESTORE FAILED, check the rig by hand";

    private static string FormatMs(double? ms) =>
        ms is { } v ? v.ToString("0", CultureInfo.InvariantCulture) + " ms" : "n/a";
}
