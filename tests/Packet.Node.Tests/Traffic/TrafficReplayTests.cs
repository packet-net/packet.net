using Packet.Ax25;
using Packet.Ax25.Monitor;
using Packet.Core;
using Packet.Node.Core.Traffic;

namespace Packet.Node.Tests.Traffic;

/// <summary>The replay half of SP-003 (#178): a capture goes back through the parser and the
/// link observer, every frame gets a parse verdict, and the links are read as a third party
/// would read them.</summary>
[Trait("Category", "Node")]
public sealed class TrafficReplayTests
{
    private static readonly Callsign A = new("M0AAA", 1);
    private static readonly Callsign B = new("M0BBB", 2);
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 20, 0, 0, TimeSpan.Zero);

    private static TrafficCaptureFrame At(int ms, bool tx, Ax25Frame frame) =>
        new(T0 + TimeSpan.FromMilliseconds(ms), "vhf-1", tx, frame.ToBytes());

    [Fact]
    public void A_connection_replays_into_one_connected_link_with_its_narration()
    {
        var capture = new[]
        {
            At(0, true, Ax25Frame.Sabm(B, A)),
            At(400, false, Ax25Frame.Ua(A, B, finalBit: true)),
            At(900, true, Ax25Frame.I(B, A, nr: 0, ns: 0, "hello\r"u8)),
            At(1300, false, Ax25Frame.Rr(A, B, nr: 1, isCommand: false)),
        };

        var report = TrafficReplay.Run(capture);

        report.Total.Should().Be(4);
        report.StrictCount.Should().Be(4);
        report.RejectedCount.Should().Be(0);
        report.Frames.Should().AllSatisfy(f => f.Event.Should().NotBeNull());
        report.Links.Should().ContainSingle().Which.State.Should().Be(Ax25LinkState.Connected);
        report.Links[0].Id.Should().Be(Ax25LinkObserver.LinkIdFor("vhf-1", A, B));

        var text = TrafficReplay.Render(report);
        text.Should().Contain("20:00:00.000 vhf-1 tx M0AAA-1>M0BBB-2 SABM");
        text.Should().Contain("links: 1").And.Contain("Connected");
        text.Should().Contain("frames: 4 (strict 4, lenient only 0, rejected 0)");
    }

    [Fact]
    public void A_frame_nothing_parses_is_reported_and_the_rest_still_replays()
    {
        var capture = new[]
        {
            At(0, false, Ax25Frame.Sabm(B, A)),
            new TrafficCaptureFrame(T0 + TimeSpan.FromMilliseconds(100), "vhf-1", false, [0x01, 0x02, 0x03]),
            At(400, true, Ax25Frame.Ua(A, B, finalBit: true)),
        };

        var report = TrafficReplay.Run(capture);

        report.RejectedCount.Should().Be(1);
        report.Frames[1].Parse.Should().Be(TrafficReplay.ParseOutcome.Rejected);
        report.Frames[1].Line.Should().Be(2);
        report.Frames[1].Event.Should().BeNull();
        report.Links.Should().ContainSingle().Which.State.Should().Be(Ax25LinkState.Connected);
        TrafficReplay.Render(report).Should().Contain("REJECTED by Strict and Lenient: 010203");
    }

    [Fact]
    public void A_port_filter_keeps_only_that_port_and_an_unanswered_call_expires()
    {
        var capture = new[]
        {
            At(0, false, Ax25Frame.Sabm(B, A)),
            new TrafficCaptureFrame(T0 + TimeSpan.FromMilliseconds(50), "uhf-2", false, Ax25Frame.Sabm(A, B).ToBytes()),
        };

        var report = TrafficReplay.Run(capture, port: "vhf-1");

        report.Total.Should().Be(1);
        report.Links.Should().ContainSingle().Which.Port.Should().Be("vhf-1");
        // Nothing answered the call; the replay ages the link out as a live monitor's timer would.
        report.Expired.Should().NotBeEmpty();
        report.Links[0].State.Should().NotBe(Ax25LinkState.Connected);
    }
}
