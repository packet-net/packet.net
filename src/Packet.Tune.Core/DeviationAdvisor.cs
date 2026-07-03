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
}

/// <summary>
/// Turns a burst's <see cref="MeterReport"/> (plus the previous burst, for
/// trend) into <see cref="TuningAdvice"/>. Heuristic, and documented as such:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Lost-ADC samples during the burst = the RX audio clipped the
///     meter TNC's ADC — gross over-deviation, always <c>DN</c>.</item>
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
/// </remarks>
public static class DeviationAdvisor
{
    /// <summary>Decode rate at/above which a burst counts as "solid".</summary>
    public const double SolidDecodeRate = 0.9;

    /// <summary>FEC-corrected bytes per decoded frame above which the FEC is
    /// considered to be working hard.</summary>
    public const double BusyFecBytesPerFrame = 8;

    /// <summary>Advise from the current burst (and the previous one, for the FEC trend).</summary>
    public static TuningAdvice Advise(MeterReport current, MeterReport? previous = null)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (current.LostAdcSamplesDelta is > 0)
        {
            return TuningAdvice.Down;
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

    /// <summary>The wire token (<c>UP</c>/<c>DN</c>/<c>OK</c>) for an advice value.</summary>
    public static string ToWire(TuningAdvice advice) => advice switch
    {
        TuningAdvice.Up => "UP",
        TuningAdvice.Down => "DN",
        TuningAdvice.Ok => "OK",
        _ => throw new ArgumentOutOfRangeException(nameof(advice), advice, "unknown advice"),
    };

    /// <summary>Parse a wire token back to advice (<c>null</c> = unknown token).</summary>
    public static TuningAdvice? FromWire(string? wire) => wire switch
    {
        "UP" => TuningAdvice.Up,
        "DN" => TuningAdvice.Down,
        "OK" => TuningAdvice.Ok,
        _ => null,
    };
}
