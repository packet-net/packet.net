using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Tests for <see cref="FrameSpecExtensions"/> - the bridge between
/// dispatcher-emitted frame specs and <see cref="Ax25Frame"/> instances
/// ready for wire serialisation.
/// </summary>
public class FrameSpecExtensionsTests
{
    private static Ax25SessionContext NewContext(IEnumerable<Callsign>? digipeaters = null) => new()
    {
        Local = new Callsign("M0LTE", 0),
        Remote = new Callsign("G7XYZ", 7),
        Digipeaters = digipeaters?.ToList() ?? new List<Callsign>(),
    };

    [Fact]
    public void SupervisoryFrameSpec_To_Ax25Frame_Uses_Session_Addressing_And_Correct_Control()
    {
        var ctx = NewContext();
        var spec = new SupervisoryFrameSpec(SupervisoryFrameType.Rr, IsCommand: false, Nr: 5, PfBit: true);

        var frame = spec.ToAx25Frame(ctx);

        frame.Destination.Callsign.Should().Be(ctx.Remote);
        frame.Source.Callsign.Should().Be(ctx.Local);
        // RR with N(R)=5, F=1 → 5<<5 | 1<<4 | 0x01 = 0xB1
        frame.Control.Should().Be(0xB1);
        frame.IsCommand.Should().BeFalse();
    }

    [Theory]
    [InlineData(UFrameType.Sabm, true, true, 0x3F)]  // SABM command P=1
    [InlineData(UFrameType.Sabme, true, false, 0x6F)]  // SABME command P=0
    [InlineData(UFrameType.Disc, true, true, 0x53)]  // DISC command P=1
    [InlineData(UFrameType.Ua, false, true, 0x73)]  // UA response F=1
    [InlineData(UFrameType.Dm, false, false, 0x0F)]  // DM response F=0
    [InlineData(UFrameType.Dm, false, true, 0x1F)]  // DM response F=1
    public void UFrameSpec_To_Ax25Frame_Picks_Right_Control_Byte(
        UFrameType type, bool isCommand, bool pfBit, byte expectedControl)
    {
        var ctx = NewContext();
        var spec = new UFrameSpec(type, isCommand, pfBit);

        var frame = spec.ToAx25Frame(ctx);

        frame.Control.Should().Be(expectedControl);
        frame.IsCommand.Should().Be(isCommand);
    }

    [Fact]
    public void UiFrameSpec_To_Ax25Frame_Carries_Info_And_Pid()
    {
        var ctx = NewContext();
        var payload = "abc"u8.ToArray();
        var spec = new UiFrameSpec(IsCommand: true, PfBit: true, Info: payload, Pid: 0xCF);

        var frame = spec.ToAx25Frame(ctx);

        frame.Control.Should().Be(Ax25Frame.ControlUiPollFinal);
        frame.Pid.Should().Be((byte)0xCF);
        frame.Info.ToArray().Should().Equal(payload);
        frame.IsCommand.Should().BeTrue();
    }

    [Fact]
    public void IFrameSpec_To_Ax25Frame_Composes_Nr_Ns_PBit_Into_Control()
    {
        var ctx = NewContext();
        var payload = "data"u8.ToArray();
        var spec = new IFrameSpec(IsCommand: true, PBit: true, Nr: 3, Ns: 2, Info: payload, Pid: 0xF0);

        var frame = spec.ToAx25Frame(ctx);

        // (3<<5) | (1<<4) | (2<<1) | 0 = 0x60 | 0x10 | 0x04 = 0x74
        frame.Control.Should().Be(0x74);
        frame.Pid.Should().Be(Ax25Frame.PidNoLayer3);
        frame.Info.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void Frame_Spec_Conversion_Passes_Digipeaters_Through()
    {
        var ctx = NewContext(new[] { new Callsign("RELAY1", 0), new Callsign("RELAY2", 0) });
        var spec = new UFrameSpec(UFrameType.Sabm, IsCommand: true, PfBit: true);

        var frame = spec.ToAx25Frame(ctx);

        frame.Digipeaters.Should().HaveCount(2);
        frame.Digipeaters[0].Callsign.Base.Should().Be("RELAY1");
        frame.Digipeaters[1].Callsign.Base.Should().Be("RELAY2");
    }

    [Fact]
    public void Frame_Spec_Bytes_Are_Wire_Serialisable_End_To_End()
    {
        // The whole point: a dispatcher emission can become bytes ready to
        // hand to KISS or AXUDP.
        var ctx = NewContext();
        var spec = new UFrameSpec(UFrameType.Sabm, IsCommand: true, PfBit: true);

        var bytes = spec.ToAx25Frame(ctx).ToBytesWithFcs();

        bytes.Length.Should().Be(17);  // 14 addr + 1 control + 2 FCS
        bytes[14].Should().Be(0x3F, "SABM P=1");
    }
}
