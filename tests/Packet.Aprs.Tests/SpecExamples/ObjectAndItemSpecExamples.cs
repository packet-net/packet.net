namespace Packet.Aprs.Tests.SpecExamples;

/// <summary>Objects and items: APRS12c §11, plus storm and weather objects from §12.</summary>
public class ObjectAndItemSpecExamples : AprsSpec
{
    [Fact]
    public void Section11_live_object_with_course_and_speed()
    {
        GivenInformationField(";LEADER   *092345z4903.50N/07201.75W>088/036");
        WhenDecoded();
        var obj = ThenDataIs<AprsObjectReport>();
        obj.Name.Should().Be("LEADER");
        obj.IsAlive.Should().BeTrue();
        ThenTimestampIs("092345z");
        ThenCourseAndSpeedAre(88, 36);
        ThenSymbolIs("/>");
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section11_killed_object()
    {
        GivenInformationField(";LEADER   _092345z4903.50N/07201.75W>088/036");
        WhenDecoded();
        ThenDataIs<AprsObjectReport>().IsAlive.Should().BeFalse();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section11_compressed_object()
    {
        GivenInformationField(";LEADER   *092345z/5L!!<*e7>7P[");
        WhenDecoded();
        ThenDataIs<AprsObjectReport>().IsCompressed.Should().BeTrue();
        ThenPositionIs(49.5, -72.75, tolerance: 1e-5);
        ThenCourseAndSpeedAre(88, 36.2, tolerance: 0.05);
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData(")AID#2!4903.50N/07201.75WA", "AID#2", true, "/A")]
    [InlineData(")AID #2_4903.50N/07201.75WA", "AID #2", false, "/A")]
    public void Section11_items(string info, string name, bool alive, string symbol)
    {
        GivenInformationField(info);
        WhenDecoded();
        var item = ThenDataIs<AprsItemReport>();
        item.Name.Should().Be(name);
        item.IsAlive.Should().Be(alive);
        ThenSymbolIs(symbol);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section11_item_somewhere_in_england_to_the_nearest_degree()
    {
        GivenInformationField(")G/WB4APR!53  .  N\\002  .  Wd");
        WhenDecoded();
        ThenDataIs<AprsItemReport>().Name.Should().Be("G/WB4APR");
        ThenPositionIs(53.5, -2.5, ambiguity: 4);
        ThenSymbolIs("\\d");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section11_compressed_item()
    {
        GivenInformationField(")MOBIL!\\5L!!<*e79 sT");
        WhenDecoded();
        ThenDataIs<AprsItemReport>().Name.Should().Be("MOBIL");
        ThenSymbolIs("\\9");
        ThenPositionIs(49.5, -72.75, tolerance: 1e-5);
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData(";SEARCH   *092345z4903.50N\\07201.75Wl710/310", AprsAreaShape.FilledEllipse, AprsAreaColor.Cyan)]
    [InlineData(";SEARCH   *092345z4903.50N\\07201.75Wl8101310", AprsAreaShape.FilledTriangle, AprsAreaColor.VioletLow)]
    public void Section11_area_objects(string info, AprsAreaShape shape, AprsAreaColor color)
    {
        GivenInformationField(info);
        WhenDecoded();
        var area = ThenDataIs<AprsObjectReport>().AreaObject!.Value;
        area.Shape.Should().Be(shape);
        area.Color.Should().Be(color);
        area.LatitudeOffsetCode.Should().Be(10);
        area.LongitudeOffsetCode.Should().Be(10);
        area.LatitudeOffsetDegrees.Should().BeApproximately(4 / 60.0, 1e-12);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    /// <summary>The spec's own example has no timestamp (see docs/aprs-spec-interpretations.md).</summary>
    [Fact]
    public void Section11_line_object_with_corridor_width()
    {
        GivenInformationField(";FLIGHTPTH*4903.50N\\07201.75Wl610/310{100}");
        WhenDecoded();
        var obj = ThenDataIs<AprsObjectReport>();
        obj.Name.Should().Be("FLIGHTPTH");
        obj.AreaObject.Should().Be(new AprsAreaObject(AprsAreaShape.LineDownLeft, AprsAreaColor.Cyan, 10, 10, 100));
        ThenCommentIs("");
        ThenToleratedWith(AprsDiagnosticCode.ObjectWithoutTimestamp);
    }

    [Fact]
    public void Section11_object_without_timestamp_is_rejected_when_strict()
    {
        GivenInformationField(";FLIGHTPTH*4903.50N\\07201.75Wl610/310{100}");
        GivenStrictDecoding();
        WhenDecoded();
        ThenRejectedWith(AprsDiagnosticCode.ObjectWithoutTimestamp);
    }

    [Fact]
    public void Section11_signpost_item()
    {
        GivenInformationField(")I91 3N!4903.50N\\07201.75Wm{55}");
        WhenDecoded();
        var item = ThenDataIs<AprsItemReport>();
        item.Name.Should().Be("I91 3N");
        item.SignpostText.Should().Be("55");
        ThenCommentIs("");
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData(";BRENDA   *092345z4903.50N\\07202.75W@088/036/HC/150^200/0980>090&030%040", 150, 200, 980, 90, 30, 40)]
    [InlineData(";BRENDA   *100045z4905.50N/07201.75W@101/047/HC/104^123/0980>065&020%040", 104, 123, 980, 65, 20, 40)]
    public void Section12_hurricane_objects_with_storm_data(string info, int wind, int gust, int pressure, int hurricaneRadius, int stormRadius, int galeRadius)
    {
        GivenInformationField(info);
        WhenDecoded();
        var storm = ThenDataIs<AprsObjectReport>().Storm!;
        storm.Type.Should().Be(AprsStormType.Hurricane);
        storm.SustainedWindKnots.Should().Be(wind);
        storm.GustKnots.Should().Be(gust);
        storm.CentralPressureMillibars.Should().Be(pressure);
        storm.HurricaneWindRadiusNauticalMiles.Should().Be(hurricaneRadius);
        storm.TropicalStormWindRadiusNauticalMiles.Should().Be(stormRadius);
        storm.WholeGaleRadiusNauticalMiles.Should().Be(galeRadius);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section12_weather_object_with_timestamp()
    {
        GivenInformationField(";BRENDA   *092345z4903.50N/07201.75W_220/004g005b0990");
        WhenDecoded();
        var wx = ThenDataIs<AprsObjectReport>().Weather!;
        wx.WindDirectionDegrees.Should().Be(220);
        wx.WindSpeedMph.Should().Be(4);
        wx.WindGustMph.Should().Be(5);

        // "b0990" is 4 digits, one short of the 5 the field needs (APRS12c 1.2c fixed the width at 5),
        // read in tenths as Ham::APRS::FAP does; and there is no temperature field.
        wx.PressureMillibars.Should().Be(99.0);
        ThenToleratedWith(AprsDiagnosticCode.NonStandardWeatherFieldWidth);
        ThenToleratedWith(AprsDiagnosticCode.IncompleteWeather);
        ThenCommentIs("");
    }

    [Fact]
    public void Understanding_2_3_object_with_permanent_timestamp_and_dao()
    {
        GivenPacket("W1OEM-5>APWW11,EKONCT,WA1PLE-4*:;ELYME    *190116z4122.06N/07212.98W#145.03 Packet Node ELYME!W98!");
        WhenDecoded();
        var obj = ThenDataIs<AprsObjectReport>();
        obj.Name.Should().Be("ELYME");
        obj.Dao.Should().Be(new AprsDao('W', AprsDaoPrecision.Thousandths));
        ThenPositionIs(41 + (22.069 / 60), -(72 + (12.988 / 60)), tolerance: 1e-9);
        ThenCommentIs("145.03 Packet Node ELYME");
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section18_frequency_object_with_permanent_marker()
    {
        GivenPacket("EKONCT>BEACON:;146.730CT*111111z4134.84N/07206.31Wr146.730MHz T156 R30m ECTN 9P DAILY RASON");
        WhenDecoded();
        var obj = ThenDataIs<AprsObjectReport>();
        obj.Timestamp!.Value.IsPermanentObjectMarker.Should().BeTrue();
        obj.Frequency.Should().Be(new AprsVoiceFrequency
        {
            FrequencyMHz = 146.730m,
            ToneType = AprsToneType.Tone,
            ToneValue = 156,
            Range = 30,
        });
        ThenCommentIs("ECTN 9P DAILY RASON");
        ThenReEncodesExactly();
    }
}
