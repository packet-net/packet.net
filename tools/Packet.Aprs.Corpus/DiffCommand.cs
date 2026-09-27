using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.Aprs.Tests.Vectors;

namespace Packet.Aprs.Corpus;

/// <summary>
/// Differential comparison against other implementations of the conformance vectors (aprs-vectors
/// README, "Comparing implementations"; its tools/compare.py compares any number of dumps).
/// <c>lines</c> extracts the capture once into a language-neutral file (one hex-encoded TNC2 line
/// per line, gzip), so every implementation decodes exactly the same bytes. <c>dump</c> decodes
/// each of those lines; <c>encode</c> encodes data in the neutral form (tools/generate.py);
/// <c>build</c> builds packets from builder recipes (tools/generate.py --recipes). Each writes one
/// JSON object per input line, gzip, in the input's order.
/// </summary>
internal static class DiffCommand
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string Usage = """
        diff lines <corpus-dir> <lines.hex.gz>
        diff dump <lines.hex.gz> <out.jsonl.gz> [--limit n]
        diff encode <data.jsonl.gz> <out.jsonl.gz> [--limit n]
        diff build <recipes.jsonl.gz> <out.jsonl.gz> [--limit n]
        """;

    public static int Run(string[] args)
    {
        long limit = long.MaxValue;
        int at = Array.IndexOf(args, "--limit");
        if (at >= 0)
        {
            if (at + 1 >= args.Length || !long.TryParse(args[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out limit))
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            args = [.. args[..at], .. args[(at + 2)..]];
        }

        switch (args.FirstOrDefault())
        {
            case "lines" when args.Length == 3:
                return Lines(args[1], args[2]);
            case "dump" when args.Length == 3:
                return Each(args[1], args[2], limit, "packets", (n, line) => DecodeRecord(n, Convert.FromHexString(line)));
            case "encode" when args.Length == 3:
                return Each(args[1], args[2], limit, "data", (n, line) => EncodeRecord(n, JsonNode.Parse(line)!.AsObject()));
            case "build" when args.Length == 3:
                return Each(args[1], args[2], limit, "recipes", (n, line) => DiffBuild.Record(n, JsonNode.Parse(line)!.AsObject()));
            default:
                Console.Error.WriteLine(Usage);
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

    /// <summary>One output record per input line (numbered from 0), up to <paramref name="limit"/> lines.</summary>
    private static int Each(string input, string output, long limit, string what, Func<long, string, JsonObject> record)
    {
        using var inGz = new GZipStream(File.OpenRead(input), CompressionMode.Decompress);
        using var reader = new StreamReader(inGz, new UTF8Encoding(false));
        using var outGz = new GZipStream(File.Create(output), CompressionLevel.Fastest);
        using var w = new StreamWriter(outGz, new UTF8Encoding(false)) { NewLine = "\n" };
        long n = 0;
        while (n < limit && reader.ReadLine() is { } line)
        {
            w.WriteLine(record(n, line).ToJsonString(Compact));
            n++;
        }

        Console.WriteLine($"{output}: {n:N0} {what}");
        return 0;
    }

    // ------------------------------------------------------------------ decode

    private static JsonObject DecodeRecord(long n, byte[] line)
    {
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

        if (lenient is not null)
        {
            o["api"] = ApiView.Of(lenient);
        }

        return o;
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

        return Decoded(packet, withHeader: true);
    }

    /// <summary>A decoded packet in the neutral form: <c>header</c> (when asked for), <c>data</c> and <c>diagnostics</c>.</summary>
    internal static JsonObject Decoded(AprsPacket packet, bool withHeader)
    {
        var o = new JsonObject();
        if (withHeader)
        {
            o["header"] = NeutralJson.Header(packet);
        }

        o["data"] = NeutralJson.Data(packet.Data);
        o["diagnostics"] = NeutralJson.Diagnostics(packet.Diagnostics);
        return o;
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

    // ------------------------------------------------------------------ encode

    private static readonly AprsAddress EncodeSource = AprsAddress.Parse("N0CALL");
    private static readonly AprsAddress EncodeDestination = AprsAddress.Parse("APZ001");

    /// <summary>
    /// <c>data</c> in the neutral form, read into Packet.Aprs data as the vectors' encode cases are,
    /// then encoded: <c>written</c> with the information field, the destination (computed for Mic-E)
    /// and what the field decodes to, leniently, under <c>N0CALL&gt;</c> and that destination;
    /// <c>refused</c> when the encoder declines; <c>unsupported</c> when the data cannot be held.
    /// </summary>
    private static JsonObject EncodeRecord(long n, JsonObject input)
    {
        var o = new JsonObject { ["n"] = n };
        JsonObject neutral = input["data"]!.AsObject();

        // A third-party packet's inner packet is built, and so encoded, as it is read: its data is
        // read first on its own, so that the encoder refusing it is told apart from data that
        // cannot be held.
        bool thirdParty = neutral["packet"]?["data"] is JsonObject && (string?)neutral["type"] == "third-party";
        AprsData data;
        try
        {
            if (thirdParty)
            {
                _ = NeutralReader.Data(neutral["packet"]!["data"]!.AsObject());
            }

            data = NeutralReader.Data(neutral);
        }
        catch (Exception ex) when (thirdParty && ex is ArgumentException and not ArgumentNullException)
        {
            o["result"] = "refused";
            o["reason"] = $"the inner packet: {ex.Message}";
            return o;
        }
#pragma warning disable CA1031 // any failure to read the data is reported for that line alone
        catch (Exception ex)
#pragma warning restore CA1031
        {
            o["result"] = "unsupported";
            o["reason"] = $"the data cannot be held as Packet.Aprs data: {ex.Message}";
            return o;
        }

        byte[] info;
        AprsAddress destination = EncodeDestination;
        try
        {
            if (data is AprsMicEReport mic)
            {
                AprsPacket created = AprsPacket.CreateMicE(EncodeSource, mic);
                info = created.Information.ToArray();
                destination = created.Destination;
            }
            else
            {
                info = data.ToInformationField();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            o["result"] = "refused";
            o["reason"] = ex.Message;
            return o;
        }

        o["result"] = "written";
        o["info"] = Convert.ToHexStringLower(info);
        o["destination"] = destination.Value;
        AprsPacket again = AprsPacket.Decode(EncodeSource, destination, [], info, AprsParseOptions.Lenient);
        o["again"] = Decoded(again, withHeader: false);
        return o;
    }
}
