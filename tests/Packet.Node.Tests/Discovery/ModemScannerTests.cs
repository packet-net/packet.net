using Packet.Node.Core.Configuration;
using Packet.Node.Core.Modems;

namespace Packet.Node.Tests.Discovery;

/// <summary>
/// The first-run wizard's modem discovery (<c>GET /setup/devices</c>).
/// </summary>
/// <remarks>
/// This is the one scanner in the node that <b>opens serial ports and writes to them</b> (the rig
/// scan is passive), and it runs unauthenticated on an unclaimed node. So the tests that matter
/// are about restraint as much as discovery: a device something already uses is never opened, a
/// device that could not plausibly be a NinoTNC is never opened, and a failure to open one is
/// reported as a reason rather than swallowed into an empty list.
/// </remarks>
[Trait("Category", "Node")]
public sealed class ModemScannerTests
{
    private static NodeConfig Config(params PortConfig[] ports) => new()
    {
        SchemaVersion = 1,
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports = [.. ports],
    };

    private static PortConfig NinoPort(string id, string device) => new()
    {
        Id = id,
        Enabled = true,
        Transport = new NinoTncTransport { Device = device, Baud = 57600, Mode = 4 },
    };

    /// <summary>A scanner over a fixed device list, with a scripted identify and every device
    /// treated as probe-worthy unless the caller says otherwise.</summary>
    private static ModemScanner Scanner(
        IReadOnlyList<string> devices,
        Func<string, CancellationToken, Task<string>> identify,
        Func<string, bool>? probeWorthy = null)
        => new(
            byId: new Packet.Node.Core.Radios.SerialByIdResolver(byIdDirectory: Path.Combine(Path.GetTempPath(), "pdn-no-such-by-id")),
            identify: identify,
            enumerate: () => devices,
            probeCandidate: probeWorthy ?? (_ => true),
            probeTimeout: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(5));

    [Fact]
    public async Task A_device_that_answers_is_a_NinoTNC_with_its_firmware_version()
    {
        using var scanner = Scanner(["/dev/ttyACM0"], (_, _) => Task.FromResult("3.44"));

        var scan = await scanner.ScanAsync(Config());

        scan.Devices.Should().ContainSingle();
        var device = scan.Devices[0];
        device.Kind.Should().Be("nino-tnc");
        device.FirmwareVersion.Should().Be("3.44");
        device.ProbeError.Should().BeNull();
        scan.PermissionDenied.Should().BeFalse();
    }

    [Fact]
    public async Task A_device_that_does_not_answer_is_still_offered_with_the_reason_why()
    {
        // Silence is NOT "not a TNC": a generic KISS TNC has no identify handshake to answer, so
        // hiding the device would hide the very thing a Generic KISS port needs to be pointed at.
        using var scanner = Scanner(["/dev/ttyUSB0"], (_, _) => throw new TimeoutException("no reply"));

        var scan = await scanner.ScanAsync(Config());

        scan.Devices.Should().ContainSingle();
        scan.Devices[0].Kind.Should().Be("serial");
        scan.Devices[0].ProbeError.Should().Be("no reply");
        scan.PermissionDenied.Should().BeFalse();
    }

    [Fact]
    public async Task A_permissions_failure_is_reported_as_one_because_it_has_a_fix()
    {
        // The packetnet user not being in dialout is THE first-contact failure this whole
        // endpoint exists to make visible. An empty list would send the operator to journalctl.
        using var scanner = Scanner(
            ["/dev/ttyACM0"],
            (_, _) => throw new UnauthorizedAccessException("Access to the port '/dev/ttyACM0' is denied."));

        var scan = await scanner.ScanAsync(Config());

        scan.PermissionDenied.Should().BeTrue();
        scan.Devices[0].ProbeError.Should().Be("permission denied");
    }

    [Fact]
    public async Task A_device_the_config_already_claims_is_marked_and_never_opened()
    {
        var opened = new List<string>();
        using var scanner = Scanner(
            ["/dev/ttyACM0", "/dev/ttyACM1"],
            (device, _) => { opened.Add(device); return Task.FromResult("3.44"); });

        var scan = await scanner.ScanAsync(Config(NinoPort("vhf-1", "/dev/ttyACM0")));

        // Opening a port a running transport already holds would fail at best and disturb a live
        // link at worst.
        opened.Should().Equal("/dev/ttyACM1");
        var claimed = scan.Devices.Single(d => d.KernelPath == "/dev/ttyACM0");
        claimed.ClaimedBy.Should().Be("port 'vhf-1' transport (nino-tnc)");
        claimed.Kind.Should().Be("serial");
    }

    [Fact]
    public async Task A_device_that_could_not_be_a_NinoTNC_is_listed_but_left_alone()
    {
        // A CAT cable or a GPS on /dev/ttyUSB0 should not be written to by a device picker.
        var opened = new List<string>();
        using var scanner = Scanner(
            ["/dev/ttyACM0", "/dev/ttyUSB0"],
            (device, _) => { opened.Add(device); return Task.FromResult("3.44"); },
            probeWorthy: d => d.Contains("ACM", StringComparison.Ordinal));

        var scan = await scanner.ScanAsync(Config());

        opened.Should().Equal("/dev/ttyACM0");
        scan.Devices.Should().HaveCount(2);
        scan.Devices.Single(d => d.KernelPath == "/dev/ttyUSB0").ProbeError.Should().BeNull();
    }

    [Fact]
    public async Task Identified_modems_come_first_and_claimed_ones_last()
    {
        // The picker pre-selects the first free row, so the order IS the default choice.
        using var scanner = Scanner(
            ["/dev/ttyACM0", "/dev/ttyACM1", "/dev/ttyUSB0"],
            (device, _) => device.EndsWith("ACM1", StringComparison.Ordinal)
                ? Task.FromResult("3.44")
                : throw new TimeoutException("no reply"));

        var scan = await scanner.ScanAsync(Config(NinoPort("vhf-1", "/dev/ttyACM0")));

        scan.Devices.Select(d => d.KernelPath).Should().Equal("/dev/ttyACM1", "/dev/ttyUSB0", "/dev/ttyACM0");
    }

    [Fact]
    public async Task A_wedged_device_cannot_hang_the_scan()
    {
        // The whole-scan ceiling is what keeps an unauthenticated endpoint from being a free
        // denial of service on the box's own request pipeline.
        using var scanner = new ModemScanner(
            byId: new Packet.Node.Core.Radios.SerialByIdResolver(byIdDirectory: Path.Combine(Path.GetTempPath(), "pdn-no-such-by-id")),
            identify: async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return "never"; },
            enumerate: () => ["/dev/ttyACM0", "/dev/ttyACM1"],
            probeCandidate: _ => true,
            probeTimeout: TimeSpan.FromMilliseconds(50),
            timeout: TimeSpan.FromSeconds(5));

        var scan = await scanner.ScanAsync(Config());

        scan.Devices.Should().HaveCount(2);
        scan.Devices.Should().OnlyContain(d => d.ProbeError == "no reply");
    }

    [Fact]
    public void The_env_override_replaces_enumeration_entirely()
    {
        // The escape hatch for a dev box whose /dev is full of unrelated USB-CDC devices, and the
        // seam CI uses to keep a scan away from real hardware.
        Environment.SetEnvironmentVariable(ModemScanner.PortsOverrideEnvVar, "/dev/pts/9, /dev/pts/10");
        try
        {
            ModemScanner.EnumerateCandidateDevices().Should().Equal("/dev/pts/9", "/dev/pts/10");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModemScanner.PortsOverrideEnvVar, null);
        }
    }
}
