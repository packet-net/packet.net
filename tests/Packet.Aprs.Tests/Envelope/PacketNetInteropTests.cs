using Packet.Ax25;
using Packet.Core;

namespace Packet.Aprs.Tests.Envelope;

/// <summary>
/// The seams to the rest of Packet.NET: frames pass between <see cref="Ax25Frame"/> and
/// <see cref="AprsPacket"/> as KISS-form bytes (no flags, no FCS), and addresses convert to and
/// from <see cref="Callsign"/>.
/// </summary>
public class PacketNetInteropTests
{
    [Fact]
    public void A_ui_frame_built_by_packet_ax25_decodes_as_aprs()
    {
        Ax25Frame frame = Ax25Frame.Ui(
            destination: new Callsign("APZ001"),
            source: new Callsign("M0LTE", 9),
            info: "!5130.00N/00007.00W>Mobile"u8,
            digipeaters: [new Callsign("WIDE1", 1)]);

        AprsPacket packet = AprsPacket.DecodeAx25(frame.ToBytes());

        packet.ToString().Should().Be("M0LTE-9>APZ001,WIDE1-1:!5130.00N/00007.00W>Mobile");
        packet.Data.Should().BeOfType<AprsPositionReport>();
        packet.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void An_encoded_packet_parses_as_a_strict_packet_ax25_ui_frame()
    {
        AprsPacket packet = AprsPacket.Create("M0LTE-9", "APZ001", new AprsStatusReport { Text = "Net Control" }, "WIDE1-1,WIDE2-1");

        Ax25Frame.TryParse(packet.ToAx25Frame(), Ax25ParseOptions.Strict, out Ax25Frame? frame).Should().BeTrue();

        frame!.Source.Callsign.Should().Be(new Callsign("M0LTE", 9));
        frame.Destination.Callsign.Should().Be(new Callsign("APZ001"));
        frame.Digipeaters.Select(d => d.Callsign).Should().Equal(new Callsign("WIDE1", 1), new Callsign("WIDE2", 1));
        frame.IsCommand.Should().BeTrue();
        frame.Pid.Should().Be(Ax25Frame.PidNoLayer3);
        frame.Info.ToArray().Should().Equal(packet.Information.ToArray());
        frame.ToBytes().Should().Equal(packet.ToAx25Frame());
    }

    [Fact]
    public void Callsigns_convert_both_ways()
    {
        AprsAddress.FromCallsign(new Callsign("M0LTE", 9)).Value.Should().Be("M0LTE-9");
        AprsAddress.FromCallsign(new Callsign("M0LTE")).Value.Should().Be("M0LTE");

        AprsAddress.Parse("M0LTE-9").TryGetCallsign(out Callsign callsign).Should().BeTrue();
        callsign.Should().Be(new Callsign("M0LTE", 9));
    }

    [Theory]
    [InlineData("WHO-IS")]
    [InlineData("qAC")]
    [InlineData("T2SPAIN")]
    [InlineData("m0lte")]
    [InlineData("N0CALL-0")]
    public void Aprs_is_names_are_not_callsigns(string address) =>
        AprsAddress.Parse(address).TryGetCallsign(out _).Should().BeFalse();

    [Fact]
    public void An_empty_callsign_names_no_aprs_station()
    {
        FluentActions.Invoking(() => AprsAddress.FromCallsign(new Callsign(""))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AprsAddress.FromCallsign(default)).Should().Throw<ArgumentException>();
    }
}
