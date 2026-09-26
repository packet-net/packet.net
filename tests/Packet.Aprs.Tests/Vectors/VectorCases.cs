using System.Text;
using System.Text.Json.Nodes;

namespace Packet.Aprs.Tests.Vectors;

/// <summary>
/// The cases in <c>spec/aprs/cases/*.json</c> (a git submodule of packet-net/aprs-vectors), loaded
/// once and looked up by id.
/// </summary>
internal static class VectorCases
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonObject>> All = new(Load);

    public static IReadOnlyDictionary<string, JsonObject> ById => All.Value;

    public static JsonObject Get(string id) => ById[id];

    public static IEnumerable<string> Ids(Func<JsonObject, bool> where) => ById.Where(kv => where(kv.Value)).Select(kv => kv.Key).Order(StringComparer.Ordinal);

    public static bool IsEncodeCase(JsonObject c) => c["input"]!.AsObject().ContainsKey("encode");

    public static string Directory => TestPaths.InRepo("spec", "aprs");

    private static Dictionary<string, JsonObject> Load()
    {
        string folder = System.IO.Path.Combine(Directory, "cases");
        if (!System.IO.Directory.Exists(folder))
        {
            throw new InvalidOperationException("spec/aprs is a git submodule (packet-net/aprs-vectors) and is not checked out: run 'git submodule update --init spec/aprs'.");
        }

        var cases = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (string file in System.IO.Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            JsonArray array = JsonNode.Parse(File.ReadAllText(file))!["cases"]!.AsArray();
            foreach (JsonNode? node in array)
            {
                JsonObject c = node!.AsObject();
                string id = (string)c["id"]!;
                if (!cases.TryAdd(id, c))
                {
                    throw new InvalidOperationException($"duplicate case id '{id}' in {System.IO.Path.GetFileName(file)}");
                }
            }
        }

        return cases;
    }

    /// <summary>Decodes a case's input, returning the packet, or null with the diagnostics when the header is unusable.</summary>
    public static AprsPacket? Decode(JsonObject input, AprsParseOptions options, out IReadOnlyList<AprsDiagnostic> headerDiagnostics)
    {
        headerDiagnostics = [];
        try
        {
            if (input["tnc2"] is { } tnc2)
            {
                return AprsPacket.Decode(System.Text.Encoding.UTF8.GetBytes((string)tnc2!), options);
            }

            if (input["tnc2_hex"] is { } tnc2Hex)
            {
                return AprsPacket.Decode(Convert.FromHexString((string)tnc2Hex!), options);
            }

            if (input["ax25_hex"] is { } ax25)
            {
                return AprsPacket.DecodeAx25(Convert.FromHexString((string)ax25!), options);
            }

            byte[] info = input["info_hex"] is { } infoHex ? Convert.FromHexString((string)infoHex!) : System.Text.Encoding.UTF8.GetBytes((string)input["info"]!);
            return AprsPacket.Decode(Source(input), Destination(input), PathOf(input), info, options);
        }
        catch (AprsFormatException ex)
        {
            headerDiagnostics = ex.Diagnostics;
            return null;
        }
    }

    public static AprsAddress Source(JsonObject input) => AprsAddress.Parse((string?)input["source"] ?? "N0CALL");

    public static AprsAddress Destination(JsonObject input) => AprsAddress.Parse((string?)input["destination"] ?? "APZ001");

    private static IEnumerable<AprsPathEntry> PathOf(JsonObject input) =>
        input["path"] is JsonArray path
            ? path.Select(p => (string)p!).Select(p => p.EndsWith('*') ? new AprsPathEntry(AprsAddress.Parse(p[..^1]), true) : new AprsPathEntry(AprsAddress.Parse(p)))
            : [];
}
