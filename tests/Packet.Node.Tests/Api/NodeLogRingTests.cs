using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Api;
using Packet.Node.Core.Api;

namespace Packet.Node.Tests.Api;

/// <summary>
/// The in-memory log tail behind <c>GET /api/v1/log</c> (review item C008, #694). The endpoint
/// was a permanent empty array - nothing in the node ever produced a <see cref="LogLine"/>  - 
/// while the OpenAPI doc and the dashboard's "Recent activity" card presented it as live.
/// </summary>
[Trait("Category", "Node")]
public sealed class NodeLogRingTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_tail_is_newest_first()
    {
        var ring = new NodeLogRing();
        ring.Add(new LogLine("12:00:00", "info", "first"));
        ring.Add(new LogLine("12:00:01", "info", "second"));
        ring.Add(new LogLine("12:00:02", "warn", "third"));

        ring.Recent(10).Select(l => l.Msg).Should().Equal("third", "second", "first");
    }

    [Fact]
    public void The_limit_takes_the_most_recent_lines()
    {
        var ring = new NodeLogRing();
        for (int i = 0; i < 10; i++)
        {
            ring.Add(new LogLine("12:00:00", "info", $"line {i}"));
        }

        ring.Recent(3).Select(l => l.Msg).Should().Equal("line 9", "line 8", "line 7");
    }

    [Fact]
    public void The_ring_is_bounded_so_a_long_uptime_cannot_grow_memory()
    {
        var ring = new NodeLogRing();
        for (int i = 0; i < NodeLogRing.Capacity * 3; i++)
        {
            ring.Add(new LogLine("12:00:00", "info", $"line {i}"));
        }

        var all = ring.Recent(NodeLogRing.MaxTail);
        all.Should().HaveCount(NodeLogRing.Capacity);
        all[0].Msg.Should().Be($"line {(NodeLogRing.Capacity * 3) - 1}", "the newest line survives");
    }

    private static void Emit(ILogger logger, LogLevel level, string message, Exception? exception)
        => logger.Log(level, new EventId(1), message, exception, static (state, _) => state);

    [Fact]
    public void The_logger_provider_fills_the_ring_with_the_severity_the_ui_understands()
    {
        var ring = new NodeLogRing();
        var clock = new FakeTimeProvider(Noon);
        using var provider = new NodeLogRingProvider(ring, clock);
        var logger = provider.CreateLogger("Packet.Node.Core.Hosting.PortSupervisor");

        // The raw ILogger.Log (not the LoggerExtensions helpers) keeps CA1848 happy and is
        // exactly what a LoggerMessage-generated call does anyway.
        Emit(logger, LogLevel.Information, "port vhf up", null);
        Emit(logger, LogLevel.Warning, "port vhf degraded", null);
        Emit(logger, LogLevel.Error, "port vhf faulted", new InvalidOperationException("no device"));

        var lines = ring.Recent(10);
        lines.Select(l => l.Lvl).Should().Equal("error", "warn", "info");
        lines[2].T.Should().Be("12:00:00", "the timestamp rides the injected clock");
        lines[2].Msg.Should().Be("PortSupervisor: port vhf up", "the short category prefixes the message");
        lines[0].Msg.Should().Contain("InvalidOperationException").And.Contain("no device");
    }
}
