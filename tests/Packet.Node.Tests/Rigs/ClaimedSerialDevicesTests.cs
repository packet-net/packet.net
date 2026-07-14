using Packet.Node.Core.Configuration;
using Packet.Node.Core.Rigs;

namespace Packet.Node.Tests.Rigs;

/// <summary>
/// <see cref="ClaimedSerialDevices"/>: the "what already owns this device?" set behind the rig
/// scan. Every serial-shaped claim in config must register — transport devices (enabled AND
/// disabled ports), radio control devices, rig CAT devices — keyed canonically so a by-id
/// symlink in config and a raw <c>/dev/ttyUSB*</c> path from a scan collide. Serial-number
/// bindings (<c>radio.serial:</c>) are deliberately absent: which device they land on is only
/// knowable by probing, and the helper is passive.
/// </summary>
[Trait("Category", "Node")]
public sealed class ClaimedSerialDevicesTests : IDisposable
{
    private readonly string dir;

    public ClaimedSerialDevicesTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-claimed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    private static NodeConfig Config(params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports = ports,
    };

    // Identity canonicalisation: these tests assert WHAT is collected; the symlink test below
    // asserts HOW keys canonicalise.
    private static IReadOnlyDictionary<string, string> Collect(NodeConfig config)
        => ClaimedSerialDevices.Collect(config, canonicalise: p => p);

    [Fact]
    public void Collects_transport_radio_and_rig_devices_with_human_descriptions()
    {
        var claimed = Collect(Config(
            new PortConfig
            {
                Id = "hf",
                Transport = new SerialKissTransport { Device = "/dev/ttyUSB0" },
                Rig = new PortRigConfig { Kind = "hamlib", Device = "/dev/ttyUSB1", Model = 3073 },
            },
            new PortConfig
            {
                Id = "vhf",
                Transport = new NinoTncTransport { Device = "/dev/ttyACM0" },
                Radio = new PortRadioConfig { Kind = "tait-ccdi", Port = "/dev/ttyUSB2" },
            },
            new PortConfig
            {
                Id = "link",
                Transport = new TaitTransparentTransportConfig { Device = "/dev/ttyUSB3" },
            }));

        claimed.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["/dev/ttyUSB0"] = "port 'hf' transport (serial-kiss)",
            ["/dev/ttyUSB1"] = "port 'hf' rig",
            ["/dev/ttyACM0"] = "port 'vhf' transport (nino-tnc)",
            ["/dev/ttyUSB2"] = "port 'vhf' radio",
            ["/dev/ttyUSB3"] = "port 'link' transport (tait-transparent)",
        });
    }

    [Fact]
    public void Disabled_ports_still_claim_their_devices()
    {
        // A port the operator merely toggled off keeps its hardware — the scan must not offer it.
        var claimed = Collect(Config(new PortConfig
        {
            Id = "hf",
            Enabled = false,
            Transport = new SerialKissTransport { Device = "/dev/ttyUSB0" },
        }));

        claimed.Should().ContainKey("/dev/ttyUSB0")
            .WhoseValue.Should().Be("port 'hf' transport (serial-kiss)");
    }

    [Fact]
    public void Serial_number_bindings_and_head_end_bindings_claim_nothing()
    {
        // radio.serial: / tait-transparent serial: name a CCDI serial, not a device — resolving
        // one to a path needs a probe, and this helper is passive. Head-end devices are remote.
        var claimed = Collect(Config(new PortConfig
        {
            Id = "hf",
            Transport = new TaitTransparentTransportConfig { Serial = "1G000123" },
            Radio = new PortRadioConfig { Kind = "tait-ccdi", Serial = "1G000456" },
        },
        new PortConfig
        {
            Id = "remote",
            Transport = new NinoTncTcpTransport { HeadEndId = "shack", DeviceId = "tnc0" },
        }));

        claimed.Should().BeEmpty();
    }

    [Fact]
    public void Non_serial_transports_claim_nothing()
    {
        var claimed = Collect(Config(new PortConfig
        {
            Id = "ip",
            Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8001 },
        }));

        claimed.Should().BeEmpty();
    }

    [Fact]
    public void First_claim_wins_when_two_blocks_name_the_same_device()
    {
        var claimed = Collect(Config(new PortConfig
        {
            Id = "hf",
            Transport = new SerialKissTransport { Device = "/dev/ttyUSB0" },
            Rig = new PortRigConfig { Kind = "hamlib", Device = "/dev/ttyUSB0", Model = 1 },
        }));

        claimed.Should().HaveCount(1);
        claimed["/dev/ttyUSB0"].Should().Be("port 'hf' transport (serial-kiss)");
    }

    [SkippableFact]
    public void By_id_symlink_in_config_and_raw_path_collide_on_the_canonical_key()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "symlink canonicalisation is exercised on Linux");

        // A real device node stand-in + a by-id-style symlink to it.
        var device = Path.Combine(dir, "ttyUSB0");
        File.WriteAllText(device, string.Empty);
        var byIdLink = Path.Combine(dir, "usb-Icom_Inc._IC-705_IC-705_12345678-if00");
        File.CreateSymbolicLink(byIdLink, device);

        // Config claims via the (stable) by-id path — the default canonicalisation must land the
        // claim on the same key the scanner computes from the raw device path.
        var claimed = ClaimedSerialDevices.Collect(Config(new PortConfig
        {
            Id = "hf",
            Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8001 },
            Rig = new PortRigConfig { Kind = "hamlib", Device = byIdLink, Model = 3085 },
        }));

        claimed.Should().ContainKey(ClaimedSerialDevices.Canonicalise(device))
            .WhoseValue.Should().Be("port 'hf' rig");
    }

    [Fact]
    public void Canonicalise_falls_back_to_the_literal_path_when_the_device_does_not_exist()
    {
        // An unplugged device's claim must still register (and still collide with an identical
        // literal path from elsewhere).
        ClaimedSerialDevices.Canonicalise("/dev/ttyUSB99")
            .Should().Be(Path.GetFullPath("/dev/ttyUSB99"));
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
