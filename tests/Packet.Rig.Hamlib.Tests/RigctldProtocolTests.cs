namespace Packet.Rig.Hamlib.Tests;

/// <summary>
/// Parser tests against wire text captured from a REAL <c>rigctld</c> 4.5.5 dummy rig
/// (transcripts in <c>docs/research/rig-control-spike.md</c>) — the recorded-transcript
/// technique, so the parser meets bytes the daemon actually emits.
/// </summary>
public class RigctldProtocolTests
{
    [Theory]
    [InlineData("RPRT 0", true, 0)]
    [InlineData("RPRT -1", true, -1)]
    [InlineData("RPRT -11", true, -11)]
    [InlineData("Frequency: 14074000", false, 0)]
    [InlineData("RPRT", false, 0)]
    [InlineData("RPRTX 0", false, 0)]
    public void TryParseRprt_Recognises_Terminator_Lines(string line, bool expected, int expectedCode)
    {
        RigctldProtocol.TryParseRprt(line, out var code).Should().Be(expected);
        if (expected)
        {
            code.Should().Be(expectedCode);
        }
    }

    [Theory]
    [InlineData("0", false)]           // 4.x default protocol (captured)
    [InlineData("1", true)]
    [InlineData("CHKVFO 0", false)]    // 3.3 wire shape
    [InlineData("CHKVFO 1", true)]
    [InlineData("ChkVFO: 0", false)]   // extended-form echo (captured from 4.5.5)
    [InlineData("RPRT -1", false)]     // pre-chk_vfo server: no VFO mode to be in
    public void ParseChkVfo_Accepts_All_Known_Wire_Shapes(string line, bool expected)
        => RigctldProtocol.ParseChkVfo(line).Should().Be(expected);

    [Fact]
    public void ParseChkVfo_Rejects_Garbage()
    {
        var act = () => RigctldProtocol.ParseChkVfo("USB");
        act.Should().Throw<RigProtocolException>();
    }

    [Fact]
    public void GetField_Reads_Labelled_Payload_Lines()
    {
        string[] payload = ["Mode: USB", "Passband: 2400"];
        RigctldProtocol.GetField(payload, "Mode").Should().Be("USB");
        RigctldProtocol.GetField(payload, "Passband").Should().Be("2400");
        RigctldProtocol.GetField(payload, "Frequency").Should().BeNull();
    }

    [Fact]
    public void GetField_Does_Not_Confuse_Prefix_Keys()
    {
        // "PTT" must not match a hypothetical "PTTX: 1" line.
        string[] payload = ["PTTX: 1", "PTT: 0"];
        RigctldProtocol.GetField(payload, "PTT").Should().Be("0");
    }

    [Theory]
    [InlineData("14074000", 14_074_000)]
    [InlineData("14074000.000000", 14_074_000)] // hamlib freq_t is a double; be liberal
    [InlineData("145000000", 145_000_000)]
    public void ParseHz_Accepts_Integer_And_Double_Forms(string wire, long expected)
        => RigctldProtocol.ParseHz(wire, "get_freq").Should().Be(expected);

    [Fact]
    public void ParseHz_Rejects_Garbage()
    {
        var act = () => RigctldProtocol.ParseHz("VFOA", "get_freq");
        act.Should().Throw<RigProtocolException>();
    }

    [Theory]
    [InlineData(-1, "RIG_EINVAL (-1)")]
    [InlineData(-11, "RIG_ENAVAIL (-11)")]
    [InlineData(-20, "RIG_EPOWER (-20)")]
    [InlineData(-99, "unknown hamlib error (-99)")]
    public void DescribeError_Names_Hamlib_Codes(int rprt, string expected)
        => RigctldProtocol.DescribeError(rprt).Should().Be(expected);

    [Fact]
    public void ParseDumpCaps_Digests_Real_Dummy_Rig_Output()
    {
        // Verbatim lines (tabs included) from `+\dump_caps` against rigctld 4.5.5 -m 1.
        string[] payload =
        [
            "Caps dump for model: 1",
            "Model name:\tDummy",
            "Mfg name:\tHamlib",
            "Backend version:\t20221128.0",
            "Backend copyright:\tLGPL",
            "Rig type:\tOther ",
            "Can set Frequency:\tY",
            "Can get Frequency:\tY",
            "Can set Mode:\tY",
            "Can get Mode:\tY",
            "Can set PTT:\tY",
            "Can get PTT:\tY",
            "Can get DCD:\tY",
            "Get level: PREAMP(0..0/0) ATT(0..0/0) RFPOWER(0..0/0) METER(0..0/0) RAWSTR(0..0/0) SWR(0..0/0) ALC(0..0/0) STRENGTH(0..0/0) RFPOWER_METER(0..0/0) RFPOWER_METER_WATTS(0..0/0) SPECTRUM_REF(-30..10/0.5)",
        ];

        var (caps, info) = RigctldProtocol.ParseDumpCaps(payload);

        caps.Should().Be(
            RigCapabilities.FrequencyGet | RigCapabilities.FrequencySet |
            RigCapabilities.ModeGet | RigCapabilities.ModeSet |
            RigCapabilities.PttGet | RigCapabilities.PttSet |
            RigCapabilities.SwrMeter | RigCapabilities.RfPowerMeter | RigCapabilities.RfPowerMeterWatts);
        info.Backend.Should().Be("Hamlib rigctld");
        info.Model.Should().Be("Dummy");
        info.Manufacturer.Should().Be("Hamlib");
    }

    [Fact]
    public void ParseDumpCaps_Honours_N_Flags_And_Missing_Levels()
    {
        string[] payload =
        [
            "Model name:\tReadOnlyRig",
            "Mfg name:\tNowhere",
            "Can set Frequency:\tN",
            "Can get Frequency:\tY",
            "Can set Mode:\tN",
            "Can get Mode:\tY",
            "Can set PTT:\tN",
            "Can get PTT:\tN",
            "Get level: STRENGTH(0..0/0)",
        ];

        var (caps, _) = RigctldProtocol.ParseDumpCaps(payload);

        caps.Should().Be(RigCapabilities.FrequencyGet | RigCapabilities.ModeGet);
    }

    [Fact]
    public void ParseDumpCaps_Treats_Emulated_As_Supported()
    {
        string[] payload = ["Can get PTT:\tE"];
        var (caps, _) = RigctldProtocol.ParseDumpCaps(payload);
        caps.Should().HaveFlag(RigCapabilities.PttGet);
    }

    [Fact]
    public void ParseDumpCaps_Does_Not_Mistake_RfPowerMeter_For_Watts()
    {
        string[] payload = ["Get level: RFPOWER_METER(0..0/0)"];
        var (caps, _) = RigctldProtocol.ParseDumpCaps(payload);
        caps.Should().HaveFlag(RigCapabilities.RfPowerMeter);
        caps.Should().NotHaveFlag(RigCapabilities.RfPowerMeterWatts);
    }
}
