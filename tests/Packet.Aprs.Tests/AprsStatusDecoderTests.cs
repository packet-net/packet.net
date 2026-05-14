using Packet.Aprs;

namespace Packet.Aprs.Tests;

public class AprsStatusDecoderTests
{
    [Fact]
    public void Decodes_Status_Without_Timestamp()
    {
        // APRS101 §16 example.
        var info = System.Text.Encoding.ASCII.GetBytes(">Net Control Center");
        AprsStatusDecoder.TryDecode(info, out var s).Should().BeTrue();
        s.Timestamp.Should().BeNull();
        s.Text.Should().Be("Net Control Center");
    }

    [Fact]
    public void Decodes_Status_With_DHM_Zulu_Timestamp()
    {
        // APRS101 §16 example.
        var info = System.Text.Encoding.ASCII.GetBytes(">092345zNet Control Center");
        AprsStatusDecoder.TryDecode(info, out var s).Should().BeTrue();
        s.Timestamp.Should().Be("092345z");
        s.Text.Should().Be("Net Control Center");
    }

    [Theory]
    // Real corpus samples.
    [InlineData(">Powered by WPSD (https://wpsd.radio)", null, "Powered by WPSD (https://wpsd.radio)")]
    [InlineData(">QRV", null, "QRV")]
    [InlineData(">", null, "")]
    [InlineData(">092345zStatus", "092345z", "Status")]
    public void Theory_Cases(string infoText, string? expectedTs, string expectedText)
    {
        var info = System.Text.Encoding.ASCII.GetBytes(infoText);
        AprsStatusDecoder.TryDecode(info, out var s).Should().BeTrue();
        s.Timestamp.Should().Be(expectedTs);
        s.Text.Should().Be(expectedText);
    }

    [Fact]
    public void DHM_Local_Is_NOT_Treated_As_Timestamp()
    {
        // Spec §16 restricts status timestamps to DHM-zulu. A trailing '/'
        // means "no timestamp, the text starts with a slash".
        var info = System.Text.Encoding.ASCII.GetBytes(">092345/StatusText");
        AprsStatusDecoder.TryDecode(info, out var s).Should().BeTrue();
        s.Timestamp.Should().BeNull();
        s.Text.Should().Be("092345/StatusText");
    }

    [Fact]
    public void HMS_Is_NOT_Treated_As_Timestamp()
    {
        var info = System.Text.Encoding.ASCII.GetBytes(">123045hStatusText");
        AprsStatusDecoder.TryDecode(info, out var s).Should().BeTrue();
        s.Timestamp.Should().BeNull();
        s.Text.Should().Be("123045hStatusText");
    }

    [Fact]
    public void Strips_DTI_If_Present()
    {
        var with    = System.Text.Encoding.ASCII.GetBytes(">Status text");
        var without = System.Text.Encoding.ASCII.GetBytes("Status text");
        AprsStatusDecoder.TryDecode(with,    out var a).Should().BeTrue();
        AprsStatusDecoder.TryDecode(without, out var b).Should().BeTrue();
        a.Should().Be(b);
    }

    [Fact]
    public void Empty_Info_Is_Rejected()
    {
        AprsStatusDecoder.TryDecode(System.ReadOnlySpan<byte>.Empty, out _).Should().BeFalse();
    }

    [Fact]
    public void Trims_Trailing_CRLF_And_Space()
    {
        var info = System.Text.Encoding.ASCII.GetBytes(">Some status text  \r\n");
        AprsStatusDecoder.TryDecode(info, out var s).Should().BeTrue();
        s.Text.Should().Be("Some status text");
    }
}
