namespace Packet.Aprs.Tests.Encoding;

/// <summary>
/// Building whole packets through the C# API. What the encoder writes for given data, and what it
/// refuses, is in the language-neutral vectors (<c>spec/aprs/cases/encode.json</c>).
/// </summary>
public class EncodingTests : AprsSpec
{
    private static readonly AprsPosition Somewhere = new(49 + (3.50 / 60), -(72 + (1.75 / 60)));

    [Fact]
    public void Creating_a_packet_needs_mic_e_to_go_through_create_mic_e()
    {
        var mic = new AprsMicEReport { Position = Somewhere, Symbol = AprsSymbol.Parse("/>") };
        FluentActions.Invoking(() => AprsPacket.Create("N0CALL", "APZ001", mic)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_builds_the_whole_packet()
    {
        var status = new AprsStatusReport { Text = "Net Control" };
        AprsPacket packet = AprsPacket.Create("M0LTE-9", "APZ001", status, "WIDE1-1,WIDE2-1");
        packet.ToString().Should().Be("M0LTE-9>APZ001,WIDE1-1,WIDE2-1:>Net Control");
        packet.Diagnostics.Should().BeEmpty();
        AprsPacket.DecodeAx25(packet.ToAx25Frame()).Should().Be(packet);
    }
}
