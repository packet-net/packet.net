using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Telemetry;
using Packet.Node.Core.Traffic;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Traffic;

/// <summary>
/// The traffic-log writer: frames traced through <see cref="NodeTelemetry"/> land
/// in the store (the no-second-decode-path contract), the bounded hand-off queue
/// drops + counts on overflow without ever blocking, and the
/// <see cref="TimeProvider"/>-driven prune enforces the live-config retention.
/// </summary>
[Trait("Category", "Node")]
public sealed class TrafficLogServiceTests : IDisposable
{
    private static readonly Callsign Local = Callsign.Parse("M0LTE-1");
    private static readonly Callsign Peer = Callsign.Parse("G7XYZ-2");

    private readonly string dir;
    private readonly string dbPath;

    public TrafficLogServiceTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-trafficsvc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "traffic.db");
    }

    private static TestConfigProvider Config(int retentionDays = 14, int maxMb = 512)
        => new(new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Traffic = new TrafficConfig { RetentionDays = retentionDays, MaxMb = maxMb },
        });

    private static MonitorEvent Evt(long seq) => new(
        Seq: seq,
        Timestamp: new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero),
        PortId: "vhf",
        Direction: "in",
        Source: "M0LTE-1",
        Dest: "G7XYZ-2",
        Type: "I",
        ClassKind: "I",
        Pid: "0xF0",
        PidName: "No layer 3",
        Ns: 0,
        Nr: 0,
        Pf: 0,
        Command: true,
        Length: 20,
        Summary: "I",
        Raw: new int[20],
        Path: [])
    {
        Control = 0x00,
        InfoLength = 3,
    };

    private static Ax25FrameEventArgs Rx(Ax25Frame frame, DateTimeOffset at)
        => new() { Frame = frame, Direction = FrameDirection.Received, Timestamp = at };

    private static Ax25FrameEventArgs Tx(Ax25Frame frame, DateTimeOffset at)
        => new() { Frame = frame, Direction = FrameDirection.Transmitted, Timestamp = at };

    [Fact]
    public void Overflow_drops_are_counted_and_enqueue_never_blocks()
    {
        // The service is deliberately NOT started: nothing drains the queue, so it
        // models the worst case — a wedged writer (stalled disk). Capacity 4.
        var service = new TrafficLogService(
            new NodeTelemetry(), new SqliteTrafficStore(dbPath), Config(),
            new FakeTimeProvider(), queueCapacity: 4);

        for (int i = 0; i < 4; i++)
        {
            service.TryEnqueue(Evt(i)).Should().BeTrue("the queue holds {0} frames", 4);
        }
        for (int i = 4; i < 10; i++)
        {
            // Returns (false) immediately rather than ever waiting on the writer —
            // TryWrite on a bounded channel is non-blocking by construction.
            service.TryEnqueue(Evt(i)).Should().BeFalse("frame {0} overflows the full queue", i);
        }

        service.DroppedFrames.Should().Be(6, "every overflowed frame is counted, none is silently lost");
    }

    [Fact]
    public async Task Frames_traced_through_telemetry_are_persisted()
    {
        var telemetry = new NodeTelemetry();
        var store = new SqliteTrafficStore(dbPath);
        var service = new TrafficLogService(telemetry, store, Config(), TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // TrafficLogService is a BackgroundService whose ExecuteAsync does
            // `await Task.Yield()` before it calls telemetry.Subscribe(...), so the
            // base StartAsync returns to us at that yield — BEFORE the subscription is
            // live. NodeTelemetry has no backlog, so a frame observed in that window is
            // delivered to no subscriber and never reaches the store (the 30s "both
            // traced frames reach the store" timeout — surfaced under CI CPU
            // contention, which delays the post-yield continuation). Gate the Observe
            // calls on the service actually being subscribed so the sequence is
            // deterministic.
            await Wait.ForAsync(() => telemetry.SubscriberCount > 0,
                "the traffic-log writer has subscribed to telemetry");

            var at = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
            telemetry.Observe("vhf", Rx(Ax25Frame.I(Local, Peer, nr: 0, ns: 0, "ab"u8), at));
            telemetry.Observe("vhf", Tx(Ax25Frame.Rr(Peer, Local, nr: 1, isCommand: false), at.AddSeconds(1)));

            await Wait.ForAsync(() => store.Count() == 2, "both traced frames reach the store");

            var rows = store.Query(null, null, null, 10);
            rows.Should().HaveCount(2);
            rows[0].Direction.Should().Be("tx", "newest first — the RR was transmitted");
            rows[0].Kind.Should().Be("RR");
            rows[0].Nr.Should().Be(1);
            rows[1].Direction.Should().Be("rx");
            rows[1].Kind.Should().Be("I");
            rows[1].Source.Should().Be(Peer.ToString(), "the I-frame came from the peer");
            rows[1].InfoLength.Should().Be(2);
            rows[1].Raw.Should().NotBeEmpty("the raw frame bytes are persisted");

            service.DroppedFrames.Should().Be(0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Prune_enforces_the_live_config_retention_from_the_clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));
        var store = new SqliteTrafficStore(dbPath);
        var config = Config(retentionDays: 14);
        var service = new TrafficLogService(new NodeTelemetry(), store, config, clock);

        var now = clock.GetUtcNow();
        store.Append(
        [
            TrafficRecord.From(Evt(1) with { Timestamp = now.AddDays(-15) }),   // past retention
            TrafficRecord.From(Evt(2) with { Timestamp = now.AddDays(-1) }),    // inside retention
        ]).Should().BeTrue();

        service.PruneNow();

        var rows = store.Query(null, null, null, 10);
        rows.Should().ContainSingle("the 15-day-old row is past the 14-day retention")
            .Which.Timestamp.Should().Be(now.AddDays(-1));

        // Tightening retention is a hot edit: the next prune reads the LIVE config.
        config.Apply(config.Current with { Traffic = new TrafficConfig { RetentionDays = 1, MaxMb = 512 } });
        clock.Advance(TimeSpan.FromHours(25));
        service.PruneNow();
        store.Count().Should().Be(0, "the surviving row aged past the tightened retention");
    }

    [Fact]
    public async Task The_periodic_timer_drives_the_prune()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));
        var store = new SqliteTrafficStore(dbPath);
        var service = new TrafficLogService(new NodeTelemetry(), store, Config(retentionDays: 14), clock);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Seed AFTER start so the startup prune pass can't be the one that
            // removes it. The row is already past retention.
            store.Append([TrafficRecord.From(Evt(1) with { Timestamp = clock.GetUtcNow().AddDays(-30) })])
                .Should().BeTrue();
            store.Count().Should().Be(1);

            // ExecuteAsync arms the timer on a background continuation, so advance
            // inside the poll until the (5-minute-period) tick has fired it.
            await Wait.ForAsync(
                () =>
                {
                    clock.Advance(TimeSpan.FromMinutes(5));
                    return store.Count() == 0;
                },
                "the periodic prune removes the stale row");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
