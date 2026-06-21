using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Api;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Api;

/// <summary>
/// Unit tests for the connectionless TEST-ping correlation core (<see cref="AxPinger"/>),
/// driven through the <see cref="IAxPingChannel"/> seam by a fully synthetic fake — no
/// modem, no live <see cref="Ax25Listener"/>. The fake captures the TEST command's info
/// tag at send time and lets the test fire a synthetic <see cref="Ax25Listener.FrameTraced"/>
/// TEST response, so each correlation path (echo → RTT; no echo → timeout/loss; wrong-tag /
/// command / TX frames → ignored) is asserted deterministically against a
/// <see cref="FakeTimeProvider"/>.
/// </summary>
[Trait("Category", "Node")]
public sealed class AxPingerTests
{
    private static readonly Callsign Us = Callsign.Parse("M0LTE-1");
    private static readonly Callsign Peer = Callsign.Parse("GB7RDG-1");

    /// <summary>
    /// A synthetic <see cref="IAxPingChannel"/>: records each TEST command's destination +
    /// info tag, and re-raises <see cref="FrameTraced"/> on demand so a test can inject a
    /// synthetic response. <see cref="SendTestAsync"/> can be made to throw to model a
    /// port that went away mid-run.
    /// </summary>
    private sealed class FakeChannel : IAxPingChannel
    {
        public List<(Callsign Dest, byte[] Tag)> Sent { get; } = new();
        public bool ThrowOnSend { get; set; }

        public Callsign MyCall => Us;

        public event EventHandler<Ax25FrameEventArgs>? FrameTraced;

        public Task SendTestAsync(Callsign destination, ReadOnlyMemory<byte> info, CancellationToken ct = default)
        {
            if (ThrowOnSend)
            {
                throw new InvalidOperationException("port gone");
            }
            Sent.Add((destination, info.ToArray()));
            return Task.CompletedTask;
        }

        /// <summary>Fire a synthetic frame trace (the thing a live listener would raise).</summary>
        public void Raise(Ax25Frame frame, FrameDirection direction) =>
            FrameTraced?.Invoke(this, new Ax25FrameEventArgs
            {
                Frame = frame,
                Direction = direction,
                Timestamp = DateTimeOffset.UnixEpoch,
            });

        /// <summary>Build the spec-compliant TEST response that echoes a captured tag back
        /// from <see cref="Peer"/> — source = peer, destination = us, C-bit = response.</summary>
        public static Ax25Frame EchoResponse(ReadOnlyMemory<byte> tag) =>
            Ax25Frame.Test(destination: Us, source: Peer, info: tag.Span, isCommand: false, pollFinal: true);
    }

    [Fact]
    public async Task Matching_echo_yields_a_reply_with_non_null_rtt()
    {
        var clock = new FakeTimeProvider();
        var channel = new FakeChannel();

        // Drive the run on a background task; we step the clock + inject the echo as it
        // sends each probe. Subscribe to detect the send so we echo at the right moment.
        var run = AxPinger.RunAsync(channel, Peer, count: 1, perPingTimeout: TimeSpan.FromSeconds(5), clock, CancellationToken.None);

        // Wait until the probe has been sent (handler is subscribed before the send, so the
        // captured tag is available the instant Sent has an entry).
        await Wait.ForAsync(() => channel.Sent.Count == 1, "the first probe was sent");

        // Advance the clock so the RTT is a measurable non-zero value, then echo the tag.
        clock.Advance(TimeSpan.FromMilliseconds(42));
        channel.Raise(FakeChannel.EchoResponse(channel.Sent[0].Tag), FrameDirection.Received);

        var result = await run;

        result.Replies.Should().HaveCount(1);
        var reply = result.Replies[0];
        reply.Seq.Should().Be(0);
        reply.Timeout.Should().BeFalse();
        reply.RttMs.Should().NotBeNull();
        reply.RttMs!.Value.Should().Be(42);
        result.LossPct.Should().Be(0);
        result.MinMs.Should().Be(42);
        result.AvgMs.Should().Be(42);
        result.MaxMs.Should().Be(42);
    }

    [Fact]
    public async Task No_response_yields_timeout_and_full_loss()
    {
        var clock = new FakeTimeProvider();
        var channel = new FakeChannel();

        var run = AxPinger.RunAsync(channel, Peer, count: 3, perPingTimeout: TimeSpan.FromSeconds(5), clock, CancellationToken.None);

        // Never echo. Step past each probe's timeout in turn so all three time out. The
        // run is sequential, so one Advance per probe (a little over the 5 s budget) drains
        // each probe's Task.Delay; poll between to let the next probe arm.
        for (int i = 0; i < 3; i++)
        {
            int expected = i + 1;
            await Wait.ForAsync(() => channel.Sent.Count == expected, $"probe {i} was sent");
            clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(1));
        }

        var result = await run;

        result.Replies.Should().HaveCount(3);
        result.Replies.Should().AllSatisfy(r =>
        {
            r.Timeout.Should().BeTrue();
            r.RttMs.Should().BeNull();
        });
        result.LossPct.Should().Be(100);
        result.MinMs.Should().Be(0);
        result.AvgMs.Should().Be(0);
        result.MaxMs.Should().Be(0);
    }

    [Fact]
    public async Task Mismatched_frames_do_not_false_match()
    {
        var clock = new FakeTimeProvider();
        var channel = new FakeChannel();

        var run = AxPinger.RunAsync(channel, Peer, count: 1, perPingTimeout: TimeSpan.FromSeconds(5), clock, CancellationToken.None);
        await Wait.ForAsync(() => channel.Sent.Count == 1, "the probe was sent");
        var tag = channel.Sent[0].Tag;

        // (a) A TEST *command* with our tag (not a response) — must NOT match.
        channel.Raise(
            Ax25Frame.Test(destination: Us, source: Peer, info: tag, isCommand: true, pollFinal: true),
            FrameDirection.Received);

        // (b) A TEST response from a DIFFERENT station, our tag — must NOT match.
        channel.Raise(
            Ax25Frame.Test(destination: Us, source: Callsign.Parse("M7XYZ"), info: tag, isCommand: false, pollFinal: true),
            FrameDirection.Received);

        // (c) A TEST response from the peer but a DIFFERENT (stray) tag — must NOT match.
        channel.Raise(
            Ax25Frame.Test(destination: Us, source: Peer, info: "STRAY"u8, isCommand: false, pollFinal: true),
            FrameDirection.Received);

        // (d) Our own TEST command echoed on the TRANSMITTED side — wrong direction.
        channel.Raise(
            Ax25Frame.Test(destination: Peer, source: Us, info: tag, isCommand: true, pollFinal: true),
            FrameDirection.Transmitted);

        // None matched → stepping past the timeout makes the probe a loss.
        clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(1));

        var result = await run;
        result.Replies.Should().ContainSingle().Which.Timeout.Should().BeTrue();
        result.LossPct.Should().Be(100);
    }

    [Fact]
    public async Task Mixed_success_and_loss_aggregates_correctly()
    {
        var clock = new FakeTimeProvider();
        var channel = new FakeChannel();

        var run = AxPinger.RunAsync(channel, Peer, count: 4, perPingTimeout: TimeSpan.FromSeconds(5), clock, CancellationToken.None);

        // Probe 0: echo after 10 ms.
        await Wait.ForAsync(() => channel.Sent.Count == 1, "probe 0 sent");
        clock.Advance(TimeSpan.FromMilliseconds(10));
        channel.Raise(FakeChannel.EchoResponse(channel.Sent[0].Tag), FrameDirection.Received);

        // Probe 1: time out.
        await Wait.ForAsync(() => channel.Sent.Count == 2, "probe 1 sent");
        clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(1));

        // Probe 2: echo after 30 ms.
        await Wait.ForAsync(() => channel.Sent.Count == 3, "probe 2 sent");
        clock.Advance(TimeSpan.FromMilliseconds(30));
        channel.Raise(FakeChannel.EchoResponse(channel.Sent[2].Tag), FrameDirection.Received);

        // Probe 3: time out.
        await Wait.ForAsync(() => channel.Sent.Count == 4, "probe 3 sent");
        clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(1));

        var result = await run;

        result.Replies.Should().HaveCount(4);
        result.Replies[0].RttMs.Should().Be(10);
        result.Replies[1].Timeout.Should().BeTrue();
        result.Replies[2].RttMs.Should().Be(30);
        result.Replies[3].Timeout.Should().BeTrue();

        result.MinMs.Should().Be(10);
        result.MaxMs.Should().Be(30);
        result.AvgMs.Should().Be(20);     // (10 + 30) / 2
        result.LossPct.Should().Be(50);   // 2 of 4
    }

    [Fact]
    public async Task Probe_tags_are_unique_per_probe()
    {
        var clock = new FakeTimeProvider();
        var channel = new FakeChannel();

        var run = AxPinger.RunAsync(channel, Peer, count: 3, perPingTimeout: TimeSpan.FromSeconds(5), clock, CancellationToken.None);
        for (int i = 0; i < 3; i++)
        {
            int expected = i + 1;
            await Wait.ForAsync(() => channel.Sent.Count == expected, $"probe {i} sent");
            clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(1));
        }
        await run;

        channel.Sent.Should().HaveCount(3);
        // Every probe carries the PDNPING magic and a distinct tag.
        channel.Sent.Should().AllSatisfy(s => s.Tag.AsSpan(0, 7).SequenceEqual("PDNPING"u8).Should().BeTrue());
        var distinct = channel.Sent.Select(s => Convert.ToHexString(s.Tag)).Distinct();
        distinct.Should().HaveCount(3);
    }

    [Fact]
    public async Task Send_failure_records_the_probe_as_loss_without_aborting_the_run()
    {
        var clock = new FakeTimeProvider();
        var channel = new FakeChannel { ThrowOnSend = true };

        // The port "went away" — every SendTestAsync throws. The run must complete with all
        // probes recorded as loss, not surface the exception.
        var result = await AxPinger.RunAsync(channel, Peer, count: 2, perPingTimeout: TimeSpan.FromSeconds(5), clock, CancellationToken.None);

        result.Replies.Should().HaveCount(2);
        result.Replies.Should().AllSatisfy(r => r.Timeout.Should().BeTrue());
        result.LossPct.Should().Be(100);
    }

    /// <summary>
    /// End-to-end over the real seam: a live <see cref="Ax25Listener"/> driven through
    /// <see cref="ListenerAxPingChannel"/>, pinging a second live listener that runs a
    /// spec-compliant TEST responder (echoes the TEST command's info back as a TEST
    /// response). Proves the real adapter + real <see cref="Ax25Listener.SendTestAsync"/> +
    /// real <see cref="Ax25Listener.FrameTraced"/> correlation all line up on the wire (no
    /// modem — an in-memory KISS channel). Uses <see cref="TimeProvider.System"/> because the
    /// listener pump is inherently real-time (see <c>Wait</c> / <c>TestAx25Timing</c>).
    /// </summary>
    [Fact]
    public async Task Real_listener_over_in_memory_radio_measures_an_echo()
    {
        var (a, b) = InMemoryRadio.CreatePair();
        await using var pinger = new Ax25Listener(a, new Ax25ListenerOptions { MyCall = Us }, TimeProvider.System);
        await using var responder = new Ax25Listener(b, new Ax25ListenerOptions { MyCall = Peer }, TimeProvider.System);

        // Spec-compliant TEST responder: when a TEST *command* addressed to us arrives, echo
        // its info field back as a TEST *response* (§4.3.4.2). This is what a node that
        // implements TEST does; it's what GB7RDG may or may not do (the open question). The
        // listener's SendTestAsync only sends TEST *commands*, so the response frame is built
        // directly (C-bit = response) and written on the responder's own modem endpoint.
        responder.FrameTraced += (_, e) =>
        {
            if (e.Direction != FrameDirection.Received)
            {
                return;
            }
            var f = e.Frame;
            bool isTest = (f.Control & 0xEF) == 0xE3;
            if (isTest && f.IsCommand && f.Destination.Callsign.Equals(Peer))
            {
                var echo = Ax25Frame.Test(
                    destination: f.Source.Callsign, source: Peer, info: f.Info.Span,
                    isCommand: false, pollFinal: false);
                _ = b.SendAsync(echo.ToBytes());
            }
        };

        await pinger.StartAsync();
        await responder.StartAsync();

        var channel = new ListenerAxPingChannel(pinger);
        var result = await AxPinger.RunAsync(
            channel, Peer, count: 2, perPingTimeout: TimeSpan.FromSeconds(5), TimeProvider.System, CancellationToken.None);

        result.Replies.Should().HaveCount(2);
        result.Replies.Should().AllSatisfy(r =>
        {
            r.Timeout.Should().BeFalse("the responder echoes every TEST command");
            r.RttMs.Should().NotBeNull();
            r.RttMs!.Value.Should().BeGreaterThanOrEqualTo(0);
        });
        result.LossPct.Should().Be(0);
    }
}
