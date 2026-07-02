using System.Text;
using Packet.Kiss;

namespace Packet.Kiss.NinoTnc.Tests;

public class NinoTncRssiReadingTests
{
    /// <summary>
    /// A real GETRSSI reply captured on the bench (firmware 3.41,
    /// 2026-07-02): raw KISS command byte 0xE0 (port 14 / command 0
    /// through the generic decoder) with ASCII payload.
    /// </summary>
    private static readonly byte[] CapturedReplyPayload = Convert.FromHexString("525353493A2D33322E3836");

    private static KissFrame ReplyFrame(byte[] payload) => new(14, KissCommand.Data, payload);

    [Fact]
    public void Parses_The_Captured_Reply()
    {
        NinoTncRssiReading.TryParse(ReplyFrame(CapturedReplyPayload), out var parsed).Should().BeTrue();
        parsed!.LevelDb.Should().BeApproximately(-32.86f, 1e-4f);
    }

    [Fact]
    public void Rejects_A_Frame_That_Is_Not_On_The_Reply_Command_Byte()
    {
        // Same payload on a plain port-0 Data frame: an over-air AX.25 frame
        // could carry these bytes, so the command byte gate matters.
        var frame = new KissFrame(0, KissCommand.Data, CapturedReplyPayload);
        NinoTncRssiReading.TryParse(frame, out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Fact]
    public void Rejects_Wrong_Prefix_And_Unparseable_Levels()
    {
        NinoTncRssiReading.TryParse(Encoding.ASCII.GetBytes("NOISE:-32.86"), out _).Should().BeFalse();
        NinoTncRssiReading.TryParse(Encoding.ASCII.GetBytes("RSSI:banana"), out _).Should().BeFalse();
        NinoTncRssiReading.TryParse(Encoding.ASCII.GetBytes("RSSI:"), out _).Should().BeFalse();
    }

    [Fact]
    public void Positive_Levels_Parse_Too()
    {
        NinoTncRssiReading.TryParse(Encoding.ASCII.GetBytes("RSSI:3.50"), out var parsed).Should().BeTrue();
        parsed!.LevelDb.Should().BeApproximately(3.5f, 1e-4f);
    }

    [Fact]
    public void Classifier_Upgrades_A_Reply_To_The_Typed_Event()
    {
        var evt = NinoTncFrameClassifier.Classify(ReplyFrame(CapturedReplyPayload));

        evt.Should().BeOfType<NinoTncRssiReadingReceivedEvent>();
        ((NinoTncRssiReadingReceivedEvent)evt).Reading.LevelDb.Should().BeApproximately(-32.86f, 1e-4f);
    }
}
