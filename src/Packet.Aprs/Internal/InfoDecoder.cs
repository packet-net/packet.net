namespace Packet.Aprs.Internal;

/// <summary>Chooses a decoder from the data type identifier (APRS12c §5) and runs it.</summary>
internal static class InfoDecoder
{
    public static AprsData Decode(ReadOnlySpan<byte> raw, AprsAddress destination, DecodeContext ctx)
    {
        ReadOnlySpan<byte> info = raw;
        int end = info.Length;
        while (end > 0 && info[end - 1] is (byte)'\r' or (byte)'\n')
        {
            end--;
        }

        if (end < info.Length)
        {
            if (!ctx.Tolerate(
                    ctx.Options.StripTrailingLineBreaks,
                    AprsDiagnosticCode.TrailingLineBreak,
                    "information field ends with CR/LF; removed (APRS12c ch. 5, UAP 5.13)",
                    end))
            {
                return Unrecognized(raw, AprsUnrecognizedReason.Malformed);
            }

            info = info[..end];
        }

        if (info.Length == 0)
        {
            return Unrecognized(raw, AprsUnrecognizedReason.Empty);
        }

        AprsData? data;
        switch ((char)info[0])
        {
            case '!' when info.Length > 1 && info[1] == (byte)'!':
                data = RawWeatherCodec.Decode(info, ctx);
                break;
            case '!' or '=' or '/' or '@':
                data = PositionReportCodec.Decode(info, ctx);
                break;
            case '`' or '\'' or '\x1c' or '\x1d':
                data = MicECodec.Decode(info, destination, ctx);
                break;
            case ';':
                data = ObjectCodec.DecodeObject(info, ctx);
                break;
            case ')':
                data = ObjectCodec.DecodeItem(info, ctx);
                break;
            case ':':
                data = MessageCodec.Decode(info, ctx);
                break;
            case '>':
                data = StatusCodec.Decode(info, ctx);
                break;
            case 'T':
                data = TelemetryCodec.Decode(info, ctx);
                break;
            case '_':
                data = WeatherReportCodec.Decode(info, ctx);
                break;
            case '#' or '*':
                data = RawWeatherCodec.Decode(info, ctx);
                break;
            case '$' when info.StartsWith("$ULTW"u8):
                data = RawWeatherCodec.Decode(info, ctx);
                break;
            case '$':
                data = NmeaCodec.Decode(info, ctx);
                break;
            case '[':
                data = MaidenheadBeaconCodec.Decode(info, ctx);
                break;
            case '?':
                data = QueryCodec.DecodeGeneral(info, ctx);
                break;
            case '<':
                data = CapabilitiesCodec.Decode(info, ctx);
                break;
            case '}':
                data = ThirdPartyCodec.Decode(info, ctx);
                break;
            case '{':
                data = UserDefinedCodec.Decode(info, ctx);
                break;
            case ',':
                data = TestDataCodec.Decode(info, ctx);
                break;
            case '%':
                data = AgreloCodec.Decode(info, ctx);
                break;
            case '&' or '+' or '.':
                ctx.Info(AprsDiagnosticCode.ReservedDataType, $"data type identifier '{(char)info[0]}' is reserved and has no defined format (APRS12c ch. 5)", 0);
                return Unrecognized(raw, AprsUnrecognizedReason.ReservedDataType);
            default:
                if (PositionReportCodec.TryDecodeNotAtStart(info, ctx) is { } late)
                {
                    return late;
                }

                ctx.Info(AprsDiagnosticCode.NotAprs, $"'{(char)info[0]}' is not an APRS data type identifier; treated as a non-APRS beacon (APRS12c ch. 20)", 0);
                return Unrecognized(raw, AprsUnrecognizedReason.NotAprs);
        }

        return data ?? Unrecognized(raw, AprsUnrecognizedReason.Malformed);
    }

    private static AprsUnrecognizedData Unrecognized(ReadOnlySpan<byte> raw, AprsUnrecognizedReason reason) =>
        new() { Reason = reason, Raw = raw.ToArray() };
}

/// <summary>Position reports: <c>!</c> <c>=</c> <c>/</c> <c>@</c> (APRS12c §8, §9).</summary>
internal static class PositionReportCodec
{
    /// <summary>
    /// A <c>/</c> or <c>@</c> report whose timestamp is missing or garbled. A missing timestamp is
    /// tried first (a position straight after the DTI cannot be mistaken for a timestamp, which
    /// starts with 6 digits); then 7 garbled characters are skipped. Ham::APRS::FAP only does the
    /// second, and misreads reports with no timestamp at all.
    /// </summary>
    private static AprsPositionReport? DecodeWithBadTimestamp(ReadOnlySpan<byte> info, DecodeContext ctx, char dti)
    {
        foreach (int pos in (int[])[1, 8])
        {
            var probe = new DecodeContext(ctx.Options) { Depth = ctx.Depth };
            if (pos >= info.Length || !PositionCodec.TryReadBody(info, pos, probe, out PositionedFields fields))
            {
                continue;
            }

            if (!ctx.Tolerate(
                    ctx.Options.AllowMalformedTimestamp,
                    AprsDiagnosticCode.MalformedTimestamp,
                    pos == 1
                        ? "report type expects a timestamp but the position follows the data type identifier directly; timestamp ignored"
                        : $"'{Text.Latin1(info.Slice(1, 7))}' is not a timestamp (6 digits then z, / or h, APRS12c ch. 6); skipped",
                    1))
            {
                return null;
            }

            ctx.Diagnostics.AddRange(probe.Diagnostics);
            return fields.ApplyTo(new AprsPositionReport
            {
                Position = fields.Position,
                Symbol = fields.Symbol,
                MessagingCapable = dti == '@',
            });
        }

        ctx.Error(AprsDiagnosticCode.MalformedTimestamp, $"'{Text.Latin1(info.Slice(1, Math.Min(7, info.Length - 1)))}' is not a timestamp (APRS12c ch. 6)", 1);
        return null;
    }

    /// <summary>
    /// The obsolete TNC beacon rule (the "X1J exception"): a <c>!</c> position anywhere in the first
    /// 40 characters of a packet that has no other data type. Deprecated in 2012 (APRS 1.1
    /// corrections); recognised only under <see cref="AprsParseOptions.AllowPositionNotAtStart"/>.
    /// </summary>
    public static AprsPositionReport? TryDecodeNotAtStart(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        if (!ctx.Options.AllowPositionNotAtStart)
        {
            return null;
        }

        int bang = info[..Math.Min(info.Length, 40)].IndexOf((byte)'!');
        if (bang < 1)
        {
            return null;
        }

        var probe = new DecodeContext(ctx.Options) { Depth = ctx.Depth };
        if (!PositionCodec.TryReadBody(info, bang + 1, probe, out PositionedFields fields))
        {
            return null;
        }

        ctx.Warn(AprsDiagnosticCode.PositionNotAtStart, $"'!' position found at offset {bang} instead of the start; the text before it is ignored (obsolete TNC beacon rule, APRS 1.1)", bang);
        ctx.Diagnostics.AddRange(probe.Diagnostics);
        return fields.ApplyTo(new AprsPositionReport { Position = fields.Position, Symbol = fields.Symbol });
    }

    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        char dti = (char)info[0];
        bool timestamped = dti is '/' or '@';
        int pos = 1;
        AprsTimestamp? timestamp = null;
        if (timestamped)
        {
            if (TimestampCodec.HasShape(info, pos))
            {
                if (!TimestampCodec.TryRead(info, pos, ctx, out AprsTimestamp ts))
                {
                    return null;
                }

                timestamp = ts;
                pos += 7;
            }
            else
            {
                return DecodeWithBadTimestamp(info, ctx, dti);
            }
        }

        if (!PositionCodec.TryReadBody(info, pos, ctx, out PositionedFields fields))
        {
            return null;
        }

        return fields.ApplyTo(new AprsPositionReport
        {
            Position = fields.Position,
            Symbol = fields.Symbol,
            Timestamp = timestamp,
            MessagingCapable = dti is '=' or '@',
        });
    }
}
