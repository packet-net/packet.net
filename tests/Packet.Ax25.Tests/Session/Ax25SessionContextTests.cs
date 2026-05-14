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

        ctx.VS.Should().Be((byte)0);
        ctx.VA.Should().Be((byte)0);
        ctx.VR.Should().Be((byte)0);
        ctx.RC.Should().Be(0);

        ctx.OwnReceiverBusy.Should().BeFalse();
        ctx.PeerReceiverBusy.Should().BeFalse();
        ctx.AcknowledgePending.Should().BeFalse();
        ctx.RejectException.Should().BeFalse();
        ctx.SelectiveRejectException.Should().BeFalse();
        ctx.Layer3Initiated.Should().BeFalse();

        // XID defaults per §6.3.2 / §6.7.2.
        ctx.N1.Should().Be(256);
        ctx.N2.Should().Be(10);
        ctx.K.Should().Be(4);   // mod-8 default; mod-128 will bump to 32
        ctx.IsExtended.Should().BeFalse();
        ctx.SrejEnabled.Should().BeFalse();

        ctx.IFrameQueue.Count.Should().Be(0);
        ctx.SentIFrames.Count.Should().Be(0);
    }

    [Theory]
    [InlineData(false, 8)]    // mod-8
    [InlineData(true, 128)]   // mod-128 (SABME-negotiated)
    public void Modulus_Reflects_Extended_Mode(bool isExtended, byte expectedModulus)
    {
        var ctx = NewContext();
        ctx.IsExtended = isExtended;
        ctx.Modulus.Should().Be(expectedModulus);
    }

    [Fact]
    public void IncrementSeq_Wraps_At_Modulus_Mod8()
    {
        var ctx = NewContext(); // mod-8 by default
        ctx.IncrementSeq(0).Should().Be((byte)1);
        ctx.IncrementSeq(7).Should().Be((byte)0);
    }

    [Fact]
    public void IncrementSeq_Wraps_At_Modulus_Mod128()
    {
        var ctx = NewContext();
        ctx.IsExtended = true;
        ctx.IncrementSeq(126).Should().Be((byte)127);
        ctx.IncrementSeq(127).Should().Be((byte)0);
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
        ctx.IFrameQueue.Enqueue((new byte[] { 0xAA }, Ax25Frame.PidNoLayer3));

        ctx.ResetState();

        ctx.VS.Should().Be((byte)0);
        ctx.VA.Should().Be((byte)0);
        ctx.VR.Should().Be((byte)0);
        ctx.RC.Should().Be(0);
        ctx.OwnReceiverBusy.Should().BeFalse();
        ctx.AcknowledgePending.Should().BeFalse();
        ctx.IFrameQueue.Count.Should().Be(0);

        // Negotiated link parameters survive a state reset — they're set by XID,
        // not by the connection lifecycle.
        ctx.N1.Should().Be(256);
        ctx.N2.Should().Be(10);
    }
}
