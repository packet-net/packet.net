using Packet.Core;

namespace Packet.NetRom.Wire;

/// <summary>
/// Decoders for the two callsign/text encodings a NET/ROM NODES broadcast uses.
/// NET/ROM rides on AX.25, so a <em>callsign</em> field is the familiar 7-octet
/// AX.25 shifted form (6 chars left-shifted by one, plus the SSID byte) — but a
/// node's 6-character <em>alias / mnemonic</em> is plain space-padded ASCII, not
/// shifted, and has no SSID octet.
/// </summary>
/// <remarks>
/// These are deliberately small static helpers rather than a type: the parsed
/// results are ordinary <see cref="Callsign"/>s (for callsign fields) and
/// trimmed strings (for alias fields), so they flow straight into the routing
/// model without a wrapper. The AX.25 7-octet decode delegates to
/// <see cref="Ax25Address.Read(System.ReadOnlySpan{byte}, Ax25ParseOptions)"/> —
/// the same shifted-callsign codec the frame layer uses — so there is one source
/// of truth for the shift/SSID/EOA semantics.
/// </remarks>
public static class NetRomCallsign
{
    /// <summary>Octets occupied by an AX.25 shifted callsign field (with SSID byte).</summary>
    public const int ShiftedLength = Ax25Address.EncodedLength;   // 7

    /// <summary>Octets occupied by a NET/ROM alias / mnemonic field (plain ASCII, no SSID).</summary>
    public const int AliasLength = 6;

    /// <summary>
    /// Decode a 7-octet AX.25 shifted callsign field (callsign chars in the upper
    /// 7 bits, SSID + flags in the 7th octet). The end-of-address / command bits
    /// in the SSID octet are read but not significant here — inside a NODES entry
    /// these fields are payload, not an AX.25 address chain.
    /// </summary>
    /// <param name="source">At least <see cref="ShiftedLength"/> octets.</param>
    /// <param name="callsign">The decoded callsign (base + SSID) on success.</param>
    /// <returns><c>true</c> if the field decoded to a syntactically valid callsign.</returns>
    public static bool TryReadShifted(ReadOnlySpan<byte> source, out Callsign callsign)
    {
        callsign = default;
        if (source.Length < ShiftedLength)
        {
            return false;
        }

        try
        {
            // Allow an all-space ("empty") base: some nodes pad an absent
            // best-neighbour slot, and Ax25Address.Read otherwise throws. The
            // routing-table builder decides what an empty callsign means; the
            // codec just decodes faithfully.
            var addr = Ax25Address.Read(source[..ShiftedLength], Ax25ParseOptions.Lenient);
            callsign = addr.Callsign;
            return true;
        }
        catch (ArgumentException)
        {
            // Non-A-Z/0-9 char, or a non-space octet after padding — not a
            // decodable callsign field.
            return false;
        }
    }

    /// <summary>
    /// Decode a 6-octet NET/ROM alias / mnemonic field: plain ASCII, space-padded
    /// on the right, no shift and no SSID. Trailing spaces are stripped; an
    /// all-space field yields the empty string. Non-printable octets are dropped
    /// (a noisy link can corrupt a byte) so the result is always a clean display
    /// string.
    /// </summary>
    /// <param name="source">At least <see cref="AliasLength"/> octets.</param>
    /// <returns>The trimmed alias, or <see cref="string.Empty"/> if blank / too short.</returns>
    public static string ReadAlias(ReadOnlySpan<byte> source)
    {
        if (source.Length < AliasLength)
        {
            return string.Empty;
        }

        Span<char> chars = stackalloc char[AliasLength];
        int len = 0;
        for (int i = 0; i < AliasLength; i++)
        {
            char c = (char)source[i];
            // Printable ASCII only (0x20..0x7E). Anything else (a corrupted or
            // high-bit octet) is skipped rather than rendered as mojibake.
            if (c is >= ' ' and <= '~')
            {
                chars[len++] = c;
            }
        }

        return new string(chars[..len]).TrimEnd();
    }
}
