using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

public class Ax25SessionContextTests
{
    private static Ax25SessionContext NewContext() => new()
    {
        Local  = new Callsign("M0LTE", 0),
        Remote = new Callsign("G7XYZ", 7),
    };

    [Fact]
    public void Fresh_Context_Has_Spec_Defaults()
    {
        var ctx = NewContext();

        ctx.VS.ShouldBe((byte)0);
        ctx.VA.ShouldBe((byte)0);
        ctx.VR.ShouldBe((byte)0);
        ctx.RC.ShouldBe(0);

        ctx.OwnReceiverBusy.ShouldBeFalse();
        ctx.PeerReceiverBusy.ShouldBeFalse();
        ctx.AcknowledgePending.ShouldBeFalse();
        ctx.RejectException.ShouldBeFalse();
        ctx.SelectiveRejectException.ShouldBeFalse();
        ctx.Layer3Initiated.ShouldBeFalse();

        // XID defaults per §6.3.2 / §6.7.2.
        ctx.N1.ShouldBe(256);
        ctx.N2.ShouldBe(10);
        ctx.K.ShouldBe(4);   // mod-8 default; mod-128 will bump to 32
        ctx.IsExtended.ShouldBeFalse();
        ctx.SrejEnabled.ShouldBeFalse();

        ctx.IFrameQueue.Count.ShouldBe(0);
        ctx.SentIFrames.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(false, 8)]    // mod-8
    [InlineData(true, 128)]   // mod-128 (SABME-negotiated)
    public void Modulus_Reflects_Extended_Mode(bool isExtended, byte expectedModulus)
    {
        var ctx = NewContext();
        ctx.IsExtended = isExtended;
        ctx.Modulus.ShouldBe(expectedModulus);
    }

    [Fact]
    public void IncrementSeq_Wraps_At_Modulus_Mod8()
    {
        var ctx = NewContext(); // mod-8 by default
        ctx.IncrementSeq(0).ShouldBe((byte)1);
        ctx.IncrementSeq(7).ShouldBe((byte)0);
    }

    [Fact]
    public void IncrementSeq_Wraps_At_Modulus_Mod128()
    {
        var ctx = NewContext();
        ctx.IsExtended = true;
        ctx.IncrementSeq(126).ShouldBe((byte)127);
        ctx.IncrementSeq(127).ShouldBe((byte)0);
    }

    [Fact]
    public void ResetState_Returns_All_Mutable_Fields_To_Defaults()
    {
        var ctx = NewContext();
        ctx.VS = 5;
        ctx.VA = 4;
        ctx.VR = 3;
        ctx.RC = 7;
        ctx.OwnReceiverBusy = true;
        ctx.AcknowledgePending = true;
        ctx.IFrameQueue.Enqueue(new byte[] { 0xAA });

        ctx.ResetState();

        ctx.VS.ShouldBe((byte)0);
        ctx.VA.ShouldBe((byte)0);
        ctx.VR.ShouldBe((byte)0);
        ctx.RC.ShouldBe(0);
        ctx.OwnReceiverBusy.ShouldBeFalse();
        ctx.AcknowledgePending.ShouldBeFalse();
        ctx.IFrameQueue.Count.ShouldBe(0);

        // Negotiated link parameters survive a state reset — they're set by XID,
        // not by the connection lifecycle.
        ctx.N1.ShouldBe(256);
        ctx.N2.ShouldBe(10);
    }
}
