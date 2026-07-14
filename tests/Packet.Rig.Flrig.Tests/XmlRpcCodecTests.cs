using Packet.Rig;

namespace Packet.Rig.Flrig.Tests;

public class XmlRpcCodecTests
{
    [Fact]
    public void BuildCall_Serialises_Typed_Arguments()
    {
        var xml = XmlRpcCodec.BuildCall("main.set_frequency", 14074000.0);
        xml.Should().Contain("<methodName>main.set_frequency</methodName>");
        xml.Should().Contain("<double>14074000</double>");

        XmlRpcCodec.BuildCall("rig.set_ptt", 1).Should().Contain("<i4>1</i4>");
        XmlRpcCodec.BuildCall("rig.set_mode", "DATA-U").Should().Contain("<string>DATA-U</string>");
    }

    [Fact]
    public void BuildCall_Escapes_Xml_In_String_Arguments()
    {
        var xml = XmlRpcCodec.BuildCall("rig.cat_string", "<FA&;>");
        xml.Should().Contain("<string>&lt;FA&amp;;&gt;</string>");
    }

    [Fact]
    public void ParseResponse_Reads_Untyped_Values_As_Strings()
    {
        const string xml = "<?xml version=\"1.0\"?><methodResponse><params><param><value>14074000</value></param></params></methodResponse>";
        XmlRpcCodec.ParseResponse(xml, "rig.get_vfo").Should().Be("14074000");
    }

    [Fact]
    public void ParseResponse_Reads_Typed_Scalars()
    {
        const string xml = "<?xml version=\"1.0\"?><methodResponse><params><param><value><i4>1</i4></value></param></params></methodResponse>";
        XmlRpcCodec.ParseResponse(xml, "rig.get_ptt").Should().Be("1");
    }

    [Fact]
    public void ParseResponse_Flattens_Arrays_To_Lines()
    {
        const string xml = "<?xml version=\"1.0\"?><methodResponse><params><param><value><array><data>" +
            "<value><string>USB</string></value><value><string>LSB</string></value>" +
            "</data></array></value></param></params></methodResponse>";
        XmlRpcCodec.ParseResponse(xml, "rig.get_modes").Should().Be("USB\nLSB");
    }

    [Fact]
    public void ParseResponse_Handles_Void_Replies()
    {
        const string xml = "<?xml version=\"1.0\"?><methodResponse><params/></methodResponse>";
        XmlRpcCodec.ParseResponse(xml, "rig.set_ptt").Should().Be("");
    }

    [Fact]
    public void ParseResponse_Throws_Typed_Fault()
    {
        const string xml = "<?xml version=\"1.0\"?><methodResponse><fault><value><struct>" +
            "<member><name>faultCode</name><value><int>-32601</int></value></member>" +
            "<member><name>faultString</name><value><string>server error. method not found</string></value></member>" +
            "</struct></value></fault></methodResponse>";

        var act = () => XmlRpcCodec.ParseResponse(xml, "rig.get_SWR");
        act.Should().Throw<RigCommandException>()
            .Which.BackendErrorCode.Should().Be(-32601);
    }

    [Fact]
    public void ParseResponse_Rejects_Non_Xml()
    {
        var act = () => XmlRpcCodec.ParseResponse("HTTP garbage", "rig.get_vfo");
        act.Should().Throw<RigProtocolException>();
    }
}
