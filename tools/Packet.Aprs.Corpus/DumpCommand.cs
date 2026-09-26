using System.Text.Json;

namespace Packet.Aprs.Corpus;

/// <summary>
/// Decodes a file of raw TNC2 lines (one per line, bytes as captured) and writes one JSON object
/// per line with the decoded values, for differential comparison with other decoders
/// (tools/Packet.Aprs.Corpus/fap/*.py). Values are in this library's units; the comparison script converts.
/// </summary>
internal static class DumpCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("dump <lines-file> <json-out> [strict]");
            return 2;
        }

        AprsParseOptions options = args.Length > 2 && args[2] == "strict" ? AprsParseOptions.Strict : AprsParseOptions.Lenient;
        byte[] all = File.ReadAllBytes(args[0]);
        using var output = new FileStream(args[1], FileMode.Create);
        int start = 0;
        for (int i = 0; i <= all.Length; i++)
        {
            if (i < all.Length && all[i] != (byte)'\n')
            {
                continue;
            }

            ReadOnlySpan<byte> line = all.AsSpan(start, i - start);
            start = i + 1;
            if (line.IsEmpty)
            {
                continue;
            }

            using var json = new Utf8JsonWriter(output);
            json.WriteStartObject();
            if (!AprsPacket.TryDecode(line, out AprsPacket? packet, options))
            {
                json.WriteString("type", "header-error");
            }
            else
            {
                Write(json, packet);
            }

            json.WriteEndObject();
            json.Flush();
            output.WriteByte((byte)'\n');
        }

        return 0;
    }

    private static void Write(Utf8JsonWriter j, AprsPacket packet)
    {
        AprsData data = packet.Data is AprsThirdPartyTraffic third ? third.Packet.Data : packet.Data;
        j.WriteString("type", data is AprsUnrecognizedData u ? $"unrecognized-{u.Reason}" : data.GetType().Name);
        j.WriteString("src", (packet.Data is AprsThirdPartyTraffic t ? t.Packet : packet).Source.Value);
        j.WriteStartArray("diag");
        foreach (AprsDiagnostic d in packet.Diagnostics)
        {
            j.WriteStringValue($"{d.Severity}:{d.Code}");
        }

        j.WriteEndArray();

        switch (data)
        {
            case AprsPositionedData p:
                j.WriteNumber("lat", p.Position.Latitude);
                j.WriteNumber("lon", p.Position.Longitude);
                j.WriteNumber("amb", p.Position.Ambiguity);
                j.WriteString("sym", p.Symbol.ToString());
                j.WriteBoolean("compressed", p.IsCompressed);
                Num(j, "course", p.CourseDegrees);
                Num(j, "speed_kn", p.SpeedKnots);
                Num(j, "alt_ft", p.AltitudeFeet);
                j.WriteString("comment", p.Comment);
                if (p.Phg is { } phg)
                {
                    j.WriteString("phg", $"{phg.PowerCode}{(char)('0' + phg.HeightCode)}{phg.GainCode}{phg.DirectivityCode}");
                }

                Num(j, "rng", p.RadioRangeMiles);
                if (p.Dao is { } dao)
                {
                    j.WriteString("dao", dao.Datum.ToString());
                }

                if (p.Telemetry is { } tl)
                {
                    j.WriteNumber("tlm_seq", tl.Sequence);
                    j.WriteString("tlm_vals", string.Join(",", tl.Analog));
                }

                if (p.Weather is { } wx)
                {
                    WriteWeather(j, wx);
                }

                if (p.Frequency is { } f)
                {
                    j.WriteNumber("freq", f.FrequencyMHz);
                }

                switch (p)
                {
                    case AprsPositionReport r:
                        j.WriteBoolean("messaging", r.MessagingCapable);
                        j.WriteString("ts", r.Timestamp?.ToString());
                        break;
                    case AprsObjectReport o:
                        j.WriteString("name", o.Name);
                        j.WriteBoolean("alive", o.IsAlive);
                        j.WriteString("ts", o.Timestamp?.ToString());
                        break;
                    case AprsItemReport it:
                        j.WriteString("name", it.Name);
                        j.WriteBoolean("alive", it.IsAlive);
                        break;
                    case AprsMicEReport m:
                        j.WriteString("mice_msg", m.Message.ToString());
                        j.WriteString("mice_type", m.TypeCode?.ToString());
                        j.WriteString("mice_suffix", m.DeviceSuffix);
                        break;
                }

                break;
            case AprsMessage msg:
                j.WriteString("to", msg.Addressee);
                j.WriteString("msgid", msg.MessageId);
                switch (msg)
                {
                    case AprsTextMessage tm:
                        j.WriteString("text", tm.Text);
                        break;
                    case AprsMessageAck a:
                        j.WriteString("ack", a.AcknowledgedId);
                        break;
                    case AprsMessageReject r:
                        j.WriteString("rej", r.RejectedId);
                        break;
                    case AprsBulletin b:
                        j.WriteString("text", b.Text);
                        break;
                    case AprsNwsBulletin n:
                        j.WriteString("text", n.Text);
                        break;
                }

                break;
            case AprsStatusReport s:
                j.WriteString("status", s.Text);
                j.WriteString("locator", s.MaidenheadLocator);
                j.WriteString("ts", s.Timestamp?.ToString());
                break;
            case AprsTelemetryReport tr:
                j.WriteString("tlm_seq", tr.Sequence);
                j.WriteString("tlm_vals", string.Join(",", tr.Analog));
                if (tr.Digital is { } bits)
                {
                    j.WriteNumber("tlm_bits", bits);
                }

                break;
            case AprsWeatherReport w:
                WriteWeather(j, w.Weather);
                break;
            case AprsNmeaReport n:
                if (n.Position is { } np)
                {
                    j.WriteNumber("lat", np.Latitude);
                    j.WriteNumber("lon", np.Longitude);
                }

                break;
        }
    }

    private static void WriteWeather(Utf8JsonWriter j, AprsWeather wx)
    {
        Num(j, "wx_dir", wx.WindDirectionDegrees);
        Num(j, "wx_speed_mph", wx.WindSpeedMph);
        Num(j, "wx_gust_mph", wx.WindGustMph);
        Num(j, "wx_temp_f", wx.TemperatureFahrenheit);
        Num(j, "wx_hum", wx.HumidityPercent);
        Num(j, "wx_pres", wx.PressureMillibars);
        Num(j, "wx_rain1h_in", wx.RainLastHourInches);
        Num(j, "wx_rain24h_in", wx.RainLast24HoursInches);
        Num(j, "wx_rainmid_in", wx.RainSinceMidnightInches);
        Num(j, "wx_lum", wx.LuminosityWattsPerSquareMetre);
    }

    private static void Num(Utf8JsonWriter j, string name, double? value)
    {
        if (value is { } v)
        {
            j.WriteNumber(name, v);
        }
    }

    private static void Num(Utf8JsonWriter j, string name, int? value)
    {
        if (value is { } v)
        {
            j.WriteNumber(name, v);
        }
    }
}
