namespace Packet.Aprs.Tests.Encoding;

/// <summary>
/// The fluent builder, <c>Aprs.From(...)</c>: each kind of packet it builds, written out as TNC2
/// and read back under strict parsing, plus the defaults and the refusals.
/// </summary>
public class BuilderTests
{
    private static readonly AprsStation Me = Aprs.From("M0LTE");

    [Fact]
    public void A_position_report()
    {
        AprsPacket packet = Aprs.From("M0LTE-9").Via("WIDE1-1", "WIDE2-1")
            .Position(51.4543, -0.9781)
            .Symbol(AprsSymbol.Car)
            .Course(88).Speed(36).Altitude(120)
            .Comment("Mobile")
            .Build();

        Sent(packet).Should().Be("M0LTE-9>APZ001,WIDE1-1,WIDE2-1:!5127.26N/00058.69W>088/036/A=000120Mobile");
    }

    [Fact]
    public void A_position_report_with_messaging_and_a_timestamp()
    {
        AprsPacket packet = Me.Position(51.4543, -0.9781).Symbol(AprsSymbol.House)
            .Messaging().Timestamp(new DateTime(2026, 9, 26, 14, 5, 0, DateTimeKind.Utc))
            .Build();

        Sent(packet).Should().Be("M0LTE>APZ001:@261405z5127.26N/00058.69W-");
    }

    [Fact]
    public void A_compressed_position_with_extra_precision_decodes_to_what_was_given()
    {
        AprsPacket packet = Me.Position(51.4543, -0.9781).Symbol(AprsSymbol.Car).Compressed().Build();

        var heard = (AprsPositionReport)AprsPacket.Decode(Sent(packet)).Data;
        heard.IsCompressed.Should().BeTrue();
        heard.Position.Latitude.Should().BeApproximately(51.4543, 0.00001);
        heard.Position.Longitude.Should().BeApproximately(-0.9781, 0.00001);
    }

    [Fact]
    public void Other_units_are_converted_to_the_ones_aprs_sends()
    {
        var report = Me.Position(51.4543, -0.9781).Symbol(AprsSymbol.Car).SpeedKmh(100).AltitudeMetres(100).ToData();

        report.SpeedKnots.Should().BeApproximately(54, 0.1);
        report.AltitudeFeet.Should().BeApproximately(328.08, 0.01);
    }

    [Fact]
    public void Ambiguity_blanks_digits_whenever_it_is_set()
    {
        Sent(Me.Position(51.4543, -0.9781).Ambiguity(2).Symbol(AprsSymbol.House).Build())
            .Should().Be("M0LTE>APZ001:!5127.  N/00058.  W-");
        Sent(Me.Object("HIDDEN").Ambiguity(1).At(51.4543, -0.9781).Symbol(AprsSymbol.House).Timestamp(AprsTimestamp.DayHoursMinutes(26, 14, 5)).Build())
            .Should().Be("M0LTE>APZ001:;HIDDEN   *261405z5127.2 N/00058.6 W-");
    }

    [Fact]
    public void A_message_with_an_id()
    {
        Sent(Me.Message("G3NRW", "Hi Ian").WithId("01").Build()).Should().Be("M0LTE>APZ001::G3NRW    :Hi Ian{01");
    }

    [Fact]
    public void A_message_in_reply_ack_form()
    {
        Sent(Me.Message("G3NRW", "Hi Ian").WithId("02").ReplyAck("17").Build()).Should().Be("M0LTE>APZ001::G3NRW    :Hi Ian{02}17");
        Sent(Me.Message("G3NRW", "Hi Ian").WithId("03").ReplyAck().Build()).Should().Be("M0LTE>APZ001::G3NRW    :Hi Ian{03}");
    }

    [Fact]
    public void Acks_and_rejects()
    {
        Sent(Me.Ack("G3NRW", "01").Build()).Should().Be("M0LTE>APZ001::G3NRW    :ack01");
        Sent(Me.Reject("G3NRW", "01").Build()).Should().Be("M0LTE>APZ001::G3NRW    :rej01");
        Sent(Me.Ack("G3NRW", "02").ReplyAck("17").Build()).Should().Be("M0LTE>APZ001::G3NRW    :ack02}17");
    }

    [Fact]
    public void Bulletins()
    {
        Sent(Me.Bulletin('1', "Net tonight 8pm").Build()).Should().Be("M0LTE>APZ001::BLN1     :Net tonight 8pm");
        Sent(Me.Bulletin('A', "Rally Sunday").Build()).Should().Be("M0LTE>APZ001::BLNA     :Rally Sunday");
        Sent(Me.GroupBulletin('4', "WX", "Gales later").Build()).Should().Be("M0LTE>APZ001::BLN4WX   :Gales later");
    }

    [Fact]
    public void An_object_announcing_a_repeater()
    {
        AprsPacket packet = Me.Object("MYRPTR").At(51.45, -0.98).Symbol(AprsSymbol.Repeater)
            .Timestamp(AprsTimestamp.DayHoursMinutes(25, 18, 30))
            .Frequency(145.725, tone: 118.8, offsetKHz: -600)
            .Build();

        Sent(packet).Should().Be("M0LTE>APZ001:;MYRPTR   *251830z5127.00N/00058.80Wr145.725MHz T118 -060");
    }

    [Fact]
    public void An_object_is_stamped_now_unless_told_otherwise()
    {
        DateTime before = DateTime.UtcNow;
        var sent = (AprsObjectReport)AprsPacket.Decode(Sent(Me.Object("MYRPTR").At(51.45, -0.98).Symbol(AprsSymbol.Repeater).Build())).Data;

        sent.Timestamp!.Value.Format.Should().Be(AprsTimestampFormat.DayHoursMinutesUtc);
        sent.Timestamp.Value.Resolve(before).Should().BeCloseTo(before, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void A_permanent_object_and_a_killed_one()
    {
        AprsObjectBuilder obj = Me.Object("MYRPTR").At(51.45, -0.98).Symbol(AprsSymbol.Repeater).Permanent();

        Sent(obj.Build()).Should().Be("M0LTE>APZ001:;MYRPTR   *111111z5127.00N/00058.80Wr");
        Sent(obj.Kill().Build()).Should().Be("M0LTE>APZ001:;MYRPTR   _111111z5127.00N/00058.80Wr");
    }

    [Fact]
    public void An_item_and_a_killed_one()
    {
        AprsItemBuilder item = Me.Item("AID #2").At(49 + (3.50 / 60), -(72 + (1.75 / 60))).Symbol(AprsSymbol.AidStation);

        Sent(item.Build()).Should().Be("M0LTE>APZ001:)AID #2!4903.50N/07201.75WA");
        Sent(item.Kill().Build()).Should().Be("M0LTE>APZ001:)AID #2_4903.50N/07201.75WA");
    }

    [Fact]
    public void A_mic_e_report_computes_its_destination_and_is_not_an_emergency_by_default()
    {
        AprsPacket packet = Aprs.From("M0LTE-9").Via("WIDE1-1").MicE(42.179, -71.1985).Symbol(AprsSymbol.Car).Course(215).Speed(9).Messaging().Build();

        var heard = (AprsMicEReport)AprsPacket.Decode(Sent(packet)).Data;
        packet.Destination.Value.Should().NotBe(Aprs.DefaultDestination);
        heard.Message.Should().Be(AprsMicEMessage.OffDuty);
        heard.MessagingCapable.Should().BeTrue();
        heard.Position.Latitude.Should().BeApproximately(42.179, 0.01 / 60);
        heard.Position.Longitude.Should().BeApproximately(-71.1985, 0.01 / 60);
        heard.CourseDegrees.Should().Be(215);
        heard.SpeedKnots.Should().Be(9);

        var enRoute = (AprsMicEReport)AprsPacket.Decode(Sent(Me.MicE(42.179, -71.1985).Symbol(AprsSymbol.Car).Message(AprsMicEMessage.EnRoute).Build())).Data;
        enRoute.Message.Should().Be(AprsMicEMessage.EnRoute);
    }

    [Fact]
    public void A_weather_report_with_a_position()
    {
        AprsPacket packet = Me.Weather().At(51.45, -0.98).Wind(220, 4, gust: 5).Temperature(77).Humidity(54).Pressure(1013.2).Build();

        Sent(packet).Should().Be("M0LTE>APZ001:!5127.00N/00058.80W_220/004g005t077h54b10132");
    }

    [Fact]
    public void A_weather_report_without_a_position()
    {
        AprsPacket packet = Me.Weather().Timestamp(new DateTime(2026, 9, 25, 18, 30, 0, DateTimeKind.Utc))
            .Wind(220, 4, gust: 5).TemperatureCelsius(25).Rain(lastHour: 0.1, sinceMidnight: 0.25)
            .Build();

        Sent(packet).Should().Be("M0LTE>APZ001:_09251830c220s004g005t077r010P025");
    }

    [Fact]
    public void A_weather_report_needs_a_position_for_a_symbol_or_compression()
    {
        FluentActions.Invoking(() => Me.Weather().Temperature(77).Compressed().Build()).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => Me.Weather().Temperature(77).Symbol(AprsSymbol.WeatherStationWithDigipeater).Build()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_status_report()
    {
        Sent(Me.Status("Net Control").Build()).Should().Be("M0LTE>APZ001:>Net Control");
        Sent(Me.Status("Net Control").Timestamp(AprsTimestamp.DayHoursMinutes(26, 14, 5)).Build()).Should().Be("M0LTE>APZ001:>261405zNet Control");
        Sent(Me.Status("On the air").Locator("IO91SX", AprsSymbol.House).Build()).Should().Be("M0LTE>APZ001:>IO91SX/- On the air");
    }

    [Fact]
    public void Telemetry_and_what_it_means()
    {
        AprsStation wx = Aprs.From("M0LTE-11");

        Sent(wx.Telemetry(123).Analog(172, 123, 150, 50, 113).Digital("10100000").Build())
            .Should().Be("M0LTE-11>APZ001:T#123,172,123,150,050,113,10100000");
        Sent(wx.Telemetry(7).Analog(1.5m).Build()).Should().Be("M0LTE-11>APZ001:T#007,1.5,,,,,00000000");
        Sent(wx.Telemetry(8).Analog(null, 2.5m).Build()).Should().Be("M0LTE-11>APZ001:T#008,,2.5,,,,00000000");
        Sent(wx.TelemetryNames("Battery", "Temp").Build()).Should().Be("M0LTE-11>APZ001::M0LTE-11 :PARM.Battery,Temp");
        Sent(wx.TelemetryUnits("V", "degC").Build()).Should().Be("M0LTE-11>APZ001::M0LTE-11 :UNIT.V,degC");
        Sent(wx.TelemetryCoefficients(0, 0.075, 0, 0, 0.5, -40).Build()).Should().Be("M0LTE-11>APZ001::M0LTE-11 :EQNS.0,0.075,0,0,0.5,-40");
        Sent(wx.TelemetryBits(0xFF, "Garden station").Build()).Should().Be("M0LTE-11>APZ001::M0LTE-11 :BITS.11111111,Garden station");
    }

    [Fact]
    public void Comment_telemetry_rides_in_a_position_report()
    {
        AprsPacket packet = Me.Position(51.4543, -0.9781).Symbol(AprsSymbol.House).Comment("Home").Telemetry(1, 100, 200).Build();

        var heard = (AprsPositionReport)AprsPacket.Decode(Sent(packet)).Data;
        heard.Telemetry.Should().Be(new AprsCommentTelemetry { Sequence = 1, Analog = [100, 200] });
        heard.Comment.Should().Be("Home");
    }

    [Fact]
    public void The_station_is_immutable_and_sets_the_header()
    {
        AprsStation plain = Aprs.From("M0LTE");
        AprsStation routed = plain.To("APXYZ1").Via("WIDE2-1");

        plain.Destination.Value.Should().Be("APZ001");
        plain.Path.Should().BeEmpty();
        Sent(routed.Status("Hi").Build()).Should().Be("M0LTE>APXYZ1,WIDE2-1:>Hi");
    }

    [Fact]
    public void To_data_gives_the_record_for_anything_the_builder_does_not_cover()
    {
        AprsPositionReport report = Me.Position(51.4543, -0.9781).Symbol(AprsSymbol.ValueSign).ToData();

        Sent(Me.Data(report with { SignpostText = "30" }).Build()).Should().Be("M0LTE>APZ001:!5127.26N\\00058.69Wm{30}");
    }

    [Fact]
    public void Missing_pieces_are_named()
    {
        FluentActions.Invoking(() => Me.Position(51.4543, -0.9781).Build())
            .Should().Throw<InvalidOperationException>().WithMessage("*symbol*");
        FluentActions.Invoking(() => Me.Object("MYRPTR").Symbol(AprsSymbol.Repeater).Build())
            .Should().Throw<InvalidOperationException>().WithMessage("*position*");
    }

    [Fact]
    public void What_the_spec_forbids_is_refused_when_built()
    {
        FluentActions.Invoking(() => Me.Message("G3NRW", "Hi").WithId("TOOLONG").Build()).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Me.Object("NAMEISTOOLONG").At(51.45, -0.98).Symbol(AprsSymbol.Repeater).Build()).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Me.Position(51.4543, -0.9781).Symbol(AprsSymbol.Car).Compressed().Phg(new AprsPhg(5, 3, 6, 0)).Build()).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Me.Telemetry(1).Analog(1, 2, 3, 4, 5, 6)).Should().Throw<ArgumentException>();
    }

    /// <summary>The packet as TNC2 text, after checking it reads back under strict parsing with no
    /// warning or error (a positionless weather report still gets its "not recommended" note).</summary>
    private static string Sent(AprsPacket packet)
    {
        string line = packet.ToString();
        AprsPacket heard = AprsPacket.Decode(line, AprsParseOptions.Strict);
        heard.Diagnostics.Where(d => d.Severity != AprsDiagnosticSeverity.Info).Should().BeEmpty(line);
        heard.Data.Should().NotBeOfType<AprsUnrecognizedData>();
        return line;
    }
}
