using System.Globalization;
using System.Text.Json.Nodes;

namespace Packet.Aprs.Tests.Vectors;

/// <summary>
/// Builds Packet.Aprs data from the neutral form: the inverse of <see cref="NeutralJson"/>, for
/// encode cases. A field it does not know is an error rather than ignored, so a case can never
/// pass because part of it was skipped; <c>AprsVectorTests</c> checks that writing and reading
/// back gives the same neutral form for every decoded case.
/// </summary>
internal static class NeutralReader
{
    public static AprsData Data(JsonObject d)
    {
        string type = Str(d, "type");
        return type switch
        {
            "position" => Positioned(d, new AprsPositionReport
            {
                Position = default,
                Symbol = default,
                Timestamp = Timestamp(d["timestamp"]),
                MessagingCapable = Flag(d, "messaging"),
            }, "timestamp", "messaging"),
            "mic-e" => Positioned(d, new AprsMicEReport
            {
                Position = default,
                Symbol = default,
                Message = Enum<AprsMicEMessage>(Str(d, "mic_e_message")),
                IsCurrent = !Flag(d, "old_data"),
                TypeCode = OptChar(d, "type_code"),
                DeviceSuffix = OptStr(d, "device_suffix") ?? "",
                MaidenheadLocator = OptStr(d, "locator"),
                LegacyTelemetry = d["legacy_telemetry"] is JsonArray lt ? [.. lt.Select(v => (int)v!)] : null,
                DestinationSsid = (int?)d["destination_ssid"] ?? 0,
            }, "mic_e_message", "old_data", "type_code", "device_suffix", "locator", "legacy_telemetry", "destination_ssid"),
            "object" => Positioned(d, new AprsObjectReport
            {
                Position = default,
                Symbol = default,
                Name = Str(d, "name"),
                IsAlive = !Flag(d, "killed"),
                Timestamp = Timestamp(d["timestamp"]),
            }, "name", "killed", "timestamp"),
            "item" => Positioned(d, new AprsItemReport
            {
                Position = default,
                Symbol = default,
                Name = Str(d, "name"),
                IsAlive = !Flag(d, "killed"),
            }, "name", "killed"),
            "message" => Only(d, new AprsTextMessage { Addressee = Str(d, "addressee"), Text = OptStr(d, "text") ?? "", ReplyAck = RawStr(d, "reply_ack"), MessageId = OptStr(d, "message_id") }, "addressee", "text", "reply_ack", "message_id"),
            "ack" => Only(d, new AprsMessageAck { Addressee = Str(d, "addressee"), AcknowledgedId = Str(d, "acked_id"), ReplyAck = RawStr(d, "reply_ack"), MessageId = OptStr(d, "message_id") }, "addressee", "acked_id", "reply_ack", "message_id"),
            "reject" => Only(d, new AprsMessageReject { Addressee = Str(d, "addressee"), RejectedId = Str(d, "rejected_id"), ReplyAck = RawStr(d, "reply_ack"), MessageId = OptStr(d, "message_id") }, "addressee", "rejected_id", "reply_ack", "message_id"),
            "bulletin" => Only(d, new AprsBulletin { Addressee = Str(d, "addressee"), Text = OptStr(d, "text") ?? "", MessageId = OptStr(d, "message_id") }, "addressee", "text", "message_id"),
            "nws-bulletin" => Only(d, new AprsNwsBulletin { Addressee = Str(d, "addressee"), Text = OptStr(d, "text") ?? "", MessageId = OptStr(d, "message_id") }, "addressee", "text", "message_id"),
            "telemetry-names" => Only(d, new AprsTelemetryParameterNames { Addressee = Str(d, "addressee"), Names = Strings(d["names"]), MessageId = OptStr(d, "message_id") }, "addressee", "names", "message_id"),
            "telemetry-units" => Only(d, new AprsTelemetryUnits { Addressee = Str(d, "addressee"), Units = Strings(d["units"]), MessageId = OptStr(d, "message_id") }, "addressee", "units", "message_id"),
            "telemetry-coefficients" => Only(d, new AprsTelemetryCoefficients { Addressee = Str(d, "addressee"), Coefficients = [.. (d["coefficients"] as JsonArray ?? []).Select(v => (decimal)v!)], MessageId = OptStr(d, "message_id") }, "addressee", "coefficients", "message_id"),
            "telemetry-bits" => Only(d, new AprsTelemetryBitSense { Addressee = Str(d, "addressee"), Bits = Bits(Str(d, "bits")), ProjectTitle = OptStr(d, "project") ?? "", MessageId = OptStr(d, "message_id") }, "addressee", "bits", "project", "message_id"),
            "directed-query" => Only(d, new AprsDirectedQuery { Addressee = Str(d, "addressee"), QueryType = Str(d, "query_type"), Target = OptStr(d, "target"), MessageId = OptStr(d, "message_id") }, "addressee", "query_type", "target", "message_id"),
            "status" => Only(d, new AprsStatusReport
            {
                Timestamp = Timestamp(d["timestamp"]),
                MaidenheadLocator = OptStr(d, "locator"),
                Symbol = OptStr(d, "symbol") is { } s ? Symbol(s) : null,
                BeamHeading = d["beam"] is JsonObject b ? new AprsBeamHeading(Str(b, "heading_code")[0], Str(b, "power_code")[0]) : null,
                Text = OptStr(d, "text") ?? "",
            }, "timestamp", "locator", "symbol", "beam", "text"),
            "telemetry" => Only(d, new AprsTelemetryReport
            {
                Sequence = Str(d, "sequence"),
                Analog = [.. (d["analog"] as JsonArray ?? []).Select(v => v is null ? (decimal?)null : (decimal)v)],
                Digital = OptStr(d, "bits") is { } bits ? Bits(bits) : null,
                Comment = OptStr(d, "comment") ?? "",
            }, "sequence", "analog", "bits", "comment"),
            "weather" => Only(d, new AprsWeatherReport { Timestamp = Timestamp(d["timestamp"])!.Value, Weather = Weather(d["weather"]!.AsObject()), Comment = OptStr(d, "comment") ?? "" }, "timestamp", "weather", "comment"),
            "raw-weather" => Only(d, new AprsRawWeatherReport { Format = Enum<AprsRawWeatherFormat>(Str(d, "format")), Data = Str(d, "data") }, "format", "data"),
            "nmea" => Only(d, new AprsNmeaReport
            {
                Sentence = Str(d, "sentence"),
                Position = d["latitude"] is { } lat ? new AprsPosition((double)lat, (double)d["longitude"]!) : null,
                FixValid = OptStr(d, "fix") is { } fix ? fix == "valid" : null,
                CourseDegrees = (double?)d["course_degrees"],
                SpeedKnots = (double?)d["speed_knots"],
                AltitudeMetres = (double?)d["altitude_m"],
                Time = OptStr(d, "time") is { } t ? TimeOnly.ParseExact(t, ["HH:mm:ss", "HH:mm:ss.fff"], CultureInfo.InvariantCulture) : null,
                WaypointName = OptStr(d, "waypoint"),
            }, "sentence", "has_checksum", "latitude", "longitude", "fix", "course_degrees", "speed_knots", "altitude_m", "time", "waypoint"),
            "maidenhead-beacon" => Only(d, new AprsMaidenheadBeacon { Locator = Str(d, "locator"), Comment = OptStr(d, "comment") ?? "" }, "locator", "comment"),
            "query" => Only(d, new AprsGeneralQuery
            {
                QueryType = Str(d, "query_type"),
                Footprint = d["footprint"] is JsonObject f ? new AprsQueryFootprint((decimal)f["latitude"]!, (decimal)f["longitude"]!, (int)f["radius_miles"]!) : null,
            }, "query_type", "footprint"),
            "capabilities" => Only(d, new AprsStationCapabilities
            {
                Capabilities = [.. (d["capabilities"] as JsonArray ?? []).Select(p => p!.AsArray()).Select(p => new AprsCapability((string)p[0]!, p.Count > 1 ? (string?)p[1] : null))],
            }, "capabilities"),
            "third-party" => Only(d, ThirdParty(d["packet"]!.AsObject()), "packet"),
            "user-defined" => Only(d, new AprsUserDefinedData
            {
                UserId = Str(d, "user_id")[0],
                PacketType = Str(d, "packet_type")[0],
                Data = System.Text.Encoding.Latin1.GetBytes(OptStr(d, "data") ?? ""),
            }, "user_id", "packet_type", "data"),
            "test" => Only(d, new AprsTestData { Data = OptStr(d, "data") ?? "" }, "data"),
            "agrelo-df" => Only(d, new AprsAgreloDfReport { BearingDegrees = (int)d["bearing_degrees"]!, Quality = (int)d["quality"]! }, "bearing_degrees", "quality"),
            _ => throw new NotSupportedException($"no neutral reader for '{type}' data"),
        };
    }

    private static AprsThirdPartyTraffic ThirdParty(JsonObject p)
    {
        IEnumerable<AprsPathEntry> path = Strings(p["path"]).Select(e => e.EndsWith('*') ? new AprsPathEntry(AprsAddress.Parse(e[..^1]), true) : new AprsPathEntry(AprsAddress.Parse(e)));
        AprsPacket inner = AprsPacket.Create(AprsAddress.Parse(Str(p, "source")), AprsAddress.Parse(Str(p, "destination")), Data(p["data"]!.AsObject()), path);
        return new AprsThirdPartyTraffic { Packet = inner };
    }

    private static T Positioned<T>(JsonObject d, T data, params string[] ownFields)
        where T : AprsPositionedData
    {
        string[] known = ["latitude", "longitude", "ambiguity", "symbol", "compressed", "compression", "course_degrees", "speed_knots", "altitude_feet", "phg", "range_miles", "dfs", "area", "df_bearing", "storm", "dao", "telemetry", "frequency", "weather", "signpost", "comment"];
        Only(d, data, [.. ownFields, .. known]);
        return data with
        {
            Position = new AprsPosition((double)d["latitude"]!, (double)d["longitude"]!, (int?)d["ambiguity"] ?? 0),
            Symbol = Symbol(Str(d, "symbol")),
            IsCompressed = Flag(d, "compressed"),
            CompressionType = d["compression"] is JsonObject ct
                ? new AprsCompressionType(Enum<AprsGpsFix>(Str(ct, "fix")), Enum<AprsNmeaSource>(Str(ct, "source")), Enum<AprsCompressionOrigin>(Str(ct, "origin")))
                : null,
            CourseDegrees = (int?)d["course_degrees"],
            SpeedKnots = (double?)d["speed_knots"],
            AltitudeFeet = (double?)d["altitude_feet"],
            Phg = d["phg"] is JsonObject phg ? new AprsPhg((int)phg["power"]!, (int)phg["height"]!, (int)phg["gain"]!, (int)phg["directivity"]!, (int?)phg["beacons_per_hour"]) : null,
            RadioRangeMiles = (double?)d["range_miles"],
            DfSignalStrength = d["dfs"] is JsonObject dfs ? new AprsDfSignalStrength((int)dfs["strength"]!, (int)dfs["height"]!, (int)dfs["gain"]!, (int)dfs["directivity"]!) : null,
            AreaObject = d["area"] is JsonObject a
                ? new AprsAreaObject(Enum<AprsAreaShape>(Str(a, "shape")), Enum<AprsAreaColor>(Str(a, "color")), (int)a["lat_offset"]!, (int)a["lon_offset"]!, (int?)a["corridor_width_miles"])
                : null,
            DfBearing = d["df_bearing"] is JsonObject b ? new AprsDfBearing((int)b["bearing_degrees"]!, (int)b["number"]!, (int)b["range"]!, (int)b["quality"]!) : null,
            Storm = d["storm"] is JsonObject s
                ? new AprsStorm
                {
                    Type = Enum<AprsStormType>(Str(s, "type")),
                    SustainedWindKnots = (int?)s["sustained_wind_knots"],
                    GustKnots = (int?)s["gust_knots"],
                    CentralPressureMillibars = (int?)s["central_pressure_mbar"],
                    HurricaneWindRadiusNauticalMiles = (int?)s["hurricane_radius_nm"],
                    TropicalStormWindRadiusNauticalMiles = (int?)s["tropical_storm_radius_nm"],
                    WholeGaleRadiusNauticalMiles = (int?)s["whole_gale_radius_nm"],
                }
                : null,
            Dao = d["dao"] is JsonObject dao ? new AprsDao(Str(dao, "datum")[0], Enum<AprsDaoPrecision>(Str(dao, "precision"))) : null,
            Telemetry = d["telemetry"] is JsonObject t
                ? new AprsCommentTelemetry { Sequence = (int)t["sequence"]!, Analog = [.. t["analog"]!.AsArray().Select(v => (int)v!)], Digital = (byte?)(int?)t["digital"] }
                : null,
            Frequency = d["frequency"] is JsonObject f
                ? new AprsVoiceFrequency
                {
                    FrequencyMHz = (decimal)f["mhz"]!,
                    ToneType = OptStr(f, "tone") is { } tone ? Enum<AprsToneType>(tone) : null,
                    ToneValue = (int?)f["tone_value"],
                    OffsetKHz = (int?)f["offset_khz"],
                    Range = (int?)f["range"],
                    RangeInKilometres = Flag(f, "range_km"),
                    Narrow = Flag(f, "narrow"),
                    TenKilohertzResolution = Flag(f, "ten_khz_resolution"),
                }
                : null,
            Weather = d["weather"] is JsonObject w ? Weather(w) : null,
            SignpostText = OptStr(d, "signpost"),
            Comment = OptStr(d, "comment") ?? "",
        };
    }

    private static AprsWeather Weather(JsonObject w) => new()
    {
        WindDirectionDegrees = (int?)w["wind_direction_degrees"],
        WindSpeedMph = (double?)w["wind_speed_mph"],
        WindGustMph = (int?)w["wind_gust_mph"],
        TemperatureFahrenheit = (int?)w["temperature_f"],
        RainLastHourInches = (double?)w["rain_1h_in"],
        RainLast24HoursInches = (double?)w["rain_24h_in"],
        RainSinceMidnightInches = (double?)w["rain_midnight_in"],
        RainRawCounter = (int?)w["rain_raw"],
        HumidityPercent = (int?)w["humidity_percent"],
        PressureMillibars = (double?)w["pressure_mbar"],
        LuminosityWattsPerSquareMetre = (int?)w["luminosity_w_m2"],
        SnowfallLast24HoursInches = (decimal?)w["snow_24h_in"],
        SoftwareType = OptChar(w, "software"),
        UnitType = OptStr(w, "unit"),
        AdditionalFields = w["extra"] is JsonArray extra ? [.. extra.Select(e => new AprsWeatherField(Str(e!.AsObject(), "letter")[0], Str(e.AsObject(), "value")))] : [],
    };

    /// <summary>On-air timestamp text: DDHHMMz, DDHHMM/, HHMMSSh or MMDDHHMM. Out-of-range fields are kept, as the decoder keeps them.</summary>
    private static AprsTimestamp? Timestamp(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        string s = (string)node!;
        int F(int at) => int.Parse(s.AsSpan(at, 2), CultureInfo.InvariantCulture);
        return s.Length == 8
            ? AprsTimestamp.Unchecked(AprsTimestampFormat.MonthDayHoursMinutesUtc, F(0), F(2), F(4), F(6), 0)
            : s[6] switch
            {
                'z' => AprsTimestamp.Unchecked(AprsTimestampFormat.DayHoursMinutesUtc, 0, F(0), F(2), F(4), 0),
                '/' => AprsTimestamp.Unchecked(AprsTimestampFormat.DayHoursMinutesLocal, 0, F(0), F(2), F(4), 0),
                'h' => AprsTimestamp.Unchecked(AprsTimestampFormat.HoursMinutesSecondsUtc, 0, 0, F(0), F(2), F(4)),
                _ => throw new FormatException($"'{s}' is not an on-air timestamp"),
            };
    }

    private static byte Bits(string onAir)
    {
        byte b = 0;
        for (int i = 0; i < onAir.Length; i++)
        {
            if (onAir[i] == '1')
            {
                b |= (byte)(1 << i);
            }
        }

        return b;
    }

    private static AprsSymbol Symbol(string s) => new(s[0], s[1]);

    private static T Enum<T>(string kebab)
        where T : struct, Enum => System.Enum.Parse<T>(kebab.Replace("-", "", StringComparison.Ordinal), ignoreCase: true);

    private static bool Flag(JsonObject d, string name) => (bool?)d[name] ?? false;

    private static string Str(JsonObject d, string name) => (string?)d[name] ?? throw new FormatException($"'{name}' is required");

    private static string? OptStr(JsonObject d, string name) => (string?)d[name];

    /// <summary>A string where empty is meaningful (reply_ack).</summary>
    private static string? RawStr(JsonObject d, string name) => d.ContainsKey(name) ? (string?)d[name] : null;

    private static char? OptChar(JsonObject d, string name) => OptStr(d, name) is { Length: > 0 } s ? s[0] : null;

    private static string[] Strings(JsonNode? node) => node is JsonArray a ? [.. a.Select(v => (string)v!)] : [];

    private static T Only<T>(JsonObject d, T data, params string[] known)
    {
        string[] unknown = [.. d.Select(kv => kv.Key).Where(k => k != "type").Except(known)];
        return unknown.Length == 0 ? data : throw new NotSupportedException($"unknown field(s) {string.Join(", ", unknown)} for '{(string?)d["type"]}'");
    }
}
