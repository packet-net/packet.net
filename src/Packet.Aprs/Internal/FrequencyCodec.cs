using System.Globalization;

namespace Packet.Aprs.Internal;

/// <summary>The APRS voice frequency format (APRS12c §18).</summary>
internal static class FrequencyCodec
{
    // Microwave letter prefixes: the letter replaces the first digit(s) (APRS12c §18 Microwaves).
    private static readonly (char Letter, int BaseMHz)[] Microwave =
    [
        ('A', 1200), ('B', 2300), ('C', 2400), ('D', 3400), ('E', 5600), ('F', 5700), ('G', 5800), ('H', 10100),
        ('I', 10200), ('J', 10300), ('K', 10400), ('L', 10500), ('M', 24000), ('N', 24100), ('O', 24200),
    ];

    public static bool TryRead(ReadOnlySpan<byte> s, out AprsVoiceFrequency? frequency, out int consumed)
    {
        frequency = null;
        consumed = 0;
        if (s.Length < 10 || s[3] != (byte)'.' || !"MHZ"u8.SequenceEqual(Upper(s.Slice(7, 3))))
        {
            return false;
        }

        bool tenKhz = s[6] == (byte)' ';
        if (!Text.AllDigits(s.Slice(1, 2)) || !Text.AllDigits(s.Slice(4, tenKhz ? 2 : 3)))
        {
            return false;
        }

        int baseMhz;
        char first = (char)s[0];
        if (Text.IsDigit(s[0]))
        {
            baseMhz = (s[0] - '0') * 100;
        }
        else if (Array.FindIndex(Microwave, m => m.Letter == first) is var m and >= 0)
        {
            baseMhz = Microwave[m].BaseMHz;
        }
        else
        {
            return false;
        }

        decimal mhz = baseMhz + Text.ParseDigits(s.Slice(1, 2)) + (Text.ParseDigits(s.Slice(4, tenKhz ? 2 : 3)) / (tenKhz ? 100m : 1000m));
        int pos = 10;
        AprsToneType? toneType = null;
        int? toneValue = null;
        bool narrow = false;
        int? offset = null;
        int? range = null;
        bool rangeKm = false;

        if (TryField(s, pos, 4, out ReadOnlySpan<byte> tone) && TryTone(tone, out toneType, out toneValue, out narrow))
        {
            pos += 5;
        }

        if (TryField(s, pos, 4, out ReadOnlySpan<byte> off) && off[0] is (byte)'+' or (byte)'-' && Text.AllDigits(off[1..]))
        {
            offset = Text.ParseDigits(off[1..]) * 10 * (off[0] == (byte)'-' ? -1 : 1);
            pos += 5;
        }

        if (TryField(s, pos, 4, out ReadOnlySpan<byte> rng) && rng[0] == (byte)'R' && Text.AllDigits(rng.Slice(1, 2)) && rng[3] is (byte)'m' or (byte)'k')
        {
            range = Text.ParseDigits(rng.Slice(1, 2));
            rangeKm = rng[3] == (byte)'k';
            pos += 5;
        }

        frequency = new AprsVoiceFrequency
        {
            FrequencyMHz = mhz,
            TenKilohertzResolution = tenKhz,
            ToneType = toneType,
            ToneValue = toneValue,
            Narrow = narrow,
            OffsetKHz = offset,
            Range = range,
            RangeInKilometres = rangeKm,
        };
        consumed = pos;
        return true;
    }

    /// <summary>A space then exactly <paramref name="length"/> bytes, ending at a space or the end.</summary>
    private static bool TryField(ReadOnlySpan<byte> s, int pos, int length, out ReadOnlySpan<byte> field)
    {
        field = default;
        if (s.Length < pos + 1 + length || s[pos] != (byte)' ' || (s.Length > pos + 1 + length && s[pos + 1 + length] != (byte)' '))
        {
            return false;
        }

        field = s.Slice(pos + 1, length);
        return true;
    }

    private static bool TryTone(ReadOnlySpan<byte> t, out AprsToneType? type, out int? value, out bool narrow)
    {
        type = null;
        value = null;
        narrow = false;
        if (t.SequenceEqual("Toff"u8) || t.SequenceEqual("toff"u8))
        {
            type = AprsToneType.Off;
            narrow = t[0] == (byte)'t';
            return true;
        }

        if (t.SequenceEqual("1750"u8) || t.SequenceEqual("l750"u8))
        {
            type = AprsToneType.ToneBurst;
            narrow = t[0] == (byte)'l';
            return true;
        }

        if (!Text.AllDigits(t[1..]))
        {
            return false;
        }

        type = (char)(t[0] | 0x20) switch
        {
            't' => AprsToneType.Tone,
            'c' => AprsToneType.Ctcss,
            'd' => AprsToneType.Dcs,
            _ => null,
        };
        if (type is null)
        {
            return false;
        }

        value = Text.ParseDigits(t[1..]);
        narrow = Text.IsLower(t[0]);
        return true;
    }

    private static byte[] Upper(ReadOnlySpan<byte> s)
    {
        byte[] r = s.ToArray();
        for (int i = 0; i < r.Length; i++)
        {
            if (Text.IsLower(r[i]))
            {
                r[i] -= 32;
            }
        }

        return r;
    }

    /// <summary>
    /// Writes the frequency and then the comment. The usual separating space is left out when the
    /// comment starts with something that would otherwise be read as another tone/offset/range
    /// field (e.g. <c>-500 ...</c>), so the pair reads back exactly as given.
    /// </summary>
    public static void WriteWithComment(InfoWriter w, AprsVoiceFrequency f, string comment)
    {
        // The decoder drops one leading space or slash of the comment as a delimiter.
        if (comment.Length > 0 && comment[0] is ' ' or '/')
        {
            comment = "/" + comment;
        }

        var alone = new InfoWriter();
        Write(alone, f);
        byte[] text = System.Text.Encoding.UTF8.GetBytes(comment);
        foreach (bool separator in (bool[])[true, false])
        {
            if (!separator && comment.Length > 0 && comment[0] == ' ')
            {
                continue;
            }

            byte[] candidate = [.. alone.Written, .. (separator && comment.Length > 0 ? " "u8.ToArray() : []), .. text];
            if (TryRead(candidate, out AprsVoiceFrequency? back, out int consumed) && back == f
                && consumed + (separator && comment.Length > 0 ? 1 : 0) == alone.Length + (separator && comment.Length > 0 ? 1 : 0))
            {
                w.Bytes(candidate);
                return;
            }
        }

        throw new ArgumentException("the comment starts with text that would be read as part of the frequency specification", nameof(comment));
    }

    public static void Write(InfoWriter w, AprsVoiceFrequency f)
    {
        f.Validate(nameof(f));
        decimal mhz = f.FrequencyMHz;
        string lead;
        decimal rest;
        if (mhz < 1000)
        {
            lead = ((int)(mhz / 100)).ToString(CultureInfo.InvariantCulture);
            rest = mhz - ((int)(mhz / 100) * 100);
        }
        else
        {
            int i = Array.FindLastIndex(Microwave, m => m.BaseMHz <= mhz && mhz < m.BaseMHz + 100);
            if (i < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(f), mhz, "no APRS frequency form for this frequency (APRS12c ch. 18)");
            }

            lead = Microwave[i].Letter.ToString();
            rest = mhz - Microwave[i].BaseMHz;
        }

        string digits = f.TenKilohertzResolution
            ? rest.ToString("00.00", CultureInfo.InvariantCulture) + " MHz"
            : rest.ToString("00.000", CultureInfo.InvariantCulture) + "MHz";
        w.Ascii(lead + digits);

        if (f.ToneType is { } tone)
        {
            w.Char(' ');
            string text = tone switch
            {
                AprsToneType.Off => "Toff",
                AprsToneType.ToneBurst => f.Narrow ? "l750" : "1750",
                AprsToneType.Tone => "T" + f.ToneValue!.Value.ToString("000", CultureInfo.InvariantCulture),
                AprsToneType.Ctcss => "C" + f.ToneValue!.Value.ToString("000", CultureInfo.InvariantCulture),
                _ => "D" + f.ToneValue!.Value.ToString("000", CultureInfo.InvariantCulture),
            };
            w.Ascii(f.Narrow && tone is not AprsToneType.ToneBurst ? char.ToLowerInvariant(text[0]) + text[1..] : text);
        }

        if (f.OffsetKHz is { } offset)
        {
            w.Char(' ').Char(offset < 0 ? '-' : '+').Digits(Math.Abs(offset) / 10, 3);
        }

        if (f.Range is { } range)
        {
            w.Char(' ').Char('R').Digits(range, 2).Char(f.RangeInKilometres ? 'k' : 'm');
        }
    }
}
