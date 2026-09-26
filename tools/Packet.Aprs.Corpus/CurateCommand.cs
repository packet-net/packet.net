using System.Text;

namespace Packet.Aprs.Corpus;

/// <summary>
/// Picks a small regression set from the corpus: for each distinct "shape" (data type, the set of
/// diagnostic codes, and which optional elements are present), up to N example packets. Writes them
/// as escaped text lines for tests/Packet.Aprs.Tests/Corpus/samples.txt.
/// </summary>
internal static class CurateCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("curate <corpus-dir> <samples-out> [per-shape=2]");
            return 2;
        }

        int perShape = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 2;
        var shapes = new Dictionary<string, int>();
        var seenSources = new HashSet<string>();
        var picked = new List<string>();
        foreach (CorpusReader.Record record in CorpusReader.Read(args[0]))
        {
            if (!AprsPacket.TryDecode(record.Line, out AprsPacket? packet))
            {
                continue;
            }

            string shape = Shape(packet);
            int count = shapes.GetValueOrDefault(shape);

            // Prefer variety of senders within a shape.
            if (count >= perShape || !seenSources.Add(packet.Source.Value + "|" + shape))
            {
                continue;
            }

            shapes[shape] = count + 1;
            picked.Add(Escape(record.Line));
        }

        File.WriteAllLines(args[1], picked);
        Console.WriteLine($"{picked.Count} samples across {shapes.Count} shapes");
        return 0;
    }

    private static string Shape(AprsPacket packet)
    {
        AprsData data = packet.Data is AprsThirdPartyTraffic t ? t.Packet.Data : packet.Data;
        var sb = new StringBuilder(data is AprsUnrecognizedData u ? $"Unrecognized({u.Reason})" : data.GetType().Name);
        foreach (AprsDiagnosticCode code in packet.Diagnostics.Select(d => d.Code).Distinct().Order())
        {
            sb.Append('|').Append(code);
        }

        if (data is AprsPositionedData p)
        {
            sb.Append(p.IsCompressed ? "|compressed" : "")
                .Append(p.Position.Ambiguity > 0 ? "|ambiguous" : "")
                .Append(p.CourseDegrees is not null ? "|course" : "")
                .Append(p.Phg is not null ? "|phg" : "")
                .Append(p.RadioRangeMiles is not null ? "|rng" : "")
                .Append(p.AltitudeFeet is not null ? "|alt" : "")
                .Append(p.Dao is not null ? "|dao" : "")
                .Append(p.Telemetry is not null ? "|tlm" : "")
                .Append(p.Frequency is not null ? "|freq" : "")
                .Append(p.Weather is not null ? "|wx" : "")
                .Append(p.Storm is not null ? "|storm" : "")
                .Append(p.AreaObject is not null ? "|area" : "")
                .Append(p.DfBearing is not null ? "|df" : "")
                .Append(p is AprsMicEReport m ? $"|type:{m.TypeCode}|suffix:{m.DeviceSuffix.Length > 0}" : "");
        }

        return sb.ToString();
    }

    /// <summary>Printable ASCII kept; backslash and everything else written as \xNN.</summary>
    internal static string Escape(byte[] line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (byte b in line)
        {
            sb.Append(b is >= 0x20 and < 0x7F && b != (byte)'\\' ? ((char)b).ToString() : $"\\x{b:x2}");
        }

        return sb.ToString();
    }
}
