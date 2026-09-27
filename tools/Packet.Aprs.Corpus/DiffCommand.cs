using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.Aprs.Tests.Vectors;

namespace Packet.Aprs.Corpus;

/// <summary>
/// Differential comparison against other implementations of the conformance vectors.
/// <c>lines</c> extracts the capture once into a language-neutral file (one hex-encoded TNC2 line
/// per line, gzip), so every implementation decodes exactly the same bytes. <c>dump</c> decodes
/// each of those lines and writes one JSON object per line in the vectors' neutral form (see
/// aprs-vectors tools/compare.py, which compares two such files).
/// </summary>
internal static class DiffCommand
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "lines" when args.Length == 3:
                return Lines(args[1], args[2]);
            case "dump" when args.Length == 3:
                return Dump(args[1], args[2]);
            default:
                Console.Error.WriteLine("diff lines <corpus-dir> <lines.hex.gz>   |   diff dump <lines.hex.gz> <out.jsonl.gz>");
                return 2;
        }
    }

    /// <summary>Every packet line from the completed hourly files (a .partial file is still being written).</summary>
    private static int Lines(string corpusDir, string output)
    {
        IEnumerable<string> files = Directory.EnumerateFiles(corpusDir, "aprsis-*.txt.gz", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
        using var gz = new GZipStream(File.Create(output), CompressionLevel.Fastest);
        using var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" };
        long n = 0;
        int fileCount = 0;
        foreach (string file in files)
        {
            fileCount++;
            foreach (CorpusReader.Record r in CorpusReader.ReadFile(file))
            {
                w.WriteLine(Convert.ToHexString(r.Line));
                n++;
            }
        }

        Console.WriteLine($"{output}: {n:N0} lines from {fileCount} files");
        return 0;
    }

    private static int Dump(string input, string output)
    {
        using var inGz = new GZipStream(File.OpenRead(input), CompressionMode.Decompress);
        using var reader = new StreamReader(inGz, Encoding.ASCII);
        using var outGz = new GZipStream(File.Create(output), CompressionLevel.Fastest);
        using var w = new StreamWriter(outGz, new UTF8Encoding(false)) { NewLine = "\n" };
        long n = 0;
        while (reader.ReadLine() is { } hex)
        {
            byte[] line = Convert.FromHexString(hex);
            var o = new JsonObject
            {
                ["n"] = n,
                ["lenient"] = Result(line, AprsParseOptions.Lenient, out AprsPacket? lenient),
                ["strict"] = Result(line, AprsParseOptions.Strict, out _),
                ["reencode"] = Reencode(lenient, out byte[]? written, out AprsAddress? writtenDestination),
            };
            if (written is not null)
            {
                o["written"] = Convert.ToHexStringLower(written);
            }

            if (writtenDestination is not null)
            {
                o["written_destination"] = writtenDestination.ToString();
            }

            w.WriteLine(o.ToJsonString(Compact));
            n++;
        }

        Console.WriteLine($"{output}: {n:N0} packets");
        return 0;
    }

    /// <summary>The header, data and diagnostics, or the header error's diagnostics.</summary>
    private static JsonObject Result(byte[] line, AprsParseOptions options, out AprsPacket? packet)
    {
        try
        {
            packet = AprsPacket.Decode(line, options);
        }
        catch (AprsFormatException ex)
        {
            packet = null;
            return new JsonObject { ["header_error"] = NeutralJson.Diagnostics(ex.Diagnostics) };
        }

        return new JsonObject
        {
            ["header"] = NeutralJson.Header(packet),
            ["data"] = NeutralJson.Data(packet.Data),
            ["diagnostics"] = NeutralJson.Diagnostics(packet.Diagnostics),
        };
    }

    /// <summary>
    /// What encoding the lenient data again gives, as the vectors' reencode check sees it:
    /// <c>identical</c>, <c>equivalent</c> (decodes, leniently, to the same data with no warnings or
    /// errors), <c>refused</c>, <c>fails</c> (it wrote something that does not), or <c>none</c> (nothing
    /// was decoded). <paramref name="written"/> is the information field it wrote, and
    /// <paramref name="writtenDestination"/> the Mic-E destination it computed.
    /// </summary>
    private static string Reencode(AprsPacket? packet, out byte[]? written, out AprsAddress? writtenDestination)
    {
        written = null;
        writtenDestination = null;
        if (packet is null || packet.Data is AprsUnrecognizedData)
        {
            return "none";
        }

        byte[] info;
        AprsAddress destination = packet.Destination;
        try
        {
            if (packet.Data is AprsMicEReport mic)
            {
                AprsPacket created = AprsPacket.CreateMicE(packet.Source, mic, packet.Path);
                info = created.Information.ToArray();
                destination = created.Destination;
                writtenDestination = destination;
            }
            else
            {
                info = packet.Data.ToInformationField();
            }
        }
        catch (ArgumentException)
        {
            return "refused";
        }

        written = info;
        byte[] original = packet.Information.ToArray();
        int end = original.Length;
        while (end > 0 && original[end - 1] is (byte)'\r' or (byte)'\n')
        {
            end--;
        }

        if (info.AsSpan().SequenceEqual(original.AsSpan(0, end)) && destination == packet.Destination)
        {
            return "identical";
        }

        AprsPacket again = AprsPacket.Decode(packet.Source, destination, packet.Path, info);
        bool same = JsonMatch.Differences(NeutralJson.Data(packet.Data), NeutralJson.Data(again.Data)).Count == 0;
        return same && !again.HasWarnings && !again.HasErrors ? "equivalent" : "fails";
    }
}
