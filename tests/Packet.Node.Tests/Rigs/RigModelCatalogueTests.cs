using Packet.Node.Core.Rigs;
using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Tests.Rigs;

/// <summary>
/// <see cref="RigModelCatalogue"/>: the parsed, process-lifetime-cached <c>rigctl -l</c> table.
/// The parser is exercised against a verbatim sample captured from rigctl (Hamlib 4.5.5) — the
/// columns are fixed-width and model names contain spaces, so whitespace-splitting would mangle
/// them; header-derived offsets must not. A missing rigctl is a clean
/// <see cref="RigModelCatalogue.Available"/> <c>false</c>, never a throw.
/// </summary>
[Trait("Category", "Node")]
public sealed class RigModelCatalogueTests
{
    // Verbatim `rigctl -l` lines captured from rigctl Hamlib 4.5.5 (Apr 2023) — spacing intact.
    // Deliberately includes the models-with-spaces rows (NET rigctl / TRXManager 5.7.630+ /
    // Dummy No VFO / MARK-V FT-1000MP) and a non-Stable status (IC-R8600, Beta).
    private const string Sample =
"""
 Rig #  Mfg                    Model                   Version         Status      Macro
     1  Hamlib                 Dummy                   20221128.0      Stable      RIG_MODEL_DUMMY
     2  Hamlib                 NET rigctl              20230328.0      Stable      RIG_MODEL_NETRIGCTL
     4  FLRig                  FLRig                   20221109.0      Stable      RIG_MODEL_FLRIG
     5  TRXManager             TRXManager 5.7.630+     20210613.0      Stable      RIG_MODEL_TRXMANAGER_RIG
     6  Hamlib                 Dummy No VFO            20220510.0      Stable      RIG_MODEL_DUMMY_NOVFO
  1004  Yaesu                  MARK-V FT-1000MP        20230104.0      Stable      RIG_MODEL_FT1000MPMKV
  2042  Kenwood                TH-D74                  20230318.3      Stable      RIG_MODEL_THD74
  2047  Elecraft               K4                      20230318.25     Stable      RIG_MODEL_K4
  3061  Icom                   IC-7200                 20230109.2      Stable      RIG_MODEL_IC7200
  3068  Icom                   IC-9100                 20230109.4      Stable      RIG_MODEL_IC9100
  3070  Icom                   IC-7100                 20230109.3      Stable      RIG_MODEL_IC7100
  3073  Icom                   IC-7300                 20230109.10     Stable      RIG_MODEL_IC7300
  3078  Icom                   IC-7610                 20230109.11     Stable      RIG_MODEL_IC7610
  3079  Icom                   IC-R8600                20230109.3      Beta        RIG_MODEL_ICR8600
  3081  Icom                   IC-9700                 20230109.11     Stable      RIG_MODEL_IC9700
  3085  Icom                   IC-705                  20230109.8      Stable      RIG_MODEL_IC705
""";

    /// <summary>Canned-result runner recording every call — the local, non-hardcoded sibling of
    /// the private fake in InstallChannelProviderTests.</summary>
    private sealed class FakeProcessRunner(ProcessRunResult result) : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public ProcessRunResult Run(string fileName, IReadOnlyList<string> arguments)
        {
            Calls.Add((fileName, arguments));
            return result;
        }
    }

    private static RigModelCatalogue Catalogue(ProcessRunResult result)
        => new(new FakeProcessRunner(result));

    [Fact]
    public void Parses_the_verbatim_sample_including_models_with_spaces()
    {
        var models = RigModelCatalogue.Parse(Sample);

        models.Should().HaveCount(16);
        models[0].Should().Be(new Packet.Node.Core.Api.RigCatalogueModel(1, "Hamlib", "Dummy", "Stable"));
        // Model names with internal spaces must survive (whitespace-splitting would mangle them).
        models.Should().ContainSingle(m => m.Number == 2).Which.Model.Should().Be("NET rigctl");
        models.Should().ContainSingle(m => m.Number == 5).Which.Model.Should().Be("TRXManager 5.7.630+");
        models.Should().ContainSingle(m => m.Number == 6).Which.Model.Should().Be("Dummy No VFO");
        models.Should().ContainSingle(m => m.Number == 1004).Which.Model.Should().Be("MARK-V FT-1000MP");
        // Spot the wizard's flagship + a non-Stable status.
        models.Should().ContainSingle(m => m.Number == 3073)
            .Which.Should().Be(new Packet.Node.Core.Api.RigCatalogueModel(3073, "Icom", "IC-7300", "Stable"));
        models.Should().ContainSingle(m => m.Number == 3079).Which.Status.Should().Be("Beta");
    }

    [Fact]
    public void Parser_skips_preamble_and_malformed_lines_defensively()
    {
        var noisy = "rigctl: some warning line\n\n" + Sample
            + "\n   not-a-number  Junk                   Junk\n  9999\n";

        var models = RigModelCatalogue.Parse(noisy);

        models.Should().HaveCount(16, "junk before the header and malformed rows after it are skipped");
    }

    [Fact]
    public void Parser_returns_empty_when_no_header_is_present()
    {
        RigModelCatalogue.Parse("no table here\njust noise\n").Should().BeEmpty();
    }

    [Fact]
    public void Available_catalogue_resolves_numbers_by_name_case_insensitively()
    {
        var catalogue = Catalogue(ProcessRunResult.Ran(0, Sample));

        catalogue.Available.Should().BeTrue();
        catalogue.Models.Should().HaveCount(16);
        catalogue.ResolveNumber("Icom", "IC-7300").Should().Be(3073);
        catalogue.ResolveNumber("icom", "ic-7300").Should().Be(3073, "matching is case-insensitive");
        catalogue.ResolveNumber(" Icom ", " IC-705 ").Should().Be(3085, "matching trims");
    }

    [Fact]
    public void ResolveNumber_returns_null_on_no_match_ambiguity_or_blank_input()
    {
        var duplicated = Sample + "\n  9998  Icom                   IC-7300                 20230109.10     Stable      RIG_MODEL_DUPE\n";
        var catalogue = Catalogue(ProcessRunResult.Ran(0, duplicated));

        catalogue.ResolveNumber("Icom", "IC-9999").Should().BeNull("unknown model");
        catalogue.ResolveNumber("Yaesu", "IC-7300").Should().BeNull("manufacturer must match too");
        catalogue.ResolveNumber("Icom", "IC-7300").Should().BeNull("two rows claim the name — refuse to guess");
        catalogue.ResolveNumber("", "IC-7300").Should().BeNull();
        catalogue.ResolveNumber("Icom", " ").Should().BeNull();
    }

    [Fact]
    public void Missing_rigctl_is_unavailable_and_empty_never_a_throw()
    {
        var catalogue = Catalogue(ProcessRunResult.NotLaunched);

        catalogue.Available.Should().BeFalse();
        catalogue.Models.Should().BeEmpty();
        catalogue.ResolveNumber("Icom", "IC-7300").Should().BeNull();
    }

    [Fact]
    public void Failing_rigctl_is_unavailable_too()
    {
        Catalogue(ProcessRunResult.Ran(1, "")).Available.Should().BeFalse();
    }

    [Fact]
    public void Rigctl_runs_once_and_the_parse_is_cached_for_the_instance_lifetime()
    {
        var runner = new FakeProcessRunner(ProcessRunResult.Ran(0, Sample));
        var catalogue = new RigModelCatalogue(runner);

        _ = catalogue.Available;
        _ = catalogue.Models;
        _ = catalogue.ResolveNumber("Icom", "IC-7300");

        runner.Calls.Should().ContainSingle("the catalogue is static per hamlib install");
        runner.Calls[0].FileName.Should().Be("rigctl");
        runner.Calls[0].Arguments.Should().Equal("-l");
    }

    [Fact]
    public void Binary_env_override_names_the_executable()
    {
        Environment.SetEnvironmentVariable(RigModelCatalogue.BinaryEnvVar, "/opt/hamlib/bin/rigctl");
        try
        {
            var runner = new FakeProcessRunner(ProcessRunResult.Ran(0, Sample));
            _ = new RigModelCatalogue(runner).Available;

            runner.Calls.Should().ContainSingle()
                .Which.FileName.Should().Be("/opt/hamlib/bin/rigctl");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RigModelCatalogue.BinaryEnvVar, null);
        }
    }

    [SkippableFact]
    public void Real_rigctl_yields_a_usable_catalogue()
    {
        var catalogue = new RigModelCatalogue(SystemProcessRunner.Instance);
        Skip.If(!catalogue.Available, "rigctl is not installed on this machine");

        catalogue.Models.Should().NotBeEmpty();
        // Model 1 is hamlib's own dummy rig — present in every install.
        catalogue.Models.Should().Contain(m => m.Number == 1 && m.Manufacturer == "Hamlib");
        catalogue.ResolveNumber("Icom", "IC-7300").Should().BePositive();
    }
}
