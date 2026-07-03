using System.Globalization;

namespace Packet.Tune.Core;

/// <summary>Advice for the human at the TX-DEV pot.</summary>
public enum TuningAdvice
{
    /// <summary><c>UP</c> — turn the deviation up (frames are being missed
    /// without evidence of clipping — likely too quiet to decode).</summary>
    Up,

    /// <summary><c>DN</c> — turn the deviation down (ADC clipping, or bit
    /// errors on an otherwise strong signal).</summary>
    Down,

    /// <summary><c>OK</c> — decode rate is solid and error correction is
    /// (near-)idle: leave the pot alone.</summary>
    Ok,

    /// <summary><c>SW</c> — no decode at all: nothing decoded and no
    /// clipping, so the burst carries no directional information (far too
    /// little deviation and far too much both decode zero frames — as does a
    /// dead RF path, which no pot position fixes). Sweep the pot across its
    /// range until frames start decoding, then the directional advice takes
    /// over. Peers that predate this state parse the token as unknown advice
    /// (<see cref="DeviationAdvisor.FromWire"/> → <c>null</c>) — never as a
    /// false directional verdict.</summary>
    Sweep,
}

/// <summary>
/// Turns a burst's <see cref="MeterReport"/> (plus the previous burst, for
/// trend) into <see cref="TuningAdvice"/>. Heuristic, and documented as such:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Lost-ADC samples during the burst = the RX audio clipped the
///     meter TNC's ADC — gross over-deviation, always <c>DN</c> (clipping is
///     directional evidence even when nothing decoded).</item>
///   <item>Zero decodes with no clipping = <c>SW</c>: a fully-dead burst has
///     no direction in it (too quiet, too loud, and no-path all read 0/n —
///     bench-observed on the pre-R2-retap rig, where every GFSK cell was 0/5
///     at any pot position and a directional <c>UP</c> would have been a
///     lie). Sweep the pot until decodes appear.</item>
///   <item>Full-ish decode (≥90%) with little FEC repair work = <c>OK</c>.</item>
///   <item>Decode failures / heavy FEC with a healthy RF path (that is what
///     the CCDI RSSI check is for) and <em>no</em> clipping most often mean
///     the audio is too quiet to open the decoder = <c>UP</c>; but if the FEC
///     byte count is climbing burst-on-burst while decode holds, the signal
///     is degrading at the loud end = <c>DN</c>.</item>
/// </list>
/// FEC-corrected bytes (register 11) only count in IL2P modes — prefer mode
/// 7 (1200 AFSK IL2P+CRC) for tuning sessions so the FEC signal exists. In
/// plain-AX.25 modes the advisor works from decode rate + clipping alone.
/// <para>
/// When the meter runs the GETRSSI fast path (NinoTNC <b>firmware 3.41-era
/// only — REMOVED in 3.44</b>), <see cref="MeterReport.AudioLevelDb"/>
/// carries a continuous RX-audio level. <see cref="DescribeLevel"/> turns it
/// into mid-plateau guidance (level, quieting vs idle, burst-on-burst
/// trend); it never changes the <see cref="Advise"/> verdict — the
/// decode/clip cliffs remain the authoritative edge detection.
/// </para>
/// </remarks>
public static class DeviationAdvisor
{
    /// <summary>Decode rate at/above which a burst counts as "solid".</summary>
    public const double SolidDecodeRate = 0.9;

    /// <summary>FEC-corrected bytes per decoded frame above which the FEC is
    /// considered to be working hard.</summary>
    public const double BusyFecBytesPerFrame = 8;

    /// <summary>Audio-level movement (dB, either way) within which
    /// burst-on-burst level is described as "steady".</summary>
    public const double LevelSteadyToleranceDb = 1.5;

    /// <summary>Advise from the current burst (and the previous one, for the FEC trend).</summary>
    public static TuningAdvice Advise(MeterReport current, MeterReport? previous = null)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.LostAdcSamplesDelta is > 0)
        {
            return TuningAdvice.Down;
        }

        // Nothing decoded and no clipping: no directional information at
        // all — sweep, don't guess a direction.
        if (current.DecodedFrames == 0)
        {
            return TuningAdvice.Sweep;
        }

        double fecPerFrame = current.FecCorrectedBytesDelta is { } fec && current.DecodedFrames > 0
            ? (double)fec / current.DecodedFrames
            : 0;
        bool fecBusy = fecPerFrame > BusyFecBytesPerFrame;

        if (current.DecodeRate >= SolidDecodeRate && !fecBusy)
        {
            return TuningAdvice.Ok;
        }

        // Struggling. Rising FEC while decode holds = degrading at the loud
        // end; otherwise assume too quiet.
        if (fecBusy && previous?.FecCorrectedBytesDelta is { } prevFec &&
            current.FecCorrectedBytesDelta is { } curFec && curFec > prevFec &&
            current.DecodeRate >= previous.DecodeRate)
        {
            return TuningAdvice.Down;
        }
        return TuningAdvice.Up;
    }

    /// <summary>
    /// Human guidance from the optional RX-audio level (GETRSSI — NinoTNC
    /// <b>firmware 3.41-era only; REMOVED in 3.44</b>): the level during the
    /// burst plus, when the meter's idle baseline is known, the quieting
    /// (idle − level; a carrier quiets the demodulated audio, so bigger =
    /// stronger signal into the modem), plus the burst-on-burst trend when a
    /// previous levelled report exists. Returns <c>null</c> when
    /// <paramref name="current"/> carries no level. Enrichment only: the
    /// <see cref="Advise"/> verdict ignores the level entirely — decode and
    /// clipping cliffs decide UP/DN/OK; the level shows where inside the
    /// plateau the pot sits and which way it is moving.
    /// </summary>
    /// <param name="current">The burst's report.</param>
    /// <param name="previous">The previous burst's report, for the level trend (null = none).</param>
    /// <param name="idleLevelDb">The meter's idle-channel baseline, when known (null = omit quieting).</param>
    public static string? DescribeLevel(MeterReport current, MeterReport? previous = null, double? idleLevelDb = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.AudioLevelDb is not { } level)
        {
            return null;
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"level {level:0.0} dB");
        if (idleLevelDb is { } idle)
        {
            text += string.Create(CultureInfo.InvariantCulture, $", {idle - level:0.0} dB quieting");
        }
        if (previous?.AudioLevelDb is { } prev)
        {
            double delta = level - prev;
            text += Math.Abs(delta) <= LevelSteadyToleranceDb
                ? ", level steady"
                : string.Create(CultureInfo.InvariantCulture, $", level {delta:+0.0;-0.0} dB vs last burst");
        }
        return text;
    }

    /// <summary>The human phrase for an advice value — what the operator at
    /// the pot should actually do.</summary>
    public static string Describe(TuningAdvice advice) => advice switch
    {
        TuningAdvice.Up => "turn the deviation up",
        TuningAdvice.Down => "turn the deviation down",
        TuningAdvice.Ok => "leave the pot alone",
        TuningAdvice.Sweep => "no decode — sweep the pot",
        _ => throw new ArgumentOutOfRangeException(nameof(advice), advice, "unknown advice"),
    };

    /// <summary>The wire token (<c>UP</c>/<c>DN</c>/<c>OK</c>/<c>SW</c>) for
    /// an advice value. The <c>AD</c> telegram args stay backward-compatible:
    /// the original tokens are unchanged, and a peer that predates <c>SW</c>
    /// parses it via <see cref="FromWire"/> as unknown (<c>null</c>) rather
    /// than misreading a direction.</summary>
    public static string ToWire(TuningAdvice advice) => advice switch
    {
        TuningAdvice.Up => "UP",
        TuningAdvice.Down => "DN",
        TuningAdvice.Ok => "OK",
        TuningAdvice.Sweep => "SW",
        _ => throw new ArgumentOutOfRangeException(nameof(advice), advice, "unknown advice"),
    };

    /// <summary>Parse a wire token back to advice (<c>null</c> = unknown token).</summary>
    public static TuningAdvice? FromWire(string? wire) => wire switch
    {
        "UP" => TuningAdvice.Up,
        "DN" => TuningAdvice.Down,
        "OK" => TuningAdvice.Ok,
        "SW" => TuningAdvice.Sweep,
        _ => null,
    };
}
