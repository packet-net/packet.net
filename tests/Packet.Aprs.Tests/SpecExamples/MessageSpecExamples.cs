namespace Packet.Aprs.Tests.SpecExamples;

/// <summary>Messages, bulletins and queries: APRS12c §14 and §15.</summary>
public class MessageSpecExamples : AprsSpec
{
    [Fact]
    public void Section14_message_without_id_expects_no_ack()
    {
        GivenInformationField(":WU2Z     :Testing");
        WhenDecoded();
        var msg = ThenDataIs<AprsTextMessage>();
        msg.Addressee.Should().Be("WU2Z");
        msg.Text.Should().Be("Testing");
        msg.MessageId.Should().BeNull();
        msg.RequestsAck.Should().BeFalse();
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section14_message_with_id()
    {
        GivenInformationField(":WU2Z     :Testing{003");
        WhenDecoded();
        var msg = ThenDataIs<AprsTextMessage>();
        msg.MessageId.Should().Be("003");
        msg.RequestsAck.Should().BeTrue();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section14_email_message()
    {
        GivenInformationField(":EMAIL    :msproul@ap.org Test email");
        WhenDecoded();
        ThenDataIs<AprsTextMessage>().Text.Should().Be("msproul@ap.org Test email");
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData(":KB2ICI-14:ack003", true)]
    [InlineData(":KB2ICI-14:rej003", false)]
    public void Section14_ack_and_reject(string info, bool ack)
    {
        GivenInformationField(info);
        WhenDecoded();
        if (ack)
        {
            ThenDataIs<AprsMessageAck>().AcknowledgedId.Should().Be("003");
        }
        else
        {
            ThenDataIs<AprsMessageReject>().RejectedId.Should().Be("003");
        }

        ((AprsMessage)Packet.Data).Addressee.Should().Be("KB2ICI-14");
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section14_reply_ack_message_carries_an_ack_for_the_other_station()
    {
        GivenInformationField(":WU2Z     :Hello again{12}34");
        WhenDecoded();
        var msg = ThenDataIs<AprsTextMessage>();
        msg.MessageId.Should().Be("12");
        msg.ReplyAck.Should().Be("34");
        msg.Text.Should().Be("Hello again");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section14_reply_ack_capable_with_nothing_to_ack()
    {
        GivenInformationField(":WU2Z     :Hello{12}");
        WhenDecoded();
        ThenDataIs<AprsTextMessage>().ReplyAck.Should().Be("");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section14_ack_of_a_reply_ack_id()
    {
        GivenInformationField(":WU2Z     :ack12}34");
        WhenDecoded();
        var ack = ThenDataIs<AprsMessageAck>();
        ack.AcknowledgedId.Should().Be("12");
        ack.ReplyAck.Should().Be("34");
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData(":BLN3     :Snow expected in Tampa RSN", AprsBulletinKind.General, '3', null)]
    [InlineData(":BLNQ     :Mt St Helen digi will be QRT this weekend", AprsBulletinKind.Announcement, 'Q', null)]
    [InlineData(":BLN4WX   :Stand by your snowplows", AprsBulletinKind.Group, '4', "WX")]
    public void Section14_bulletins_and_announcements(string info, AprsBulletinKind kind, char id, string? group)
    {
        GivenInformationField(info);
        WhenDecoded();
        var bln = ThenDataIs<AprsBulletin>();
        bln.Kind.Should().Be(kind);
        bln.Id.Should().Be(id);
        bln.GroupName.Should().Be(group);
        ThenNoDiagnostics();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section14_nws_bulletin_id_is_for_reference_only()
    {
        GivenInformationField(":NWS-WARN :092010z,THUNDER_STORM,AR_ASHLEY,{S9JbA");
        WhenDecoded();
        var nws = ThenDataIs<AprsNwsBulletin>();
        nws.Severity.Should().Be("WARN");
        nws.Text.Should().Be("092010z,THUNDER_STORM,AR_ASHLEY,");
        nws.MessageId.Should().Be("S9JbA");
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section14_nts_radiogram_lines_are_ordinary_message_text()
    {
        GivenInformationField(":W1AW     :N1\\line 1 of NTS message text{12");
        WhenDecoded();
        ThenDataIs<AprsTextMessage>().Text.Should().Be("N1\\line 1 of NTS message text");
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData("?APRS?", "APRS")]
    [InlineData("?IGATE?", "IGATE")]
    [InlineData("?WX?", "WX")]
    public void Section15_general_queries(string info, string type)
    {
        GivenInformationField(info);
        WhenDecoded();
        var q = ThenDataIs<AprsGeneralQuery>();
        q.QueryType.Should().Be(type);
        q.Footprint.Should().BeNull();
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section15_general_query_with_footprint_uses_a_space_for_positive()
    {
        GivenInformationField("?APRS? 34.02,-117.15,0200");
        WhenDecoded();
        ThenDataIs<AprsGeneralQuery>().Footprint.Should().Be(new AprsQueryFootprint(34.02m, -117.15m, 200));
        ThenReEncodesExactly();
    }

    [Theory]
    [InlineData(":KH2Z     :?APRSD", "APRSD", null)]
    [InlineData(":KH2Z     :?APRSHN0QBF", "APRSH", "N0QBF")]
    [InlineData(":KH2Z     :?APRSM", "APRSM", null)]
    [InlineData(":KH2Z     :?APRSO", "APRSO", null)]
    [InlineData(":KH2Z     :?APRSP", "APRSP", null)]
    [InlineData(":KH2Z     :?APRSS", "APRSS", null)]
    [InlineData(":KH2Z     :?APRST", "APRST", null)]
    [InlineData(":KH2Z     :?PING?", "PING?", null)]
    public void Section15_directed_queries(string info, string type, string? target)
    {
        GivenInformationField(info);
        WhenDecoded();
        var q = ThenDataIs<AprsDirectedQuery>();
        q.Addressee.Should().Be("KH2Z");
        q.QueryType.Should().Be(type);
        q.Target.Should().Be(target);
        ThenReEncodesExactly();
    }

    [Fact]
    public void Section15_igate_capabilities()
    {
        GivenInformationField("<IGATE,MSG_CNT=43,LOC_CNT=14");
        WhenDecoded();
        ThenDataIs<AprsStationCapabilities>().Capabilities.Should().Equal(
            new AprsCapability("IGATE", null),
            new AprsCapability("MSG_CNT", "43"),
            new AprsCapability("LOC_CNT", "14"));
        ThenReEncodesExactly();
    }
}
