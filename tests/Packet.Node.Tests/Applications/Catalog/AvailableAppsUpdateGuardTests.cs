using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Api;
using Packet.Node.Core.Applications.Catalog;
using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Tests.Applications.Catalog;

/// <summary>
/// The "Available apps" backwards-update guard (<see cref="PdnAvailableAppsApi"/> <c>IsUpgrade</c>):
/// an update is offered ONLY when the catalog pin sorts strictly above what's installed under
/// Debian version ordering (<see cref="DebianVersion"/>). Guards against a backwards "update vX → vY" when the catalog version is OLDER than the
/// installed one (an out-of-band install left the box ahead, or the catalog was rolled back), and
/// against the old naive Ordinal string compare under which "0.2.9" sorted after "0.2.10".
/// </summary>
public sealed class AvailableAppsUpdateGuardTests
{
    [Theory]
    [InlineData("0.2.9", "0.2.10", true)]   // catalog strictly newer → an update
    [InlineData("0.2.0", "0.3.0", true)]    // minor bump
    [InlineData("0.2.9", "0.2.9", false)]   // equal → up to date, not an update
    [InlineData("0.2.10", "0.2.9", false)]  // catalog OLDER → NOT a (backwards) update - the bug
    [InlineData("0.3.0", "0.2.9", false)]   // catalog older across minor → not an update
    public void IsUpgrade_OffersAnUpdateOnlyWhenTheCatalogIsStrictlyNewer(
        string installed, string catalog, bool expected) =>
        PdnAvailableAppsApi.IsUpgrade(installed, catalog).Should().Be(expected);

    [Fact]
    public void IsUpgrade_OrdersNumerically_NotAsStrings()
    {
        // "0.2.10" > "0.2.9" numerically, though Ordinal string compare puts "0.2.10" first.
        PdnAvailableAppsApi.IsUpgrade("0.2.9", "0.2.10").Should().BeTrue();
        PdnAvailableAppsApi.IsUpgrade("0.2.10", "0.2.9").Should().BeFalse();
    }

    [Theory]
    [InlineData("0.2.9", null)]             // no catalog version
    [InlineData("0.2.9", "")]               // empty
    [InlineData("0.2.9", "not-a-version")]  // unparseable catalog
    [InlineData("garbage", "0.2.10")]       // unparseable installed
    public void IsUpgrade_IsConservative_WhenEitherSideIsUnparseable(string installed, string? catalog) =>
        PdnAvailableAppsApi.IsUpgrade(installed, catalog).Should().BeFalse();

    [Theory]
    [InlineData("6.0.25.41-pdn1", "6.0.25.41-pdn2", true)]  // the suffix counts now
    [InlineData("6.0.25.41-pdn2", "6.0.25.41-pdn1", false)]
    [InlineData("6.0.25.41-pdn2", "6.0.25.42-pdn1", true)]
    [InlineData("6.0.25.41-pdn1", "6.0.25.41-pdn1", false)]
    [InlineData("1.0~rc1", "1.0", true)]                    // a pre-release is below its release
    [InlineData("1.0", "1.0~rc1", false)]
    [InlineData("1:0.1", "0.9", false)]                     // a higher epoch installed wins
    public void IsUpgrade_UsesDebianOrdering(string installed, string catalog, bool expected) =>
        PdnAvailableAppsApi.IsUpgrade(installed, catalog).Should().Be(expected);

    [Theory]
    [InlineData("v0.2.1", "0.2.2", true)]    // the release-tag spelling is tolerated on either side
    [InlineData("0.2.1", "v0.2.2", true)]
    [InlineData("v0.2.2", "0.2.2", false)]
    [InlineData("V0.2.1", "0.2.2", true)]
    public void IsUpgrade_IgnoresALeadingTagV(string installed, string catalog, bool expected) =>
        PdnAvailableAppsApi.IsUpgrade(installed, catalog).Should().Be(expected);

    /// <summary>
    /// Every version the committed catalog ships, against neighbours one step either side in
    /// each dotted component: the new ordering must agree with the old numeric comparer
    /// (<see cref="NodeVersion"/>) for all of them, so switching to Debian ordering changes no
    /// "update available" answer for anything the catalog carries today.
    /// </summary>
    [Fact]
    public void IsUpgrade_AgreesWithTheOldComparer_ForEveryShippedCatalogVersion()
    {
        var path = Path.Combine(CatalogTestSupport.RepoRoot(), "catalog", "apps.yaml");
        var versions = new EmbeddedAppCatalog(NullLoggerFactory.Instance, path).List()
            .Select(a => a.Version!)
            .ToList();
        versions.Should().NotBeEmpty();

        foreach (var version in versions)
        {
            DebianVersion.IsValid(version).Should().BeTrue($"{version} is a Debian version");
            PdnAvailableAppsApi.IsUpgrade(version, version).Should().BeFalse();

            foreach (var (older, newer) in Neighbours(version))
            {
                PdnAvailableAppsApi.IsUpgrade(older, newer).Should().BeTrue($"{newer} is an update over {older}");
                PdnAvailableAppsApi.IsUpgrade(newer, older).Should().BeFalse($"{older} is not an update over {newer}");
                OldIsUpgrade(older, newer).Should().BeTrue($"the old comparer agreed {newer} > {older}");
                OldIsUpgrade(newer, older).Should().BeFalse();
            }
        }
    }

    private static bool OldIsUpgrade(string installed, string catalog) =>
        NodeVersion.TryParse(catalog, out var c) && NodeVersion.TryParse(installed, out var i) && c.IsUpdateOver(i);

    /// <summary>(older, newer) pairs around <paramref name="version"/>: each dotted component of
    /// the upstream version bumped by one (and dropped by one where it is above zero), keeping
    /// any revision suffix as is.</summary>
    private static IEnumerable<(string Older, string Newer)> Neighbours(string version)
    {
        int dash = version.LastIndexOf('-');
        var upstream = dash >= 0 ? version[..dash] : version;
        var suffix = dash >= 0 ? version[dash..] : "";
        var parts = upstream.Split('.').Select(int.Parse).ToArray();
        for (int i = 0; i < parts.Length; i++)
        {
            var up = (int[])parts.Clone();
            up[i]++;
            yield return (version, string.Join('.', up) + suffix);
            if (parts[i] > 0)
            {
                var down = (int[])parts.Clone();
                down[i]--;
                yield return (string.Join('.', down) + suffix, version);
            }
        }
    }
}
