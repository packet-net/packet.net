namespace Packet.Aprs.Tests.SpecExamples;

/// <summary>Every position example in APRS12c §6-§9 decodes as the spec describes.</summary>
public class PositionReportSpecExamples : AprsSpec
{
    private const double Lat4903_50 = 49 + (3.50 / 60);
    private const double Lon07201_75 = -(72 + (1.75 / 60));

    // Compressed positions resolve to 1/380926 degree of latitude and 1/190463 of longitude.
    private const double CompressedTolerance = 1e-5;

    [Fact]
    public void Section8_no_timestamp_no_messaging_with_comment()
    {
        GivenInformationField("!4903.50N/07201.75W-Test 001234");
        WhenDecoded();
        var report = ThenDataIs<AprsPositionReport>();
        report.Timestamp.Should().BeNull();
        report.MessagingCapable.Should().BeFalse();
        ThenPositionIs(Lat4903_50, Lon07201_75);
        ThenSymbolIs("/-");
        ThenCommentIs("Test 001234");
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section8_altitude_anywhere_in_the_comment()
    {
        GivenInformationField("!4903.50N/07201.75W-Test /A=001234");
        WhenDecoded();
        ThenAltitudeFeetIs(1234);
        ThenCommentIs("Test ");
        ThenNoDiagnostics();
        ThenRoundTrips();
    }

    [Fact]
    public void Section8_position_to_the_nearest_degree_is_the_centre_of_that_degree()
    {
        GivenInformationField("!49  .  N/072  .  W-");
        WhenDecoded();
        ThenPositionIs(49.5, -72.5, ambiguity: 4);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section8_symbol_n()
    {
        GivenInformationField("!4903.50N/07201.75Wn");
        WhenDecoded();
        ThenSymbolIs("/n");
        ThenCommentIs("");
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("/092345z4903.50N/07201.75W>Test1234", "092345z", false)]
    [InlineData("@092345/4903.50N/07201.75W>Test1234", "092345/", true)]
    public void Section8_with_timestamp(string info, string timestamp, bool messaging)
    {
        GivenInformationField(info);
        WhenDecoded();
        ThenDataIs<AprsPositionReport>().MessagingCapable.Should().Be(messaging);
        ThenTimestampIs(timestamp);
        ThenPositionIs(Lat4903_50, Lon07201_75);
        ThenSymbolIs("/>");
        ThenCommentIs("Test1234");
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section8_with_phg_extension()
    {
        GivenInformationField("=4903.50N/07201.75W#PHG5132");
        WhenDecoded();
        var report = ThenDataIs<AprsPositionReport>();
        report.MessagingCapable.Should().BeTrue();
        report.Phg.Should().Be(new AprsPhg(5, 1, 3, 2));
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section7_phg_5132_means_25_watts_20_feet_3_dbi_east_and_about_8_miles()
    {
        var phg = new AprsPhg(5, 1, 3, 2);
        phg.PowerWatts.Should().Be(25);
        phg.HeightFeet.Should().Be(20);
        phg.GainDbi.Should().Be(3);
        phg.DirectivityDegrees.Should().Be(90);
        phg.RangeMiles.Should().BeApproximately(7.9, 0.05);
    }

    [Fact]
    public void Section8_weather_report_in_a_position()
    {
        GivenInformationField("=4903.50N/07201.75W_225/000g000t050r000p001h00b10138dU2k");
        WhenDecoded();
        var wx = ThenDataIs<AprsPositionReport>().Weather!;
        wx.WindDirectionDegrees.Should().Be(225);
        wx.WindSpeedMph.Should().Be(0);
        wx.WindGustMph.Should().Be(0);
        wx.TemperatureFahrenheit.Should().Be(50);
        wx.RainLastHourInches.Should().Be(0);
        wx.RainLast24HoursInches.Should().Be(0.01);
        wx.HumidityPercent.Should().Be(100);
        wx.PressureMillibars.Should().Be(1013.8);
        wx.SoftwareType.Should().Be('d');
        wx.UnitType.Should().Be("U2k");
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("@092345/4903.50N/07201.75W>088/036", 88, 36.0)]
    public void Section8_with_course_and_speed(string info, int course, double speed)
    {
        GivenInformationField(info);
        WhenDecoded();
        ThenCourseAndSpeedAre(course, speed);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section8_hms_timestamp_with_phg()
    {
        GivenInformationField("@234517h4903.50N/07201.75W>PHG5132");
        WhenDecoded();
        ThenTimestampIs("234517h");
        ThenDataIs<AprsPositionReport>().Phg.Should().Be(new AprsPhg(5, 1, 3, 2));
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section8_radio_range()
    {
        GivenInformationField("@092345z4903.50N/07201.75W>RNG0050");
        WhenDecoded();
        ThenDataIs<AprsPositionReport>().RadioRangeMiles.Should().Be(50);
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section8_df_signal_strength()
    {
        GivenInformationField("/234517h4903.50N/07201.75W>DFS2360");
        WhenDecoded();
        var dfs = ThenDataIs<AprsPositionReport>().DfSignalStrength!.Value;
        dfs.StrengthCode.Should().Be(2);
        dfs.HeightFeet.Should().Be(80);
        dfs.GainCode.Should().Be(6);
        dfs.DirectivityDegrees.Should().BeNull();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section8_weather_with_timestamp_and_software_type()
    {
        GivenInformationField("@092345z4903.50N/07201.75W_090/000g000t066r000p000dUII");
        WhenDecoded();
        var wx = ThenDataIs<AprsPositionReport>().Weather!;
        wx.WindDirectionDegrees.Should().Be(90);
        wx.TemperatureFahrenheit.Should().Be(66);
        wx.SoftwareType.Should().Be('d');
        wx.UnitType.Should().Be("UII");
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("=4903.50N/07201.75W\\088/036/270/729", 88)]
    [InlineData("=4903.50N/07201.75W\\000/036/270/729", 0)]
    [InlineData("@092345z4903.50N/07201.75W\\088/036/270/729", 88)]
    public void Section8_df_report_with_bearing_and_nrq(string info, int course)
    {
        GivenInformationField(info);
        WhenDecoded();
        var report = ThenDataIs<AprsPositionReport>();
        report.CourseDegrees.Should().Be(course);
        report.DfBearing.Should().Be(new AprsDfBearing(270, 7, 2, 9));
        report.DfBearing!.Value.RangeMiles.Should().Be(4);
        report.DfBearing!.Value.BeamwidthDegrees.Should().Be(1);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section8_df_report_without_course_and_speed()
    {
        GivenInformationField("/092345z4903.50N/07201.75W\\000/000/270/729");
        WhenDecoded();
        ThenCourseAndSpeedAre(0, 0);
        ThenDataIs<AprsPositionReport>().DfBearing.Should().Be(new AprsDfBearing(270, 7, 2, 9));
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section6_null_position_keeps_its_west_hemisphere()
    {
        GivenInformationField("!0000.00N\\00000.00W.");
        WhenDecoded();
        ThenPositionIs(0, 0);
        double.IsNegative(PositionOf(Packet.Data).Longitude).Should().BeTrue();
        ThenSymbolIs("\\.");
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("!4903.5 N/07201.75W-", 1, 49 + (3.55 / 60), -(72 + (1.75 / 60)))]
    [InlineData("!4903.  N/07201.75W-", 2, 49 + (3.5 / 60), -(72 + (1.5 / 60)))]
    [InlineData("!490 .  N/07201.75W-", 3, 49 + (5.0 / 60), -(72 + (5.0 / 60)))]
    [InlineData("!49  .  N/07201.75W-", 4, 49.5, -72.5)]
    public void Section6_ambiguity_in_the_latitude_applies_to_the_longitude(string info, int ambiguity, double lat, double lon)
    {
        GivenInformationField(info);
        WhenDecoded();
        ThenPositionIs(lat, lon, ambiguity);
        ThenNoDiagnostics();
        ThenRoundTrips();
    }

    [Theory]
    [InlineData("=/5L!!<*e7> sTComment")]
    public void Section9_compressed_without_course_speed(string info)
    {
        GivenInformationField(info);
        WhenDecoded();
        var report = ThenDataIs<AprsPositionReport>();
        report.IsCompressed.Should().BeTrue();
        report.CompressionType.Should().BeNull();
        ThenPositionIs(49.5, -72.75, tolerance: CompressedTolerance);
        ThenSymbolIs("/>");
        ThenCommentIs("Comment");
        ThenCourseAndSpeedAre(null, null);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section9_compressed_with_course_and_speed_from_rmc()
    {
        GivenInformationField("=/5L!!<*e7>7P[");
        WhenDecoded();
        var report = ThenDataIs<AprsPositionReport>();
        ThenPositionIs(49.5, -72.75, tolerance: CompressedTolerance);
        ThenCourseAndSpeedAre(88, 36.2, tolerance: 0.05);
        report.CompressionType.Should().Be(new AprsCompressionType(AprsGpsFix.Current, AprsNmeaSource.Rmc, AprsCompressionOrigin.Software));
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section9_compressed_radio_range()
    {
        GivenInformationField("=/5L!!<*e7>{?!");
        WhenDecoded();
        ThenDataIs<AprsPositionReport>().RadioRangeMiles.Should().BeApproximately(20, 0.2);
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section9_compressed_altitude_from_gga()
    {
        GivenInformationField("=/5L!!<*e7OS]S");
        WhenDecoded();
        ThenSymbolIs("/O");
        ThenAltitudeFeetIs(10004, tolerance: 1);
        ThenDataIs<AprsPositionReport>().CompressionType!.Value.Source.Should().Be(AprsNmeaSource.Gga);
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section9_compressed_with_timestamp()
    {
        GivenInformationField("@092345z/5L!!<*e7>{?!");
        WhenDecoded();
        ThenTimestampIs("092345z");
        ThenPositionIs(49.5, -72.75, tolerance: CompressedTolerance);
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section21_compressed_numeric_overlay_is_written_as_a_letter()
    {
        GivenInformationField("=d5L!!<*e7>7P[");
        WhenDecoded();
        ThenSymbolIs("3>");
        ThenDataIs<AprsPositionReport>().Symbol.Overlay.Should().Be('3');
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section21_uncompressed_overlay()
    {
        GivenInformationField("@092345z4903.50N307201.75W>");
        WhenDecoded();
        ThenSymbolIs("3>");
        ThenReEncodesExactly();
    }

    /// <summary>APRS12c §21 prints this as <c>=BL!!&lt;*e7&gt;7P[</c>, one character short: the
    /// <c>5</c> of the latitude is missing (docs/aprs-spec-interpretations.md).</summary>
    [Fact]
    public void Section21_compressed_letter_overlay()
    {
        GivenInformationField("=B5L!!<*e7>7P[");
        WhenDecoded();
        ThenSymbolIs("B>");
        ThenReEncodesExactly();
    }
}
