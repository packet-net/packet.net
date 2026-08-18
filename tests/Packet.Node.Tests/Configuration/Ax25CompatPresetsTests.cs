using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// Pins <see cref="Ax25CompatPresets"/> - the single name→preset authority the
/// validator and the <c>PortSupervisor</c> share: preset names resolve to the
/// library's <see cref="Ax25ParseOptions"/> instances, per-flag overrides apply
/// on top (explicit wins), and absent compat resolves to the no-change defaults
/// (lenient parsing + spec-correct quirks).
/// </summary>
public class Ax25CompatPresetsTests
{
    [Fact]
    public void Null_compat_resolves_to_the_historical_defaults()
    {
        Ax25CompatPresets.ResolveParseOptions(null).Should().Be(Ax25ParseOptions.Lenient);
        Ax25CompatPresets.ResolveQuirks(null).Should().BeSameAs(Ax25SessionQuirks.Default);
    }

    [Theory]
    [InlineData("strict")]
    [InlineData("STRICT")]
    public void Preset_names_resolve_case_insensitively(string name)
    {
        Ax25CompatPresets.ResolveParseOptions(new PortCompatConfig { Preset = name })
            .Should().Be(Ax25ParseOptions.Strict);
    }

    [Fact]
    public void Each_preset_name_maps_to_its_library_instance()
    {
        Ax25CompatPresets.ResolveParseOptions(new PortCompatConfig { Preset = "lenient" }).Should().Be(Ax25ParseOptions.Lenient);
        Ax25CompatPresets.ResolveParseOptions(new PortCompatConfig { Preset = "bpq" }).Should().Be(Ax25ParseOptions.Bpq);
        Ax25CompatPresets.ResolveParseOptions(new PortCompatConfig { Preset = "xrouter" }).Should().Be(Ax25ParseOptions.Xrouter);
        Ax25CompatPresets.ResolveParseOptions(new PortCompatConfig { Preset = "direwolf" }).Should().Be(Ax25ParseOptions.Direwolf);
    }

    [Fact]
    public void Flag_overrides_apply_on_top_of_the_preset()
    {
        // Strict base, but re-allow the #142 v1.x accommodation: the named flag
        // wins over the preset, the other two stay at the preset's (strict) value.
        var resolved = Ax25CompatPresets.ResolveParseOptions(new PortCompatConfig
        {
            Preset = "strict",
            AllowCommandFrameAsResponse = true,
        });

        resolved.AllowCommandFrameAsResponse.Should().BeTrue("the explicit flag overrides the preset");
        resolved.AllowEmptyCallsignBase.Should().BeFalse("unset flags keep the preset's value");
        resolved.AllowInfoOnSupervisoryFrames.Should().BeFalse("unset flags keep the preset's value");
    }

    [Fact]
    public void Quirks_selector_resolves_both_names()
    {
        Ax25CompatPresets.ResolveQuirks(new PortCompatConfig { Quirks = "default" })
            .Should().BeSameAs(Ax25SessionQuirks.Default);
        Ax25CompatPresets.ResolveQuirks(new PortCompatConfig { Quirks = "strictly-faithful" })
            .Should().BeSameAs(Ax25SessionQuirks.StrictlyFaithful);
        Ax25CompatPresets.ResolveQuirks(new PortCompatConfig { Quirks = "StrictlyFaithful" })
            .Should().BeSameAs(Ax25SessionQuirks.StrictlyFaithful, "selector names are case- and separator-insensitive");
    }

    [Fact]
    public void Name_knowledge_matches_what_the_validator_advertises()
    {
        // Every advertised name must resolve - a name the validator accepts can
        // never fail at bring-up (the single-authority contract).
        foreach (var preset in Ax25CompatPresets.PresetNames)
        {
            Ax25CompatPresets.IsKnownPreset(preset).Should().BeTrue();
        }
        foreach (var quirks in Ax25CompatPresets.QuirksNames)
        {
            Ax25CompatPresets.IsKnownQuirks(quirks).Should().BeTrue();
        }
        Ax25CompatPresets.IsKnownPreset("kenwood").Should().BeFalse();
        Ax25CompatPresets.IsKnownQuirks("faithful").Should().BeFalse();
    }
}
