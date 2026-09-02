using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests;

public class Ax25FrameTypeTests
{
    private static readonly Callsign Local = new("M0LTE", 0);
    private static readonly Callsign Remote = new("G7XYZ", 7);

    [Theory]
    [InlineData(0x00, Ax25FrameType.I)]
    [InlineData(0xFE, Ax25FrameType.I)]
    [InlineData(0x01, Ax25FrameType.Rr)]
    [InlineData(0xF1, Ax25FrameType.Rr)]
    [InlineData(0x05, Ax25FrameType.Rnr)]
    [InlineData(0x09, Ax25FrameType.Rej)]
    [InlineData(0x0D, Ax25FrameType.Srej)]
    [InlineData(0x03, Ax25FrameType.Ui)]
    [InlineData(0x13, Ax25FrameType.Ui)]
    [InlineData(0x2F, Ax25FrameType.Sabm)]
    [InlineData(0x3F, Ax25FrameType.Sabm)]
    [InlineData(0x6F, Ax25FrameType.Sabme)]
    [InlineData(0x43, Ax25FrameType.Disc)]
    [InlineData(0x63, Ax25FrameType.Ua)]
    [InlineData(0x0F, Ax25FrameType.Dm)]
    [InlineData(0x87, Ax25FrameType.Frmr)]
    [InlineData(0xAF, Ax25FrameType.Xid)]
    [InlineData(0xE3, Ax25FrameType.Test)]
    [InlineData(0x07, Ax25FrameType.Unknown)]
    [InlineData(0xFF, Ax25FrameType.Unknown)]
    public void FrameTypeOf_Reads_The_Control_Octet(byte control, Ax25FrameType expected)
    {
        Ax25Frame.FrameTypeOf(control).Should().Be(expected);
    }

    [Fact]
    public void FrameTypeOf_Ignores_The_Pf_Bit_For_Every_Octet()
    {
        for (var c = 0; c < 256; c++)
        {
            Ax25Frame.FrameTypeOf((byte)c).Should().Be(Ax25Frame.FrameTypeOf((byte)(c ^ 0x10)), $"control 0x{c:X2}");
        }
    }

    [Fact]
    public void Classifier_Agrees_With_FrameTypeOf_For_Every_Control_Octet()
    {
        var header = Ax25Frame.Ua(Local, Remote).ToBytes()[..14];
        for (var c = 0; c < 256; c++)
        {
            // I and UI frames need a PID to parse; nothing else may carry one.
            var needsPid = (c & 0x01) == 0 || (c & 0xEF) == 0x03;
            var bytes = needsPid ? header.Append((byte)c).Append((byte)0xF0).ToArray() : header.Append((byte)c).ToArray();
            Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue($"control 0x{c:X2}");
            var expected = frame!.FrameType switch
            {
                Ax25FrameType.I => typeof(IFrameReceived),
                Ax25FrameType.Rr => typeof(RrReceived),
                Ax25FrameType.Rnr => typeof(RnrReceived),
                Ax25FrameType.Rej => typeof(RejReceived),
                Ax25FrameType.Srej => typeof(SrejReceived),
                Ax25FrameType.Ui => typeof(UiReceived),
                Ax25FrameType.Sabm => typeof(SabmReceived),
                Ax25FrameType.Sabme => typeof(SabmeReceived),
                Ax25FrameType.Disc => typeof(DiscReceived),
                Ax25FrameType.Ua => typeof(UaReceived),
                Ax25FrameType.Dm => typeof(DmReceived),
                Ax25FrameType.Frmr => typeof(FrmrReceived),
                Ax25FrameType.Xid => typeof(XidReceived),
                Ax25FrameType.Test => typeof(TestReceived),
                _ => typeof(ControlFieldError),
            };
            Ax25FrameClassifier.Classify(frame).Should().BeOfType(expected, $"control 0x{c:X2}");
        }
    }

    [Fact]
    public void Every_Type_Has_A_Distinct_Mnemonic()
    {
        var mnemonics = Enum.GetValues<Ax25FrameType>().Select(t => t.Mnemonic()).ToList();
        mnemonics.Should().OnlyHaveUniqueItems();
        Ax25FrameType.Sabme.Mnemonic().Should().Be("SABME");
        Ax25FrameType.Unknown.Mnemonic().Should().Be("U");
    }

    [Fact]
    public void Type_Queries_Partition_The_Enum()
    {
        foreach (var type in Enum.GetValues<Ax25FrameType>())
        {
            var buckets = new[] { type.IsInformation(), type.IsSupervisory(), type.IsUnnumbered() }.Count(b => b);
            buckets.Should().Be(1, $"{type} must be exactly one of I, S, U");
            type.CarriesNr().Should().Be(type.IsInformation() || type.IsSupervisory());
        }
    }

    [Fact]
    public void Extended_Frames_Classify_Without_Knowing_The_Modulo()
    {
        var i = Ax25Frame.I(Local, Remote, nr: 100, ns: 77, "x"u8, extended: true);
        var rr = Ax25Frame.Rr(Local, Remote, nr: 127, isCommand: true, pollFinal: true, extended: true);
        i.FrameType.Should().Be(Ax25FrameType.I);
        rr.FrameType.Should().Be(Ax25FrameType.Rr);
    }
}
