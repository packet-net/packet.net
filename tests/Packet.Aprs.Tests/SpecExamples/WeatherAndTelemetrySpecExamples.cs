namespace Packet.Aprs.Tests.SpecExamples;

/// <summary>Weather (APRS12c §12) and telemetry (§13).</summary>
public class WeatherAndTelemetrySpecExamples : AprsSpec
{
    [Fact]
    public void Section12_positionless_weather_report()
    {
        GivenInformationField("_10090556c220s004g005t077r000p000P000h50b09900wRSW");
        WhenDecoded();
        var report = ThenDataIs<AprsWeatherReport>();
        ThenTimestampIs("10090556");
        report.Weather.Should().Be(new AprsWeather
        {
            WindDirectionDegrees = 220,
            WindSpeedMph = 4,
            WindGustMph = 5,
            TemperatureFahrenheit = 77,
            RainLastHourInches = 0,
            RainLast24HoursInches = 0,
            RainSinceMidnightInches = 0,
            HumidityPercent = 50,
            PressureMillibars = 990.0,
            SoftwareType = 'w',
            UnitType = "RSW",
        });
        ThenDiagnostic(AprsDiagnosticCode.ObsoleteFormat, AprsDiagnosticSeverity.Info);
        ThenNoProblems();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section12_rain_gauge_with_unknown_wind_and_temperature()
    {
        GivenInformationField("_10090556c...s...g...t...P012Jim");
        WhenDecoded();
        var wx = ThenDataIs<AprsWeatherReport>().Weather;
        wx.WindDirectionDegrees.Should().BeNull();
        wx.TemperatureFahrenheit.Should().BeNull();
        wx.RainSinceMidnightInches.Should().Be(0.12);
        wx.SoftwareType.Should().Be('J');
        wx.UnitType.Should().Be("im");
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("!4903.50N/07201.75W_220/004g005t077r000p000P000h50b09900wRSW", 77)]
    [InlineData("@092345z4903.50N/07201.75W_220/004g005t-07r000p000P000h50b09900wRSW", -7)]
    public void Section12_complete_weather_reports(string info, int temperature)
    {
        GivenInformationField(info);
        WhenDecoded();
        var wx = ThenDataIs<AprsPositionReport>().Weather!;
        wx.WindDirectionDegrees.Should().Be(220);
        wx.WindSpeedMph.Should().Be(4);
        wx.TemperatureFahrenheit.Should().Be(temperature);
        wx.PressureMillibars.Should().Be(990);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section12_complete_weather_report_with_unknown_pressure_digits()
    {
        // As printed: "b. ...", dots for an unknown value (APRS12c §12). Five characters: "....." here.
        GivenInformationField("!4903.50N/07201.75W_220/004g005t077r000p000P000h50b.....wRSW");
        WhenDecoded();
        ThenDataIs<AprsPositionReport>().Weather!.PressureMillibars.Should().BeNull();
        ThenNoDiagnostics();
    }

    [Fact]
    public void Section12_compressed_weather_report_carries_wind_in_cs()
    {
        GivenInformationField("@092345z/5L!!<*e7_7P[g005t077r000p000P000h50b09900wRSW");
        WhenDecoded();
        var report = ThenDataIs<AprsPositionReport>();
        report.IsCompressed.Should().BeTrue();
        report.Weather!.WindDirectionDegrees.Should().Be(88);
        report.Weather.WindSpeedMph.Should().BeApproximately(36.2 * 1.15078, 0.1);
        report.Weather.TemperatureFahrenheit.Should().Be(77);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("!!006B005803500000----03E9--------002105140000005D", AprsRawWeatherFormat.UltimeterLogging)]
    [InlineData("#50B7500820082", AprsRawWeatherFormat.PeetBrosHash)]
    [InlineData("$ULTW0031003702CE0069----000086A00001----011901CC00000005", AprsRawWeatherFormat.UltimeterPacket)]
    [InlineData("*7007600000000", AprsRawWeatherFormat.PeetBrosStar)]
    public void Section12_raw_weather_station_formats_are_kept_as_text(string info, AprsRawWeatherFormat format)
    {
        GivenInformationField(info);
        WhenDecoded();
        ThenDataIs<AprsRawWeatherReport>().Format.Should().Be(format);
        ThenDiagnostic(AprsDiagnosticCode.ObsoleteFormat, AprsDiagnosticSeverity.Info);
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("T#005,199,000,255,073,123,01101001", "005", new[] { 199.0, 0, 255, 73, 123 }, 0b10010110)]
    [InlineData("T#MIC199,000,255,073,123,01101001", "MIC", new[] { 199.0, 0, 255, 73, 123 }, 0b10010110)]
    [InlineData("T#151,45.7,2.3,190.0,91.0,-7.3,00001100", "151", new[] { 45.7, 2.3, 190.0, 91.0, -7.3 }, 0b00110000)]
    public void Section13_telemetry_reports(string info, string sequence, double[] analog, int digital)
    {
        GivenInformationField(info);
        WhenDecoded();
        var t = ThenDataIs<AprsTelemetryReport>();
        t.Sequence.Should().Be(sequence);
        t.Analog.Select(v => (double)v!.Value).Should().Equal(analog);
        t.Digital.Should().Be((byte)digital);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("|ss11|", 7544, new[] { 1472 }, null)]
    [InlineData("|ss112233|", 7544, new[] { 1472, 1564, 1656 }, null)]
    [InlineData("|ss1122334455!\"|", 7544, new[] { 1472, 1564, 1656, 1748, 1840 }, 1)]
    [InlineData("|!!!!|", 0, new[] { 0 }, null)]
    public void Section13_base91_comment_telemetry(string telemetry, int sequence, int[] analog, int? digital)
    {
        GivenInformationField("!4903.50N/07201.75W-Hello" + telemetry);
        WhenDecoded();
        var report = ThenDataIs<AprsPositionReport>();
        report.Telemetry!.Sequence.Should().Be(sequence);
        report.Telemetry.Analog.Should().Equal(analog);
        report.Telemetry.Digital.Should().Be((byte?)digital);
        ThenCommentIs("Hello");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section13_parameter_names()
    {
        GivenInformationField(":N0QBF-11 :PARM.Battery,Btemp,ATemp,Pres,Alt,Camra,Chut,Sun,10m,ATV");
        WhenDecoded();
        var parm = ThenDataIs<AprsTelemetryParameterNames>();
        parm.Addressee.Should().Be("N0QBF-11");
        parm.Names.Should().Equal("Battery", "Btemp", "ATemp", "Pres", "Alt", "Camra", "Chut", "Sun", "10m", "ATV");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section13_units()
    {
        GivenInformationField(":N0QBF-11 :UNIT.v/100,deg.F,deg.F,Mbar,Kft,Click,OPEN,on,on,hi");
        WhenDecoded();
        ThenDataIs<AprsTelemetryUnits>().Units.Should().Equal("v/100", "deg.F", "deg.F", "Mbar", "Kft", "Click", "OPEN", "on", "on", "hi");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section13_equation_coefficients_scale_199_to_10_348_volts()
    {
        GivenInformationField(":N0QBF-11 :EQNS.0,5.2,0,0,.53,-32,3,4.39,49,-32,3,18,1,2,3");
        WhenDecoded();
        var eqns = ThenDataIs<AprsTelemetryCoefficients>();
        eqns.Coefficients.Should().HaveCount(15);
        eqns.Scale(1, 199).Should().Be(1034.8m);
        ThenRoundTrips();
    }

    [Fact]
    public void Section13_bit_sense_and_project_name()
    {
        GivenInformationField(":N0QBF-11 :BITS.10110000,N0QBF's Big Balloon");
        WhenDecoded();
        var bits = ThenDataIs<AprsTelemetryBitSense>();
        bits.Bits.Should().Be(0b00001101);
        bits.ProjectTitle.Should().Be("N0QBF's Big Balloon");
        ThenReEncodesExactly();
    }
}
