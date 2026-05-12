using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

public class Ax25EventTests
{
    [Theory]
    [InlineData(typeof(DlConnectRequest),    "DL_CONNECT_request")]
    [InlineData(typeof(DlDisconnectRequest), "DL_DISCONNECT_request")]
    [InlineData(typeof(DlFlowOffRequest),    "DL_FLOW_OFF_request")]
    [InlineData(typeof(DlFlowOnRequest),     "DL_FLOW_ON_request")]
    [InlineData(typeof(IFramePopsOffQueue),  "I_frame_pops_off_queue")]
    [InlineData(typeof(T1Expiry),            "T1_expiry")]
    [InlineData(typeof(T2Expiry),            "T2_expiry")]
    [InlineData(typeof(T3Expiry),            "T3_expiry")]
    public void Event_Names_Match_Spec_Catalog(Type eventType, string expectedName)
    {
        var evt = (Ax25Event)Activator.CreateInstance(eventType)!;
        evt.Name.ShouldBe(expectedName);
    }

    [Fact]
    public void DlDataRequest_Carries_Its_Payload()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        var evt = new DlDataRequest(payload);

        evt.Name.ShouldBe("DL_DATA_request");
        evt.Data.ToArray().ShouldBe(payload);
    }

    [Fact]
    public void IFrameReceived_Carries_The_Frame()
    {
        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source:      new Callsign("G7XYZ", 7),
            info:        "x"u8);

        var evt = new IFrameReceived(frame);
        evt.Name.ShouldBe("I_received");
        evt.Frame.ShouldBe(frame);
    }
}
