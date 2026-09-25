using Packet.Ax25;
using Packet.Core;

namespace Packet.Aprs.Tests;

/// <summary>The examples in src/Packet.Aprs/README.md, kept honest.</summary>
public class ReadmeExamples
{
    [Fact]
    public void Decode_example()
    {
        AprsPacket packet = AprsPacket.Decode("N0CALL-9>APZ001,WIDE1-1,qAR,M0LTE-10:!5130.00N/00007.00W>088/036/A=001234Hello");
        var p = packet.Data.Should().BeOfType<AprsPositionReport>().Subject;
        p.Position.Latitude.Should().Be(51.5);
        p.Symbol.Description.Should().Be("Car");
        p.CourseDegrees.Should().Be(88);
        p.SpeedKnots.Should().Be(36);
        p.AltitudeFeet.Should().Be(1234);
        p.Comment.Should().Be("Hello");
        packet.QConstruct!.Value.Station!.Value.Value.Should().Be("M0LTE-10");
    }

    [Fact]
    public void Encode_example()
    {
        var report = new AprsPositionReport
        {
            Position = new AprsPosition(51.5, -0.1166667),
            Symbol = AprsSymbol.Parse("/>"),
            MessagingCapable = true,
            CourseDegrees = 88,
            SpeedKnots = 36,
            Comment = "Mobile",
        };

        AprsPacket packet = AprsPacket.Create("M0LTE-9", "APZ001", report, "WIDE1-1,WIDE2-1");
        packet.ToString().Should().Be("M0LTE-9>APZ001,WIDE1-1,WIDE2-1:=5130.00N/00007.00W>088/036Mobile");
        packet.ToAx25Frame().Should().NotBeEmpty();
    }

    [Fact]
    public void Options_example()
    {
        const string line = "W1TG2>APU25N:@091842z4256.20N/07049.42W_310/004g015t081r000p033P002h54b10001/ - Hampton, NH Wx";
        AprsPacket.Decode(line).Data.Should().BeOfType<AprsPositionReport>();
        AprsPacket.Decode(line, AprsParseOptions.Strict).Data.Should().BeOfType<AprsUnrecognizedData>();
        AprsPacket.Decode(line, AprsParseOptions.Lenient with { AllowWeatherComment = false }).Data.Should().BeOfType<AprsUnrecognizedData>();
    }

    [Fact]
    public void Packet_ax25_example()
    {
        Ax25Frame frame = Ax25Frame.Ui(new Callsign("APZ001"), new Callsign("N0CALL", 9), "!5130.00N/00007.00W>"u8);
        AprsPacket packet = AprsPacket.Create("M0LTE-9", "APZ001", new AprsStatusReport { Text = "hi" });

        AprsPacket aprs = AprsPacket.DecodeAx25(frame.ToBytes());
        Ax25Frame.TryParse(packet.ToAx25Frame(), out Ax25Frame? ui).Should().BeTrue();

        AprsAddress me = AprsAddress.FromCallsign(new Callsign("M0LTE", 9));
        aprs.Source.TryGetCallsign(out Callsign source).Should().BeTrue();

        aprs.Data.Should().BeOfType<AprsPositionReport>();
        ui!.Info.ToArray().Should().Equal(">hi"u8.ToArray());
        me.Value.Should().Be("M0LTE-9");
        source.Should().Be(new Callsign("N0CALL", 9));
    }
}
