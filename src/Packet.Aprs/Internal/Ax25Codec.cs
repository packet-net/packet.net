namespace Packet.Aprs.Internal;

/// <summary>
/// AX.25 UI frames as used by APRS (APRS12c §3): destination, source, up to 8 digipeaters,
/// control 0x03, PID 0xF0, information. Frames here exclude flags and FCS, which is the form a
/// KISS TNC delivers.
/// </summary>
internal static class Ax25Codec
{
    public const int MaxDigipeaters = 8;

    public static bool TryDecode(ReadOnlySpan<byte> frame, DecodeContext ctx, out Tnc2Codec.Header header, out int infoStart)
    {
        header = default;
        infoStart = -1;
        var addresses = new List<(AprsAddress Address, bool H)>();
        int pos = 0;
        while (true)
        {
            if (frame.Length < pos + 7)
            {
                ctx.Error(AprsDiagnosticCode.NotAprsFrame, "frame ends inside the address field");
                return false;
            }

            if (!TryReadAddress(frame.Slice(pos, 7), addresses.Count == 1 ? "destination" : "address", ctx, out AprsAddress address, out bool h, out bool empty))
            {
                return false;
            }

            if (empty && addresses.Count == 0)
            {
                if (!ctx.Tolerate(ctx.Options.AllowEmptyDestination, AprsDiagnosticCode.EmptyDestination, "destination address is all spaces (UAP 5.2)"))
                {
                    return false;
                }
            }
            else if (empty)
            {
                if (!ctx.Tolerate(ctx.Options.AllowEmptyPathEntry, AprsDiagnosticCode.EmptyPathEntry, "empty digipeater address (UAP 5.6)"))
                {
                    return false;
                }
            }

            addresses.Add((address, h));
            bool last = (frame[pos + 6] & 0x01) != 0;
            pos += 7;
            if (last)
            {
                break;
            }

            if (addresses.Count == 2 + MaxDigipeaters)
            {
                ctx.Error(AprsDiagnosticCode.TooManyDigipeaters, "more than 8 digipeater addresses");
                return false;
            }
        }

        if (addresses.Count < 2)
        {
            ctx.Error(AprsDiagnosticCode.NotAprsFrame, "frame has no source address");
            return false;
        }

        if (frame.Length < pos + 2)
        {
            ctx.Error(AprsDiagnosticCode.NotAprsFrame, "frame has no control and PID bytes");
            return false;
        }

        byte control = frame[pos];
        byte pid = frame[pos + 1];
        if ((control & ~0x10) != 0x03 || pid != 0xF0)
        {
            ctx.Error(AprsDiagnosticCode.NotAprsFrame, $"not an APRS frame: control 0x{control:X2}, PID 0x{pid:X2} (APRS uses UI frames, 0x03 / 0xF0)");
            return false;
        }

        var path = addresses.Skip(2).Where(a => a.Address.Value.Length > 0).Select(a => new AprsPathEntry(a.Address, a.H)).ToList();
        header = new Tnc2Codec.Header(addresses[1].Address, addresses[0].Address, path);
        infoStart = pos + 2;
        return true;
    }

    private static bool TryReadAddress(ReadOnlySpan<byte> a, string role, DecodeContext ctx, out AprsAddress address, out bool hBit, out bool empty)
    {
        address = default;
        Span<char> chars = stackalloc char[6];
        int length = 0;
        bool nul = false;
        bool padding = false;
        bool invalid = false;
        for (int i = 0; i < 6; i++)
        {
            char c = (char)(a[i] >> 1);
            if (c == ' ' || c == '\0')
            {
                nul |= c == '\0';
                padding = true;
                continue;
            }

            if (padding)
            {
                ctx.Error(AprsDiagnosticCode.InvalidAddress, $"{role} address has a character after its padding");
                hBit = empty = false;
                return false;
            }

            invalid |= !AprsAddress.IsAx25Char(c);
            chars[length++] = c;
        }

        byte ssidByte = a[6];
        int ssid = (ssidByte >> 1) & 0x0F;
        hBit = (ssidByte & 0x80) != 0;
        empty = length == 0;
        if (nul && !ctx.Tolerate(ctx.Options.AllowNulPaddedAddress, AprsDiagnosticCode.NulPaddedAddress, $"{role} address is padded with NUL instead of spaces (UAP 5.29)"))
        {
            return false;
        }

        string call = new(chars[..length]);
        if (invalid && !ctx.Tolerate(
                ctx.Options.AllowInvalidAx25AddressCharacters,
                AprsDiagnosticCode.InvalidAx25AddressCharacters,
                $"{role} address '{call}' has characters other than upper-case letters and digits (UAP 1.1)"))
        {
            return false;
        }

        string text = ssid == 0 ? call : $"{call}-{ssid}";
        if (empty)
        {
            address = AprsAddress.CreateUnchecked("");
            return true;
        }

        if (!AprsAddress.TryParse(text, out address))
        {
            ctx.Error(AprsDiagnosticCode.InvalidAddress, $"{role} address has bytes that cannot appear in an address");
            return false;
        }

        return true;
    }

    public static byte[] Encode(AprsAddress source, AprsAddress destination, IReadOnlyList<AprsPathEntry> path, ReadOnlySpan<byte> information)
    {
        if (path.Count > MaxDigipeaters)
        {
            throw new ArgumentException("an AX.25 frame carries at most 8 digipeater addresses", nameof(path));
        }

        byte[] frame = new byte[(7 * (2 + path.Count)) + 2 + information.Length];
        WriteAddress(frame.AsSpan(0, 7), destination, commandBit: true, last: false, nameof(destination));
        WriteAddress(frame.AsSpan(7, 7), source, commandBit: false, last: path.Count == 0, nameof(source));
        for (int i = 0; i < path.Count; i++)
        {
            WriteAddress(frame.AsSpan(14 + (7 * i), 7), path[i].Address, path[i].HasBeenRepeated, i == path.Count - 1, nameof(path));
        }

        int pos = 7 * (2 + path.Count);
        frame[pos] = 0x03;
        frame[pos + 1] = 0xF0;
        information.CopyTo(frame.AsSpan(pos + 2));
        return frame;
    }

    /// <summary>The C bit is set on the destination and clear on the source for a UI command (APRS12c §3);
    /// on digipeaters the same bit is the has-been-repeated flag.</summary>
    private static void WriteAddress(Span<byte> a, AprsAddress address, bool commandBit, bool last, string paramName)
    {
        if (!address.IsAx25)
        {
            throw new ArgumentException($"'{address}' is not a valid AX.25 address (1-6 upper-case letters and digits, SSID 1-15)", paramName);
        }

        string call = address.Base.PadRight(6);
        for (int i = 0; i < 6; i++)
        {
            a[i] = (byte)(call[i] << 1);
        }

        a[6] = (byte)((commandBit ? 0x80 : 0) | 0x60 | ((address.NumericSsid ?? 0) << 1) | (last ? 1 : 0));
    }
}
