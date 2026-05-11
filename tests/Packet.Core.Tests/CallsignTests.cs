using Packet.Core;

namespace Packet.Core.Tests;

public class CallsignTests
{
    [Theory]
    [InlineData("G7XYZ", 0, "G7XYZ")]
    [InlineData("G7XYZ", 7, "G7XYZ-7")]
    [InlineData("M0LTE", 1, "M0LTE-1")]
    [InlineData("K1ABC", 15, "K1ABC-15")]
    [InlineData("WB2OSZ", 0, "WB2OSZ")]
    public void Construct_And_Format(string @base, byte ssid, string expected)
    {
        var c = new Callsign(@base, ssid);
        c.ToString().ShouldBe(expected);
        c.Base.ShouldBe(@base);
        c.Ssid.ShouldBe(ssid);
    }

    [Theory]
    [InlineData("G7XYZ", "G7XYZ", 0)]
    [InlineData("G7XYZ-0", "G7XYZ", 0)]
    [InlineData("G7XYZ-7", "G7XYZ", 7)]
    [InlineData("M0LTE-15", "M0LTE", 15)]
    public void Parse_RoundTrips(string text, string expectedBase, byte expectedSsid)
    {
        var c = Callsign.Parse(text);
        c.Base.ShouldBe(expectedBase);
        c.Ssid.ShouldBe(expectedSsid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("g7xyz")]            // lowercase
    [InlineData("G7XYZ-16")]         // SSID > 15
    [InlineData("G7XYZ-A")]          // non-numeric SSID
    [InlineData("TOOLONGCALL")]      // > 6 chars
    [InlineData("G7-XY")]            // dash mid-base
    [InlineData("G7!XY")]            // non-alphanumeric
    public void TryParse_Rejects_Invalid(string? text)
    {
        Callsign.TryParse(text, out _).ShouldBeFalse();
    }

    [Fact]
    public void Constructor_Rejects_Invalid_Ssid()
    {
        Should.Throw<ArgumentException>(() => new Callsign("G7XYZ", 16));
    }

    [Fact]
    public void Constructor_Rejects_Empty_Base()
    {
        Should.Throw<ArgumentException>(() => new Callsign("", 0));
    }

    [Fact]
    public void Equality_Treats_Same_Base_And_Ssid_As_Equal()
    {
        var a = new Callsign("G7XYZ", 7);
        var b = new Callsign("G7XYZ", 7);
        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }
}
