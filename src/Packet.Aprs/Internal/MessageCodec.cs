using System.Globalization;

namespace Packet.Aprs.Internal;

/// <summary>The <c>:</c> message family (APRS12c §13, §14, §15).</summary>
internal static class MessageCodec
{
    public const int MaxTextLength = 67;

    /// <summary>PARM. and UNIT. name the 5 analog and 8 digital channels (APRS12c ch. 13).</summary>
    public const int MaxChannelNames = 13;

    /// <summary>EQNS. carries 3 coefficients for each of the 5 analog channels.</summary>
    public const int MaxCoefficients = 15;

    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        int second = info.Length > 10 && info[10] == (byte)':' ? 10 : -1;
        if (second < 0)
        {
            int found = info[1..Math.Min(info.Length, 11)].IndexOf((byte)':');
            if (found < 1 || !ctx.Tolerate(
                    ctx.Options.AllowUnpaddedAddressee,
                    AprsDiagnosticCode.UnpaddedAddressee,
                    "message addressee is not padded to 9 characters (APRS12c ch. 14)",
                    1))
            {
                if (found < 1)
                {
                    ctx.Error(AprsDiagnosticCode.InvalidMessage, "message has no ':' after a 9-character addressee (APRS12c ch. 14)", 0);
                }

                return null;
            }

            second = found + 1;
        }

        ReadOnlySpan<byte> addresseeBytes = info[1..second];
        foreach (byte b in addresseeBytes)
        {
            if (!Text.IsPrintableAscii(b))
            {
                ctx.Error(AprsDiagnosticCode.InvalidMessage, "message addressee must be printable ASCII", 1);
                return null;
            }
        }

        string addressee = Text.Latin1(addresseeBytes).TrimEnd(' ');
        if (addressee.Length == 0)
        {
            ctx.Error(AprsDiagnosticCode.InvalidMessage, "message addressee is empty", 1);
            return null;
        }

        // Trailing spaces are padding; a space or ':' inside the addressee is not an address.
        if (addressee.Any(c => c is ' ' or ':') && !ctx.Tolerate(
                ctx.Options.AllowInvalidAddresseeCharacters,
                AprsDiagnosticCode.InvalidAddresseeCharacters,
                $"message addressee '{addressee}' contains a space or ':' (APRS12c ch. 14)",
                1))
        {
            return null;
        }

        int textAt = second + 1;
        ReadOnlySpan<byte> text = info[textAt..];

        if ((text.StartsWith("ack"u8) || text.StartsWith("rej"u8)) && TryAckId(text[3..], textAt + 3, ctx, out string? acked, out string? ackReply, out bool ackOk))
        {
            if (!ackOk)
            {
                return null;
            }

            return text[0] == (byte)'a'
                ? new AprsMessageAck { Addressee = addressee, AcknowledgedId = acked!, ReplyAck = ackReply }
                : new AprsMessageReject { Addressee = addressee, RejectedId = acked!, ReplyAck = ackReply };
        }

        if (IsBulletinAddressee(addressee))
        {
            if (addressee.Length > 4 && !char.IsAsciiDigit(addressee[3]) && !ctx.Tolerate(
                    ctx.Options.AllowLetterGroupBulletin,
                    AprsDiagnosticCode.LetterGroupBulletin,
                    $"'{addressee}' names a group after a letter; a group bulletin has a digit after BLN, and an announcement has no group (APRS12c ch. 14)",
                    1))
            {
                return null;
            }

            SplitId(text, allowReplyAck: false, out ReadOnlySpan<byte> body, out string? id, out _);
            return CheckBrace(body, textAt, ctx) && Text.TryDecode(body, ctx, textAt, out string s) ? new AprsBulletin { Addressee = addressee, Text = s, MessageId = id } : null;
        }

        if (addressee.StartsWith("NWS", StringComparison.Ordinal))
        {
            SplitId(text, allowReplyAck: false, out ReadOnlySpan<byte> body, out string? id, out _);
            return CheckBrace(body, textAt, ctx) && Text.TryDecode(body, ctx, textAt, out string s) ? new AprsNwsBulletin { Addressee = addressee, Text = s, MessageId = id } : null;
        }

        if (text.Length >= 5 && text[4] == (byte)'.' && TryMetadata(addressee, text, textAt, ctx, out AprsData? metadata))
        {
            return metadata;
        }

        if (text.Length > 1 && text[0] == (byte)'?' && TryDirectedQuery(addressee, text, textAt, ctx, out AprsDirectedQuery? query))
        {
            return query;
        }

        SplitId(text, allowReplyAck: true, out ReadOnlySpan<byte> messageText, out string? messageId, out string? replyAck);
        if (!CheckBrace(messageText, textAt, ctx) || !Text.TryDecode(messageText, ctx, textAt, out string decoded))
        {
            return null;
        }

        return new AprsTextMessage { Addressee = addressee, Text = decoded, MessageId = messageId, ReplyAck = replyAck };
    }

    /// <summary>
    /// Message text never contains <c>{</c>, which starts the message ID (APRS12c ch. 14). One that
    /// is left in the text after the ID was split off is not followed by a valid ID.
    /// </summary>
    private static bool CheckBrace(ReadOnlySpan<byte> text, int offset, DecodeContext ctx)
    {
        int brace = text.IndexOf((byte)'{');
        return brace < 0 || ctx.Tolerate(
            ctx.Options.AllowBraceInMessageText,
            AprsDiagnosticCode.BraceInMessageText,
            "message text contains '{' that does not start a valid message ID (up to 5 letters or digits, APRS12c ch. 14); kept as text",
            offset + brace);
    }

    /// <summary>
    /// The ID after <c>ack</c> / <c>rej</c>: 1-5 alphanumerics, optionally <c>}</c> and a reply-ack.
    /// Returns false (not an ack at all) if the text does not fit; <paramref name="ok"/> false if it
    /// is an ack that a disabled tolerance rejects.
    /// </summary>
    private static bool TryAckId(ReadOnlySpan<byte> s, int offset, DecodeContext ctx, out string? id, out string? replyAck, out bool ok)
    {
        id = null;
        replyAck = null;
        ok = true;
        int brace = s.IndexOf((byte)'{');
        ReadOnlySpan<byte> core = brace >= 0 ? s[..brace] : s;
        int close = core.IndexOf((byte)'}');
        ReadOnlySpan<byte> idPart = close >= 0 ? core[..close] : core;
        if (!IsId(idPart) || (close >= 0 && !IsIdOrEmpty(core[(close + 1)..])))
        {
            return false;
        }

        if (brace >= 0)
        {
            if (!IsId(s[(brace + 1)..]))
            {
                return false;
            }

            ok = ctx.Tolerate(
                ctx.Options.AllowMessageIdOnAck,
                AprsDiagnosticCode.MessageIdOnAck,
                "ack/rej carries a message ID of its own; ignored (UAP 5.32)",
                offset + brace);
        }

        id = Text.Latin1(idPart);
        replyAck = close >= 0 ? Text.Latin1(core[(close + 1)..]) : null;
        return true;
    }

    private static void SplitId(ReadOnlySpan<byte> text, bool allowReplyAck, out ReadOnlySpan<byte> body, out string? id, out string? replyAck)
    {
        body = text;
        id = null;
        replyAck = null;
        int brace = text.LastIndexOf((byte)'{');
        if (brace < 0)
        {
            return;
        }

        ReadOnlySpan<byte> tail = text[(brace + 1)..];
        int close = tail.IndexOf((byte)'}');
        if (close < 0)
        {
            if (IsId(tail))
            {
                id = Text.Latin1(tail);
                body = text[..brace];
            }

            return;
        }

        if (allowReplyAck && IsId(tail[..close]) && IsIdOrEmpty(tail[(close + 1)..]))
        {
            id = Text.Latin1(tail[..close]);
            replyAck = Text.Latin1(tail[(close + 1)..]);
            body = text[..brace];
        }
    }

    private static bool TryMetadata(string addressee, ReadOnlySpan<byte> text, int offset, DecodeContext ctx, out AprsData? data)
    {
        data = null;
        ReadOnlySpan<byte> kind = text[..5];
        if (!kind.SequenceEqual("PARM."u8) && !kind.SequenceEqual("UNIT."u8) && !kind.SequenceEqual("EQNS."u8) && !kind.SequenceEqual("BITS."u8))
        {
            return false;
        }

        SplitId(text[5..], allowReplyAck: false, out ReadOnlySpan<byte> body, out string? id, out _);
        if (!CheckBrace(body, offset + 5, ctx) || !Text.TryDecode(body, ctx, offset + 5, out string s))
        {
            return true;
        }

        switch ((char)kind[0])
        {
            case 'P' or 'U':
            {
                string[] entries = s.Split(',');
                if (entries.Length > MaxChannelNames)
                {
                    ctx.Info(AprsDiagnosticCode.InvalidTelemetryMetadata, $"{Text.Latin1(kind)} has {entries.Length} entries, more than the 13 channels; decoded as a plain message", offset);
                    return false;
                }

                data = kind[0] == (byte)'P'
                    ? new AprsTelemetryParameterNames { Addressee = addressee, Names = entries, MessageId = id }
                    : new AprsTelemetryUnits { Addressee = addressee, Units = entries, MessageId = id };
                return true;
            }

            case 'E':
            {
                // "The list may stop at any field" (APRS12c §13): trailing empty entries are the list stopping.
                var values = new List<decimal>();
                foreach (string part in s.TrimEnd(' ').TrimEnd(',').Split(','))
                {
                    if (!decimal.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v))
                    {
                        ctx.Info(AprsDiagnosticCode.InvalidTelemetryMetadata, $"EQNS value '{part}' is not a number; decoded as a plain message", offset + 5);
                        return false;
                    }

                    values.Add(v);
                }

                if (values.Count > MaxCoefficients)
                {
                    ctx.Info(AprsDiagnosticCode.InvalidTelemetryMetadata, "EQNS has more than 15 coefficients; decoded as a plain message", offset + 5);
                    return false;
                }

                data = new AprsTelemetryCoefficients { Addressee = addressee, Coefficients = values, MessageId = id };
                return true;
            }

            default:
            {
                int comma = s.IndexOf(',', StringComparison.Ordinal);
                string bits = comma >= 0 ? s[..comma] : s;
                if (bits.Length != 8 || bits.Any(c => c is not ('0' or '1')))
                {
                    ctx.Info(AprsDiagnosticCode.InvalidTelemetryMetadata, "BITS needs exactly 8 digits 0/1; decoded as a plain message", offset + 5);
                    return false;
                }

                byte value = 0;
                for (int i = 0; i < 8; i++)
                {
                    if (bits[i] == '1')
                    {
                        value |= (byte)(1 << i);
                    }
                }

                data = new AprsTelemetryBitSense { Addressee = addressee, Bits = value, ProjectTitle = comma >= 0 ? s[(comma + 1)..] : "", MessageId = id };
                return true;
            }
        }
    }

    private static bool TryDirectedQuery(string addressee, ReadOnlySpan<byte> text, int offset, DecodeContext ctx, out AprsDirectedQuery? query)
    {
        query = null;
        string s = Text.Latin1(text[1..]);
        if (s.Contains('{', StringComparison.Ordinal))
        {
            ctx.Info(AprsDiagnosticCode.InvalidQuery, "a directed query never has a message ID; decoded as a plain message (UAP 5.18)", offset);
            return false;
        }

        foreach (string type in AprsDirectedQuery.KnownTypes)
        {
            if (s.StartsWith(type, StringComparison.Ordinal))
            {
                string target = s[type.Length..].Trim();
                if (target.Length > 9 || target.Any(c => c is < '!' or > '~'))
                {
                    ctx.Info(AprsDiagnosticCode.InvalidQuery, "a query target is one callsign of up to 9 characters; decoded as a plain message", offset);
                    return false;
                }

                query = new AprsDirectedQuery { Addressee = addressee, QueryType = type, Target = target.Length > 0 ? target : null };
                return true;
            }

            if (s.StartsWith(type, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Info(AprsDiagnosticCode.InvalidQuery, "query types must be upper case; decoded as a plain message (UAP 5.18)", offset);
                return false;
            }
        }

        // An unrecognised upper-case type is still a query (the recipient should ignore it).
        int end = 0;
        while (end < s.Length && s[end] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            end++;
        }

        if (end == 0 || (end < s.Length && s[end] != ' '))
        {
            return false;
        }

        string rest = s[end..].Trim();
        if (rest.Length > 9 || rest.Any(c => c is < '!' or > '~'))
        {
            ctx.Info(AprsDiagnosticCode.InvalidQuery, "a query target is one callsign of up to 9 characters; decoded as a plain message", offset);
            return false;
        }

        query = new AprsDirectedQuery { Addressee = addressee, QueryType = s[..end], Target = rest.Length > 0 ? rest : null };
        return true;
    }

    public static bool IsBulletinAddressee(string addressee) =>
        addressee.Length is >= 4 and <= 9 && addressee.StartsWith("BLN", StringComparison.Ordinal)
        && (char.IsAsciiDigit(addressee[3]) || char.IsAsciiLetterUpper(addressee[3]));

    private static bool IsId(ReadOnlySpan<byte> s)
    {
        if (s.Length is < 1 or > 5)
        {
            return false;
        }

        foreach (byte b in s)
        {
            if (!Text.IsAlphanumeric(b))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdOrEmpty(ReadOnlySpan<byte> s) => s.Length == 0 || IsId(s);

    // ------------------------------------------------------------------ encoding

    public static void ValidateAddressee(string addressee)
    {
        ArgumentNullException.ThrowIfNull(addressee);
        if (addressee.Length is < 1 or > 9 || addressee.Any(c => c is <= ' ' or > '~' or ':'))
        {
            throw new ArgumentException("addressee must be 1-9 printable ASCII characters without spaces or ':' (APRS12c ch. 14)", nameof(addressee));
        }
    }

    public static void ValidateId(string id, string paramName)
    {
        ArgumentNullException.ThrowIfNull(id, paramName);
        if (!IsId(System.Text.Encoding.ASCII.GetBytes(id)) || id.Any(c => c > 0x7F))
        {
            throw new ArgumentException("a message ID is 1-5 letters or digits (APRS12c ch. 14)", paramName);
        }
    }

    public static void ValidateText(string text, string paramName)
    {
        ArgumentNullException.ThrowIfNull(text, paramName);
        Text.RequireNoLineBreaks(text, paramName);
        if (text.Contains('{', StringComparison.Ordinal))
        {
            throw new ArgumentException("message text must not contain '{', which starts the message ID (APRS12c ch. 14)", paramName);
        }

        if (text.Length > MaxTextLength)
        {
            throw new ArgumentException($"message text is limited to {MaxTextLength} characters (APRS12c ch. 14)", paramName);
        }
    }

    /// <summary>Writes <c>PARM.</c>, <c>UNIT.</c> or <c>EQNS.</c> and its comma-separated values.</summary>
    public static void WriteList(InfoWriter writer, string prefix, IReadOnlyList<string> items, AprsMessage message)
    {
        int max = prefix == "EQNS." ? MaxCoefficients : MaxChannelNames;
        if (items.Count < 1 || items.Count > max)
        {
            throw new ArgumentException($"{prefix} needs between 1 and {max} entries");
        }

        foreach (string item in items)
        {
            Text.RequireNoLineBreaks(item, nameof(items));
            if (item.Contains(',', StringComparison.Ordinal) || item.Contains('{', StringComparison.Ordinal))
            {
                throw new ArgumentException($"{prefix} entries must not contain ',' or '{{'");
            }
        }

        writer.Ascii(prefix).Utf8(string.Join(',', items));
        message.WriteMessageId(writer);
    }
}
