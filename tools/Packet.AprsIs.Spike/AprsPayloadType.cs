namespace Packet.AprsIs.Spike;

/// <summary>
/// Classifies an APRS payload by its first-byte type indicator
/// (APRS101.pdf §5, plus community corrections from
/// <c>how.aprs.works/aprs101-pdf-is-obsolete</c>).
/// </summary>
/// <remarks>
/// APRS uses the very first byte of the information field as a Data Type
/// Identifier (DTI). The corpus passes us raw bytes - we map each to a
/// human-readable label so the analyser can bucket the corpus content.
///
/// Designed for *coverage stats*, not for actual decoding. The labels
/// reflect what the spec says the byte *means*; we don't validate the
/// rest of the payload's structure here.
/// </remarks>
public static class AprsPayloadType
{
    /// <summary>
    /// Map an APRS info field to a coarse payload-type label.
    /// </summary>
    public static string Classify(ReadOnlySpan<byte> info)
    {
        if (info.Length == 0)
        {
            return "empty";
        }

        byte b = info[0];
        return b switch
        {
            // ─── Position reports ────────────────────────────────────
            (byte)'!' => "position_no_ts_no_msg",      // ! - no timestamp, no msg
            (byte)'=' => "position_no_ts_msg",         // = - no timestamp, with msg
            (byte)'/' => "position_ts_no_msg",         // / - with timestamp, no msg
            (byte)'@' => "position_ts_msg",            // @ - with timestamp, with msg

            // ─── Mic-E ────────────────────────────────────────────────
            // Current Mic-E uses 0x60 (`) as DTI; legacy uses 0x27 (').
            // Other "letter / digit" first-byte data is also Mic-E proper
            // but we only catch the DTI byte here.
            (byte)'`' => "mic_e_current",
            (byte)'\'' => "mic_e_old",

            // ─── Other reports ───────────────────────────────────────
            (byte)';' => "object",
            (byte)')' => "item",
            (byte)':' => "message",
            (byte)'>' => "status",
            (byte)'<' => "station_capabilities",
            (byte)'?' => "query",
            (byte)'T' => "telemetry",
            (byte)'_' => "weather_positionless",
            (byte)'#' => "weather_peet_bros_complete",
            (byte)'*' => "weather_peet_bros_partial",
            (byte)'$' => "raw_gps_or_ultimeter",
            (byte)'%' => "agrelo_dfjr",
            (byte)'[' => "grid_beacon",
            (byte)'{' => "user_defined",
            (byte)'}' => "third_party",
            (byte)'(' => "dtmf",
            (byte)',' => "invalid_or_test",

            // Printable but unassigned - keep the raw char so we can
            // see what's actually appearing.
            >= 0x20 and <= 0x7E => $"other_printable_{(char)b}",

            // Non-printable first byte - Mic-E proper, weather burst,
            // binary positions, or garbage. Bucket by high nibble for now.
            _ => $"non_printable_{b:x2}",
        };
    }
}
