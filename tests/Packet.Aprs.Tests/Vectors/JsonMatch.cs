using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packet.Aprs.Tests.Vectors;

/// <summary>
/// Compares a produced value with an expected one by the rules in <c>spec/aprs/README.md</c>:
/// objects must have exactly the same keys, numbers agree within 1e-9 (relative above 1),
/// everything else exactly. Returns each difference with its path, so a failure says where.
/// </summary>
internal static class JsonMatch
{
    public static IReadOnlyList<string> Differences(JsonNode? expected, JsonNode? actual)
    {
        var differences = new List<string>();
        Compare(expected, actual, "$", differences);
        return differences;
    }

    /// <summary>Diagnostics compare as multisets of "severity:code" strings.</summary>
    public static IReadOnlyList<string> DiagnosticDifferences(JsonNode? expected, IEnumerable<string> actual)
    {
        List<string> want = expected is JsonArray a ? [.. a.Select(n => (string)n!)] : [];
        List<string> got = [.. actual];
        var differences = new List<string>();
        foreach (string w in want)
        {
            if (!got.Remove(w))
            {
                differences.Add($"diagnostics: expected {w}, not produced");
            }
        }

        differences.AddRange(got.Select(g => $"diagnostics: produced {g}, not expected"));
        return differences;
    }

    private static void Compare(JsonNode? expected, JsonNode? actual, string path, List<string> differences)
    {
        switch (expected, actual)
        {
            case (null, null):
                return;
            case (JsonObject e, JsonObject a):
                foreach (var (key, value) in e)
                {
                    if (!a.ContainsKey(key))
                    {
                        differences.Add($"{path}.{key}: expected {Show(value)}, not produced");
                    }
                    else
                    {
                        Compare(value, a[key], $"{path}.{key}", differences);
                    }
                }

                foreach (var (key, value) in a)
                {
                    if (!e.ContainsKey(key))
                    {
                        differences.Add($"{path}.{key}: produced {Show(value)}, not expected");
                    }
                }

                return;
            case (JsonArray e, JsonArray a):
                if (e.Count != a.Count)
                {
                    differences.Add($"{path}: expected {e.Count} items {Show(e)}, produced {a.Count} {Show(a)}");
                    return;
                }

                for (int i = 0; i < e.Count; i++)
                {
                    Compare(e[i], a[i], $"{path}[{i}]", differences);
                }

                return;
            case (JsonValue e, JsonValue a) when e.GetValueKind() == JsonValueKind.Number && a.GetValueKind() == JsonValueKind.Number:
                // Via the JSON text: a node built in memory holds whatever CLR type made it (int, decimal, double).
                double x = double.Parse(e.ToJsonString(), CultureInfo.InvariantCulture);
                double y = double.Parse(a.ToJsonString(), CultureInfo.InvariantCulture);
                if (Math.Abs(x - y) > 1e-9 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y))))
                {
                    differences.Add($"{path}: expected {Show(e)}, produced {Show(a)}");
                }

                return;
            default:
                if (Show(expected) != Show(actual))
                {
                    differences.Add($"{path}: expected {Show(expected)}, produced {Show(actual)}");
                }

                return;
        }
    }

    private static string Show(JsonNode? node) => node is null ? "null" : node.ToJsonString();

    public static string Describe(IReadOnlyList<string> differences) =>
        string.Join(Environment.NewLine, differences.Select(d => "  " + d).Prepend(string.Create(CultureInfo.InvariantCulture, $"{differences.Count} difference(s):")));
}
