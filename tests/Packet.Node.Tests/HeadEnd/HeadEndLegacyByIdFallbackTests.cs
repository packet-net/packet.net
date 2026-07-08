using Microsoft.Extensions.Logging;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Radios;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// The legacy-binding fallback (#578): head-end ≤0.1.2 used <c>/dev/serial/by-id</c> basenames as
/// device ids; 0.1.3 switched to by-path ids (#575), keeping by-id as the informational
/// <c>byId</c> hint. A NodeConfig adopted against the old head-end must keep resolving after the
/// upgrade — via the hint, with a re-adopt warning — in both the device resolver (bring-up) and
/// the fleet scanner's bound-device detection.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndLegacyByIdFallbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string ByPathId = "platform-3f980000.usb-usb-0:1.1:1.0-port0";
    private const string LegacyById = "usb-Silicon_Labs_CP2102_USB_to_UART_Bridge_Controller_0001-if00-port0";
    private const string ByIdPath = "/dev/serial/by-id/" + LegacyById;

    private static HeadEndDeviceResolver ResolverOver(
        StubHeadEndHandler handler, ILoggerFactory? loggerFactory = null) => new(
        [new HeadEndConfig { Id = "pi-shack", Address = "127.0.0.1:7300" }],
        _ => new HeadEndClient(new Uri("http://127.0.0.1:7300/"), new HttpClient(handler)),
        addressResolver: null,
        loggerFactory);

    [Fact]
    public async Task A_legacy_by_id_device_id_resolves_via_the_byId_hint_and_binds_the_current_id()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports =
            [
                new HeadEndPortInfo { Id = ByPathId, ById = ByIdPath, TcpPort = 7401, Baud = 28800 },
            ],
        });
        var log = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(log));

        var binding = await ResolverOver(handler, loggerFactory)
            .ResolveAsync("pi-shack", LegacyById)
            .WaitAsync(Timeout);

        binding.TcpPort.Should().Be(7401, "the legacy id must find the same physical device");
        binding.DeviceId.Should().Be(ByPathId,
            "the binding must carry the CURRENT inventory id — the line verb addresses the device by it");

        await binding.SetBaud(28800, CancellationToken.None).WaitAsync(Timeout);
        handler.LineCalls.Should().ContainSingle().Which.DeviceId.Should().Be(ByPathId);

        log.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("legacy by-id"),
            "the fallback warns the operator to re-adopt onto the stable by-path id");
    }

    [Fact]
    public async Task An_exact_id_match_needs_no_fallback_and_no_warning()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports =
            [
                new HeadEndPortInfo { Id = ByPathId, ById = ByIdPath, TcpPort = 7401, Baud = 28800 },
            ],
        });
        var log = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(log));

        var binding = await ResolverOver(handler, loggerFactory)
            .ResolveAsync("pi-shack", ByPathId)
            .WaitAsync(Timeout);

        binding.DeviceId.Should().Be(ByPathId);
        log.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task An_id_matching_neither_the_inventory_nor_any_byId_hint_still_throws()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports =
            [
                new HeadEndPortInfo { Id = ByPathId, ById = ByIdPath, TcpPort = 7401, Baud = 28800 },
            ],
        });

        var act = () => ResolverOver(handler).ResolveAsync("pi-shack", "usb-Some_Other_Dongle-if00-port0");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no device*");
    }

    [Fact]
    public async Task The_scanner_treats_a_legacy_by_id_binding_as_bound_and_does_not_probe_it()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports =
            [
                // A discard TCP port the scan must NEVER dial: the device is bound to a configured
                // port, but the binding predates the by-path ids (it names the by-id basename).
                new HeadEndPortInfo { Id = ByPathId, ById = ByIdPath, TcpPort = 9, Baud = 28800, UsbVid = "10c4" },
            ],
        });
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-shack", "127.0.0.1", 7300));
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Ports =
            [
                new PortConfig
                {
                    Id = "p1",
                    Transport = new NinoTncTcpTransport { HeadEndId = "pi-shack", DeviceId = "some-nino" },
                    Radio = new PortRadioConfig { Kind = "tait-ccdi", HeadEndId = "pi-shack", DeviceId = LegacyById },
                },
            ],
        };
        var scanner = new HeadEndRadioScanner(
            discovery,
            clientFactory: uri => new HeadEndClient(uri, new HttpClient(handler)),
            discoveryTimeout: TimeSpan.FromMilliseconds(200),
            identifyTimeout: TimeSpan.FromSeconds(2),
            connectTimeout: TimeSpan.FromSeconds(2));

        var scan = await scanner.ScanAsync(config).WaitAsync(Timeout);

        var device = scan.Instances.Single().Devices.Single();
        device.DeviceId.Should().Be(ByPathId);
        device.Free.Should().BeFalse("a legacy by-id binding still marks the device bound — probing it would fight the running port");
        device.Kind.Should().Be(HeadEndDeviceKind.TaitCcdi, "the bound role is known from the radio binding");
    }

    /// <summary>Captures log entries across categories so a test can assert a warning fired.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public sealed record Entry(LogLevel Level, string Message);

        private readonly List<Entry> entries = [];

        public IReadOnlyList<Entry> Entries
        {
            get
            {
                lock (entries)
                {
                    return [.. entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.entries)
                {
                    owner.entries.Add(new Entry(logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
