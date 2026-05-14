namespace Packet.Aprs;

/// <summary>
/// Per-call configuration for the APRS payload decoders. Each pragmatic
/// accommodation beyond strict APRS101 compliance is a named,
/// individually-toggleable flag — see
/// <c>docs/strict-vs-pragmatic-audit.md</c> for the inventory and
/// rationale.
/// </summary>
/// <remarks>
/// <para>
/// Spec philosophy: Packet.NET is spec-compliant by default. The
/// parameterless decoder overloads use <see cref="Lenient"/> (kitchen-
/// sink accept-everything mode) to preserve current behaviour without
/// breaking callers. Callers who want strict spec adherence should
/// pass <see cref="Strict"/>; callers consuming a specific source
/// can pick that source's named preset (<see cref="Direwolf"/>,
/// <see cref="AprsIs"/>).
/// </para>
/// <para>
/// When you discover a new real-world quirk, add a named flag here
/// (defaulted to keep current behaviour), enable it in the preset(s)
/// it belongs to, and update the audit doc. Do not silently widen an
/// existing decoder to accept new shapes.
/// </para>
/// </remarks>
public sealed record AprsParseOptions
{
    /// <summary>
    /// Accept status-text (<c>&gt;</c> DTI) bodies containing non-ASCII
    /// bytes. The decoder converts via UTF-8 with the replacement
    /// character for invalid sequences.
    /// </summary>
    /// <remarks>
    /// Strict APRS101 §16: "The text may contain any printable ASCII
    /// characters except <c>|</c> or <c>~</c>" — i.e. codes 33–126
    /// only. Driver: Chinese-station beacons in the APRS-IS firehose
    /// (and similar UTF-8 emitters).
    /// </remarks>
    public bool AllowNonAsciiStatusText { get; init; } = true;

    /// <summary>
    /// Accept telemetry analog values that aren't 3-digit zero-padded
    /// integers in the 000–255 range. Today the decoder parses each
    /// channel as <c>double</c>, accepting variable-width integers
    /// (<c>0</c> instead of <c>000</c>) and floats (<c>3.2</c>).
    /// </summary>
    /// <remarks>
    /// Strict APRS101 §13: "five 8-bit unsigned analog data values
    /// (expressed as 3-digit decimal numbers in the range 000–255)".
    /// Driver: real-world telemetry from SvxLink RepeaterLogic,
    /// SimplexLogic, various BPQ-attached weather stations etc. — at
    /// least 30 % of corpus telemetry uses non-conforming widths or
    /// floating-point.
    /// </remarks>
    public bool AllowNonIntegerTelemetry { get; init; } = true;

    /// <summary>
    /// Accept the legacy Mic-E data-type identifiers <c>0x1C</c>
    /// (current GPS data, Rev. 0 beta) and <c>0x1D</c> (old GPS data,
    /// Rev. 0 beta) in addition to <c>`</c> (0x60) and <c>'</c> (0x27).
    /// </summary>
    /// <remarks>
    /// Strict APRS101 §10 mentions <c>0x1C</c> / <c>0x1D</c> as
    /// "Rev. 0 beta units only" — i.e. effectively deprecated. Real
    /// early Kenwood TM-D700 firmware still emits them.
    /// </remarks>
    public bool AllowMicELegacyDtiBytes { get; init; } = true;

    /// <summary>Strict APRS101 — all pragmatic accommodations disabled.</summary>
    public static AprsParseOptions Strict { get; } = new()
    {
        AllowNonAsciiStatusText = false,
        AllowNonIntegerTelemetry = false,
        AllowMicELegacyDtiBytes = false,
    };

    /// <summary>
    /// Accept-everything mode. All currently known pragmatic flags
    /// enabled. Used by the parameterless decoder overloads to
    /// preserve historical behaviour.
    /// </summary>
    public static AprsParseOptions Lenient { get; } = new();

    /// <summary>
    /// Direwolf's APRS-decoder tolerances. Today identical to
    /// <see cref="Lenient"/>; may diverge as we map out direwolf's
    /// specific behaviour (e.g. its compressed-position fallback when
    /// timestamp parse fails).
    /// </summary>
    public static AprsParseOptions Direwolf { get; } = Lenient;

    /// <summary>
    /// APRS-IS firehose preset — the wild-internet aggregate of every
    /// station's emitter quirks. Today identical to <see cref="Lenient"/>.
    /// </summary>
    public static AprsParseOptions AprsIs { get; } = Lenient;
}
