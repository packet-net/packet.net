namespace Packet.Aprs.Internal;

/// <summary>Storm data after course/speed: <c>/ST/www^GGG/pppp&gt;RRR&amp;rrr[%ggg]</c> (APRS12c §12).</summary>
internal static class StormCodec
{
    public static bool TryRead(ReadOnlySpan<byte> info, ref int pos, out AprsStorm? storm)
    {
        storm = null;
        ReadOnlySpan<byte> s = info[pos..];
        if (s.Length < 24 || s[0] != (byte)'/' || s[3] != (byte)'/' || s[7] != (byte)'^' || s[11] != (byte)'/' || s[16] != (byte)'>' || s[20] != (byte)'&')
        {
            return false;
        }

        AprsStormType? type = (s[1], s[2]) switch
        {
            ((byte)'T', (byte)'S') => AprsStormType.TropicalStorm,
            ((byte)'H', (byte)'C') => AprsStormType.Hurricane,
            ((byte)'T', (byte)'D') => AprsStormType.TropicalDepression,
            _ => null,
        };
        if (type is null || !Group(s.Slice(4, 3), out int? www) || !Group(s.Slice(8, 3), out int? gust) || !Group(s.Slice(12, 4), out int? pressure)
            || !Group(s.Slice(17, 3), out int? hurricane) || !Group(s.Slice(21, 3), out int? tropical))
        {
            return false;
        }

        int length = 24;
        int? gale = null;
        if (s.Length >= 28 && s[24] == (byte)'%' && Group(s.Slice(25, 3), out int? g))
        {
            gale = g;
            length = 28;
        }

        storm = new AprsStorm
        {
            Type = type.Value,
            SustainedWindKnots = www,
            GustKnots = gust,
            CentralPressureMillibars = pressure,
            HurricaneWindRadiusNauticalMiles = hurricane,
            TropicalStormWindRadiusNauticalMiles = tropical,
            WholeGaleRadiusNauticalMiles = gale,
        };
        pos += length;
        return true;
    }

    public static void Write(InfoWriter w, AprsStorm storm)
    {
        w.Char('/').Ascii(storm.Type switch
        {
            AprsStormType.TropicalStorm => "TS",
            AprsStormType.Hurricane => "HC",
            _ => "TD",
        });
        w.Char('/');
        Value(w, storm.SustainedWindKnots, 3);
        w.Char('^');
        Value(w, storm.GustKnots, 3);
        w.Char('/');
        Value(w, storm.CentralPressureMillibars, 4);
        w.Char('>');
        Value(w, storm.HurricaneWindRadiusNauticalMiles, 3);
        w.Char('&');
        Value(w, storm.TropicalStormWindRadiusNauticalMiles, 3);
        if (storm.WholeGaleRadiusNauticalMiles is { } gale)
        {
            w.Char('%').Digits(gale, 3);
        }
    }

    private static void Value(InfoWriter w, int? value, int width)
    {
        if (value is { } v)
        {
            w.Digits(v, width);
        }
        else
        {
            w.Ascii(new string('.', width));
        }
    }

    private static bool Group(ReadOnlySpan<byte> g, out int? value)
    {
        value = null;
        if (Text.AllDigits(g))
        {
            value = Text.ParseDigits(g);
            return true;
        }

        foreach (byte b in g)
        {
            if (b is not ((byte)'.' or (byte)' '))
            {
                return false;
            }
        }

        return true;
    }
}
