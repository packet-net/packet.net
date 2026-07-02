using System.Text;
using Packet.Core;

namespace Packet.Kiss.NinoTnc.Tests;

public class NinoTncCqBeepTests
{
    private static readonly Callsign Source = new("M0LTE", 1);

    [Fact]
    public void Arming_Frame_Is_A_Ui_Frame_Whose_Info_Starts_TARPNstat()
    {
        var frame = NinoTncCqBeep.BuildArmingFrame(Source);

        frame.IsUi.Should().BeTrue();
        frame.Source.Callsign.Should().Be(Source);
        frame.Destination.Callsign.Should().Be(new Callsign("IDENT"));
        Encoding.ASCII.GetString(frame.Info.Span).Should().StartWith("[TARPNstat");
    }

    [Fact]
    public void Arming_Frame_Accepts_A_Custom_Status_Text_With_The_Prefix()
    {
        var frame = NinoTncCqBeep.BuildArmingFrame(Source, statusText: "[TARPNstatus 1 2 3]");
        Encoding.ASCII.GetString(frame.Info.Span).Should().Be("[TARPNstatus 1 2 3]");
    }

    [Fact]
    public void Arming_Frame_Rejects_Status_Text_Without_The_Prefix()
    {
        var act = () => NinoTncCqBeep.BuildArmingFrame(Source, statusText: "TARPN status");
        act.Should().Throw<ArgumentException>().WithParameterName("statusText");
    }

    [Fact]
    public void Beep_Request_Addresses_CQBEEP_With_The_Seconds_As_Ssid()
    {
        var frame = NinoTncCqBeep.BuildBeepRequest(Source, seconds: 8);

        frame.IsUi.Should().BeTrue();
        frame.Destination.Callsign.Should().Be(new Callsign("CQBEEP", 8));
        frame.Source.Callsign.Should().Be(Source);
        frame.Info.Length.Should().Be(53, "the info carries the TX-Test '{{N ' + 50-byte pattern shape");
    }

    [Fact]
    public void Beep_Request_Info_Is_Recognised_As_An_Air_Test_Frame()
    {
        var frame = NinoTncCqBeep.BuildBeepRequest(Source, seconds: 7, sequenceCounter: 2);

        NinoTncAirTestFrame.TryRecognise(frame, out var recognised).Should().BeTrue();
        recognised!.DestinationSsid.Should().Be((byte)7);
        recognised.SequenceCounter.Should().Be(2);
        recognised.LearnedCallsign.Should().Be(Source);
        recognised.PatternAsAscii().Should().StartWith("\"#$%");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void Beep_Request_Rejects_Out_Of_Range_Seconds(int seconds)
    {
        var act = () => NinoTncCqBeep.BuildBeepRequest(Source, seconds);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(seconds));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void Beep_Request_Rejects_Out_Of_Range_Sequence_Counters(int counter)
    {
        var act = () => NinoTncCqBeep.BuildBeepRequest(Source, seconds: 5, sequenceCounter: counter);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("sequenceCounter");
    }
}
