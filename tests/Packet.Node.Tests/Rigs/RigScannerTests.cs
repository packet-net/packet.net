using Packet.Node.Core.Configuration;
using Packet.Node.Core.Radios;
using Packet.Node.Core.Rigs;
using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Tests.Rigs;

/// <summary>
/// <see cref="RigScanner"/> against a temp directory of stand-in device nodes (via the
/// <c>PACKETNET_RIG_PORTS</c> override) and by-id symlinks: claimed marking through
/// <see cref="ClaimedSerialDevices"/>, suggestion wiring through
/// <see cref="RigDescriptorSuggestions"/> + <see cref="RigModelCatalogue"/>, and the
/// bare-device round-trip. Entirely passive — no serial port is ever opened, so no hardware
/// (or Linux perms) is needed beyond symlink creation for the by-id tests.
/// </summary>
[Trait("Category", "Node")]
public sealed class RigScannerTests : IDisposable
{
    private const string Ic7300Descriptor =
        "usb-Silicon_Labs_CP2102_USB_to_UART_Bridge_Controller_IC-7300_03001234-if00-port0";

    // A trimmed verbatim `rigctl -l` (Hamlib 4.5.5) — enough for name→number resolution.
    private const string CatalogueSample =
"""
 Rig #  Mfg                    Model                   Version         Status      Macro
     1  Hamlib                 Dummy                   20221128.0      Stable      RIG_MODEL_DUMMY
  3073  Icom                   IC-7300                 20230109.10     Stable      RIG_MODEL_IC7300
  3085  Icom                   IC-705                  20230109.8      Stable      RIG_MODEL_IC705
""";

    private readonly string dir;
    private readonly string byIdDir;
    private readonly string devKiss;   // stand-in /dev/ttyUSB0 — claimed by a serial-kiss port
    private readonly string devRig;    // stand-in /dev/ttyUSB1 — an IC-7300 bridge (by-id link added in-test)
    private readonly string devBare;   // stand-in /dev/ttyACM0 — free, anonymous

    public RigScannerTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-rigscan-" + Guid.NewGuid().ToString("N"));
        byIdDir = Path.Combine(dir, "by-id");
        Directory.CreateDirectory(byIdDir);
        devKiss = Device("ttyUSB0");
        devRig = Device("ttyUSB1");
        devBare = Device("ttyACM0");
        Environment.SetEnvironmentVariable(
            RigScanner.PortsOverrideEnvVar, string.Join(',', devKiss, devRig, devBare));
    }

    private string Device(string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private string Link(string linkName, string target)
    {
        var link = Path.Combine(byIdDir, linkName);
        File.CreateSymbolicLink(link, target);
        return link;
    }

    private RigScanner Scanner(ProcessRunResult catalogueResult) => new(
        new RigModelCatalogue(new CannedRunner(catalogueResult)),
        new SerialByIdResolver(byIdDir));

    private sealed class CannedRunner(ProcessRunResult result) : IProcessRunner
    {
        public ProcessRunResult Run(string fileName, IReadOnlyList<string> arguments) => result;
    }

    private static NodeConfig Config(params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports = ports,
    };

    [Fact]
    public async Task Marks_devices_claimed_by_config_and_leaves_free_ones_unclaimed()
    {
        using var scanner = Scanner(ProcessRunResult.Ran(0, CatalogueSample));
        var config = Config(new PortConfig
        {
            Id = "vhf",
            Enabled = false, // disabled ports still claim
            Transport = new SerialKissTransport { Device = devKiss },
        });

        var scan = await scanner.ScanAsync(config);

        scan.Devices.Should().HaveCount(3);
        scan.Devices.Should().ContainSingle(d => d.DevicePath == devKiss)
            .Which.ClaimedBy.Should().Be("port 'vhf' transport (serial-kiss)");
        scan.Devices.Should().ContainSingle(d => d.DevicePath == devBare)
            .Which.ClaimedBy.Should().BeNull();
    }

    [SkippableFact]
    public async Task Suggests_a_model_from_the_by_id_descriptor_with_the_number_resolved_by_name()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "by-id resolution is Linux-only");
        var link = Link(Ic7300Descriptor, devRig);
        using var scanner = Scanner(ProcessRunResult.Ran(0, CatalogueSample));

        var scan = await scanner.ScanAsync(Config());

        scan.CatalogueAvailable.Should().BeTrue();
        var row = scan.Devices.Should().ContainSingle(d => d.DevicePath == devRig).Which;
        row.ByIdPath.Should().Be(link);
        row.Descriptor.Should().Be(Ic7300Descriptor);
        row.ClaimedBy.Should().BeNull();
        row.Suggestion.Should().Be(new Packet.Node.Core.Api.RigSuggestion(
            "Icom", "IC-7300", ModelNumber: 3073, Source: "by-id"));
    }

    [SkippableFact]
    public async Task Suggestion_survives_a_missing_rigctl_with_a_null_number()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "by-id resolution is Linux-only");
        Link(Ic7300Descriptor, devRig);
        using var scanner = Scanner(ProcessRunResult.NotLaunched);

        var scan = await scanner.ScanAsync(Config());

        scan.CatalogueAvailable.Should().BeFalse();
        scan.Devices.Should().ContainSingle(d => d.DevicePath == devRig)
            .Which.Suggestion.Should().Be(new Packet.Node.Core.Api.RigSuggestion(
                "Icom", "IC-7300", ModelNumber: null, Source: "by-id"));
    }

    [SkippableFact]
    public async Task A_config_by_id_binding_claims_the_raw_device_path()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "symlink canonicalisation is exercised on Linux");
        var link = Link(Ic7300Descriptor, devRig);
        using var scanner = Scanner(ProcessRunResult.Ran(0, CatalogueSample));
        var config = Config(new PortConfig
        {
            Id = "hf",
            Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8001 },
            Rig = new PortRigConfig { Kind = "hamlib", Device = link, Model = 3073 },
        });

        var scan = await scanner.ScanAsync(config);

        scan.Devices.Should().ContainSingle(d => d.DevicePath == devRig)
            .Which.ClaimedBy.Should().Be("port 'hf' rig");
    }

    [Fact]
    public async Task An_unclaimed_anonymous_device_round_trips_with_all_nulls()
    {
        using var scanner = Scanner(ProcessRunResult.Ran(0, CatalogueSample));

        var scan = await scanner.ScanAsync(Config());

        scan.Devices.Should().ContainSingle(d => d.DevicePath == devBare)
            .Which.Should().Be(new Packet.Node.Core.Api.RigScanDevice(
                devBare, ByIdPath: null, Descriptor: null, ClaimedBy: null, Suggestion: null));
    }

    [Fact]
    public async Task Concurrent_scans_single_flight_and_both_complete()
    {
        using var scanner = Scanner(ProcessRunResult.Ran(0, CatalogueSample));

        var first = scanner.ScanAsync(Config());
        var second = scanner.ScanAsync(Config());
        var scans = await Task.WhenAll(first, second);

        scans[0].Devices.Should().HaveCount(3);
        scans[1].Devices.Should().HaveCount(3);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RigScanner.PortsOverrideEnvVar, null);
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
