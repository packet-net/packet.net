using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;

namespace Packet.Mcp.Decoding;

/// <summary>
/// The pure engine behind the <c>decode_frame</c> tool: hex string → a
/// <see cref="DecodedFrame"/>. Stateless, node-independent — it only consumes
/// the parser libraries. KISS-unwraps when the input is KISS-framed, then
/// decodes the AX.25 body. See docs/mcp-design.md.
/// </summary>
public static class FrameDecoder
{
    /// <summary>How the input bytes are framed.</summary>
    public enum Framing
    {
        /// <summary>Detect from the bytes (leading FEND ⇒ KISS, else raw).</summary>
        Auto,
        /// <summary>Bare AX.25 in KISS form (no HDLC flags, no FCS).</summary>
        Raw,
        /// <summary>A full KISS frame (FEND … command-byte … FEND).</summary>
        Kiss,
    }

    /// <summary>
    /// Decode a hex string into a <see cref="DecodedFrame"/>. Throws
    /// <see cref="FormatException"/> on un-parseable hex and
    /// <see cref="ArgumentException"/> when the bytes aren't a valid AX.25 frame.
    /// </summary>
    /// <param name="hex">Frame bytes as hex. Whitespace, <c>0x</c>, <c>:</c> and <c>,</c> separators are tolerated.</param>
    /// <param name="framing">Framing of the input; <see cref="Framing.Auto"/> detects it.</param>
    /// <param name="extended">Decode I/S frames as modulo-128 (2-octet control). Width isn't derivable from the bytes alone (Fig 4.1b), so the caller says.</param>
    public static DecodedFrame Decode(string hex, Framing framing = Framing.Auto, bool extended = false)
    {
        ArgumentNullException.ThrowIfNull(hex);
        byte[] bytes = ParseHex(hex);
        if (bytes.Length == 0)
        {
            throw new FormatException("no bytes to decode.");
        }

        var (ax25Bytes, kissPort, usedFraming) = Unwrap(bytes, framing);

        if (!Ax25Frame.TryParse(ax25Bytes, Ax25ParseOptions.Lenient, extended, out var frame))
        {
            throw new ArgumentException(
                $"the {ax25Bytes.Length}-byte body is not a valid AX.25 frame.", nameof(hex));
        }

        var (frameClass, frameType, carriesNr, carriesNs) = Classify(frame);

        return new DecodedFrame(
            Framing: usedFraming == Framing.Kiss ? "kiss" : "raw",
            KissPort: kissPort,
            Source: frame.Source.Callsign.ToString(),
            Destination: frame.Destination.Callsign.ToString(),
            Path: frame.Digipeaters
                .Select(d => d.CrhBit ? d.Callsign.ToString() + "*" : d.Callsign.ToString())
                .ToList(),
            CommandResponse: frame.IsCommand ? "command" : frame.IsResponse ? "response" : "legacy",
            FrameClass: frameClass,
            FrameType: frameType,
            PollFinal: frame.PollFinal,
            Modulo: frame.IsExtendedControl ? 128 : 8,
            Nr: carriesNr ? frame.Nr : null,
            Ns: carriesNs ? frame.Ns : null,
            Pid: frame.Pid,
            PidName: frame.Pid is byte pid ? PidName(pid) : null,
            InfoLength: frame.Info.Length,
            InfoHex: Convert.ToHexString(frame.Info.Span),
            InfoText: RenderText(frame.Info.Span));
    }

    // Strip a KISS wrapper when present (or asked for), returning the AX.25 body,
    // the KISS port if any, and the framing actually used.
    private static (byte[] ax25, int? kissPort, Framing used) Unwrap(byte[] bytes, Framing framing)
    {
        bool looksKiss = bytes[0] == KissFraming.Fend;
        bool treatAsKiss = framing == Framing.Kiss || (framing == Framing.Auto && looksKiss);

        if (!treatAsKiss)
        {
            return (bytes, null, Framing.Raw);
        }

        var frames = new KissDecoder().Push(bytes);
        var data = frames.FirstOrDefault(f => f.Command == KissCommand.Data);
        if (data.Payload is null || data.Payload.Length == 0)
        {
            throw new ArgumentException("KISS input carried no data frame to decode.", nameof(bytes));
        }
        return (data.Payload, data.Port, Framing.Kiss);
    }

    // Classify off the first control octet. The frame-type discriminator bits
    // live there in both moduli (Fig 4.1b), so this is modulo-independent; the
    // N(S)/N(R) widths differ but Ax25Frame.Nr/Ns already handle that.
    private static (string cls, string type, bool nr, bool ns) Classify(Ax25Frame frame)
    {
        byte c = frame.Control;

        if ((c & 0x01) == 0)
        {
            // I frame — carries both N(S) and N(R).
            return ("I", "I", true, true);
        }

        if ((c & 0x03) == 0x01)
        {
            // S frame — carries N(R) only. Subtype in bits 3-2 (low nibble).
            string s = (c & 0x0F) switch
            {
                0x01 => "RR",
                0x05 => "RNR",
                0x09 => "REJ",
                0x0D => "SREJ",
                _ => "S?",
            };
            return ("S", s, true, false);
        }

        // U frame — 1 octet in both moduli; type in the control byte with the
        // P/F bit (0x10) masked out.
        string u = (c & 0xEF) switch
        {
            0x2F => "SABM",
            0x6F => "SABME",
            0x43 => "DISC",
            0x63 => "UA",
            0x0F => "DM",
            0x87 => "FRMR",
            0x03 => "UI",
            0xAF => "XID",
            0xE3 => "TEST",
            _ => "U?",
        };
        return ("U", u, false, false);
    }

    private static string? PidName(byte pid) => pid switch
    {
        0xF0 => "No layer 3",
        0xCF => "NET/ROM",
        0x08 => "Segmentation fragment",
        0xCC => "ARPA IP",
        0xCD => "ARPA Address Resolution",
        0x01 => "ISO 8208/CCITT X.25 PLP",
        0x06 => "Compressed TCP/IP",
        0x07 => "Uncompressed TCP/IP",
        0xC3 => "TheNET (NET/ROM)",
        0xCE => "FlexNet",
        0xFF => "Escape (next octet is the PID)",
        _ => null,
    };

    private static string RenderText(ReadOnlySpan<byte> info)
    {
        if (info.Length == 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder(info.Length);
        foreach (byte b in info)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        return sb.ToString();
    }

    // Parse a hex string, tolerating common separators (space, 0x, :, ,, newlines).
    internal static byte[] ParseHex(string hex)
    {
        var cleaned = new StringBuilder(hex.Length);
        for (int i = 0; i < hex.Length; i++)
        {
            char ch = hex[i];
            if (ch is ' ' or '\t' or '\r' or '\n' or ':' or ',' or '-')
            {
                continue;
            }
            // Skip a 0x / 0X prefix.
            if (ch == '0' && i + 1 < hex.Length && (hex[i + 1] is 'x' or 'X'))
            {
                i++;
                continue;
            }
            cleaned.Append(ch);
        }

        if (cleaned.Length % 2 != 0)
        {
            throw new FormatException("hex has an odd number of digits.");
        }

        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(
                    cleaned.ToString(i * 2, 2),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                throw new FormatException($"'{cleaned.ToString(i * 2, 2)}' is not a hex byte.");
            }
        }
        return bytes;
    }
}
