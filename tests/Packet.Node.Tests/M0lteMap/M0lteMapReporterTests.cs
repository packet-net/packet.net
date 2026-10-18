using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.M0lteMap;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.M0lteMap;

/// <summary>
/// The UK packet map reporter's policy (#453): off by default and gated by the switch, LinBPQ's
/// cadence (two minutes, then every fifteen), retry with backoff on a failed delivery, no retry
/// on a refusal, nothing sent without a locator, and a quiet log through a collector outage.
/// Driven one <c>TickAsync</c> at a time against a fake clock; no background loop, no network.
/// </summary>
public sealed class M0lteMapReporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeState : IM0lteMapStateSource
    {
        public bool Throw { get; set; }

        public M0lteMapSnapshot Capture() => Throw
            ? throw new InvalidOperationException("supervisor gone")
            : new M0lteMapSnapshot { ServingPortIds = ["vhf"], Neighbours = [new("GB7AAA", "vhf", 192)] };
    }

    private sealed class FakeClient : IM0lteMapClient
    {
        public List<(string Call, M0lteMapNodeData Report, DateTimeOffset At)> Posts { get; } = [];
        public Queue<M0lteMapPostResult> Answers { get; } = new();
        public required FakeTimeProvider Clock { get; init; }

        public Task<M0lteMapPostResult> PostAsync(string nodeCall, M0lteMapNodeData report, CancellationToken cancellationToken = default)
        {
            Posts.Add((nodeCall, report, Clock.GetUtcNow()));
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : new M0lteMapPostResult(M0lteMapPostOutcome.Accepted, 200, null));
        }
    }

    private sealed class Harness
    {
        public FakeTimeProvider Clock { get; } = new(T0);
        public TestConfigProvider Config { get; }
        public FakeState State { get; } = new();
        public FakeClient Client { get; }
        public CapturingLogger<M0lteMapReporter> Log { get; } = new();
        public M0lteMapReporter Reporter { get; }

        public Harness(bool enabled, string? grid = "IO91wm")
        {
            Config = new TestConfigProvider(ConfigWith(enabled, grid));
            Client = new FakeClient { Clock = Clock };
            Reporter = new M0lteMapReporter(Config, State, Client, Clock, "0.68.0", Log);
        }

        /// <summary>Run the reporter's own loop on the fake clock for <paramref name="span"/>:
        /// tick, sleep for what the tick asked, repeat.</summary>
        public async Task RunFor(TimeSpan span)
        {
            var end = Clock.GetUtcNow() + span;
            while (true)
            {
                var wait = await Reporter.TickAsync(CancellationToken.None);
                wait.Should().BeLessThanOrEqualTo(M0lteMapReporter.ConfigCheckInterval);
                if (Clock.GetUtcNow() + wait > end)
                {
                    Clock.SetUtcNow(end);
                    return;
                }
                Clock.Advance(wait <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : wait);
            }
        }

        public IEnumerable<TimeSpan> PostTimes => Client.Posts.Select(p => p.At - T0);
    }

    private static NodeConfig ConfigWith(bool enabled, string? grid) => new()
    {
        Identity = new Identity { Callsign = "GB7RDG", Alias = "READNG", Grid = grid },
        M0lteMap = new M0lteMapConfig { Enabled = enabled },
        Ports = [new PortConfig { Id = "vhf", Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8001 } }],
    };

    private static M0lteMapPostResult Down => new(M0lteMapPostOutcome.TransportError, null, "Connection refused");

    [Fact]
    public async Task Off_by_default_nothing_is_ever_sent()
    {
        new NodeConfig { Identity = new Identity { Callsign = "GB7RDG" } }.M0lteMap.Enabled.Should().BeFalse();

        var h = new Harness(enabled: false);
        await h.RunFor(TimeSpan.FromHours(2));

        h.Client.Posts.Should().BeEmpty();
        h.Log.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task First_report_two_minutes_after_starting_then_every_fifteen_like_LinBpq()
    {
        var h = new Harness(enabled: true);

        await h.RunFor(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(59));
        h.Client.Posts.Should().BeEmpty();

        await h.RunFor(TimeSpan.FromMinutes(46));

        h.PostTimes.Should().Equal(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(17), TimeSpan.FromMinutes(32), TimeSpan.FromMinutes(47));
        h.Client.Posts.Should().AllSatisfy(p => p.Call.Should().Be("GB7RDG"));
        h.Client.Posts[0].Report.Ports.Should().ContainSingle();
        h.Client.Posts[0].Report.Neighbours!.Single().Node.Should().Be("GB7AAA");
    }

    [Fact]
    public async Task Turning_it_on_live_starts_reporting_and_turning_it_off_stops_it_within_the_config_check()
    {
        var h = new Harness(enabled: false);
        await h.RunFor(TimeSpan.FromMinutes(10));

        h.Config.Apply(ConfigWith(enabled: true, "IO91wm"));
        await h.RunFor(TimeSpan.FromMinutes(20));
        h.Client.Posts.Should().HaveCount(2);
        var lastPost = h.Client.Posts[^1].At;

        h.Config.Apply(ConfigWith(enabled: false, "IO91wm"));
        await h.RunFor(TimeSpan.FromHours(1));

        h.Client.Posts.Should().HaveCount(2);
        h.Client.Posts[^1].At.Should().Be(lastPost);
        h.Reporter.NextDue.Should().BeNull();
        h.Log.Has(LogLevel.Information, "UK packet map reporting off.").Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_delivery_is_retried_after_1_2_4_and_8_minutes_then_at_the_normal_interval()
    {
        var h = new Harness(enabled: true);
        for (var i = 0; i < 6; i++)
        {
            h.Client.Answers.Enqueue(Down);
        }

        await h.RunFor(TimeSpan.FromMinutes(2 + 1 + 2 + 4 + 8 + 15 + 15) + TimeSpan.FromSeconds(1));

        // 2: the first try; then +1, +2, +4 and +8; after that every +15 until one gets through.
        h.PostTimes.Should().Equal(
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(9),
            TimeSpan.FromMinutes(17), TimeSpan.FromMinutes(32), TimeSpan.FromMinutes(47));
    }

    [Fact]
    public async Task A_collector_outage_is_one_warning_and_one_recovery_line_not_a_flood()
    {
        var h = new Harness(enabled: true);
        for (var i = 0; i < 5; i++)
        {
            h.Client.Answers.Enqueue(Down);
        }

        await h.RunFor(TimeSpan.FromHours(2));

        h.Log.Warnings.Should().ContainSingle().Which.Should().Contain("did not get through (Connection refused)");
        h.Log.Render(LogLevel.Debug).Count(l => l.Contains("did not get through", StringComparison.Ordinal)).Should().Be(4);
        h.Log.Render(LogLevel.Information).Count(l => l.Contains("reported GB7RDG", StringComparison.Ordinal)).Should().Be(1);
        h.Log.Render(LogLevel.Debug).Count(l => l.Contains("reported GB7RDG", StringComparison.Ordinal)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_refused_report_is_not_retried_early_and_is_logged_once()
    {
        var h = new Harness(enabled: true);
        h.Client.Answers.Enqueue(new M0lteMapPostResult(M0lteMapPostOutcome.Rejected, 400, "bad locator"));
        h.Client.Answers.Enqueue(new M0lteMapPostResult(M0lteMapPostOutcome.Rejected, 400, "bad locator"));

        await h.RunFor(TimeSpan.FromMinutes(33));

        h.PostTimes.Should().Equal(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(17), TimeSpan.FromMinutes(32));
        h.Log.Warnings.Should().ContainSingle().Which.Should().Contain("refused the report (HTTP 400: bad locator)");
        h.Log.Has(LogLevel.Information, "reported GB7RDG").Should().BeTrue();
    }

    [Fact]
    public async Task Without_a_locator_nothing_is_sent_and_setting_one_is_picked_up_within_the_config_check()
    {
        var h = new Harness(enabled: true, grid: null);

        await h.RunFor(TimeSpan.FromMinutes(40));
        h.Client.Posts.Should().BeEmpty();
        h.Log.Warnings.Should().ContainSingle().Which.Should().Contain("identity.grid");

        var setAt = h.Clock.GetUtcNow();
        h.Config.Apply(ConfigWith(enabled: true, "IO91wm"));
        await h.RunFor(TimeSpan.FromMinutes(1));

        h.Client.Posts.Should().ContainSingle();
        (h.Client.Posts[0].At - setAt).Should().BeLessThanOrEqualTo(M0lteMapReporter.ConfigCheckInterval);
    }

    [Fact]
    public async Task A_node_state_fault_skips_the_report_rather_than_telling_the_map_there_are_no_ports()
    {
        var h = new Harness(enabled: true);
        h.State.Throw = true;

        await h.RunFor(TimeSpan.FromMinutes(5));
        h.Client.Posts.Should().BeEmpty();

        h.State.Throw = false;
        await h.RunFor(TimeSpan.FromMinutes(1));
        h.Client.Posts.Should().ContainSingle().Which.Report.Ports.Should().ContainSingle();
    }

    [Fact]
    public async Task The_background_loop_reports_on_its_own_and_stops_cleanly()
    {
        var h = new Harness(enabled: true);
        var reported = new TaskCompletionSource();
        var client = new SignallingClient(reported);
        var reporter = new M0lteMapReporter(h.Config, h.State, client, h.Clock, "0.68.0", h.Log);

        await reporter.StartAsync(CancellationToken.None);
        // The loop's first tick arms a 30 s wait (the first report is 2 minutes out); step the clock
        // until the report lands, then stop.
        for (var i = 0; i < 100 && !reported.Task.IsCompleted; i++)
        {
            await Task.Yield();
            h.Clock.Advance(TimeSpan.FromSeconds(10));
            await Task.WhenAny(reported.Task, Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System));
        }
        await reporter.StopAsync(CancellationToken.None);

        reported.Task.IsCompleted.Should().BeTrue();
    }

    private sealed class SignallingClient(TaskCompletionSource reported) : IM0lteMapClient
    {
        public Task<M0lteMapPostResult> PostAsync(string nodeCall, M0lteMapNodeData report, CancellationToken cancellationToken = default)
        {
            reported.TrySetResult();
            return Task.FromResult(new M0lteMapPostResult(M0lteMapPostOutcome.Accepted, 200, null));
        }
    }
}
