namespace Packet.Aprs.Tests;

/// <summary>
/// Values Packet.Aprs works out from decoded data for convenience: PHG in watts, a beam heading in
/// degrees, whether a message wants an ack, and so on. The language-neutral vectors in
/// <c>spec/aprs/cases</c> deliberately leave these out (another field already determines them), so
/// they are checked here, against the conversions the spec defines.
/// </summary>
public class DerivedValueTests : AprsSpec
{
    [Fact]
    public void Phg_5132_is_25_watts_20_feet_3_dbi_east_and_about_8_miles()
    {
        var phg = new AprsPhg(5, 1, 3, 2); // APRS12c ch. 7
        phg.PowerWatts.Should().Be(25);
        phg.HeightFeet.Should().Be(20);
        phg.GainDbi.Should().Be(3);
        phg.DirectivityDegrees.Should().Be(90);
        phg.RangeMiles.Should().BeApproximately(7.9, 0.05);
    }

    [Fact]
    public void Dfs_2360_is_80_feet_with_an_omni_antenna()
    {
        GivenInformationField("/234517h4903.50N/07201.75W>DFS2360"); // APRS12c ch. 8
        WhenDecoded();
        var dfs = ThenDataIs<AprsPositionReport>().DfSignalStrength!.Value;
        dfs.HeightFeet.Should().Be(80);
        dfs.DirectivityDegrees.Should().BeNull();
    }

    [Fact]
    public void Df_bearing_nrq_729_is_4_miles_with_a_1_degree_beam()
    {
        GivenInformationField("=4903.50N/07201.75W\\088/036/270/729"); // APRS12c ch. 8
        WhenDecoded();
        var bearing = ThenDataIs<AprsPositionReport>().DfBearing!.Value;
        bearing.RangeMiles.Should().Be(4);
        bearing.BeamwidthDegrees.Should().Be(1);
    }

    [Fact]
    public void A_compressed_numeric_overlay_written_as_a_letter_is_overlay_3()
    {
        GivenInformationField("=d5L!!<*e7>7P["); // APRS12c ch. 21
        WhenDecoded();
        ThenDataIs<AprsPositionReport>().Symbol.Overlay.Should().Be('3');
    }

    [Fact]
    public void An_area_offset_of_10_is_4_minutes()
    {
        GivenInformationField(";SEARCH   *092345z4903.50N\\07201.75Wl8101310"); // APRS12c ch. 11
        WhenDecoded();
        ThenDataIs<AprsObjectReport>().AreaObject!.Value.LatitudeOffsetDegrees.Should().BeApproximately(4 / 60.0, 1e-12);
    }

    [Fact]
    public void Timestamp_111111z_marks_a_permanent_object()
    {
        GivenPacket("EKONCT>BEACON:;146.730CT*111111z4134.84N/07206.31Wr146.730MHz T156 R30m ECTN 9P DAILY RASON"); // APRS12c ch. 18
        WhenDecoded();
        ThenDataIs<AprsObjectReport>().Timestamp!.Value.IsPermanentObjectMarker.Should().BeTrue();
    }

    [Fact]
    public void Meteor_scatter_beam_code_B7_is_110_degrees_490_watts()
    {
        GivenInformationField(">IO91SX/- ^B7"); // APRS12c ch. 16
        WhenDecoded();
        var beam = ThenDataIs<AprsStatusReport>().BeamHeading!.Value;
        beam.HeadingDegrees.Should().Be(110);
        beam.ErpWatts.Should().Be(490);
    }

    [Fact]
    public void Third_party_traffic_through_tcpip_came_from_the_internet()
    {
        GivenPacket("G9RXG>APxxxx,WIDE2-2:}WB4APR-14>APxxxx,TCPIP,G9RXG*::G3NRW    :Hi Ian{001"); // APRS12c ch. 17
        WhenDecoded();
        ThenDataIs<AprsThirdPartyTraffic>().CameFromInternet.Should().BeTrue();
    }

    [Fact]
    public void An_nmea_sentence_type_is_its_first_field()
    {
        GivenInformationField("$GPVTG,318.7,T,,M,35.1,N,65.0,K*69"); // APRS12c ch. 8
        WhenDecoded();
        ThenDataIs<AprsNmeaReport>().SentenceType.Should().Be("GPVTG");
    }

    [Theory]
    [InlineData(":WU2Z     :Testing", false)]
    [InlineData(":WU2Z     :Testing{003", true)]
    public void A_message_wants_an_ack_when_it_has_an_id(string info, bool requestsAck)
    {
        GivenInformationField(info); // APRS12c ch. 14
        WhenDecoded();
        ThenDataIs<AprsTextMessage>().RequestsAck.Should().Be(requestsAck);
    }

    [Theory]
    [InlineData(":BLN3     :Snow expected in Tampa RSN", AprsBulletinKind.General, '3', null)]
    [InlineData(":BLNQ     :Mt St Helen digi will be QRT this weekend", AprsBulletinKind.Announcement, 'Q', null)]
    [InlineData(":BLN4WX   :Stand by your snowplows", AprsBulletinKind.Group, '4', "WX")]
    public void A_bulletin_addressee_gives_its_kind_id_and_group(string info, AprsBulletinKind kind, char id, string? group)
    {
        GivenInformationField(info); // APRS12c ch. 14
        WhenDecoded();
        var bulletin = ThenDataIs<AprsBulletin>();
        bulletin.Kind.Should().Be(kind);
        bulletin.Id.Should().Be(id);
        bulletin.GroupName.Should().Be(group);
    }

    [Fact]
    public void An_nws_bulletin_addressee_gives_its_severity()
    {
        GivenInformationField(":NWS-WARN :092010z,THUNDER_STORM,AR_ASHLEY,{S9JbA"); // APRS12c ch. 14
        WhenDecoded();
        ThenDataIs<AprsNwsBulletin>().Severity.Should().Be("WARN");
    }

    [Fact]
    public void Telemetry_equations_scale_199_to_1034_8()
    {
        GivenInformationField(":N0QBF-11 :EQNS.0,5.2,0,0,.53,-32,3,4.39,49,-32,3,18,1,2,3"); // APRS12c ch. 13
        WhenDecoded();
        ThenDataIs<AprsTelemetryCoefficients>().Scale(1, 199).Should().Be(1034.8m);
    }

    [Theory]
    [InlineData("N1JCM-9>TRQP7T,WA1PLE-4*:`c'wl|+>/`\"4-}_%", true)]
    [InlineData("N83MZ>T2TQ5U,WA1PLE-4*:`c.l+@&'/'\"G:} KJ6TMS|!:&0'p|!w#f!|3", false)]
    public void A_mic_e_type_code_says_whether_the_radio_can_message(string packet, bool messaging)
    {
        GivenPacket(packet); // APRS12c ch. 10, UAP 2.2 and 3.5
        WhenDecoded();
        ThenDataIs<AprsMicEReport>().MessagingCapable.Should().Be(messaging);
    }
}
