using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Paging;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Paging;

/// <summary>
/// The <see cref="PagingHostedService"/> over a <c>flex:mock</c> device (in-process, no hardware):
/// the enable→listen→disable lifecycle and reconcile, plus a PAGE/OK round-trip over the TCP line
/// server; and the <see cref="PagingConfigValidator"/> shape rules.
/// </summary>
[Trait("Category", "Node")]
public sealed class PagingHostedServiceTests
{
    private static readonly PagingConfigValidator Validator = new();

    private static NodeConfig Node(PagingConfig paging) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Paging = paging,
    };

    private static PagingConfig MockEnabled() => new()
    {
        Enabled = true,
        Device = "flex:mock",
        Bind = "127.0.0.1",
        Port = 0, // ephemeral
        Baud = 1200,
    };

    [Fact]
    public async Task Enable_listens_serves_a_page_then_disable_tears_down()
    {
        var provider = new TestConfigProvider(Node(MockEnabled()));
        await using var service = new PagingHostedService(provider, NullLoggerFactory.Instance);

        await service.StartAsync(CancellationToken.None);

        // StartAsync awaits the reconcile, so the flex:mock device is open and the server is bound.
        service.LocalPort.Should().BeGreaterThan(0);

        // The TCP line server accepts a PAGE and acknowledges it.
        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", service.LocalPort);
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
            using var reader = new StreamReader(stream);

            await writer.WriteLineAsync("PAGE 1234567 3 TONE");
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string? reply = await reader.ReadLineAsync(readTimeout.Token);
            reply.Should().StartWith("OK");
        }

        // Disabling the block tears the service down (reconcile is fire-and-forget off OnChange).
        provider.Apply(Node(MockEnabled() with { Enabled = false }));
        await WaitForAsync(() => service.LocalPort == 0, TimeSpan.FromSeconds(10));
        service.LocalPort.Should().Be(0);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_disabled_service_opens_no_device()
    {
        var provider = new TestConfigProvider(Node(new PagingConfig())); // Enabled defaults false
        await using var service = new PagingHostedService(provider, NullLoggerFactory.Instance);

        await service.StartAsync(CancellationToken.None);
        service.LocalPort.Should().Be(0);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void A_valid_paging_block_validates()
    {
        Validator.Validate(MockEnabled() with { Device = "default", CaptureRate = 48000, Port = 8106 })
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void An_out_of_range_port_is_rejected(int port)
    {
        Validator.Validate(new PagingConfig { Port = port }).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(300)]
    [InlineData(9600)]
    public void An_unsupported_baud_is_rejected(int baud)
    {
        Validator.Validate(new PagingConfig { Baud = baud }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_alsa_capture_rate_that_is_not_a_multiple_of_12000_is_rejected()
    {
        Validator.Validate(new PagingConfig { Device = "default", CaptureRate = 44100 })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_flex_device_is_exempt_from_capture_rate_and_rejects_ptt()
    {
        Validator.Validate(new PagingConfig { Enabled = true, Device = "flex:mock", CaptureRate = 44100 })
            .IsValid.Should().BeTrue();

        Validator.Validate(new PagingConfig { Enabled = true, Device = "flex:mock", Ptt = "cm108:/dev/hidraw0" })
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
