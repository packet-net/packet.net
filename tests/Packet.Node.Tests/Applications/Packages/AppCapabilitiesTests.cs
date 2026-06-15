using Packet.Node.Core.Applications.Packages;

namespace Packet.Node.Tests.Applications.Packages;

/// <summary>
/// Capability display-normalisation (<see cref="AppCapabilities"/>): the <c>network</c> →
/// <c>packet</c> rename surfaced in the trust prompt, with <c>network</c> kept as a back-compat
/// input alias. Capabilities are free-form strings shown to the owner, not an enforced enum.
/// </summary>
public class AppCapabilitiesTests
{
    [Theory]
    [InlineData("network", "packet")]
    [InlineData("Network", "packet")]   // case-insensitive
    [InlineData("NETWORK", "packet")]
    [InlineData("packet", "packet")]    // already the new spelling — unchanged
    [InlineData("web", "web")]          // unrelated capability — unchanged
    [InlineData("session", "session")]
    public void Normalize_relabels_network_as_packet_and_leaves_others_alone(string input, string expected)
    {
        AppCapabilities.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeAll_preserves_order_and_relabels_only_network()
    {
        AppCapabilities.NormalizeAll(["session", "network", "web"])
            .Should().Equal("session", "packet", "web");
    }

    [Fact]
    public void NormalizeAll_is_total_on_null_and_empty()
    {
        AppCapabilities.NormalizeAll(null).Should().BeEmpty();
        AppCapabilities.NormalizeAll([]).Should().BeEmpty();
    }

    [Theory]
    [InlineData(new[] { "packet" }, true)]   // the new spelling
    [InlineData(new[] { "network" }, true)]  // the legacy alias still grants
    [InlineData(new[] { "Network" }, true)]  // case-insensitive
    [InlineData(new[] { "web", "session" }, false)]
    [InlineData(new string[0], false)]
    public void GrantsPacketAccess_accepts_both_spellings(string[] capabilities, bool expected)
    {
        AppCapabilities.GrantsPacketAccess(capabilities).Should().Be(expected);
    }

    [Fact]
    public void GrantsPacketAccess_is_false_for_null()
    {
        AppCapabilities.GrantsPacketAccess(null).Should().BeFalse();
    }
}
