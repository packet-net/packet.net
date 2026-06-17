using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.Heard;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Console;

/// <summary>
/// Dispatch tests for the MHeard <c>MH</c> console verb (#454): drive the real
/// <see cref="NodeCommandService"/> over a scripted connection against a real <see cref="HeardLog"/>
/// seeded via <see cref="HeardLog.Record"/>. They lock the output formatting, the node-wide vs
/// per-port behaviour, and the not-available + nothing-heard messages. Read-only — never demands
/// elevation.
/// </summary>
[Trait("Category", "Node")]
public sealed class MhConsoleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider clock = new(T0);

    private NodeCommandService BuildService(HeardLog? log)
    {
        var config = new TestConfigProvider(new NodeConfig
        {
            Identity = new Identity { Callsign = "M9YYY", Alias = "PDN" },
            Ports = [],
        });
        var env = new NodeConsoleEnvironment(
            config, outboundConnector: null, netRom: null, sysop: null,
            applications: null, connectRouter: null, capabilities: null, heard: log);
        return new NodeCommandService(env, NullLogger<NodeCommandService>.Instance, clock);
    }

    private HeardLog SeededLog()
    {
        var log = new HeardLog(store: null, time: clock);
        // M0LTE-1 heard on two ports (node-wide merge → ports 2); G0ABC on vhf only.
        log.Record("vhf", "M0LTE-1", T0.AddMinutes(-10));
        log.Record("vhf", "M0LTE-1", T0.AddMinutes(-5));
        log.Record("hf", "M0LTE-1", T0.AddMinutes(-2));
        log.Record("vhf", "G0ABC", T0.AddMinutes(-1));
        clock.SetUtcNow(T0);   // so "ago" is measured from a fixed now
        return log;
    }

    [Fact]
    public async Task Mh_lists_the_node_wide_heard_stations()
    {
        var svc = BuildService(SeededLog());
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, ["MH", "B"]);

        await svc.RunAsync(conn);

        conn.Text.Should().Contain("Heard (node-wide):");
        conn.Text.Should().Contain("M0LTE-1");
        conn.Text.Should().Contain("G0ABC");
        // M0LTE-1 was heard on 2 ports with a total count of 3 — the node-wide merge.
        conn.Text.Should().MatchRegex(@"M0LTE-1\s+\S+\s+3\s+2");
        conn.Text.Should().NotContain("Not authorised");   // read-only, no elevation
    }

    [Fact]
    public async Task Mh_port_lists_only_that_ports_stations()
    {
        var svc = BuildService(SeededLog());
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, ["MH hf", "B"]);

        await svc.RunAsync(conn);

        conn.Text.Should().Contain("Heard on hf:");
        conn.Text.Should().Contain("M0LTE-1");
        // G0ABC was only on vhf, so the hf view omits it.
        conn.Text.Should().NotContain("G0ABC");
    }

    [Fact]
    public async Task Mh_with_no_heard_log_reports_unavailable()
    {
        var svc = BuildService(log: null);
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, ["MH", "B"]);

        await svc.RunAsync(conn);

        conn.Text.Should().Contain("heard log not available");
    }

    [Fact]
    public async Task Mh_when_nothing_heard_says_so()
    {
        var svc = BuildService(new HeardLog(store: null, time: clock));
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, ["MH", "MH vhf", "B"]);

        await svc.RunAsync(conn);

        conn.Text.Should().Contain("nothing heard yet");
        conn.Text.Should().Contain("MHeard on vhf: (nothing heard yet)");
    }

    [Fact]
    public async Task Mh_appears_in_the_help()
    {
        var svc = BuildService(SeededLog());
        var conn = new ScriptedConnection("M0LTE-7", NodeTransportKind.Ax25, ["H", "B"]);

        await svc.RunAsync(conn);

        conn.Text.Should().Contain("MH [port]");
    }

    // Drives the command loop: each scripted line as its own CR-terminated read, then EOF.
    private sealed class ScriptedConnection(string peerId, NodeTransportKind kind, string[] lines)
        : INodeConnection
    {
        private readonly StringBuilder output = new();
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int read;

        public string Text => output.ToString();
        public string PeerId => peerId;
        public NodeTransportKind TransportKind => kind;
        public Task Completion => completion.Task;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (read >= lines.Length)
            {
                completion.TrySetResult();
                return new ValueTask<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);
            }
            var bytes = Encoding.UTF8.GetBytes(lines[read] + "\r");
            read++;
            return new ValueTask<ReadOnlyMemory<byte>>(bytes);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            output.Append(Encoding.UTF8.GetString(bytes.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
