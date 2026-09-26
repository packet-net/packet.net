using Packet.Ax25;
using Packet.Core;

namespace Packet.Aprs.Tests;

/// <summary>
/// The examples in src/Packet.Aprs/README.md, kept honest: each test runs the README's code (with
/// its placeholders filled in) and checks the output its comments claim.
/// </summary>
public class ReadmeExamples
{
    [Fact]
    public void The_shape_of_it()
    {
        AprsPacket packet = AprsPacket.Decode("N0CALL-9>APZ001,WIDE1-1,qAR,M0LTE-10:!5130.00N/00007.00W>088/036/A=001234Hello");

        var p = packet.Data.Should().BeOfType<AprsPositionReport>().Subject;
        $"{p.Position.Latitude}, {p.Position.Longitude}".Should().Be("51.5, -0.11666666666666667");
        p.Symbol.Description.Should().Be("Car");
        $"{p.CourseDegrees} deg at {p.SpeedKnots} kn".Should().Be("88 deg at 36 kn");
        $"{p.AltitudeFeet} ft, \"{p.Comment}\"".Should().Be("1234 ft, \"Hello\"");
        packet.QConstruct!.Value.Station!.Value.Value.Should().Be("M0LTE-10");
    }

    [Fact]
    public void Example_1_every_station_from_an_aprs_is_feed_on_a_map()
    {
        string[] lines =
        [
            "# aprsc 2.1.19-g730c5c0 25 Sep 2026 18:42:00 GMT T2TEST 192.0.2.1:14580",
            "N0CALL-9>APZ001,WIDE1-1,qAR,N0CALL-10:!5130.00N/00007.00W>Mobile",
            "N0CALL>APZ001:;LEADER   *092345z4903.50N/07201.75W>",
            "N0CALL>APZ001:)AID #2!4903.50N/07201.75WA",
            "N1JCM-9>TRQP7T,WA1PLE-4*:`c'wl|+>/`\"4-}_%",
            "N0CALL-10>APZ001,TCPIP*,qAC,T2TEST:}N0CALL-5>APZ001,TCPIP,N0CALL-10*:!5130.00N/00007.00W>relayed",
            "N0CALL>APZ001:;LEADER   _092345z4903.50N/07201.75W>",
            "N0CALL>APZ001::N0CALL-7 :not a position",
            "not a packet",
        ];
        var plotted = new List<string>();
        var removed = new List<string>();
        void Plot(string name, AprsPosition position, AprsSymbol symbol) => plotted.Add(name);
        void Remove(string name) => removed.Add(name);

        foreach (string line in lines)
        {
            if (line.StartsWith('#') || !AprsPacket.TryDecode(line, out AprsPacket? packet))
            {
                continue;
            }

            AprsPacket station = packet.Data is AprsThirdPartyTraffic relayed ? relayed.Packet : packet;

            switch (station.Data)
            {
                case AprsObjectReport { IsAlive: false } killed:
                    Remove(killed.Name);
                    break;
                case AprsObjectReport o:
                    Plot(o.Name, o.Position, o.Symbol);
                    break;
                case AprsItemReport i:
                    Plot(i.Name, i.Position, i.Symbol);
                    break;
                case AprsPositionedData p:
                    Plot(station.Source.Value, p.Position, p.Symbol);
                    break;
            }
        }

        plotted.Should().Equal("N0CALL-9", "LEADER", "AID #2", "N1JCM-9", "N0CALL-5");
        removed.Should().Equal("LEADER");
    }

    [Fact]
    public void Example_2_who_is_moving_and_on_what_radio()
    {
        AprsPacket packet = AprsPacket.Decode("N1JCM-9>TRQP7T,WA1PLE-4*:`c'wl|+>/`\"4-}_%");
        var mobile = (AprsMicEReport)packet.Data;

        $"{mobile.Position.Latitude}, {mobile.Position.Longitude}".Should().Be("42.179, -71.1985");
        $"{mobile.SpeedKnots} kn heading {mobile.CourseDegrees} deg".Should().Be("9 kn heading 215 deg");
        $"{mobile.AltitudeFeet:F0} ft".Should().Be("72 ft");
        mobile.Message.Should().Be(AprsMicEMessage.OffDuty);
        mobile.Device!.ToString().Should().Be("Yaesu FTM-400DR");
        mobile.MessagingCapable.Should().BeTrue();
        packet.Path[0].ToString().Should().Be("WA1PLE-4*");
        AprsDeviceIdentification.Identify(packet).Should().Be(mobile.Device);
    }

    [Fact]
    public void Example_3_a_weather_station()
    {
        DateTime receivedAt = new(2026, 9, 9, 18, 45, 0, DateTimeKind.Utc);

        AprsPacket packet = AprsPacket.Decode("W1TG2>APU25N:@091842z4256.20N/07049.42W_310/004g015t081r000p033P002h54b10001/ - Hampton, NH Wx");
        var report = (AprsPositionReport)packet.Data;
        AprsWeather wx = report.Weather!;

        double celsius = (wx.TemperatureFahrenheit!.Value - 32) / 1.8;
        $"{celsius:F1} C, {wx.HumidityPercent}% humidity, {wx.PressureMillibars} hPa".Should().Be("27.2 C, 54% humidity, 1000.1 hPa");
        $"wind {wx.WindDirectionDegrees} deg at {wx.WindSpeedMph} mph, gusting {wx.WindGustMph}".Should().Be("wind 310 deg at 4 mph, gusting 15");
        $"{wx.RainSinceMidnightInches} in of rain since midnight".Should().Be("0.02 in of rain since midnight");

        DateTime? observed = report.Timestamp?.Resolve(receivedAt);
        observed.Should().Be(new DateTime(2026, 9, 9, 18, 42, 0, DateTimeKind.Utc));
        AprsDeviceIdentification.Identify(packet)?.Model.Should().Be("UI-View32");

        packet.Diagnostics[0].Code.Should().Be(AprsDiagnosticCode.WeatherComment);
        report.Comment.Should().Contain("Hampton, NH Wx");
    }

    [Fact]
    public void Example_4_send_a_message_and_know_when_it_arrived()
    {
        var message = new AprsTextMessage { Addressee = "N0CALL-7", Text = "Meet at the club at 8?", MessageId = "42" };
        AprsPacket outgoing = AprsPacket.Create("M0LTE-9", "APZ001", message, "WIDE1-1,WIDE2-1");
        outgoing.ToString().Should().Be("M0LTE-9>APZ001,WIDE1-1,WIDE2-1::N0CALL-7 :Meet at the club at 8?{42");

        AprsPacket incoming = AprsPacket.Decode("N0CALL-7>APDR16,WIDE1-1,qAR,N0CALL-10::M0LTE-9  :ack42");
        bool delivered = incoming.Data is AprsMessageAck ack && ack.Addressee == "M0LTE-9" && ack.AcknowledgedId == message.MessageId;

        delivered.Should().BeTrue();
    }

    [Fact]
    public void Example_5_announce_a_repeater()
    {
        var repeater = new AprsObjectReport
        {
            Name = "MYRPTR",
            IsAlive = true,
            Timestamp = AprsTimestamp.DayHoursMinutes(25, 18, 30),
            Position = new AprsPosition(51.4543, -0.9781),
            Symbol = AprsSymbol.Parse("/r"),
            Frequency = new AprsVoiceFrequency { FrequencyMHz = 145.725m, ToneType = AprsToneType.Tone, ToneValue = 118, OffsetKHz = -600 },
            Comment = "Reading repeater",
        };

        AprsPacket.Create("M0LTE", "APZ001", repeater, "WIDE2-1").ToString()
            .Should().Be("M0LTE>APZ001,WIDE2-1:;MYRPTR   *251830z5127.26N/00058.69Wr145.725MHz T118 -060 Reading repeater");

        AprsPacket kill = AprsPacket.Create("M0LTE", "APZ001", repeater with { IsAlive = false }, "WIDE2-1");
        kill.ToString().Should().Contain(";MYRPTR   _251830z");

        var heard = (AprsObjectReport)AprsPacket.Decode(kill.ToString()).Data;
        heard.IsAlive.Should().BeFalse();
        heard.Frequency.Should().Be(repeater.Frequency);
        heard.Position.Latitude.Should().BeApproximately(51.4543, 0.01 / 60);
    }

    [Fact]
    public void Example_6_telemetry_into_real_values()
    {
        var output = new List<string>();
        string[] lines =
        [
            "M0LTE-11>APZ001::M0LTE-11 :PARM.Battery,Temp,Humidity,Light,Pressure,Door,Mains,Fan",
            "M0LTE-11>APZ001::M0LTE-11 :UNIT.V,degC,%,lux,hPa,open,on,on",
            "M0LTE-11>APZ001::M0LTE-11 :EQNS.0,0.075,0,0,0.5,-40,0,0.4,0,0,4,0,0,1,900",
            "M0LTE-11>APZ001:T#123,172,123,150,050,113,10100000",
        ];

        AprsTelemetryParameterNames? names = null;
        AprsTelemetryUnits? units = null;
        AprsTelemetryCoefficients? equations = null;
        foreach (string line in lines)
        {
            switch (AprsPacket.Decode(line).Data)
            {
                case AprsTelemetryParameterNames n: names = n; break;
                case AprsTelemetryUnits u: units = u; break;
                case AprsTelemetryCoefficients e: equations = e; break;
                case AprsTelemetryReport t when names is not null && units is not null && equations is not null:
                    for (int channel = 1; channel <= t.Analog.Count; channel++)
                    {
                        if (t.Analog[channel - 1] is decimal raw)
                        {
                            output.Add($"{names.Names[channel - 1]}: {equations.Scale(channel, raw):0.##} {units.Units[channel - 1]}");
                        }
                    }

                    for (int bit = 0; bit < 8 && 5 + bit < names.Names.Count; bit++)
                    {
                        bool set = ((t.Digital >> bit) & 1) == 1;
                        output.Add($"{names.Names[5 + bit]}: {(set ? units.Units[5 + bit] : "-")}");
                    }

                    break;
            }
        }

        output.Should().Equal(
            "Battery: 12.9 V",
            "Temp: 21.5 degC",
            "Humidity: 60 %",
            "Light: 200 lux",
            "Pressure: 1013 hPa",
            "Door: open",
            "Mains: -",
            "Fan: on");
    }

    [Fact]
    public void Example_7_check_your_own_beacon_against_the_spec()
    {
        const string beacon = "N1EOE>APN391,N1NCI-3*,WIDE2-1:!4216.95n/07243.20w#phg6230/ Easthampton MA";

        AprsPacket strict = AprsPacket.Decode(beacon, AprsParseOptions.Strict);
        strict.Diagnostics.Select(d => $"{d.Severity} {d.Code}: {d.Message}")
            .Should().Equal("Error LowercaseHemisphere: hemisphere 'n' must be upper case (UAP 5.9)");
        strict.Data.Should().BeOfType<AprsUnrecognizedData>();

        AprsPacket lenient = AprsPacket.Decode(beacon);
        lenient.Data.Should().BeOfType<AprsPositionReport>();
        lenient.Diagnostics.Should().HaveCount(2)
            .And.OnlyContain(d => d.Code == AprsDiagnosticCode.LowercaseHemisphere && d.Severity == AprsDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Example_8_on_the_air_with_packet_ax25()
    {
        Ax25Frame frame = Ax25Frame.Ui(new Callsign("APZ001"), new Callsign("N0CALL", 9), "!5130.00N/00007.00W>"u8);
        AprsPacket packet = AprsPacket.Create("M0LTE-9", "APZ001", new AprsStatusReport { Text = "hi" });

        AprsPacket received = AprsPacket.DecodeAx25(frame.ToBytes());
        Ax25Frame.TryParse(packet.ToAx25Frame(), out Ax25Frame? toSend).Should().BeTrue();

        AprsAddress me = AprsAddress.FromCallsign(new Callsign("M0LTE", 9));
        received.Source.TryGetCallsign(out Callsign source).Should().BeTrue();

        received.Data.Should().BeOfType<AprsPositionReport>();
        toSend!.Info.ToArray().Should().Equal(">hi"u8.ToArray());
        me.Value.Should().Be("M0LTE-9");
        source.Should().Be(new Callsign("N0CALL", 9));

        AprsPacket fromAprsIs = AprsPacket.Decode("WHO-IS>APJIW4,TCPIP*,qAC,AE5PL-JF:>x");
        FluentActions.Invoking(fromAprsIs.ToAx25Frame).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Strict_or_lenient()
    {
        const string line = "W1TG2>APU25N:@091842z4256.20N/07049.42W_310/004g015t081r000p033P002h54b10001/ - Hampton, NH Wx";
        AprsPacket.Decode(line).Data.Should().BeOfType<AprsPositionReport>();
        AprsPacket.Decode(line, AprsParseOptions.Strict).Data.Should().BeOfType<AprsUnrecognizedData>();
        AprsPacket.Decode(line, AprsParseOptions.Lenient with { AllowWeatherComment = false }).Data.Should().BeOfType<AprsUnrecognizedData>();
    }

    [Fact]
    public void Information_holds_the_bytes_as_received()
    {
        // A legal but non-canonical position: re-encoding normalises it, Information does not.
        AprsPacket packet = AprsPacket.Decode("N0CALL>APZ001:!4903.50N/07201.75W-/Test");
        packet.Information.ToArray().Should().Equal("!4903.50N/07201.75W-/Test"u8.ToArray());
        packet.Data.ToInformationField().Should().NotEqual(packet.Information.ToArray());
    }
}
