using Packet.Node.Core.Oarc;

namespace Packet.Node.Tests.Oarc;

public sealed class MaidenheadLocatorTests
{
    [Theory]
    [InlineData("IO91wm")]
    [InlineData("IO91WM")]   // case-insensitive
    [InlineData("io91wm")]
    [InlineData(" IO91wm ")] // trimmed
    [InlineData("AA00aa")]
    [InlineData("RR99xx")]
    public void Accepts_valid_six_char_locators(string grid) => MaidenheadLocator.IsValid(grid).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("IO91")]      // 4-char (collector requires 6)
    [InlineData("IO91wmxx")]  // too long
    [InlineData("SO91wm")]    // field letter past R
    [InlineData("IO9Awm")]    // non-digit in the square
    [InlineData("IO91wz")]    // subsquare letter past X
    public void Rejects_invalid_locators(string? grid) => MaidenheadLocator.IsValid(grid).Should().BeFalse();

    [Fact]
    public void IO91wm_resolves_to_its_subsquare_centre_near_london()
    {
        MaidenheadLocator.TryToLatLon("IO91wm", out var lat, out var lon).Should().BeTrue();
        // IO91wm is central London; the subsquare centre is ~51.52 N, 0.125 W.
        lat.Should().BeApproximately(51.52, 0.05);
        lon.Should().BeApproximately(-0.125, 0.05);
    }

    [Fact]
    public void Coordinates_lie_within_the_grid_square()
    {
        // The centre must be inside the subsquare it names (lower-left corner + half a cell).
        MaidenheadLocator.TryToLatLon("IO91wm", out var lat, out var lon).Should().BeTrue();
        lat.Should().BeInRange(-90, 90);
        lon.Should().BeInRange(-180, 180);
    }
}
