using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Oarc;
using Packet.Node.Core.Telemetry;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Oarc;

/// <summary>
/// Behavioural tests for the <see cref="OarcReporter"/> (#459): the policy engine driven against a
/// fake <see cref="IOarcStateSource"/> and a capturing <see cref="IOarcIngestClient"/>, with a
/// <see cref="FakeTimeProvider"/> and a real <see cref="NodeTelemetry"/> for the trace stream. Covers
/// the master edge (up/down), the per-category toggles, the link/circuit diff + direction mapping,
/// node-status cadence + counts, the locator precondition, position, the trace dialect, and the
/// sender's retry/reject handling.
/// </summary>
public sealed class OarcReporterTests
{
    private static readonly Callsign Local = Callsign.Parse("M0LTE-1");
    private static readonly Callsign Peer = Callsign.Parse("G7XYZ-2");
    private static readonly DateTimeOffset T0 = new(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);

    // ── Harness ──────────────────────────────────────────────────────────

    private sealed class FakeStateSource : IOarcStateSource
    {
        private volatile OarcNodeSnapshot snapshot = new() { UptimeSeconds = 0 };
        public void Set(OarcNodeSnapshot s) => snapshot = s;
        public OarcNodeSnapshot Capture() => snapshot;
    }

    private sealed class CapturingClient : IOarcIngestClient
    {
        private readonly object gate = new();
        private readonly List<OarcEvent> posted = new();
        private int calls;

        /// <summary>(callIndex, event) → result. Null ⇒ always Accepted.</summary>
        public Func<int, OarcEvent, OarcIngestResult>? Responder { get; set; }

        public Task<OarcIngestResult> ReportAsync(OarcEvent ev, string baseUrl, CancellationToken ct = default)
        {
            var idx = Interlocked.Increment(ref calls) - 1;
            var r = Responder?.Invoke(idx, ev) ?? new OarcIngestResult(OarcIngestOutcome.Accepted, 202, null);
            lock (gate) { posted.Add(ev); }
            return Task.FromResult(r);
        }

        public IReadOnlyList<OarcEvent> Posted { get { lock (gate) return posted.ToList(); } }
        public int CountOf<T>() { lock (gate) return posted.OfType<T>().Count(); }
        public T? Last<T>() where T : class { lock (gate) return posted.OfType<T>().LastOrDefault(); }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly object gate = new();
        public List<(LogLevel Level, string Text)> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        { lock (gate) Messages.Add((level, fmt(state, ex))); }
        public bool Has(LogLevel level, string contains)
        { lock (gate) return Messages.Any(m => m.Level == level && m.Text.Contains(contains)); }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    private static NodeConfig Cfg(OarcConfig oarc, string? grid = "IO91wm", string call = "M0LTE-1") =>
        new() { Identity = new Identity { Callsign = call, Alias = "TESTND", Grid = grid }, Oarc = oarc };

    private static OarcLinkState Link(int id, bool inbound, string remote = "G7XYZ-2", string port = "vhf") => new()
    {
        Id = id, Port = port, Local = "M0LTE-1", Remote = remote, Inbound = inbound,
        FramesSent = 5, FramesReceived = 7, BytesSent = 100, BytesReceived = 200, UpForSeconds = 30,
    };

    private static OarcCircuitState Circuit(int id, bool inbound, string remote = "GB7RDG") => new()
    {
        Id = id, Local = "M0LTE-1", Remote = remote, Inbound = inbound, UpForSeconds = 12,
    };

    private sealed record Harness(
        OarcReporter Reporter, TestConfigProvider Config, FakeStateSource State,
        CapturingClient Client, NodeTelemetry Telemetry, FakeTimeProvider Clock,
        CapturingLogger<OarcReporter> Logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await Reporter.StopAsync(CancellationToken.None); } catch { /* best effort */ }
        }
    }

    private static async Task<Harness> StartAsync(OarcConfig oarc, string? grid = "IO91wm")
    {
        var clock = new FakeTimeProvider(T0);
        var telemetry = new NodeTelemetry();
        var config = new TestConfigProvider(Cfg(oarc, grid));
        var client = new CapturingClient();
        var state = new FakeStateSource();
        var logger = new CapturingLogger<OarcReporter>();
        var reporter = new OarcReporter(config, state, client, telemetry, clock, "v1.2.3", logger);
        await reporter.StartAsync(CancellationToken.None);
        return new Harness(reporter, config, state, client, telemetry, clock, logger);
    }

    private static OarcConfig On() => new()
    {
        Enabled = true, ReportNodeStatus = true, ReportLinks = true, ReportCircuits = true,
        StatusIntervalSecs = 300, SessionStatusIntervalSecs = 300,
    };

    // ── Tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_reports_nothing()
    {
        await using var h = await StartAsync(new OarcConfig { Enabled = false });
        h.State.Set(new OarcNodeSnapshot { UptimeSeconds = 10, Links = [Link(1, inbound: true)] });
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50);
        h.Client.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task Enabling_announces_node_up_with_identity_and_locator()
    {
        await using var h = await StartAsync(On());
        await Wait.ForAsync(() => h.Client.CountOf<OarcNodeUpEvent>() >= 1, "node-up on enable");

        var up = h.Client.Last<OarcNodeUpEvent>()!;
        up.NodeCall.Should().Be("M0LTE-1");
        up.NodeAlias.Should().Be("TESTND");
        up.Locator.Should().Be("IO91wm");
        up.Software.Should().Be("pdn");
        up.Version.Should().Be("v1.2.3");
        up.Latitude.Should().BeNull("exact position is off by default");
    }

    [Fact]
    public async Task An_invalid_locator_suppresses_node_events_and_warns_once()
    {
        await using var h = await StartAsync(On(), grid: "not-a-grid");
        await Wait.ForAsync(() => h.Logger.Has(LogLevel.Warning, "no valid Maidenhead grid"), "warned about the missing grid");

        h.Client.CountOf<OarcNodeUpEvent>().Should().Be(0, "no node-up without a valid locator");

        // Advancing several ticks must not produce a flood of warnings (warn-once).
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50);
        h.Logger.Messages.Count(m => m.Text.Contains("no valid Maidenhead grid")).Should().Be(1);
    }

    [Fact]
    public async Task A_new_inbound_link_reports_link_up_as_incoming()
    {
        await using var h = await StartAsync(On());
        h.State.Set(new OarcNodeSnapshot { UptimeSeconds = 10, Links = [Link(1, inbound: true)] });
        await AdvanceUntil(h.Clock, () => h.Client.CountOf<OarcLinkUpEvent>() >= 1, "link-up for the new link");

        var up = h.Client.Last<OarcLinkUpEvent>()!;
        up.Id.Should().Be(1);
        up.Direction.Should().Be("incoming", "an inbound (remote-initiated) link is 'incoming'");
        up.Remote.Should().Be("G7XYZ-2");
    }

    [Fact]
    public async Task A_vanished_link_reports_link_down()
    {
        await using var h = await StartAsync(On());
        h.State.Set(new OarcNodeSnapshot { Links = [Link(1, inbound: false)] });
        await AdvanceUntil(h.Clock, () => h.Client.CountOf<OarcLinkUpEvent>() >= 1, "link-up first");

        h.State.Set(new OarcNodeSnapshot { Links = [] });   // link gone
        await AdvanceUntil(h.Clock, () => h.Client.CountOf<OarcLinkDownEvent>() >= 1, "link-down after it vanishes");
        h.Client.Last<OarcLinkDownEvent>()!.Direction.Should().Be("outgoing");
    }

    [Fact]
    public async Task Links_toggle_off_suppresses_link_events()
    {
        await using var h = await StartAsync(On() with { ReportLinks = false });
        h.State.Set(new OarcNodeSnapshot { Links = [Link(1, inbound: true)] });
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        h.Client.CountOf<OarcLinkUpEvent>().Should().Be(0, "links are not reported when the toggle is off");
    }

    [Fact]
    public async Task A_new_inbound_circuit_reports_circuit_up()
    {
        await using var h = await StartAsync(On());
        h.State.Set(new OarcNodeSnapshot { Circuits = [Circuit(3, inbound: true)] });
        await AdvanceUntil(h.Clock, () => h.Client.CountOf<OarcCircuitUpEvent>() >= 1, "circuit-up");
        h.Client.Last<OarcCircuitUpEvent>()!.Direction.Should().Be("incoming");
    }

    [Fact]
    public async Task Node_status_fires_on_cadence_with_link_counts()
    {
        await using var h = await StartAsync(On() with { StatusIntervalSecs = 1 });
        h.State.Set(new OarcNodeSnapshot
        {
            UptimeSeconds = 42, L3Relayed = 9,
            Links = [Link(1, inbound: true), Link(2, inbound: false)],
        });
        await Wait.ForAsync(() => h.Client.CountOf<OarcNodeUpEvent>() >= 1, "node-up first");
        await AdvanceUntil(h.Clock, () => h.Client.CountOf<OarcNodeStatusEvent>() >= 1, "node-status on cadence");

        var st = h.Client.Last<OarcNodeStatusEvent>()!;
        st.UptimeSecs.Should().Be(42);
        st.L3Relayed.Should().Be(9);
        st.LinksIn.Should().Be(1);
        st.LinksOut.Should().Be(1);
    }

    [Fact]
    public async Task Exact_position_publishes_grid_centre_coordinates()
    {
        await using var h = await StartAsync(On() with { PublishExactPosition = true });
        await Wait.ForAsync(() => h.Client.CountOf<OarcNodeUpEvent>() >= 1, "node-up");

        var up = h.Client.Last<OarcNodeUpEvent>()!;
        up.Latitude.Should().BeApproximately(51.52, 0.05);
        up.Longitude.Should().BeApproximately(-0.125, 0.05);
    }

    [Fact]
    public async Task Traces_off_emits_no_l2trace()
    {
        await using var h = await StartAsync(On() with { ReportTraces = false });
        await Wait.ForAsync(() => h.Client.CountOf<OarcNodeUpEvent>() >= 1, "node-up (so the reporter is live)");

        h.Telemetry.Observe("vhf", Rx(Ax25Frame.I(Local, Peer, nr: 0, ns: 0, "ab"u8)));
        await Task.Delay(50);
        h.Client.CountOf<OarcL2TraceEvent>().Should().Be(0);
    }

    [Fact]
    public async Task Traces_on_maps_an_i_frame_to_the_trace_dialect()
    {
        await using var h = await StartAsync(On() with { ReportTraces = true });
        await Wait.ForAsync(() => h.Client.CountOf<OarcNodeUpEvent>() >= 1, "node-up");

        h.Telemetry.Observe("vhf", Tx(Ax25Frame.I(Peer, Local, nr: 2, ns: 3, "abcde"u8)));
        await Wait.ForAsync(() => h.Client.CountOf<OarcL2TraceEvent>() >= 1, "l2trace for the I-frame");

        var t = h.Client.Last<OarcL2TraceEvent>()!;
        t.L2Type.Should().Be("I");
        t.Direction.Should().Be("sent", "a transmitted frame is 'sent'");
        t.TransmitSequence.Should().Be(3);
        t.ReceiveSequence.Should().Be(2);
        t.IFieldLength.Should().Be(5, "I-frames carry ilen");
        t.IsRf.Should().BeTrue();
    }

    [Fact]
    public async Task Trace_maps_sabm_to_C_and_a_supervisory_frame_carries_no_tseq()
    {
        await using var h = await StartAsync(On() with { ReportTraces = true });
        await Wait.ForAsync(() => h.Client.CountOf<OarcNodeUpEvent>() >= 1, "node-up");

        h.Telemetry.Observe("vhf", Tx(Ax25Frame.Sabm(Peer, Local)));
        h.Telemetry.Observe("vhf", Rx(Ax25Frame.Rr(Local, Peer, nr: 4, isCommand: false)));
        await Wait.ForAsync(() => h.Client.CountOf<OarcL2TraceEvent>() >= 2, "both traces");

        var traces = h.Client.Posted.OfType<OarcL2TraceEvent>().ToList();
        traces.Should().Contain(t => t.L2Type == "C", "SABM maps to the connect code 'C'");
        var rr = traces.Single(t => t.L2Type == "RR");
        rr.TransmitSequence.Should().BeNull("a supervisory frame has no transmit sequence");
        rr.IFieldLength.Should().BeNull("a supervisory frame has no info field");
    }

    [Fact]
    public async Task Disabling_reports_node_down()
    {
        await using var h = await StartAsync(On());
        await Wait.ForAsync(() => h.Client.CountOf<OarcNodeUpEvent>() >= 1, "node-up first");

        h.Config.Apply(Cfg(On() with { Enabled = false }));
        await AdvanceUntil(h.Clock, () => h.Client.CountOf<OarcNodeDownEvent>() >= 1, "node-down on disable");
    }

    [Fact]
    public async Task A_transport_error_is_retried_then_accepted()
    {
        await using var h = await StartAsync(On() with { ReportLinks = false, ReportCircuits = false });
        // Fail the very first POST (the node-up) once, accept thereafter.
        h.Client.Responder = (i, _) => i == 0
            ? new OarcIngestResult(OarcIngestOutcome.TransportError, 503, "boom")
            : new OarcIngestResult(OarcIngestOutcome.Accepted, 202, null);

        await Wait.ForAsync(() => h.Client.CountOf<OarcNodeUpEvent>() >= 1, "first (failing) attempt");
        await AdvanceUntil(h.Clock, () => h.Client.CountOf<OarcNodeUpEvent>() >= 2, "the retry after backoff");
    }

    [Fact]
    public async Task A_rejected_payload_is_not_retried()
    {
        await using var h = await StartAsync(On() with { ReportLinks = false, ReportCircuits = false });
        h.Client.Responder = (_, _) => new OarcIngestResult(OarcIngestOutcome.Rejected, 400, "bad");

        await Wait.ForAsync(() => h.Client.CountOf<OarcNodeUpEvent>() >= 1, "the single attempt");
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        h.Client.CountOf<OarcNodeUpEvent>().Should().Be(1, "a 400 is a payload bug, never retried");
    }

    private static Ax25FrameEventArgs Rx(Ax25Frame frame) => new() { Frame = frame, Direction = FrameDirection.Received, Timestamp = T0 };
    private static Ax25FrameEventArgs Tx(Ax25Frame frame) => new() { Frame = frame, Direction = FrameDirection.Transmitted, Timestamp = T0 };

    /// <summary>Repeatedly advance the fake clock (with real-time yields between steps) until
    /// <paramref name="condition"/> holds. Robust against the "advanced before the loop parked at its
    /// Task.Delay" race that a single advance suffers under CPU load — each step gives the autonomous
    /// reporter loop another chance to park and then fire on the next advance.</summary>
    private static async Task AdvanceUntil(FakeTimeProvider clock, Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(20);
        }
        if (!condition())
        {
            throw new TimeoutException($"condition not met within 30s: {because}");
        }
    }
}
