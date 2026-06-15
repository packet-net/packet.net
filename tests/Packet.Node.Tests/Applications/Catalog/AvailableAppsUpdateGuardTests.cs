using Packet.Node.Api;

namespace Packet.Node.Tests.Applications.Catalog;

/// <summary>
/// The "Available apps" backwards-update guard (<see cref="PdnAvailableAppsApi"/> <c>IsUpgrade</c>):
/// an update is offered ONLY when the catalog pin is a strictly-greater numeric version than what's
/// installed. Guards against a backwards "update vX → vY" when the catalog version is OLDER than the
/// installed one (an out-of-band install left the box ahead, or the catalog was rolled back), and
/// against the old naive Ordinal string compare under which "0.2.9" sorted after "0.2.10".
/// </summary>
public sealed class AvailableAppsUpdateGuardTests
{
    [Theory]
    [InlineData("0.2.9", "0.2.10", true)]   // catalog strictly newer → an update
    [InlineData("0.2.0", "0.3.0", true)]    // minor bump
    [InlineData("0.2.9", "0.2.9", false)]   // equal → up to date, not an update
    [InlineData("0.2.10", "0.2.9", false)]  // catalog OLDER → NOT a (backwards) update — the bug
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
}
