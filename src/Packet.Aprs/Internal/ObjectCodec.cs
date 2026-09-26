namespace Packet.Aprs.Internal;

/// <summary>Object (<c>;</c>) and item (<c>)</c>) reports: the name header, then a position body (APRS12c §11).</summary>
internal static class ObjectCodec
{
    public static AprsData? DecodeObject(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        // ;NNNNNNNNN* then a timestamp, then position.
        if (info.Length < 11)
        {
            ctx.Error(AprsDiagnosticCode.Truncated, "object report is shorter than its fixed header (; + 9-character name + * or _)", 0);
            return null;
        }

        int markerAt = 10;
        byte marker = info[markerAt];
        if (marker is not ((byte)'*' or (byte)'_'))
        {
            // A name that was not padded to 9 characters: find the live/killed marker early.
            int found = info[1..Math.Min(info.Length, 10)].IndexOfAny((byte)'*', (byte)'_');
            if (found < 0 || !ctx.Tolerate(
                    ctx.Options.AllowShortObjectName,
                    AprsDiagnosticCode.ObjectNameNotPadded,
                    "object name is not padded to 9 characters (APRS12c ch. 11)",
                    1))
            {
                if (found < 0)
                {
                    ctx.Error(AprsDiagnosticCode.InvalidObjectName, "no * or _ after the 9-character object name", markerAt);
                }

                return null;
            }

            markerAt = found + 1;
            marker = info[markerAt];
        }

        ReadOnlySpan<byte> nameBytes = info[1..markerAt];
        foreach (byte b in nameBytes)
        {
            if (!Text.IsPrintableAscii(b))
            {
                ctx.Error(AprsDiagnosticCode.InvalidObjectName, "object name must be printable ASCII", 1);
                return null;
            }
        }

        string name = Text.Latin1(nameBytes).TrimEnd(' ');
        if (name.Length == 0)
        {
            ctx.Error(AprsDiagnosticCode.InvalidObjectName, "object name is empty", 1);
            return null;
        }

        int pos = markerAt + 1;
        AprsTimestamp? timestamp = null;
        if (info.Length > pos && Text.IsDigit(info[pos]) && info.Length >= pos + 7 && IsTimestampAt(info, pos))
        {
            if (!TimestampCodec.TryRead(info, pos, ctx, out AprsTimestamp ts))
            {
                return null;
            }

            timestamp = ts;
            pos += 7;
        }
        else if (PositionFollowsGarbledTimestamp(info, pos, ctx))
        {
            // Seven characters shaped like a timestamp (six digits, or a z / h suffix) but not
            // one, then a position: a garbled timestamp, not a missing one. Reading the position
            // straight after the marker instead would decode those seven characters as a
            // compressed position.
            if (!ctx.Tolerate(
                    ctx.Options.AllowMalformedTimestamp,
                    AprsDiagnosticCode.MalformedTimestamp,
                    $"'{Text.Latin1(info.Slice(pos, 7))}' is not a timestamp (6 digits then z, / or h, APRS12c ch. 6); skipped",
                    pos))
            {
                return null;
            }

            pos += 7;
        }
        else if (!ctx.Tolerate(
                     ctx.Options.AllowObjectWithoutTimestamp,
                     AprsDiagnosticCode.ObjectWithoutTimestamp,
                     "object report has no timestamp; an object must always have one (APRS12c ch. 11)",
                     pos))
        {
            return null;
        }

        if (!PositionCodec.TryReadBody(info, pos, ctx, out PositionedFields fields))
        {
            return null;
        }

        return fields.ApplyTo(new AprsObjectReport
        {
            Name = name,
            IsAlive = marker == (byte)'*',
            Timestamp = timestamp,
            Position = fields.Position,
            Symbol = fields.Symbol,
        });
    }

    public static AprsData? DecodeItem(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        // )NAME! or )NAME_ where NAME is 3-9 characters.
        int limit = Math.Min(info.Length, 11);
        int markerAt = -1;
        for (int i = 4; i < limit; i++)
        {
            if (info[i] is (byte)'!' or (byte)'_')
            {
                markerAt = i;
                break;
            }
        }

        if (markerAt < 0)
        {
            ctx.Error(AprsDiagnosticCode.InvalidItemName, "no ! or _ after a 3-9 character item name (APRS12c ch. 11)", 1);
            return null;
        }

        ReadOnlySpan<byte> nameBytes = info[1..markerAt];
        foreach (byte b in nameBytes)
        {
            if (!Text.IsPrintableAscii(b))
            {
                ctx.Error(AprsDiagnosticCode.InvalidItemName, "item name must be printable ASCII", 1);
                return null;
            }
        }

        if (!PositionCodec.TryReadBody(info, markerAt + 1, ctx, out PositionedFields fields))
        {
            return null;
        }

        return fields.ApplyTo(new AprsItemReport
        {
            Name = Text.Latin1(nameBytes),
            IsAlive = info[markerAt] == (byte)'!',
            Position = fields.Position,
            Symbol = fields.Symbol,
        });
    }

    public static void ValidateObjectName(string name, string paramName)
    {
        ArgumentNullException.ThrowIfNull(name, paramName);
        // Any printable ASCII including spaces (APRS12c ch. 11), except trailing ones, which are padding.
        if (name.Length is < 1 or > 9 || name.Any(c => c is < ' ' or > '~') || name.EndsWith(' '))
        {
            throw new ArgumentException("object name must be 1-9 printable ASCII characters, not ending in a space (APRS12c ch. 11)", paramName);
        }
    }

    public static void ValidateItemName(string name, string paramName)
    {
        ArgumentNullException.ThrowIfNull(name, paramName);
        if (name.Length is < 3 or > 9 || name.Any(c => c is < ' ' or > '~' or '!' or '_'))
        {
            throw new ArgumentException("item name must be 3-9 printable ASCII characters other than ! and _ (APRS12c ch. 11)", paramName);
        }
    }

    private static bool PositionFollowsGarbledTimestamp(ReadOnlySpan<byte> info, int pos, DecodeContext ctx) =>
        info.Length > pos + 7
        && (Text.AllDigits(info.Slice(pos, 6)) || info[pos + 6] is (byte)'z' or (byte)'/' or (byte)'h')
        && PositionCodec.TryReadBody(info, pos + 7, new DecodeContext(ctx.Options) { Depth = ctx.Depth }, out _);

    private static bool IsTimestampAt(ReadOnlySpan<byte> info, int pos) =>
        Text.AllDigits(info.Slice(pos, 6)) && info[pos + 6] is (byte)'z' or (byte)'/' or (byte)'h';
}
