using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Packet.Aprs.Tests.Vectors;

/// <summary>
/// Runs the language-neutral cases in <c>spec/aprs/cases</c> against Packet.Aprs. <c>spec/aprs</c>
/// is a git submodule of packet-net/aprs-vectors, whose README describes the format; every test
/// here is named by case id, and a failure lists each field that differs.
/// </summary>
public class AprsVectorTests
{
    public static TheoryData<string> DecodeCases => [.. VectorCases.Ids(c => !VectorCases.IsEncodeCase(c))];

    public static TheoryData<string> FlagCases => [.. VectorCases.Ids(c => c["strict"] is JsonObject && FlagFor(c) is not null)];

    public static TheoryData<string> ReencodeCases => [.. VectorCases.Ids(c => c["reencode"] is not null)];

    public static TheoryData<string> EncodeCases => [.. VectorCases.Ids(VectorCases.IsEncodeCase)];

    [Theory]
    [MemberData(nameof(DecodeCases))]
    public void Lenient_decoding(string id)
    {
        JsonObject c = VectorCases.Get(id);
        AprsPacket? packet = VectorCases.Decode(Input(c), AprsParseOptions.Lenient, out var headerDiagnostics);
        Pass(c, Check(c["expect"]!.AsObject(), packet, headerDiagnostics));
    }

    [Theory]
    [MemberData(nameof(DecodeCases))]
    public void Strict_decoding(string id)
    {
        JsonObject c = VectorCases.Get(id);
        AprsPacket? packet = VectorCases.Decode(Input(c), AprsParseOptions.Strict, out var headerDiagnostics);
        Pass(c, CheckStrict(c, packet, headerDiagnostics));
    }

    /// <summary>Where strict and lenient differ, turning off only the flag for that deviation gives the strict result.</summary>
    [Theory]
    [MemberData(nameof(FlagCases))]
    public void Only_its_own_flag_makes_the_difference(string id)
    {
        JsonObject c = VectorCases.Get(id);
        PropertyInfo flag = typeof(AprsParseOptions).GetProperty(FlagFor(c)!)!;
        AprsParseOptions options = AprsParseOptions.Lenient with { };
        flag.SetValue(options, false);
        AprsPacket? packet = VectorCases.Decode(Input(c), options, out var headerDiagnostics);
        Pass(c, CheckStrict(c, packet, headerDiagnostics), $"with only {flag.Name} off");
    }

    [Theory]
    [MemberData(nameof(ReencodeCases))]
    public void Reencoding(string id)
    {
        JsonObject c = VectorCases.Get(id);
        AprsPacket packet = VectorCases.Decode(Input(c), AprsParseOptions.Lenient, out _)!;
        string expected = (string)c["reencode"]!;
        byte[] info;
        try
        {
            info = packet.Data is AprsMicEReport mic ? AprsPacket.CreateMicE(packet.Source, mic, packet.Path).Information.ToArray() : packet.Data.ToInformationField();
        }
        catch (ArgumentException ex)
        {
            Pass(c, expected == "refused" ? [] : [$"reencode: expected {expected}, but the encoder refused: {ex.Message}"]);
            return;
        }

        var differences = new List<string>();
        byte[] original = packet.Information.ToArray();
        int end = original.Length;
        while (end > 0 && original[end - 1] is (byte)'\r' or (byte)'\n')
        {
            end--;
        }

        switch (expected)
        {
            case "refused":
                differences.Add($"reencode: expected the encoder to refuse, it wrote {Show(info)}");
                break;
            case "identical":
                if (!info.AsSpan().SequenceEqual(original.AsSpan(0, end)))
                {
                    differences.Add($"reencode: expected the input back, got {Show(info)}");
                }

                if (packet.Data is AprsMicEReport m && AprsPacket.CreateMicE(packet.Source, m).Destination != packet.Destination)
                {
                    differences.Add($"reencode: expected the Mic-E destination {packet.Destination} back, got {AprsPacket.CreateMicE(packet.Source, m).Destination}");
                }

                break;
            case "equivalent":
                AprsPacket again = AprsPacket.Decode(packet.Source, packet.Destination, packet.Path, info);
                differences.AddRange(JsonMatch.Differences(NeutralJson.Data(packet.Data), NeutralJson.Data(again.Data)).Select(d => "reencode: " + d));
                if (again.HasErrors || again.HasWarnings)
                {
                    differences.Add($"reencode: {Show(info)} decodes with {string.Join(", ", again.Diagnostics.Select(d => NeutralJson.Diagnostic(d.Severity, d.Code)))}");
                }

                if (c["canonical_info"] is { } canonical && (string)canonical! != System.Text.Encoding.UTF8.GetString(info))
                {
                    differences.Add($"canonical_info: expected {canonical.ToJsonString()}, wrote {Show(info)}");
                }

                break;
            default:
                differences.Add($"reencode: unknown expectation '{expected}'");
                break;
        }

        Pass(c, differences);
    }

    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void Encoding_from_data(string id)
    {
        JsonObject c = VectorCases.Get(id);
        JsonObject input = Input(c);
        JsonObject expect = c["expect"]!.AsObject();
        AprsData data = NeutralReader.Data(input["encode"]!.AsObject());
        AprsPacket packet;
        try
        {
            packet = data is AprsMicEReport mic
                ? AprsPacket.CreateMicE(VectorCases.Source(input), mic)
                : AprsPacket.Create(VectorCases.Source(input), VectorCases.Destination(input), data);
        }
        catch (ArgumentException ex)
        {
            Pass(c, (bool?)expect["refused"] == true ? [] : [$"encode: expected {expect["info"]?.ToJsonString()}, but the encoder refused: {ex.Message}"]);
            return;
        }

        byte[] info = packet.Information.ToArray();
        var differences = new List<string>();
        if ((bool?)expect["refused"] == true)
        {
            differences.Add($"encode: expected a refusal, wrote {Show(info)}");
        }
        else
        {
            if (System.Text.Encoding.UTF8.GetString(info) != (string)expect["info"]!)
            {
                differences.Add($"encode: expected {expect["info"]!.ToJsonString()}, wrote {Show(info)}");
            }

            if (expect["destination"] is { } destination && (string)destination! != packet.Destination.Value)
            {
                differences.Add($"encode: expected destination {destination.ToJsonString()}, computed \"{packet.Destination.Value}\"");
            }
        }

        Pass(c, differences);
    }

    /// <summary>The reader is the writer's inverse: every decoded case's data reads back to the same neutral form.</summary>
    [Theory]
    [MemberData(nameof(DecodeCases))]
    public void Neutral_form_reads_back(string id)
    {
        JsonObject c = VectorCases.Get(id);
        AprsPacket? packet = VectorCases.Decode(Input(c), AprsParseOptions.Lenient, out _);
        if (packet is null || packet.Data is AprsUnrecognizedData || packet.Data is AprsThirdPartyTraffic { Packet.Data: AprsUnrecognizedData })
        {
            return; // nothing to build: undecoded data cannot be encoded
        }

        JsonObject written = JsonNode.Parse(NeutralJson.Data(packet.Data).ToJsonString())!.AsObject();
        JsonObject readBack = NeutralJson.Data(NeutralReader.Data(written));
        Pass(c, JsonMatch.Differences(written, readBack).Select(d => "read back: " + d).ToList());
    }

    // ------------------------------------------------------------------ the case files

    private static readonly string[] CaseKeys = ["id", "description", "source", "authority", "interpretations", "input", "expect", "strict", "reencode", "canonical_info"];
    private static readonly string[] InputKeys = ["tnc2", "tnc2_hex", "ax25_hex", "info", "info_hex", "encode", "source", "destination", "path"];
    private static readonly string[] ExpectKeys = ["data", "diagnostics", "header", "header_error", "device", "info", "destination", "refused"];

    /// <summary>
    /// The cases' own consistency (schema, ids, codes, interpretation links) is checked in
    /// packet-net/aprs-vectors. What is checked here is this runner: a key it does not know would
    /// otherwise be skipped silently, so a format change that adds one fails until the runner
    /// checks it.
    /// </summary>
    [Fact]
    public void The_runner_checks_every_key_the_cases_use()
    {
        var unknown = new List<string>();
        foreach (var (id, c) in VectorCases.ById)
        {
            unknown.AddRange(Unknown(c, CaseKeys).Select(k => $"{id}: '{k}'"));
            unknown.AddRange(Unknown(c["input"]!.AsObject(), InputKeys).Select(k => $"{id}: input '{k}'"));
            unknown.AddRange(Unknown(c["expect"]!.AsObject(), ExpectKeys).Select(k => $"{id}: expect '{k}'"));
        }

        unknown.Should().BeEmpty();
    }

    [Fact]
    public void The_code_catalogue_matches_the_library()
    {
        JsonObject[] catalogue = [.. CodeCatalogue()];
        catalogue.Select(c => (string)c["id"]!).Should().BeEquivalentTo(Enum.GetNames<AprsDiagnosticCode>().Select(NeutralJson.Kebab), "every AprsDiagnosticCode has exactly one entry in spec/aprs/codes.json");
        catalogue.Where(c => (bool)c["tolerable"]!).Select(c => (string)c["id"]!).Should().BeEquivalentTo(Flags.Keys, "a code is tolerable exactly when an AprsParseOptions flag tolerates it");
        Flags.Values.Should().BeEquivalentTo(typeof(AprsParseOptions).GetProperties().Where(p => p.PropertyType == typeof(bool)).Select(p => p.Name), "every flag is mapped to the code it tolerates");
    }

    /// <summary>Each tolerance needs a case showing both sides of it: strict rejecting (or reading differently) and lenient accepting.</summary>
    [Fact]
    public void Every_tolerance_flag_has_a_case()
    {
        HashSet<string> covered = [.. VectorCases.ById.Values.Where(c => c["strict"] is JsonObject).Select(FlagFor).OfType<string>()];
        Flags.Values.Except(covered).Should().BeEmpty("each flag needs a case in spec/aprs/cases whose strict result differs");
    }

    /// <summary>Which AprsParseOptions flag tolerates each code. Packet.Aprs's own detail, kept out of the neutral files.</summary>
    private static readonly Dictionary<string, string> Flags = new()
    {
        ["trailing-line-break"] = nameof(AprsParseOptions.StripTrailingLineBreaks),
        ["non-utf8-text"] = nameof(AprsParseOptions.AllowNonUtf8Text),
        ["lowercase-hemisphere"] = nameof(AprsParseOptions.AllowLowercaseHemisphere),
        ["out-of-range-value"] = nameof(AprsParseOptions.AllowOutOfRangeValues),
        ["object-without-timestamp"] = nameof(AprsParseOptions.AllowObjectWithoutTimestamp),
        ["object-name-not-padded"] = nameof(AprsParseOptions.AllowShortObjectName),
        ["incomplete-weather"] = nameof(AprsParseOptions.AllowIncompleteWeather),
        ["weather-comment"] = nameof(AprsParseOptions.AllowWeatherComment),
        ["data-extension-in-comment"] = nameof(AprsParseOptions.RecognizeDataExtensionInComment),
        ["kenwood-ff-padding"] = nameof(AprsParseOptions.AllowKenwoodFfPadding),
        ["empty-destination"] = nameof(AprsParseOptions.AllowEmptyDestination),
        ["empty-path-entry"] = nameof(AprsParseOptions.AllowEmptyPathEntry),
        ["multiple-used-markers"] = nameof(AprsParseOptions.AllowMultipleUsedMarkers),
        ["nul-padded-address"] = nameof(AprsParseOptions.AllowNulPaddedAddress),
        ["message-id-on-ack"] = nameof(AprsParseOptions.AllowMessageIdOnAck),
        ["unpadded-addressee"] = nameof(AprsParseOptions.AllowUnpaddedAddressee),
        ["invalid-ax25-address-characters"] = nameof(AprsParseOptions.AllowInvalidAx25AddressCharacters),
        ["missing-space-after-locator"] = nameof(AprsParseOptions.AllowMissingSpaceAfterLocator),
        ["invalid-telemetry"] = nameof(AprsParseOptions.AllowIncompleteTelemetry),
        ["compression-type-reserved-bits"] = nameof(AprsParseOptions.AllowCompressionTypeReservedBits),
        ["invalid-timestamp"] = nameof(AprsParseOptions.AllowInvalidTimestamp),
        ["malformed-timestamp"] = nameof(AprsParseOptions.AllowMalformedTimestamp),
        ["position-not-at-start"] = nameof(AprsParseOptions.AllowPositionNotAtStart),
        ["non-standard-weather-field-width"] = nameof(AprsParseOptions.AllowNonStandardWeatherFieldWidths),
        ["wind-fields-instead-of-extension"] = nameof(AprsParseOptions.AllowWindFieldsInPositionWeather),
        ["wind-extension-after-compressed"] = nameof(AprsParseOptions.AllowWindExtensionAfterCompressed),
        ["mic-e-altitude-not-first"] = nameof(AprsParseOptions.AllowMicEAltitudeAnywhere),
        ["dao-with-ambiguity"] = nameof(AprsParseOptions.AllowDaoWithAmbiguity),
        ["brace-in-message-text"] = nameof(AprsParseOptions.AllowBraceInMessageText),
        ["invalid-addressee-characters"] = nameof(AprsParseOptions.AllowInvalidAddresseeCharacters),
        ["letter-group-bulletin"] = nameof(AprsParseOptions.AllowLetterGroupBulletin),
        ["free-text-capabilities"] = nameof(AprsParseOptions.AllowFreeTextCapabilities),
    };

    // ------------------------------------------------------------------ checking

    private static IReadOnlyList<string> Check(JsonObject expect, AprsPacket? packet, IReadOnlyList<AprsDiagnostic> headerDiagnostics)
    {
        var differences = new List<string>();
        if (expect["header_error"] is { } headerError)
        {
            if (packet is not null)
            {
                return [$"expected a header error, decoded {NeutralJson.Data(packet.Data).ToJsonString()}"];
            }

            return JsonMatch.DiagnosticDifferences(headerError, Strings(headerDiagnostics));
        }

        if (packet is null)
        {
            return [$"header error: {string.Join(", ", Strings(headerDiagnostics))}"];
        }

        differences.AddRange(JsonMatch.Differences(expect["data"], NeutralJson.Data(packet.Data)));
        differences.AddRange(JsonMatch.DiagnosticDifferences(expect["diagnostics"], Strings(packet.Diagnostics)));
        if (expect["header"] is { } header)
        {
            differences.AddRange(JsonMatch.Differences(header, NeutralJson.Header(packet)).Select(d => "header" + d.TrimStart('$')));
        }

        if (expect["device"] is JsonObject device)
        {
            AprsDevice? found = AprsDeviceIdentification.Identify(packet);
            JsonObject actual = found is null ? [] : new JsonObject { ["vendor"] = found.Vendor, ["model"] = found.Model };
            differences.AddRange(JsonMatch.Differences(device, actual).Select(d => "device" + d.TrimStart('$')));
        }

        return differences;
    }

    private static IReadOnlyList<string> CheckStrict(JsonObject c, AprsPacket? packet, IReadOnlyList<AprsDiagnostic> headerDiagnostics)
    {
        switch (c["strict"])
        {
            case null:
            case JsonValue v when (string)v! == "same":
                return Check(c["expect"]!.AsObject(), packet, headerDiagnostics);
            case JsonObject s when s["rejected_by"] is { } code:
                string error = $"error:{(string)code!}";
                if ((bool?)s["header"] == true)
                {
                    return packet is null && Strings(headerDiagnostics).Contains(error) ? []
                        : [$"strict: expected the header to be rejected with {error}, got {Describe(packet, headerDiagnostics)}"];
                }

                return packet is { Data: AprsUnrecognizedData { Reason: AprsUnrecognizedReason.Malformed } } && Strings(packet.Diagnostics).Contains(error) ? []
                    : [$"strict: expected rejection with {error}, got {Describe(packet, headerDiagnostics)}"];
            case JsonObject s:
                return Check(s, packet, headerDiagnostics);
            default:
                return [$"strict: unknown expectation {c["strict"]!.ToJsonString()}"];
        }
    }

    /// <summary>
    /// The flag behind a case's strict difference: the one tolerance its lenient decoding used. A
    /// packet with two tolerated defects has no single flag to turn off, so it has none.
    /// </summary>
    private static string? FlagFor(JsonObject c)
    {
        string[] flags = [.. (c["expect"]!["diagnostics"] as JsonArray ?? [])
            .Select(d => (string)d!).Where(d => d.StartsWith("warning:", StringComparison.Ordinal))
            .Select(d => Flags.GetValueOrDefault(d["warning:".Length..])).OfType<string>().Distinct()];
        return flags.Length == 1 ? flags[0] : null;
    }

    private static void Pass(JsonObject c, IReadOnlyList<string> differences, string? how = null)
    {
        if (differences.Count > 0)
        {
            string header = $"{(string)c["id"]!}{(how is null ? "" : $" ({how})")}: {(string)c["description"]!}";
            differences.Should().BeEmpty(header + Environment.NewLine + JsonMatch.Describe(differences));
        }
    }

    private static JsonObject Input(JsonObject c) => c["input"]!.AsObject();

    private static IEnumerable<string> Strings(IEnumerable<AprsDiagnostic> diagnostics) => diagnostics.Select(d => NeutralJson.Diagnostic(d.Severity, d.Code));

    private static string Describe(AprsPacket? packet, IReadOnlyList<AprsDiagnostic> headerDiagnostics) =>
        packet is null ? $"header error ({string.Join(", ", Strings(headerDiagnostics))})"
            : $"{NeutralJson.Data(packet.Data).ToJsonString()} with [{string.Join(", ", Strings(packet.Diagnostics))}]";

    private static string Show(byte[] info) => JsonValue.Create(System.Text.Encoding.Latin1.GetString(info)).ToJsonString();

    private static IEnumerable<string> Unknown(JsonObject o, string[] known) => o.Select(kv => kv.Key).Except(known);

    private static IEnumerable<JsonObject> CodeCatalogue() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(VectorCases.Directory, "codes.json")))!["codes"]!.AsArray().Select(n => n!.AsObject());
}
