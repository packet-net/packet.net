using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Ardop;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Ardop;

/// <summary>
/// The <see cref="ArdopHostedService"/> over a <c>flex:mock</c> device (in-process, no hardware):
/// the enable→listen→disable lifecycle (the ardopcf-compatible command socket binds and accepts a
/// connection, then tears down), and the <see cref="ArdopConfigValidator"/> shape rules.
/// </summary>
[Trait("Category", "Node")]
public sealed class ArdopHostedServiceTests
{
    private static readonly ArdopConfigValidator Validator = new();

    private static NodeConfig Node(ArdopConfig ardop) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ardop = ardop,
    };

    private static ArdopConfig MockEnabled() => new()
    {
        Enabled = true,
        Device = "flex:mock",
        Bind = "127.0.0.1",
        Port = 0, // ephemeral command socket (the data socket is ephemeral too when port is 0)
    };

    [Fact]
    public async Task Enable_binds_the_command_socket_then_disable_tears_down()
    {
        var provider = new TestConfigProvider(Node(MockEnabled()));
        await using var service = new ArdopHostedService(provider, NullLoggerFactory.Instance);

        await service.StartAsync(CancellationToken.None);

        // StartAsync awaits the reconcile: the flex:mock device is open, the 12 kHz channel is
        // running and the ardopcf host command socket is bound.
        service.LocalCommandPort.Should().BeGreaterThan(0);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", service.LocalCommandPort);
            client.Connected.Should().BeTrue();
        }

        provider.Apply(Node(MockEnabled() with { Enabled = false }));
        await WaitForAsync(() => service.LocalCommandPort == 0, TimeSpan.FromSeconds(10));
        service.LocalCommandPort.Should().Be(0);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_disabled_service_opens_no_device()
    {
        var provider = new TestConfigProvider(Node(new ArdopConfig())); // Enabled defaults false
        await using var service = new ArdopHostedService(provider, NullLoggerFactory.Instance);

        await service.StartAsync(CancellationToken.None);
        service.LocalCommandPort.Should().Be(0);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void A_valid_ardop_block_validates()
    {
        Validator.Validate(new ArdopConfig { Enabled = true, Device = "default", CaptureRate = 48000, Port = 8515 })
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65535)] // the data socket would need 65536
    public void An_out_of_range_command_port_is_rejected(int port)
    {
        Validator.Validate(new ArdopConfig { Port = port }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_alsa_capture_rate_that_is_not_a_multiple_of_12000_is_rejected()
    {
        Validator.Validate(new ArdopConfig { Device = "default", CaptureRate = 44100 })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_flex_device_is_exempt_from_capture_rate_and_rejects_ptt()
    {
        Validator.Validate(new ArdopConfig { Enabled = true, Device = "flex:mock", CaptureRate = 44100 })
            .IsValid.Should().BeTrue();

        Validator.Validate(new ArdopConfig { Enabled = true, Device = "flex:mock", Ptt = "cm108:/dev/hidraw0" })
            .IsValid.Should().BeFalse();
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }
}
