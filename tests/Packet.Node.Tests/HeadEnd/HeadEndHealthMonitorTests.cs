using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// <see cref="HeadEndHealthMonitor"/> (#583) — the background fleet health poller, driven
/// deterministically through its <c>PollOnceAsync</c> seam over the stub head-end control plane:
/// the statusz-rich poll, the healthz fallback for a pre-0.1.4 daemon, failure counting,
/// transition-only logging (one WARNING per outage, one recovery info — never per-poll), the
/// config self-gate (no head-ends ⇒ no polls, no browses), and the config-else-mDNS address rule.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndHealthMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    private static NodeConfig ConfigWith(params HeadEndConfig[] headEnds) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-7" },
        HeadEnds = headEnds,
    };

    private static HeadEndStatus Status(string id, params (string DeviceId, int TcpPort, bool Connected)[] bridges) => new()
    {
        InstanceId = id,
        BridgeCount = bridges.Length,
        Bridges = bridges.Select(b => new HeadEndBridgeStatus
        {
            Id = b.DeviceId,
            TcpPort = b.TcpPort,
            ClientConnected = b.Connected,
        }).ToList(),
    };

    private static HeadEndHealthMonitor Monitor(
        NodeConfig config, Func<Uri, HeadEndClient> clientFactory,
        FakeTimeProvider? clock = null, IHeadEndDiscovery? discovery = null, ILoggerFactory? loggers = null) =>
        new(new TestConfigProvider(config), discovery ?? new FakeHeadEndDiscovery(),
            clientFactory, clock ?? new FakeTimeProvider(T0), loggers);

    private static Func<Uri, HeadEndClient> ClientOver(StubHeadEndHandler handler) =>
        uri => new HeadEndClient(uri, new HttpClient(handler));

    // A handler that refuses every request — the unreachable head-end (connection refused).
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }

    [Fact]
    public async Task Statusz_poll_records_reachable_bridges_and_last_seen()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory())
        {
            Status = () => Status("pi-shack", ("nino0", 8001, true), ("tait0", 8002, false)),
        };
        var clock = new FakeTimeProvider(T0);
        var monitor = Monitor(ConfigWith(new HeadEndConfig { Id = "pi-shack", Address = "pi.test:7300" }),
            ClientOver(handler), clock);

        await monitor.PollOnceAsync(CancellationToken.None);

        var health = monitor.Snapshot().Should().ContainSingle().Subject;
        health.InstanceId.Should().Be("pi-shack");
        health.Reachable.Should().BeTrue();
        health.BridgeCount.Should().Be(2);
        health.Bridges.Should().HaveCount(2);
        health.Bridges[0].Should().Be(new HeadEndBridgeHealth("nino0", 8001, ClientConnected: true));
        health.Bridges[1].Should().Be(new HeadEndBridgeHealth("tait0", 8002, ClientConnected: false));
        health.LastSeen.Should().Be(T0);
        health.ConsecutiveFailures.Should().Be(0);
        health.PollFailuresTotal.Should().Be(0);
        handler.StatusFetches.Should().Be(1);
    }

    [Fact]
    public async Task Pre_statusz_daemon_falls_back_to_healthz_reachable_with_unknown_bridge_shape()
    {
        // Status stays null → the stub 404s /statusz exactly like a ≤0.1.3 daemon; /healthz is 200.
        var handler = new StubHeadEndHandler(new HeadEndInventory(), healthy: true);
        var monitor = Monitor(ConfigWith(new HeadEndConfig { Id = "old-pi", Address = "pi.test:7300" }),
            ClientOver(handler));

        await monitor.PollOnceAsync(CancellationToken.None);

        var health = monitor.Snapshot().Should().ContainSingle().Subject;
        health.Reachable.Should().BeTrue("healthz answered even though statusz 404'd");
        health.BridgeCount.Should().BeNull("a pre-statusz daemon can't report its bridge shape");
        health.Bridges.Should().BeEmpty();
        health.LastSeen.Should().Be(T0);
    }

    [Fact]
    public async Task Unreachable_polls_count_failures_and_keep_the_last_seen_of_the_last_success()
    {
        var reachable = true;
        var good = new StubHeadEndHandler(new HeadEndInventory()) { Status = () => Status("pi-shack") };
        Func<Uri, HeadEndClient> factory = uri => new HeadEndClient(
            uri, new HttpClient(reachable ? good : new UnreachableHandler()));
        var clock = new FakeTimeProvider(T0);
        var monitor = Monitor(ConfigWith(new HeadEndConfig { Id = "pi-shack", Address = "pi.test:7300" }),
            factory, clock);

        await monitor.PollOnceAsync(CancellationToken.None);            // up
        reachable = false;
        clock.Advance(TimeSpan.FromSeconds(30));
        await monitor.PollOnceAsync(CancellationToken.None);            // down 1
        clock.Advance(TimeSpan.FromSeconds(30));
        await monitor.PollOnceAsync(CancellationToken.None);            // down 2

        var health = monitor.Snapshot().Should().ContainSingle().Subject;
        health.Reachable.Should().BeFalse();
        health.ConsecutiveFailures.Should().Be(2);
        health.PollFailuresTotal.Should().Be(2);
        health.LastSeen.Should().Be(T0, "last-seen marks the last SUCCESSFUL poll, not the last attempt");
        // Bridge shape is last-known-good across the outage (BridgeCount says what was there).
        health.BridgeCount.Should().Be(0);
    }

    [Fact]
    public async Task Recovery_resets_the_consecutive_streak_but_not_the_total()
    {
        var reachable = false;
        var good = new StubHeadEndHandler(new HeadEndInventory()) { Status = () => Status("pi-shack") };
        Func<Uri, HeadEndClient> factory = uri => new HeadEndClient(
            uri, new HttpClient(reachable ? good : new UnreachableHandler()));
        var monitor = Monitor(ConfigWith(new HeadEndConfig { Id = "pi-shack", Address = "pi.test:7300" }), factory);

        await monitor.PollOnceAsync(CancellationToken.None);            // down
        await monitor.PollOnceAsync(CancellationToken.None);            // down
        reachable = true;
        await monitor.PollOnceAsync(CancellationToken.None);            // recovered

        var health = monitor.Snapshot().Should().ContainSingle().Subject;
        health.Reachable.Should().BeTrue();
        health.ConsecutiveFailures.Should().Be(0);
        health.PollFailuresTotal.Should().Be(2, "the counter is cumulative — rate() needs it monotonic");
    }

    [Fact]
    public async Task Transitions_are_logged_once_never_per_poll()
    {
        var reachable = true;
        var good = new StubHeadEndHandler(new HeadEndInventory()) { Status = () => Status("pi-shack") };
        Func<Uri, HeadEndClient> factory = uri => new HeadEndClient(
            uri, new HttpClient(reachable ? good : new UnreachableHandler()));
        var loggers = new CapturingLoggerFactory();
        var monitor = Monitor(ConfigWith(new HeadEndConfig { Id = "pi-shack", Address = "pi.test:7300" }),
            factory, loggers: loggers);

        await monitor.PollOnceAsync(CancellationToken.None);            // up (first poll: no log)
        loggers.Entries.Should().BeEmpty("a healthy first poll is the quiet happy path");

        reachable = false;
        await monitor.PollOnceAsync(CancellationToken.None);            // up → down: ONE warning
        await monitor.PollOnceAsync(CancellationToken.None);            // still down: silent
        await monitor.PollOnceAsync(CancellationToken.None);            // still down: silent

        var warnings = loggers.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle("one WARNING per outage, not one per 30s poll")
            .Which.Message.Should().Contain("pi-shack");

        reachable = true;
        await monitor.PollOnceAsync(CancellationToken.None);            // down → up: one recovery info
        await monitor.PollOnceAsync(CancellationToken.None);            // still up: silent

        loggers.Entries.Where(e => e.Level == LogLevel.Warning).Should().HaveCount(1);
        loggers.Entries.Where(e => e.Level == LogLevel.Information).Should().ContainSingle()
            .Which.Message.Should().Contain("reachable again");
    }

    [Fact]
    public async Task First_poll_of_a_down_head_end_warns_once()
    {
        var loggers = new CapturingLoggerFactory();
        var monitor = Monitor(ConfigWith(new HeadEndConfig { Id = "pi-shack", Address = "pi.test:7300" }),
            uri => new HeadEndClient(uri, new HttpClient(new UnreachableHandler())), loggers: loggers);

        await monitor.PollOnceAsync(CancellationToken.None);
        await monitor.PollOnceAsync(CancellationToken.None);

        loggers.Entries.Where(e => e.Level == LogLevel.Warning)
            .Should().ContainSingle("unknown → unreachable is a transition too — a fleet down at node start warrants its warning, once");
    }

    [Fact]
    public async Task No_head_ends_means_no_polls_no_browses_and_an_empty_snapshot()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory()) { Status = () => Status("x") };
        var discovery = new FakeHeadEndDiscovery();
        var monitor = Monitor(ConfigWith(), ClientOver(handler), discovery: discovery);

        await monitor.PollOnceAsync(CancellationToken.None);

        monitor.Snapshot().Should().BeEmpty();
        handler.StatusFetches.Should().Be(0, "the self-gate must not dial anything");
        discovery.Browses.Should().Be(0, "the self-gate must not multicast either");
    }

    [Fact]
    public async Task Config_pinned_address_never_browses_blank_address_resolves_via_mdns()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory()) { Status = () => Status("pi-attic") };
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-attic", "attic.local", 7300));

        // Pinned: no browse at all.
        var pinned = Monitor(ConfigWith(new HeadEndConfig { Id = "pi-attic", Address = "attic.test:7300" }),
            ClientOver(handler), discovery: discovery);
        await pinned.PollOnceAsync(CancellationToken.None);
        discovery.Browses.Should().Be(0, "an operator-pinned address is used directly");
        pinned.Snapshot().Should().ContainSingle().Which.Reachable.Should().BeTrue();

        // Blank address: one browse per cycle resolves it.
        var discovered = Monitor(ConfigWith(new HeadEndConfig { Id = "pi-attic" }),
            ClientOver(handler), discovery: discovery);
        await discovered.PollOnceAsync(CancellationToken.None);
        discovery.Browses.Should().Be(1);
        discovered.Snapshot().Should().ContainSingle().Which.Reachable.Should().BeTrue();
    }

    [Fact]
    public async Task Unresolvable_address_counts_as_an_unreachable_poll()
    {
        // Declared with a blank address and nothing discovered — the poller can't even dial.
        var handler = new StubHeadEndHandler(new HeadEndInventory()) { Status = () => Status("ghost") };
        var monitor = Monitor(ConfigWith(new HeadEndConfig { Id = "ghost" }), ClientOver(handler));

        await monitor.PollOnceAsync(CancellationToken.None);

        var health = monitor.Snapshot().Should().ContainSingle().Subject;
        health.Reachable.Should().BeFalse();
        health.PollFailuresTotal.Should().Be(1);
        handler.StatusFetches.Should().Be(0);
    }

    [Fact]
    public async Task Port_referenced_head_ends_are_monitored_even_without_a_declaration()
    {
        // The union rule: a port binding (nino-tnc-tcp transport / head-end-bound radio) is enough.
        var handler = new StubHeadEndHandler(new HeadEndInventory()) { Status = () => Status("pi-shack") };
        var discovery = new FakeHeadEndDiscovery(new DiscoveredHeadEnd("pi-shack", "shack.local", 7300));
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-7" },
            Ports =
            [
                new PortConfig
                {
                    Id = "vhf",
                    Transport = new NinoTncTcpTransport { HeadEndId = "pi-shack", DeviceId = "nino0" },
                },
            ],
        };
        var monitor = Monitor(config, ClientOver(handler), discovery: discovery);

        await monitor.PollOnceAsync(CancellationToken.None);

        monitor.Snapshot().Should().ContainSingle().Which.InstanceId.Should().Be("pi-shack");
    }

    [Fact]
    public async Task An_instance_removed_from_config_is_pruned_from_the_snapshot()
    {
        var handler = new StubHeadEndHandler(new HeadEndInventory()) { Status = () => Status("pi-shack") };
        var provider = new TestConfigProvider(
            ConfigWith(new HeadEndConfig { Id = "pi-shack", Address = "pi.test:7300" }));
        var monitor = new HeadEndHealthMonitor(provider, new FakeHeadEndDiscovery(),
            ClientOver(handler), new FakeTimeProvider(T0));

        await monitor.PollOnceAsync(CancellationToken.None);
        monitor.Snapshot().Should().ContainSingle();

        provider.Apply(ConfigWith());   // operator removed the head-end
        await monitor.PollOnceAsync(CancellationToken.None);

        monitor.Snapshot().Should().BeEmpty("stale instances must stop emitting metrics rows");
    }

    /// <summary>Captures every log entry from any category — the transition-only logging assertions.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public sealed record Entry(LogLevel Level, EventId EventId, string Message);

        private readonly List<Entry> entries = [];
        private readonly object gate = new();

        public IReadOnlyList<Entry> Entries { get { lock (gate) { return entries.ToList(); } } }

        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(CapturingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.gate)
                {
                    owner.entries.Add(new Entry(logLevel, eventId, formatter(state, exception)));
                }
            }
        }
    }
}
