using System.Globalization;
using System.Text.Json.Nodes;
using Packet.Aprs.Tests.Vectors;

namespace Packet.Aprs.Corpus;

/// <summary>
/// <c>diff build</c>: builds each recipe (aprs-vectors README, "Build records") with the fluent
/// builder, as a program would, and writes the TNC2 line built (<c>built</c>), the builder's or
/// encoder's refusal (<c>refused</c>), or <c>unsupported</c> naming the recipe key the builder
/// has no way to say. Every key of a recipe is consumed or reported: one this tool does not know
/// is unsupported too, so nothing is skipped silently.
/// </summary>
internal static class DiffBuild
{
    /// <summary>The builder cannot say something the recipe asks for.</summary>
    private sealed class UnsupportedException(string reason) : Exception(reason);

    public static JsonObject Record(long n, JsonObject recipe)
    {
        var o = new JsonObject { ["n"] = n };
        AprsPacket packet;
        try
        {
            packet = Build(recipe);
        }
        catch (UnsupportedException ex)
        {
            o["result"] = "unsupported";
            o["reason"] = ex.Message;
            return o;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            o["result"] = "refused";
            o["reason"] = ex.Message;
            return o;
        }

        byte[] tnc2 = packet.ToTnc2();
        o["result"] = "built";
        o["tnc2"] = Convert.ToHexStringLower(tnc2);
        try
        {
            o["again"] = DiffCommand.Decoded(AprsPacket.Decode(tnc2, AprsParseOptions.Lenient), withHeader: true);
        }
        catch (AprsFormatException ex)
        {
            o["again"] = new JsonObject { ["header_error"] = NeutralJson.Diagnostics(ex.Diagnostics) };
        }

        return o;
    }

    private static AprsPacket Build(JsonObject recipe)
    {
        var station = recipe["station"]!.AsObject();
        var s = new Args(station, "station");
        AprsStation from = Aprs.From(s.Str("source")!);
        if (s.Str("destination") is { } destination)
        {
            from = from.To(destination);
        }

        if (s.Node("path") is JsonArray path)
        {
            from = from.Via([.. path.Select(p => (string)p!)]);
        }

        s.Done();
        string report = (string)recipe["report"]!;
        var a = new Args(recipe["args"]!.AsObject(), report);
        AprsBuilder builder = report switch
        {
            "position" => Position(from, a),
            "object" => Object(from, a),
            "item" => Item(from, a),
            "mic-e" => MicE(from, a),
            "weather" => Weather(from, a),
            "message" => Message(from, a),
            "ack" => from.Ack(a.Str("addressee")!, a.Str("message_id")!),
            "reject" => from.Reject(a.Str("addressee")!, a.Str("message_id")!),
            "bulletin" => Bulletin(from, a),
            "status" => Status(from, a),
            "telemetry" => Telemetry(from, a),
            "telemetry-names" => Metadata(from, a, "names", (addressee, list) => new AprsTelemetryParameterNames { Addressee = addressee, Names = list }, from.TelemetryNames),
            "telemetry-units" => Metadata(from, a, "units", (addressee, list) => new AprsTelemetryUnits { Addressee = addressee, Units = list }, from.TelemetryUnits),
            "telemetry-coefficients" => Coefficients(from, a),
            "telemetry-bits" => Bits(from, a),
            _ => throw new UnsupportedException($"report '{report}': no builder for it"),
        };
        a.Done();
        return builder.Build();
    }

    // ------------------------------------------------------------------ positioned reports

    private static AprsPositionBuilder Position(AprsStation from, Args a)
    {
        AprsPositionBuilder b = from.Position(a.Double("latitude")!.Value, a.Double("longitude")!.Value);
        if (a.Bool("messaging"))
        {
            b.Messaging();
        }

        if (Timestamp(a) is { } t)
        {
            b.Timestamp(t);
        }

        return Positioned(b, a);
    }

    private static AprsObjectBuilder Object(AprsStation from, Args a)
    {
        AprsObjectBuilder b = from.Object(a.Str("name")!).At(a.Double("latitude")!.Value, a.Double("longitude")!.Value);
        if (Timestamp(a) is { } t)
        {
            b.Timestamp(t);
        }

        if (a.Bool("killed"))
        {
            b.Kill();
        }

        return Positioned(b, a);
    }

    private static AprsItemBuilder Item(AprsStation from, Args a)
    {
        AprsItemBuilder b = from.Item(a.Str("name")!).At(a.Double("latitude")!.Value, a.Double("longitude")!.Value);
        if (a.Bool("killed"))
        {
            b.Kill();
        }

        return Positioned(b, a);
    }

    private static AprsMicEBuilder MicE(AprsStation from, Args a)
    {
        AprsMicEBuilder b = from.MicE(a.Double("latitude")!.Value, a.Double("longitude")!.Value);
        if (a.Str("mic_e_message") is { } message)
        {
            b.Message(Enum.Parse<AprsMicEMessage>(message.Replace("-", "", StringComparison.Ordinal), ignoreCase: true));
        }

        if (a.Bool("messaging"))
        {
            b.Messaging();
        }

        return Positioned(b, a);
    }

    /// <summary>The positioned options (README, "Build records").</summary>
    private static T Positioned<T>(T b, Args a)
        where T : AprsPositionedBuilder<T>
    {
        if (a.Str("symbol") is { } symbol)
        {
            b.Symbol(Symbol(symbol));
        }

        if (a.Int("course_degrees", "the builder takes a course in whole degrees") is { } course)
        {
            b.Course(course);
        }

        if (a.Double("speed_knots") is { } knots)
        {
            b.Speed(knots);
        }

        if (a.Double("speed_kmh") is { } kmh)
        {
            b.SpeedKmh(kmh);
        }

        if (a.Double("altitude_feet") is { } feet)
        {
            b.Altitude(feet);
        }

        if (a.Double("altitude_m") is { } metres)
        {
            b.AltitudeMetres(metres);
        }

        if (a.Str("comment") is { } comment)
        {
            b.Comment(comment);
        }

        if (a.Node("phg") is JsonObject phg)
        {
            var p = new Args(phg, "phg");
            b.Phg(new AprsPhg(p.Int("power")!.Value, p.Int("height")!.Value, p.Int("gain")!.Value, p.Int("directivity")!.Value, p.Int("beacons_per_hour")));
            p.Done();
        }

        if (a.Double("range_miles") is { } range)
        {
            b.Range(range);
        }

        if (a.Node("frequency") is JsonObject frequency)
        {
            var f = new Args(frequency, "frequency");
            b.Frequency(new AprsVoiceFrequency
            {
                FrequencyMHz = f.Decimal("mhz")!.Value,
                ToneType = f.Str("tone") switch
                {
                    null => null,
                    "tone" => AprsToneType.Tone,
                    "ctcss" => AprsToneType.Ctcss,
                    "dcs" => AprsToneType.Dcs,
                    var other => throw new UnsupportedException($"frequency.tone '{other}'"),
                },
                ToneValue = f.Int("tone_value", "the builder takes a whole tone value"),
                OffsetKHz = f.Int("offset_khz", "the builder takes an offset in whole kHz"),
            });
            f.Done();
        }

        if (a.Bool("compressed"))
        {
            b.Compressed();
        }

        if (a.Int("ambiguity") is { } ambiguity)
        {
            b.Ambiguity(ambiguity);
        }

        if (a.Bool("dao"))
        {
            b.Dao();
        }

        if (a.Node("telemetry") is JsonObject telemetry)
        {
            var t = new Args(telemetry, "telemetry");
            int sequence = t.Int("sequence")!.Value;
            int[] analog = [.. (t.Node("analog") as JsonArray ?? []).Select(v => Whole(v, "telemetry.analog"))];
            _ = t.Int("digital") is { } digital ? b.Telemetry(sequence, analog, (byte)digital) : b.Telemetry(sequence, analog);
            t.Done();
        }

        return b;
    }

    // ------------------------------------------------------------------ weather

    private static AprsWeatherBuilder Weather(AprsStation from, Args a)
    {
        AprsWeatherBuilder b = from.Weather();
        double? latitude = a.Double("latitude");
        double? longitude = a.Double("longitude");
        if (latitude is not null || longitude is not null)
        {
            b.At(latitude ?? throw new UnsupportedException("longitude without latitude"), longitude ?? throw new UnsupportedException("latitude without longitude"));
        }

        if (a.Str("symbol") is { } symbol)
        {
            b.Symbol(Symbol(symbol));
        }

        if (Timestamp(a) is { } t)
        {
            b.Timestamp(t);
        }

        // Wind(direction, speed) takes both; one alone is given as the observations it starts from.
        int? direction = a.Int("wind_direction_degrees", "the builder takes a wind direction in whole degrees");
        double? speed = a.Double("wind_speed_mph");
        if (direction is { } d && speed is { } v)
        {
            b.Wind(d, v);
        }
        else if (direction is not null || speed is not null)
        {
            b.Observations(new AprsWeather { WindDirectionDegrees = direction, WindSpeedMph = speed });
        }

        if (a.Int("wind_gust_mph", "the builder takes a gust in whole mph") is { } gust)
        {
            b.Gust(gust);
        }

        if (a.Int("temperature_f", "the builder takes a temperature in whole degrees F") is { } fahrenheit)
        {
            b.Temperature(fahrenheit);
        }

        if (a.Double("temperature_c") is { } celsius)
        {
            b.TemperatureCelsius(celsius);
        }

        double? r1 = a.Double("rain_1h_in"), r24 = a.Double("rain_24h_in"), rm = a.Double("rain_midnight_in");
        if (r1 is not null || r24 is not null || rm is not null)
        {
            b.Rain(r1, r24, rm);
        }

        double? m1 = a.Double("rain_1h_mm"), m24 = a.Double("rain_24h_mm"), mm = a.Double("rain_midnight_mm");
        if (m1 is not null || m24 is not null || mm is not null)
        {
            b.RainMillimetres(m1, m24, mm);
        }

        if (a.Int("humidity_percent", "the builder takes a humidity in whole percent") is { } humidity)
        {
            b.Humidity(humidity);
        }

        if (a.Double("pressure_mbar") is { } pressure)
        {
            b.Pressure(pressure);
        }

        if (a.Int("luminosity_w_m2", "the builder takes a luminosity in whole W/m2") is { } luminosity)
        {
            b.Luminosity(luminosity);
        }

        if (a.Decimal("snow_24h_in") is { } snow)
        {
            b.Snowfall(snow);
        }

        return b;
    }

    // ------------------------------------------------------------------ messages and the rest

    private static AprsMessageBuilder Message(AprsStation from, Args a)
    {
        AprsMessageBuilder b = from.Message(a.Str("addressee")!, a.Str("text") ?? "");
        if (a.Str("message_id") is { } id)
        {
            b.WithId(id);
        }

        if (a.Str("reply_ack") is { } replyAck)
        {
            b.ReplyAck(replyAck);
        }

        return b;
    }

    private static AprsDataBuilder Bulletin(AprsStation from, Args a)
    {
        string id = a.Str("id")!;
        if (id.Length != 1)
        {
            throw new UnsupportedException("bulletin id: the builder takes one character");
        }

        string text = a.Str("text") ?? "";
        return a.Str("group") is { } group ? from.GroupBulletin(id[0], group, text) : from.Bulletin(id[0], text);
    }

    private static AprsStatusBuilder Status(AprsStation from, Args a)
    {
        AprsStatusBuilder b = from.Status(a.Str("text") ?? "");
        if (Timestamp(a) is { } t)
        {
            b.Timestamp(t);
        }

        string? locator = a.Str("locator");
        string? symbol = a.Str("symbol");
        if (locator is not null && symbol is not null)
        {
            b.Locator(locator, Symbol(symbol));
        }
        else if (locator is not null || symbol is not null)
        {
            throw new UnsupportedException("status locator and symbol: the builder takes them together");
        }

        if (a.Node("beam") is JsonObject beam)
        {
            var be = new Args(beam, "beam");
            b.Beam(new AprsBeamHeading(One(be.Str("heading_code")!, "beam.heading_code"), One(be.Str("power_code")!, "beam.power_code")));
            be.Done();
        }

        return b;
    }

    private static AprsTelemetryBuilder Telemetry(AprsStation from, Args a)
    {
        AprsTelemetryBuilder b = from.Telemetry(a.Int("sequence", "the builder takes a whole sequence number")!.Value);
        if (a.Node("analog") is JsonArray analog)
        {
            b.Analog([.. analog.Select(v => v is null ? (decimal?)null : v.GetValue<decimal>())]);
        }

        if (a.Str("bits") is { } bits)
        {
            b.Digital(bits);
        }

        if (a.Str("comment") is { } comment)
        {
            b.Comment(comment);
        }

        return b;
    }

    /// <summary>Names or units; addressed to another station, the record is built by hand, since the builder addresses the station itself.</summary>
    private static AprsDataBuilder Metadata(AprsStation from, Args a, string key, Func<string, string[], AprsData> record, Func<string[], AprsDataBuilder> own)
    {
        string[] list = [.. (a.Node(key) as JsonArray ?? []).Select(v => (string)v!)];
        return a.Str("addressee") is { } addressee && addressee != from.Source.Value ? from.Data(record(addressee, list)) : own(list);
    }

    private static AprsDataBuilder Coefficients(AprsStation from, Args a)
    {
        double[] list = [.. (a.Node("coefficients") as JsonArray ?? []).Select(v => (double)v!)];
        return a.Str("addressee") is { } addressee && addressee != from.Source.Value
            ? from.Data(new AprsTelemetryCoefficients { Addressee = addressee, Coefficients = list })
            : from.TelemetryCoefficients(list);
    }

    private static AprsDataBuilder Bits(AprsStation from, Args a)
    {
        string bits = a.Str("bits")!;
        if (bits.Length != 8 || bits.Any(c => c is not ('0' or '1')))
        {
            throw new UnsupportedException("telemetry-bits bits: the builder takes eight 0 or 1");
        }

        byte value = 0;
        for (int i = 0; i < 8; i++)
        {
            value |= (byte)(bits[i] == '1' ? 1 << i : 0);
        }

        string project = a.Str("project") ?? "";
        return a.Str("addressee") is { } addressee && addressee != from.Source.Value
            ? from.Data(new AprsTelemetryBitSense { Addressee = addressee, Bits = value, ProjectTitle = project })
            : from.TelemetryBits(value, project);
    }

    // ------------------------------------------------------------------ values

    /// <summary><c>{"utc": "...", "format": "dhm" | "hms" | "mdhm"}</c> as the builder's timestamp.</summary>
    private static AprsTimestamp? Timestamp(Args a)
    {
        if (a.Node("timestamp") is not JsonObject ts)
        {
            return null;
        }

        var t = new Args(ts, "timestamp");
        DateTime utc = DateTime.Parse(t.Str("utc")!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        AprsTimestampFormat format = t.Str("format") switch
        {
            "dhm" => AprsTimestampFormat.DayHoursMinutesUtc,
            "hms" => AprsTimestampFormat.HoursMinutesSecondsUtc,
            "mdhm" => AprsTimestampFormat.MonthDayHoursMinutesUtc,
            var other => throw new UnsupportedException($"timestamp.format '{other}'"),
        };
        t.Done();
        return AprsTimestamp.FromDateTime(utc, format);
    }

    private static AprsSymbol Symbol(string s) =>
        s.Length == 2 ? new AprsSymbol(s[0], s[1]) : throw new UnsupportedException($"symbol '{s}': the builder takes a table and a code");

    /// <summary>A whole number; a fraction is unsupported where the builder takes an int.</summary>
    private static int Whole(JsonNode? v, string key, string? why = null)
    {
        double d = (double)v!;
        return d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d
            : throw new UnsupportedException($"{key} {d.ToString(CultureInfo.InvariantCulture)}: {why ?? "the builder takes a whole number"}");
    }

    private static char One(string s, string key) =>
        s.Length == 1 ? s[0] : throw new UnsupportedException($"{key}: the builder takes one character");

    /// <summary>A recipe object whose keys are taken one by one; <see cref="Done"/> reports any left over.</summary>
    private sealed class Args(JsonObject o, string where)
    {
        private readonly HashSet<string> taken = [];

        public JsonNode? Node(string key)
        {
            taken.Add(key);
            return o[key];
        }

        public string? Str(string key) => Node(key) is { } v ? (string?)v : null;

        public bool Bool(string key) => Node(key) is { } v && (bool)v;

        public double? Double(string key) => Node(key) is { } v ? (double)v : null;

        public decimal? Decimal(string key) => Node(key) is { } v ? v.GetValue<decimal>() : null;

        /// <summary>A whole number; a fraction is unsupported, since the builder takes an int there.</summary>
        public int? Int(string key, string? why = null)
        {
            if (Node(key) is not { } v)
            {
                return null;
            }

            return Whole(v, key, why);
        }

        public void Done()
        {
            string[] left = [.. o.Select(kv => kv.Key).Where(k => !taken.Contains(k))];
            if (left.Length > 0)
            {
                throw new UnsupportedException($"{where}: no way to say {string.Join(", ", left)}");
            }
        }
    }
}
