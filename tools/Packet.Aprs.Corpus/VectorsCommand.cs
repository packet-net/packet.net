using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.Aprs.Tests.Vectors;

namespace Packet.Aprs.Corpus;

/// <summary>
/// Maintains the conformance vectors in <c>spec/aprs/cases</c> (format: <c>spec/aprs/README.md</c>).
/// <c>fill</c> completes every decode case that has an input but no <c>expect</c> with what
/// Packet.Aprs produces, for review, and rewrites each file in the standard layout.
/// <c>from-samples</c> turns corpus sample lines (from <c>curate</c>) into observed cases.
/// </summary>
internal static class VectorsCommand
{
    private static readonly string[] CaseOrder = ["id", "description", "source", "authority", "interpretations", "input", "expect", "strict", "reencode", "canonical_info"];
    private static readonly string[] ExpectOrder = ["header", "header_error", "data", "diagnostics", "device", "info", "destination", "refused"];

    public static int Run(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "fill" when args.Length > 1:
                foreach (string file in args[1..])
                {
                    JsonArray cases = JsonNode.Parse(File.ReadAllText(file))!["cases"]!.AsArray();
                    int filled = 0;
                    foreach (JsonObject c in cases.Select(n => n!.AsObject()))
                    {
                        if (c["expect"] is null && !c["input"]!.AsObject().ContainsKey("encode"))
                        {
                            Fill(c);
                            filled++;
                        }
                    }

                    File.WriteAllText(file, Layout(cases));
                    Console.WriteLine($"{file}: {cases.Count} cases, {filled} filled");
                }

                return 0;
            case "from-samples" when args.Length is 3 or 4:
                // New samples are added after the cases already in the file, which keep their ids.
                JsonArray observed = File.Exists(args[2]) ? JsonNode.Parse(File.ReadAllText(args[2]))!["cases"]!.AsArray() : [];
                var known = observed.Select(o => o!["input"]!.ToJsonString()).ToHashSet();
                int n = observed.Select(o => int.Parse(((string)o!["id"]!).Split('/')[1], CultureInfo.InvariantCulture)).DefaultIfEmpty().Max();
                string source = args.Length == 4 ? args[3] : $"APRS-IS {DateTime.UtcNow:yyyy-MM-dd}";
                int added = 0;
                foreach (string sample in File.ReadLines(args[1]))
                {
                    byte[] line = CurateCommand.Unescape(sample);
                    var input = IsUtf8(line)
                        ? new JsonObject { ["tnc2"] = Encoding.UTF8.GetString(line) }
                        : new JsonObject { ["tnc2_hex"] = Convert.ToHexString(line) };
                    if (!known.Add(input.ToJsonString()))
                    {
                        continue;
                    }

                    var c = new JsonObject
                    {
                        ["id"] = $"corpus/{++n:D4}",
                        ["description"] = "",
                        ["source"] = source,
                        ["authority"] = "observed",
                        ["input"] = input,
                    };
                    Fill(c);
                    c["description"] = Summary(c);
                    observed.Add(c);
                    added++;
                }

                File.WriteAllText(args[2], Layout(observed));
                Console.WriteLine($"{args[2]}: {observed.Count} observed cases, {added} new");
                return 0;
            case "refresh" when args.Length > 1:
                // After an intended change in behaviour: work out every observed case again, so the
                // change shows up as a reviewable diff of the case files.
                foreach (string file in args[1..])
                {
                    JsonArray cases = JsonNode.Parse(File.ReadAllText(file))!["cases"]!.AsArray();
                    int refreshed = 0;
                    foreach (JsonObject c in cases.Select(n => n!.AsObject()).Where(c => (string?)c["authority"] == "observed" && !c["input"]!.AsObject().ContainsKey("encode")))
                    {
                        bool summarised = ((string?)c["description"])?.StartsWith(SummaryPrefix, StringComparison.Ordinal) == true;
                        c.Remove("expect");
                        c.Remove("strict");
                        c.Remove("reencode");
                        c.Remove("canonical_info");
                        Fill(c);
                        if (summarised)
                        {
                            c["description"] = Summary(c);
                        }

                        refreshed++;
                    }

                    File.WriteAllText(file, Layout(cases));
                    Console.WriteLine($"{file}: {refreshed} observed cases refreshed");
                }

                return 0;
            default:
                Console.Error.WriteLine("vectors fill <cases.json>...   |   vectors from-samples <samples.txt> <cases.json> [source]   |   vectors refresh <cases.json>...");
                return 2;
        }
    }

    private const string SummaryPrefix = "Observed on APRS-IS: ";

    private static readonly string[] SummaryParts = ["compressed", "ambiguity", "timestamp", "course_degrees", "phg", "range_miles", "dfs", "altitude_feet", "dao", "telemetry", "frequency", "weather", "storm", "area", "df_bearing", "locator", "device_suffix", "message_id", "reply_ack"];

    /// <summary>A one-line neutral summary of an observed case: its type, the optional parts present, and any warnings or errors.</summary>
    private static string Summary(JsonObject c)
    {
        JsonObject expect = c["expect"]!.AsObject();
        if (expect["data"] is not JsonObject data)
        {
            return SummaryPrefix + "the header cannot be decoded.";
        }

        string[] parts = [.. SummaryParts
            .Where(data.ContainsKey).Select(k => k.Replace('_', ' ').Replace(" degrees", "", StringComparison.Ordinal).Replace(" feet", "", StringComparison.Ordinal).Replace(" miles", "", StringComparison.Ordinal))];
        string type = data["type"] is { } t && (string)t! == "unrecognized" ? $"not decoded ({(string?)data["reason"]})" : (string)data["type"]!;
        string[] problems = [.. (expect["diagnostics"] as JsonArray ?? []).Select(d => (string)d!).Where(d => !d.StartsWith("info:", StringComparison.Ordinal)).Distinct()];
        return $"{SummaryPrefix}{type}{(parts.Length > 0 ? " with " + string.Join(", ", parts) : "")}{(problems.Length > 0 ? "; " + string.Join(", ", problems) : "")}.";
    }

    /// <summary>Adds expect / strict / reencode for a decode case from what Packet.Aprs does.</summary>
    private static void Fill(JsonObject c)
    {
        JsonObject input = c["input"]!.AsObject();
        AprsPacket? lenient = Decode(input, AprsParseOptions.Lenient, out IReadOnlyList<AprsDiagnostic> lenientHeader);
        AprsPacket? strict = Decode(input, AprsParseOptions.Strict, out IReadOnlyList<AprsDiagnostic> strictHeader);

        var expect = new JsonObject();
        if (lenient is null)
        {
            expect["header_error"] = NeutralJson.Diagnostics(lenientHeader);
        }
        else
        {
            if (lenient.Path.Count > 0 || input.ContainsKey("tnc2") || input.ContainsKey("ax25_hex"))
            {
                expect["header"] = NeutralJson.Header(lenient);
            }

            expect["data"] = NeutralJson.Data(lenient.Data);
            if (lenient.Diagnostics.Count > 0)
            {
                expect["diagnostics"] = NeutralJson.Diagnostics(lenient.Diagnostics);
            }

            // A Mic-E radio names itself in the status text; recording the lookup keeps device identification in the vectors.
            if (lenient.Data is AprsMicEReport && AprsDeviceIdentification.Identify(lenient) is { } device)
            {
                expect["device"] = new JsonObject { ["vendor"] = device.Vendor, ["model"] = device.Model };
            }
        }

        c["expect"] = expect;
        string Signature(AprsPacket? p, IReadOnlyList<AprsDiagnostic> header) =>
            p is null ? "header:" + NeutralJson.Diagnostics(header).ToJsonString() : NeutralJson.Data(p.Data).ToJsonString() + NeutralJson.Diagnostics(p.Diagnostics).ToJsonString();
        if (Signature(lenient, lenientHeader) == Signature(strict, strictHeader))
        {
            c["strict"] = "same";
        }
        else if (strict is null)
        {
            c["strict"] = new JsonObject { ["rejected_by"] = NeutralJson.Kebab(strictHeader.First(d => d.Severity == AprsDiagnosticSeverity.Error).Code.ToString()), ["header"] = true };
        }
        else if (strict.Data is AprsUnrecognizedData && strict.Diagnostics.FirstOrDefault(d => d.Severity == AprsDiagnosticSeverity.Error) is { Severity: AprsDiagnosticSeverity.Error } error)
        {
            c["strict"] = new JsonObject { ["rejected_by"] = NeutralJson.Kebab(error.Code.ToString()) };
        }
        else
        {
            var s = new JsonObject { ["data"] = NeutralJson.Data(strict.Data) };
            if (strict.Diagnostics.Count > 0)
            {
                s["diagnostics"] = NeutralJson.Diagnostics(strict.Diagnostics);
            }

            c["strict"] = s;
        }

        if (lenient is null || lenient.Data is AprsUnrecognizedData)
        {
            return;
        }

        try
        {
            byte[] again = lenient.Data is AprsMicEReport mic ? AprsPacket.CreateMicE(lenient.Source, mic, lenient.Path).Information.ToArray() : lenient.Data.ToInformationField();
            byte[] original = lenient.Information.ToArray();
            int end = original.Length;
            while (end > 0 && original[end - 1] is (byte)'\r' or (byte)'\n')
            {
                end--;
            }

            bool sameDestination = lenient.Data is not AprsMicEReport m || AprsPacket.CreateMicE(lenient.Source, m).Destination == lenient.Destination;
            if (again.AsSpan().SequenceEqual(original.AsSpan(0, end)) && sameDestination)
            {
                c["reencode"] = "identical";
            }
            else
            {
                // A clean packet must read back the same, so that is asserted whatever the encoder
                // does today. A packet read under a tolerance can lose what it had no proper place
                // for (a wind direction finer than compressed course allows), so it is asserted
                // only where it holds.
                AprsPacket back = AprsPacket.Decode(lenient.Source, lenient.Destination, lenient.Path, again);
                bool readsBack = !back.HasWarnings && !back.HasErrors && NeutralJson.Data(back.Data).ToJsonString() == NeutralJson.Data(lenient.Data).ToJsonString();
                if (readsBack || (!lenient.HasWarnings && !lenient.HasErrors))
                {
                    c["reencode"] = "equivalent";
                    c["canonical_info"] = Encoding.UTF8.GetString(again);
                }
            }
        }
        catch (ArgumentException)
        {
            c["reencode"] = "refused";
        }
    }

    private static AprsPacket? Decode(JsonObject input, AprsParseOptions options, out IReadOnlyList<AprsDiagnostic> headerDiagnostics)
    {
        headerDiagnostics = [];
        try
        {
            if (input["tnc2"] is { } tnc2)
            {
                return AprsPacket.Decode(Encoding.UTF8.GetBytes((string)tnc2!), options);
            }

            if (input["tnc2_hex"] is { } tnc2Hex)
            {
                return AprsPacket.Decode(Convert.FromHexString((string)tnc2Hex!), options);
            }

            if (input["ax25_hex"] is { } ax25)
            {
                return AprsPacket.DecodeAx25(Convert.FromHexString((string)ax25!), options);
            }

            byte[] info = input["info_hex"] is { } infoHex ? Convert.FromHexString((string)infoHex!) : Encoding.UTF8.GetBytes((string)input["info"]!);
            IEnumerable<AprsPathEntry> path = input["path"] is JsonArray pa
                ? pa.Select(e => (string)e!).Select(e => e.EndsWith('*') ? new AprsPathEntry(AprsAddress.Parse(e[..^1]), true) : new AprsPathEntry(AprsAddress.Parse(e)))
                : [];
            return AprsPacket.Decode(AprsAddress.Parse((string?)input["source"] ?? "N0CALL"), AprsAddress.Parse((string?)input["destination"] ?? "APZ001"), path, info, options);
        }
        catch (AprsFormatException ex)
        {
            headerDiagnostics = ex.Diagnostics;
            return null;
        }
    }

    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ layout

    /// <summary>One key per line for each case (and its expect), nested values compact: easy to review in a diff.</summary>
    public static string Layout(JsonArray cases)
    {
        var sb = new StringBuilder("{\n  \"cases\": [\n");
        for (int i = 0; i < cases.Count; i++)
        {
            JsonObject c = cases[i]!.AsObject();
            string[] unknown = [.. c.Select(kv => kv.Key).Except(CaseOrder)];
            if (unknown.Length > 0)
            {
                throw new InvalidOperationException($"{(string?)c["id"]}: unknown key(s) {string.Join(", ", unknown)}");
            }

            sb.Append("    {\n");
            string[] keys = [.. CaseOrder.Where(c.ContainsKey)];
            for (int k = 0; k < keys.Length; k++)
            {
                string comma = k < keys.Length - 1 ? "," : "";
                if (keys[k] == "expect")
                {
                    JsonObject expect = c["expect"]!.AsObject();
                    string[] ek = [.. ExpectOrder.Where(expect.ContainsKey)];
                    sb.Append("      \"expect\": {\n");
                    for (int e = 0; e < ek.Length; e++)
                    {
                        sb.Append("        ").Append(Compact(JsonValue.Create(ek[e]))).Append(": ").Append(Compact(expect[ek[e]])).Append(e < ek.Length - 1 ? ",\n" : "\n");
                    }

                    sb.Append("      }").Append(comma).Append('\n');
                }
                else
                {
                    sb.Append("      ").Append(Compact(JsonValue.Create(keys[k]))).Append(": ").Append(Compact(c[keys[k]])).Append(comma).Append('\n');
                }
            }

            sb.Append("    }").Append(i < cases.Count - 1 ? ",\n" : "\n");
        }

        return sb.Append("  ]\n}\n").ToString();
    }

    private static readonly JsonSerializerOptions StringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Compact(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{Compact(JsonValue.Create(kv.Key))}: {Compact(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(Compact)) + "]",
        _ => node.ToJsonString(StringOptions),
    };
}
