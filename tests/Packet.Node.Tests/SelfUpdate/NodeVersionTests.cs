using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Tests.SelfUpdate;

/// <summary>
/// <see cref="NodeVersion"/> parsing + the one rule that matters for the available-version check:
/// a dev/local build (one carrying SemVer build metadata, e.g. <c>0.1.0+dev…</c>) sorts <em>above</em>
/// the release of the same base version, so it must report "up to date", never offer a downgrade
/// (<c>docs/node-self-update-design.md</c> § Channel = github).
/// </summary>
[Trait("Category", "Node")]
public sealed class NodeVersionTests
{
    [Theory]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("v0.9.0", 0, 9, 0)]
    [InlineData("node-v1.2.3", 1, 2, 3)]
    [InlineData("  0.1.0  ", 0, 1, 0)]
    [InlineData("0.1.0+dev20260614190517.abcdef", 0, 1, 0)]
    [InlineData("0.1.0+abcdef0123", 0, 1, 0)]
    [InlineData("1.0.0-rc.1", 1, 0, 0)] // pre-release suffix ignored for ordering
    public void Parses_the_tag_spellings_and_strips_metadata(string text, int maj, int min, int patch)
    {
        NodeVersion.TryParse(text, out var v).Should().BeTrue();
        v.Components.Should().Equal(maj, min, patch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("v")]
    [InlineData("1.x.0")]
    public void Rejects_unparseable_input(string? text)
    {
        NodeVersion.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void Records_build_metadata_presence()
    {
        NodeVersion.TryParse("0.1.0+dev123", out var dev).Should().BeTrue();
        dev.HasBuildMetadata.Should().BeTrue();

        NodeVersion.TryParse("0.1.0", out var rel).Should().BeTrue();
        rel.HasBuildMetadata.Should().BeFalse();
    }

    [Theory]
    [InlineData("0.9.0", "0.8.0", true)]   // newer base → update
    [InlineData("0.8.1", "0.8.0", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.8.0", "0.8.0", false)]  // same base → up to date
    [InlineData("0.7.0", "0.8.0", false)]  // older base → never a downgrade
    public void IsUpdateOver_offers_only_a_strictly_newer_base(string candidate, string running, bool expected)
    {
        NodeVersion.TryParse(candidate, out var cand).Should().BeTrue();
        NodeVersion.TryParse(running, out var run).Should().BeTrue();
        cand.IsUpdateOver(run).Should().Be(expected);
    }

    [Fact]
    public void A_release_is_NOT_an_update_over_a_dev_build_of_the_same_base()
    {
        // THE load-bearing case: a node running 0.1.0+dev… compared against the 0.1.0 release.
        // The dev build sorts ABOVE the release, so the release is not an update (no downgrade).
        NodeVersion.TryParse("0.1.0+dev20260614190517.deadbeef", out var running).Should().BeTrue();
        NodeVersion.TryParse("node-v0.1.0", out var release).Should().BeTrue();

        release.IsUpdateOver(running).Should().BeFalse(
            "a 0.1.0 release must not be offered as an update to a 0.1.0+dev build — it'd be a downgrade");
    }

    [Fact]
    public void A_genuinely_newer_release_still_updates_a_dev_build()
    {
        // The dev-above-release rule must NOT mask a real newer release.
        NodeVersion.TryParse("0.1.0+dev20260614190517.deadbeef", out var running).Should().BeTrue();
        NodeVersion.TryParse("node-v0.2.0", out var release).Should().BeTrue();

        release.IsUpdateOver(running).Should().BeTrue("0.2.0 is genuinely newer than a 0.1.0 dev build");
    }

    [Theory]
    [InlineData("1.2", "1.2.0", 0)]    // missing components treated as 0
    [InlineData("1.2.0", "1.2", 0)]
    [InlineData("1.2.1", "1.2", 1)]
    [InlineData("1.2", "1.2.1", -1)]
    public void CompareTo_pads_missing_components_with_zero(string a, string b, int sign)
    {
        NodeVersion.TryParse(a, out var va).Should().BeTrue();
        NodeVersion.TryParse(b, out var vb).Should().BeTrue();
        Math.Sign(va.CompareTo(vb)).Should().Be(sign);
    }
}
