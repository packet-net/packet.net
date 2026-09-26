using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Packet.Aprs.Tests.Vectors;

/// <summary>
/// Writes a decoded packet in the language-neutral form of <c>spec/aprs/README.md</c>: snake_case
/// names, APRS's own units in the names, enumerations as kebab-case strings, timestamps as they
/// appear on air. Absence carries meaning, so the rules for leaving a field out are exact: null
/// values, empty strings and empty lists are left out, and booleans (each named for its less usual
/// state) are written only when true. Values another field already implies (a symbol's
/// description, PHG in watts, a bulletin's kind) are not written.
/// </summary>
internal static class NeutralJson
{
    public static JsonObject Header(AprsPacket packet)
    {
        var o = new JsonObject
        {
            ["source"] = packet.Source.Value,
            ["destination"] = packet.Destination.Value,
        };
        Put(o, "path", Array(packet.Path.Select(p => (JsonNode?)(p.HasBeenRepeated ? p.Address.Value + "*" : p.Address.Value))));
        if (packet.QConstruct is { } q)
        {
            var qo = new JsonObject { ["construct"] = q.Construct };
            Put(qo, "station", q.Station?.Value);
            o["q_construct"] = qo;
        }

        return o;
    }

    public static JsonArray Diagnostics(IEnumerable<AprsDiagnostic> diagnostics) =>
        [.. diagnostics.Select(d => (JsonNode?)Diagnostic(d.Severity, d.Code))];

    public static string Diagnostic(AprsDiagnosticSeverity severity, AprsDiagnosticCode code) =>
        $"{Kebab(severity.ToString())}:{Kebab(code.ToString())}";

    public static JsonObject Data(AprsData data)
    {
        var o = new JsonObject { ["type"] = TypeName(data) };
        switch (data)
        {
            case AprsPositionReport p:
                Put(o, "timestamp", p.Timestamp?.ToString());
                Flag(o, "messaging", p.MessagingCapable);
                Positioned(o, p);
                break;
            case AprsMicEReport m:
                o["mic_e_message"] = Kebab(m.Message.ToString());
                Flag(o, "old_data", !m.IsCurrent);
                Put(o, "type_code", m.TypeCode?.ToString());
                Put(o, "device_suffix", m.DeviceSuffix);
                Put(o, "locator", m.MaidenheadLocator);
                Put(o, "legacy_telemetry", m.LegacyTelemetry is { } lt ? Array(lt.Select(v => (JsonNode?)v)) : null);
                Put(o, "destination_ssid", m.DestinationSsid == 0 ? null : m.DestinationSsid);
                Positioned(o, m);
                break;
            case AprsObjectReport ob:
                o["name"] = ob.Name;
                Flag(o, "killed", !ob.IsAlive);
                Put(o, "timestamp", ob.Timestamp?.ToString());
                Positioned(o, ob);
                break;
            case AprsItemReport it:
                o["name"] = it.Name;
                Flag(o, "killed", !it.IsAlive);
                Positioned(o, it);
                break;
            case AprsMessage msg:
                Message(o, msg);
                break;
            case AprsStatusReport s:
                Put(o, "timestamp", s.Timestamp?.ToString());
                Put(o, "locator", s.MaidenheadLocator);
                Put(o, "symbol", s.Symbol?.ToString());
                if (s.BeamHeading is { } b)
                {
                    o["beam"] = new JsonObject { ["heading_code"] = b.HeadingCode.ToString(), ["power_code"] = b.PowerCode.ToString() };
                }

                Put(o, "text", s.Text);
                break;
            case AprsTelemetryReport t:
                o["sequence"] = t.Sequence;
                o["analog"] = Array(t.Analog.Select(v => v is { } d ? (JsonNode?)d : null));
                Put(o, "bits", t.Digital is { } bits ? Bits(bits) : null);
                Put(o, "comment", t.Comment);
                break;
            case AprsWeatherReport w:
                o["timestamp"] = w.Timestamp.ToString();
                o["weather"] = Weather(w.Weather);
                Put(o, "comment", w.Comment);
                break;
            case AprsRawWeatherReport r:
                o["format"] = Kebab(r.Format.ToString());
                o["data"] = r.Data;
                break;
            case AprsNmeaReport n:
                o["sentence"] = n.Sentence;
                Flag(o, "has_checksum", n.HasChecksum);
                if (n.Position is { } np)
                {
                    o["latitude"] = np.Latitude;
                    o["longitude"] = np.Longitude;
                }

                Put(o, "fix", n.FixValid is { } fv ? (fv ? "valid" : "invalid") : null);
                Put(o, "course_degrees", n.CourseDegrees);
                Put(o, "speed_knots", n.SpeedKnots);
                Put(o, "altitude_m", n.AltitudeMetres);
                Put(o, "time", n.Time?.ToString(n.Time.Value.Millisecond == 0 ? "HH:mm:ss" : "HH:mm:ss.fff", CultureInfo.InvariantCulture));
                Put(o, "waypoint", n.WaypointName);
                break;
            case AprsMaidenheadBeacon mb:
                o["locator"] = mb.Locator;
                Put(o, "comment", mb.Comment);
                break;
            case AprsGeneralQuery q:
                o["query_type"] = q.QueryType;
                if (q.Footprint is { } f)
                {
                    o["footprint"] = new JsonObject { ["latitude"] = f.Latitude, ["longitude"] = f.Longitude, ["radius_miles"] = f.RadiusMiles };
                }

                break;
            case AprsStationCapabilities c:
                o["capabilities"] = Array(c.Capabilities.Select(cap => (JsonNode?)(cap.Value is null ? new JsonArray(cap.Token) : new JsonArray(cap.Token, cap.Value))));
                break;
            case AprsThirdPartyTraffic tp:
                JsonObject inner = Header(tp.Packet);
                inner["data"] = Data(tp.Packet.Data);
                Put(inner, "diagnostics", tp.Packet.Diagnostics.Count == 0 ? null : Diagnostics(tp.Packet.Diagnostics));
                o["packet"] = inner;
                break;
            case AprsUserDefinedData u:
                o["user_id"] = u.UserId.ToString();
                o["packet_type"] = u.PacketType.ToString();
                Put(o, "data", Latin1(u.Data));
                break;
            case AprsTestData td:
                Put(o, "data", td.Data);
                break;
            case AprsAgreloDfReport ag:
                o["bearing_degrees"] = ag.BearingDegrees;
                o["quality"] = ag.Quality;
                break;
            case AprsUnrecognizedData un:
                o["reason"] = Kebab(un.Reason.ToString());
                break;
            default:
                throw new NotSupportedException($"no neutral form for {data.GetType().Name}");
        }

        return o;
    }

    public static string TypeName(AprsData data) => data switch
    {
        AprsPositionReport => "position",
        AprsMicEReport => "mic-e",
        AprsObjectReport => "object",
        AprsItemReport => "item",
        AprsTextMessage => "message",
        AprsMessageAck => "ack",
        AprsMessageReject => "reject",
        AprsBulletin => "bulletin",
        AprsNwsBulletin => "nws-bulletin",
        AprsTelemetryParameterNames => "telemetry-names",
        AprsTelemetryUnits => "telemetry-units",
        AprsTelemetryCoefficients => "telemetry-coefficients",
        AprsTelemetryBitSense => "telemetry-bits",
        AprsDirectedQuery => "directed-query",
        AprsStatusReport => "status",
        AprsTelemetryReport => "telemetry",
        AprsWeatherReport => "weather",
        AprsRawWeatherReport => "raw-weather",
        AprsNmeaReport => "nmea",
        AprsMaidenheadBeacon => "maidenhead-beacon",
        AprsGeneralQuery => "query",
        AprsStationCapabilities => "capabilities",
        AprsThirdPartyTraffic => "third-party",
        AprsUserDefinedData => "user-defined",
        AprsTestData => "test",
        AprsAgreloDfReport => "agrelo-df",
        AprsUnrecognizedData => "unrecognized",
        _ => throw new NotSupportedException(data.GetType().Name),
    };

    private static void Message(JsonObject o, AprsMessage msg)
    {
        o["addressee"] = msg.Addressee;
        switch (msg)
        {
            case AprsTextMessage t:
                Put(o, "text", t.Text);
                if (t.ReplyAck is not null)
                {
                    o["reply_ack"] = t.ReplyAck; // "" is meaningful: the sender supports reply-acks
                }

                break;
            case AprsMessageAck a:
                o["acked_id"] = a.AcknowledgedId;
                if (a.ReplyAck is not null)
                {
                    o["reply_ack"] = a.ReplyAck;
                }

                break;
            case AprsMessageReject r:
                o["rejected_id"] = r.RejectedId;
                if (r.ReplyAck is not null)
                {
                    o["reply_ack"] = r.ReplyAck;
                }

                break;
            case AprsBulletin b:
                Put(o, "text", b.Text);
                break;
            case AprsNwsBulletin n:
                Put(o, "text", n.Text);
                break;
            case AprsTelemetryParameterNames pn:
                o["names"] = Array(pn.Names.Select(s => (JsonNode?)s));
                break;
            case AprsTelemetryUnits un:
                o["units"] = Array(un.Units.Select(s => (JsonNode?)s));
                break;
            case AprsTelemetryCoefficients co:
                o["coefficients"] = Array(co.Coefficients.Select(c => (JsonNode?)c));
                break;
            case AprsTelemetryBitSense bs:
                o["bits"] = Bits(bs.Bits);
                Put(o, "project", bs.ProjectTitle);
                break;
            case AprsDirectedQuery dq:
                o["query_type"] = dq.QueryType;
                Put(o, "target", dq.Target);
                break;
        }

        Put(o, "message_id", msg.MessageId);
    }

    private static void Positioned(JsonObject o, AprsPositionedData d)
    {
        o["latitude"] = d.Position.Latitude;
        o["longitude"] = d.Position.Longitude;
        Put(o, "ambiguity", d.Position.Ambiguity == 0 ? null : d.Position.Ambiguity);
        o["symbol"] = d.Symbol.ToString();
        Flag(o, "compressed", d.IsCompressed);
        if (d.CompressionType is { } ct)
        {
            o["compression"] = new JsonObject
            {
                ["fix"] = Kebab(ct.Fix.ToString()),
                ["source"] = Kebab(ct.Source.ToString()),
                ["origin"] = Kebab(ct.Origin.ToString()),
            };
        }

        Put(o, "course_degrees", d.CourseDegrees);
        Put(o, "speed_knots", d.SpeedKnots);
        Put(o, "altitude_feet", d.AltitudeFeet);
        if (d.Phg is { } phg)
        {
            var p = new JsonObject { ["power"] = phg.PowerCode, ["height"] = phg.HeightCode, ["gain"] = phg.GainCode, ["directivity"] = phg.DirectivityCode };
            Put(p, "beacons_per_hour", phg.BeaconsPerHour);
            o["phg"] = p;
        }

        Put(o, "range_miles", d.RadioRangeMiles);
        if (d.DfSignalStrength is { } dfs)
        {
            o["dfs"] = new JsonObject { ["strength"] = dfs.StrengthCode, ["height"] = dfs.HeightCode, ["gain"] = dfs.GainCode, ["directivity"] = dfs.DirectivityCode };
        }

        if (d.AreaObject is { } area)
        {
            var a = new JsonObject
            {
                ["shape"] = Kebab(area.Shape.ToString()),
                ["color"] = Kebab(area.Color.ToString()),
                ["lat_offset"] = area.LatitudeOffsetCode,
                ["lon_offset"] = area.LongitudeOffsetCode,
            };
            Put(a, "corridor_width_miles", area.CorridorWidthMiles);
            o["area"] = a;
        }

        if (d.DfBearing is { } bearing)
        {
            o["df_bearing"] = new JsonObject { ["bearing_degrees"] = bearing.BearingDegrees, ["number"] = bearing.Number, ["range"] = bearing.Range, ["quality"] = bearing.Quality };
        }

        if (d.Storm is { } st)
        {
            var s = new JsonObject { ["type"] = Kebab(st.Type.ToString()) };
            Put(s, "sustained_wind_knots", st.SustainedWindKnots);
            Put(s, "gust_knots", st.GustKnots);
            Put(s, "central_pressure_mbar", st.CentralPressureMillibars);
            Put(s, "hurricane_radius_nm", st.HurricaneWindRadiusNauticalMiles);
            Put(s, "tropical_storm_radius_nm", st.TropicalStormWindRadiusNauticalMiles);
            Put(s, "whole_gale_radius_nm", st.WholeGaleRadiusNauticalMiles);
            o["storm"] = s;
        }

        if (d.Dao is { } dao)
        {
            o["dao"] = new JsonObject { ["datum"] = dao.Datum.ToString(), ["precision"] = Kebab(dao.Precision.ToString()) };
        }

        if (d.Telemetry is { } t)
        {
            var tel = new JsonObject { ["sequence"] = t.Sequence, ["analog"] = Array(t.Analog.Select(v => (JsonNode?)v)) };
            Put(tel, "digital", t.Digital is { } dg ? (int)dg : null);
            o["telemetry"] = tel;
        }

        if (d.Frequency is { } f)
        {
            var fr = new JsonObject { ["mhz"] = f.FrequencyMHz };
            Put(fr, "tone", f.ToneType is { } tone ? Kebab(tone.ToString()) : null);
            Put(fr, "tone_value", f.ToneValue);
            Put(fr, "offset_khz", f.OffsetKHz);
            Put(fr, "range", f.Range);
            Flag(fr, "range_km", f.RangeInKilometres);
            Flag(fr, "narrow", f.Narrow);
            Flag(fr, "ten_khz_resolution", f.TenKilohertzResolution);
            o["frequency"] = fr;
        }

        if (d.Weather is { } w)
        {
            o["weather"] = Weather(w);
        }

        Put(o, "signpost", d.SignpostText);
        Put(o, "comment", d.Comment);
    }

    private static JsonObject Weather(AprsWeather w)
    {
        var o = new JsonObject();
        Put(o, "wind_direction_degrees", w.WindDirectionDegrees);
        Put(o, "wind_speed_mph", w.WindSpeedMph);
        Put(o, "wind_gust_mph", w.WindGustMph);
        Put(o, "temperature_f", w.TemperatureFahrenheit);
        Put(o, "rain_1h_in", w.RainLastHourInches);
        Put(o, "rain_24h_in", w.RainLast24HoursInches);
        Put(o, "rain_midnight_in", w.RainSinceMidnightInches);
        Put(o, "rain_raw", w.RainRawCounter);
        Put(o, "humidity_percent", w.HumidityPercent);
        Put(o, "pressure_mbar", w.PressureMillibars);
        Put(o, "luminosity_w_m2", w.LuminosityWattsPerSquareMetre);
        Put(o, "snow_24h_in", w.SnowfallLast24HoursInches);
        Put(o, "software", w.SoftwareType?.ToString());
        Put(o, "unit", w.UnitType);
        Put(o, "extra", w.AdditionalFields.Count == 0 ? null
            : Array(w.AdditionalFields.Select(f => (JsonNode?)new JsonObject { ["letter"] = f.Letter.ToString(), ["value"] = f.Value })));
        return o;
    }

    /// <summary>Digital channels as on air: B1 first, '1' for on.</summary>
    private static string Bits(byte bits)
    {
        var sb = new StringBuilder(8);
        for (int i = 0; i < 8; i++)
        {
            sb.Append((bits >> i & 1) == 1 ? '1' : '0');
        }

        return sb.ToString();
    }

    /// <summary>Bytes as a string of code points U+0000-U+00FF, one per byte.</summary>
    private static string Latin1(IReadOnlyList<byte> bytes) => System.Text.Encoding.Latin1.GetString([.. bytes]);

    /// <summary>PascalCase to kebab-case, splitting only before capitals: OffDuty -> off-duty, Kpc3 -> kpc3.</summary>
    public static string Kebab(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (char.IsAsciiLetterUpper(c) && i > 0)
            {
                sb.Append('-');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private static JsonArray Array(IEnumerable<JsonNode?> items) => [.. items];

    private static void Flag(JsonObject o, string name, bool value)
    {
        if (value)
        {
            o[name] = true;
        }
    }

    private static void Put(JsonObject o, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            o[name] = value;
        }
    }

    private static void Put(JsonObject o, string name, JsonArray? value)
    {
        if (value is { Count: > 0 })
        {
            o[name] = value;
        }
    }

    private static void Put(JsonObject o, string name, JsonObject? value)
    {
        if (value is not null)
        {
            o[name] = value;
        }
    }

    private static void Put(JsonObject o, string name, int? value)
    {
        if (value is { } v)
        {
            o[name] = v;
        }
    }

    private static void Put(JsonObject o, string name, double? value)
    {
        if (value is { } v)
        {
            o[name] = v;
        }
    }

    private static void Put(JsonObject o, string name, decimal? value)
    {
        if (value is { } v)
        {
            o[name] = v;
        }
    }
}
