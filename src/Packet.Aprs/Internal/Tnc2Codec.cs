using System.Text;

namespace Packet.Aprs.Internal;

/// <summary>The TNC2 monitor / APRS-IS text form: <c>SOURCE&gt;DEST[,PATH...]:INFORMATION</c> (UAP §1).</summary>
internal static class Tnc2Codec
{
    public readonly record struct Header(AprsAddress Source, AprsAddress Destination, IReadOnlyList<AprsPathEntry> Path);

    /// <summary>Splits a line at the first colon and parses the header. False if the header is unusable.</summary>
    public static bool TrySplit(ReadOnlySpan<byte> line, DecodeContext ctx, out Header header, out int infoStart)
    {
        header = default;
        infoStart = -1;
        int colon = line.IndexOf((byte)':');
        if (colon < 0)
        {
            ctx.Error(AprsDiagnosticCode.InvalidHeader, "no ':' separating the header from the information field");
            return false;
        }

        if (!TryParseHeader(line[..colon], ctx, out header))
        {
            return false;
        }

        infoStart = colon + 1;
        return true;
    }

    public static bool TryParseHeader(ReadOnlySpan<byte> h, DecodeContext ctx, out Header header)
    {
        header = default;
        int gt = h.IndexOf((byte)'>');
        if (gt <= 0)
        {
            ctx.Error(AprsDiagnosticCode.InvalidHeader, "header must start SOURCE>DESTINATION");
            return false;
        }

        if (!TryAddress(h[..gt], "source", ctx, out AprsAddress source))
        {
            return false;
        }

        string[] rest = Encoding.Latin1.GetString(h[(gt + 1)..]).Split(',');
        AprsAddress destination;
        if (rest[0].Length == 0)
        {
            if (!ctx.Tolerate(ctx.Options.AllowEmptyDestination, AprsDiagnosticCode.EmptyDestination, "destination address is empty (UAP 5.2)"))
            {
                return false;
            }

            destination = AprsAddress.CreateUnchecked("");
        }
        else if (!TryAddress(Encoding.Latin1.GetBytes(rest[0]), "destination", ctx, out destination))
        {
            return false;
        }

        var path = new List<AprsPathEntry>(rest.Length - 1);
        int lastStar = -1;
        int stars = 0;
        for (int i = 1; i < rest.Length; i++)
        {
            string token = rest[i];
            bool star = token.EndsWith('*');
            if (star)
            {
                token = token[..^1];
            }

            if (token.Length == 0)
            {
                if (!ctx.Tolerate(ctx.Options.AllowEmptyPathEntry, AprsDiagnosticCode.EmptyPathEntry, "empty entry in the digipeater path (UAP 5.6)"))
                {
                    return false;
                }

                continue;
            }

            if (!TryAddress(Encoding.Latin1.GetBytes(token), "path", ctx, out AprsAddress address))
            {
                return false;
            }

            if (star)
            {
                stars++;
                lastStar = path.Count;
            }

            path.Add(new AprsPathEntry(address));
        }

        if (stars > 1 && !ctx.Tolerate(
                ctx.Options.AllowMultipleUsedMarkers,
                AprsDiagnosticCode.MultipleUsedMarkers,
                "more than one path entry is marked with *; only the last used entry should be (UAP 5.30)"))
        {
            return false;
        }

        for (int i = 0; i <= lastStar; i++)
        {
            path[i] = path[i] with { HasBeenRepeated = true };
        }

        header = new Header(source, destination, path);
        return true;
    }

    private static bool TryAddress(ReadOnlySpan<byte> bytes, string role, DecodeContext ctx, out AprsAddress address)
    {
        string text = Encoding.Latin1.GetString(bytes);
        if (AprsAddress.TryParse(text, out address))
        {
            return true;
        }

        ctx.Error(AprsDiagnosticCode.InvalidAddress, $"{role} address '{text}' is not a valid address");
        return false;
    }

    public static byte[] Format(AprsAddress source, AprsAddress destination, IReadOnlyList<AprsPathEntry> path, ReadOnlySpan<byte> information)
    {
        var sb = new StringBuilder();
        sb.Append(source.Value).Append('>').Append(destination.Value);
        if (path.Count > 0)
        {
            sb.Append(',').Append(AprsPathEntry.FormatList(path));
        }

        sb.Append(':');
        byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
        return [.. header, .. information];
    }
}
