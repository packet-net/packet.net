using Packet.Node.Core.Applications.Catalog;

namespace Packet.Node.Tests.Applications.Catalog;

/// <summary>
/// <see cref="DebianVersion"/> against the <c>dpkg --compare-versions</c> rules. Every ordered
/// pair below was checked against dpkg 1.23.7 (<c>dpkg --compare-versions a lt|eq|gt b</c>) when
/// the table was written.
/// </summary>
public sealed class DebianVersionTests
{
    [Theory]
    // Equal, including the spellings dpkg treats as the same version.
    [InlineData("1.0", "1.0", 0)]
    [InlineData("1.0", "1.00", 0)]              // digit runs compare numerically
    [InlineData("1.000000000000000000001", "1.1", 0)]
    [InlineData("1.0", "1.0-0", 0)]             // a missing revision equals revision 0
    [InlineData("0:1.0", "1.0", 0)]             // a missing epoch is epoch 0
    // Digits compare numerically, not as strings.
    [InlineData("1.0", "1.1", -1)]
    [InlineData("1.2", "1.10", -1)]
    [InlineData("0.2.9", "0.2.10", -1)]
    [InlineData("1.99999999999999999998", "1.99999999999999999999", -1)] // no overflow
    [InlineData("1.0", "1.0.0", -1)]            // a longer version is newer
    [InlineData("1.0", "1.0.1", -1)]
    // Tilde sorts before everything, even the end of the string.
    [InlineData("1.0~rc1", "1.0", -1)]
    [InlineData("1.0~~", "1.0~~a", -1)]
    [InlineData("1.0~~a", "1.0~", -1)]
    [InlineData("1.0~", "1.0", -1)]
    [InlineData("1~", "1", -1)]
    [InlineData("1.0~beta", "1.0~rc", -1)]
    [InlineData("1.0~rc1", "1.0~rc2", -1)]
    [InlineData("1.18.0~", "1.18.0", -1)]
    // Letters sort before non-letters; the end of the string sorts before both.
    [InlineData("1.0", "1.0a", -1)]
    [InlineData("1a", "1", 1)]
    [InlineData("1.0a", "1.0+", -1)]
    [InlineData("1.0a", "1.0.", -1)]
    [InlineData("1.0+", "1.0.", -1)]
    [InlineData("1.a", "1.1", 1)]               // a letter beats the start of a digit run
    // Revisions: compared only when epoch and upstream are equal.
    [InlineData("1.0-1", "1.0-2", -1)]
    [InlineData("1.0-9", "1.0-10", -1)]
    [InlineData("1.0-1", "1.0-1.1", -1)]
    [InlineData("1.0-1", "1.0-1~bpo1", 1)]
    [InlineData("1.0-1", "1.1-0", -1)]
    [InlineData("1.0-10", "1.1", -1)]
    [InlineData("1.0+dfsg-1", "1.0-1", 1)]
    [InlineData("1.0~dfsg-1", "1.0-1", -1)]
    // The last hyphen splits off the revision; earlier ones belong to the upstream version.
    [InlineData("1.0-beta-1", "1.0-beta-2", -1)]
    [InlineData("1.0-beta-2", "1.0-1", 1)]
    // Epochs win over everything else and compare numerically.
    [InlineData("1:0.1", "0:9.9", 1)]
    [InlineData("1:0.1", "9.9", 1)]
    [InlineData("2:1.0", "1:2.0", 1)]
    [InlineData("10:1.0", "9:1.0", 1)]
    // pdn packaging suffixes (linmail-pdn's -pdnN).
    [InlineData("6.0.25.41-pdn1", "6.0.25.41-pdn2", -1)]
    [InlineData("6.0.25.41-pdn2", "6.0.25.42-pdn1", -1)]
    [InlineData("6.0.25.41-pdn1", "6.0.25.42-pdn1", -1)]
    [InlineData("6.0.25.41-pdn9", "6.0.25.41-pdn10", -1)]
    [InlineData("6.0.25.41", "6.0.25.41-pdn1", -1)]
    public void Orders_like_dpkg(string a, string b, int expected)
    {
        DebianVersion.TryCompare(a, b, out var forward).Should().BeTrue();
        DebianVersion.TryCompare(b, a, out var backward).Should().BeTrue();

        forward.Should().Be(expected, $"{a} vs {b}");
        backward.Should().Be(-expected, $"{b} vs {a}");
        DebianVersion.IsNewer(b, a).Should().Be(expected < 0);
        DebianVersion.IsNewer(a, b).Should().Be(expected > 0);
    }

    [Theory]
    // Every version the committed catalog carries, against its neighbours.
    [InlineData("0.34.3", "0.42.0")]           // dapps
    [InlineData("0.41.1", "0.42.0")]
    [InlineData("0.42.0", "0.42.1")]
    [InlineData("0.42.0", "0.43.0")]
    [InlineData("0.42.0", "1.0.0")]
    [InlineData("0.2.0", "0.2.1")]             // bpqchat
    [InlineData("0.2.1", "0.2.2")]
    [InlineData("0.2.1", "0.10.0")]
    [InlineData("0.1.3", "0.1.4")]             // convers
    [InlineData("0.1.4", "0.1.5")]
    [InlineData("0.1.4", "0.1.10")]
    [InlineData("0.2.51", "0.2.54")]           // bbs
    [InlineData("0.2.9", "0.2.54")]
    [InlineData("0.2.54", "0.2.55")]
    [InlineData("0.2.54", "0.2.100")]
    [InlineData("6.0.25.40-pdn1", "6.0.25.41-pdn1")] // linmail
    [InlineData("6.0.25.41-pdn1", "6.0.25.41-pdn2")]
    [InlineData("6.0.25.41-pdn1", "6.0.26.0-pdn1")]
    public void Catalog_versions_sort_above_their_predecessors(string older, string newer)
    {
        DebianVersion.IsNewer(newer, older).Should().BeTrue($"{newer} should be newer than {older}");
        DebianVersion.IsNewer(older, newer).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a1.0")]              // upstream must start with a digit
    [InlineData("v1.0")]
    [InlineData("not-a-version")]
    [InlineData("garbage")]
    [InlineData("1.0 1")]             // embedded whitespace
    [InlineData(":1.0")]              // empty epoch
    [InlineData("a:1.0")]             // non-numeric epoch
    [InlineData("-1:1.0")]            // negative epoch
    [InlineData("99999999999:1.0")]   // epoch too big
    [InlineData("1:")]                // nothing after the epoch
    [InlineData("1.0-")]              // empty revision
    [InlineData("1.0-a_b")]           // bad character in the revision
    [InlineData("1.0-1:2")]           // the colon makes "1.0-1" a (non-numeric) epoch
    [InlineData("1.0_1")]             // bad character in the upstream version
    [InlineData("1.0/2")]
    public void Rejects_what_dpkg_rejects_or_warns_about(string? version)
    {
        DebianVersion.IsValid(version).Should().BeFalse();
        DebianVersion.TryCompare(version, "1.0", out var result).Should().BeFalse();
        result.Should().Be(0);
        DebianVersion.IsNewer(version, "1.0").Should().BeFalse();
        DebianVersion.IsNewer("1.0", version).Should().BeFalse();
    }

    [Theory]
    [InlineData("0.2.54")]
    [InlineData("6.0.25.41-pdn1")]
    [InlineData("1:2.3~rc1+dfsg-4.1~bpo12+1")]
    [InlineData("  1.0  ")]                   // surrounding whitespace is ignored
    public void Accepts_well_formed_versions(string version) =>
        DebianVersion.IsValid(version).Should().BeTrue();
}
