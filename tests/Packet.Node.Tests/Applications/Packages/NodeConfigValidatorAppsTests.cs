using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Applications.Packages;

/// <summary>
/// The <see cref="NodeConfigValidator"/> rules for the <c>apps:</c> package-override list and
/// <c>appPackageRoots:</c>. Whether an apps: id matches a discovered package is deliberately
/// NOT validated here — the validator has no filesystem access, and per the contract an
/// unmatched entry is tolerated (the package may be installed later).
/// </summary>
public class NodeConfigValidatorAppsTests
{
    private static readonly NodeConfigValidator Validator = new();

    private static NodeConfig Base() => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
    };

    [Fact]
    public void An_empty_apps_list_and_null_roots_are_the_valid_defaults()
    {
        Validator.Validate(Base()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Distinct_apps_ids_are_valid()
    {
        var config = Base() with
        {
            Apps =
            [
                new AppOverrideConfig { Id = "lobby", Enabled = true },
                new AppOverrideConfig { Id = "dapps" },
            ],
        };

        Validator.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Duplicate_apps_ids_are_an_error()
    {
        var config = Base() with
        {
            Apps =
            [
                new AppOverrideConfig { Id = "lobby" },
                new AppOverrideConfig { Id = "lobby", Enabled = true },
            ],
        };

        var result = Validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("unique id"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_apps_id_is_an_error(string id)
    {
        var config = Base() with { Apps = [new AppOverrideConfig { Id = id }] };

        var result = Validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("apps entry id is required"));
    }

    [Fact]
    public void An_apps_id_matching_no_discovered_package_is_tolerated_here()
    {
        // The validator has no filesystem access; the catalog owns that relationship.
        var config = Base() with { Apps = [new AppOverrideConfig { Id = "not-installed-yet", Enabled = true }] };

        Validator.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Non_empty_appPackageRoots_entries_are_valid()
    {
        var config = Base() with { AppPackageRoots = ["/tmp/apps-a", "/tmp/apps-b"] };

        Validator.Validate(config).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_appPackageRoots_entry_is_an_error(string root)
    {
        var config = Base() with { AppPackageRoots = ["/tmp/apps-a", root] };

        var result = Validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("appPackageRoots"));
    }
}
