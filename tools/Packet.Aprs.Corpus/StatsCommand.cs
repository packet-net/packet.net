using System.Globalization;
using System.Text;

namespace Packet.Aprs.Corpus;

/// <summary>
/// Decodes the whole corpus and reports what the decoder made of it: packet types, diagnostics by
/// code, how much strict mode accepts, and whether decoded data re-encodes (exactly for clean
/// packets, idempotently for all). Anything the decoder throws is a bug and is listed first.
/// </summary>
internal static class StatsCommand
{
    private const int Samples = 8;

    public static int Run(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "aprs-corpus");
        long limit = args.Length > 1 ? long.Parse(args[1], CultureInfo.InvariantCulture) : long.MaxValue;

        using StreamWriter? diffLog = Environment.GetEnvironmentVariable("APRS_DIFF_LOG") is { } path ? new StreamWriter(path) : null;
        long total = 0, headerFailures = 0, strictOk = 0, clean = 0, exact = 0, idempotent = 0, reencodeThrew = 0, cleanIdempotent = 0, cleanEncoded = 0;
        var crashes = new Bucket();
        var types = new Dictionary<string, long>();
        var diagnostics = new SortedDictionary<string, Bucket>(StringComparer.Ordinal);
        var headerSamples = new Bucket();
        var notExact = new Bucket();
        var notIdempotent = new Bucket();
        var encodeFailures = new SortedDictionary<string, Bucket>(StringComparer.Ordinal);

        foreach (CorpusReader.Record record in CorpusReader.Read(dir))
        {
            if (total++ >= limit)
            {
                break;
            }

            byte[] line = record.Line;
            AprsPacket packet;
            try
            {
                if (!AprsPacket.TryDecode(line, out AprsPacket? p))
                {
                    headerFailures++;
                    headerSamples.Add(line);
                    continue;
                }

                packet = p;
                if (AprsPacket.TryDecode(line, out AprsPacket? strict, AprsParseOptions.Strict) && !strict.HasErrors)
                {
                    strictOk++;
                }
            }
#pragma warning disable CA1031 // a decoder exception is exactly what this tool hunts for
            catch (Exception ex)
#pragma warning restore CA1031
            {
                crashes.Add(line, $"{ex.GetType().Name}: {ex.Message}");
                continue;
            }

            string type = packet.Data is AprsUnrecognizedData u ? $"Unrecognized({u.Reason})" : packet.Data.GetType().Name;
            types[type] = types.GetValueOrDefault(type) + 1;
            foreach (AprsDiagnostic d in packet.Diagnostics.DistinctBy(d => (d.Code, d.Severity)))
            {
                string key = $"{d.Severity,-7} {d.Code}";
                if (!diagnostics.TryGetValue(key, out Bucket? b))
                {
                    diagnostics[key] = b = new Bucket();
                }

                b.Add(line, d.Message);
            }

            if (packet.Data is AprsUnrecognizedData)
            {
                continue;
            }

            bool isClean = !packet.HasWarnings && !packet.HasErrors;
            clean += isClean ? 1 : 0;
            try
            {
                byte[] reencoded = packet.Data.ToInformationField();
                AprsPacket again = AprsPacket.Decode(packet.Source, packet.Destination, packet.Path, reencoded);
                bool same = Equals(again.Data, packet.Data);
                idempotent += same ? 1 : 0;
                cleanEncoded += isClean ? 1 : 0;
                cleanIdempotent += isClean && same ? 1 : 0;
                if (!same)
                {
                    notIdempotent.Add(line, (isClean ? "CLEAN re-encoded: " : "warned, re-encoded: ") + Show(reencoded));
                }

                if (isClean)
                {
                    byte[] info = packet.Information.ToArray();
                    if (reencoded.AsSpan().SequenceEqual(info))
                    {
                        exact++;
                    }
                    else
                    {
                        notExact.Add(line, "re-encoded: " + Show(reencoded));
                        diffLog?.WriteLine($"{type}\t{Show(info)}\t{Show(reencoded)}");
                    }
                }
            }
            catch (ArgumentException ex)
            {
                reencodeThrew++;
                // Refusing a clean packet is the case to look at: nothing was tolerated to read it.
                string key = $"{(isClean ? "CLEAN " : "")}{type}: {ex.Message}";
                if (!encodeFailures.TryGetValue(key, out Bucket? b))
                {
                    encodeFailures[key] = b = new Bucket();
                }

                b.Add(line);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                crashes.Add(line, $"encode {ex.GetType().Name}: {ex.Message}");
            }
        }

        var o = new StringBuilder();
        long decoded = total - headerFailures - crashes.Count;
        o.AppendLine(CultureInfo.InvariantCulture, $"lines {total:N0}; header unparseable {headerFailures:N0}; decoder exceptions {crashes.Count:N0}");
        o.AppendLine(CultureInfo.InvariantCulture, $"strict mode accepts {Pct(strictOk, decoded)} of decodable packets");
        o.AppendLine(CultureInfo.InvariantCulture, $"re-encode: exact {exact:N0} of {clean:N0} clean ({Pct(exact, clean)}); encoder refused {reencodeThrew:N0}");
        o.AppendLine(CultureInfo.InvariantCulture, $"idempotent: clean {cleanIdempotent:N0} of {cleanEncoded:N0}; all {idempotent:N0} of {cleanEncoded + (decoded - clean - reencodeThrew):N0} encoded");
        crashes.Print(o, "DECODER EXCEPTIONS (bugs)");
        o.AppendLine().AppendLine("data types:");
        foreach ((string t, long n) in types.OrderByDescending(kv => kv.Value))
        {
            o.AppendLine(CultureInfo.InvariantCulture, $"  {n,10:N0}  {Pct(n, decoded),6}  {t}");
        }

        o.AppendLine().AppendLine("diagnostics (packets affected):");
        foreach ((string key, Bucket b) in diagnostics.OrderByDescending(kv => kv.Value.Count))
        {
            b.Print(o, $"{b.Count,10:N0}  {Pct(b.Count, decoded),6}  {key}");
        }

        headerSamples.Print(o, $"header unparseable ({headerSamples.Count:N0})");
        notExact.Print(o, $"clean but not byte-exact ({notExact.Count:N0})");
        notIdempotent.Print(o, $"not idempotent ({notIdempotent.Count:N0})");
        foreach ((string key, Bucket b) in encodeFailures.OrderByDescending(kv => kv.Value.Count))
        {
            b.Print(o, $"encoder refused {b.Count:N0}: {key}");
        }

        Console.Write(o.ToString());
        return 0;
    }

    private static string Pct(long n, long of) => of == 0 ? "-" : (100.0 * n / of).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    internal static string Show(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        foreach (byte b in bytes)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? ((char)b).ToString() : $"<0x{b:x2}>");
        }

        return sb.ToString();
    }

    private sealed class Bucket
    {
        private readonly List<string> samples = [];

        public long Count { get; private set; }

        public void Add(byte[] line, string? note = null)
        {
            Count++;
            if (samples.Count < Samples)
            {
                samples.Add(note is null ? Show(line) : $"{Show(line)}\n              -> {note}");
            }
        }

        public void Print(StringBuilder o, string title)
        {
            if (Count == 0)
            {
                return;
            }

            o.AppendLine(title);
            foreach (string s in samples)
            {
                o.AppendLine(CultureInfo.InvariantCulture, $"            {s}");
            }
        }
    }
}
