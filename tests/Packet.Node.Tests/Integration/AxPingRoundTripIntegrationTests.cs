using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Api;
using Packet.Node.Tests.Support;
using Xunit;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// End-to-end pdn↔pdn axping: two real <see cref="Ax25Listener"/>s wired
/// back-to-back over an <see cref="InMemoryRadio"/> channel. Station A runs
/// <see cref="AxPinger.RunAsync"/> against station B; B's built-in connectionless
/// TEST responder (§4.3.4.2) answers each probe, and A's correlator measures the
/// round-trip. This is the live-send proof of the full TEST exchange — the loop we
/// could not close on the lab because LinBPQ does not answer TEST (XRouter does,
/// per the interop probe).
/// </summary>
[Trait("Category", "Node")]
public sealed class AxPingRoundTripIntegrationTests
{
    private static readonly Callsign StationA = Callsign.Parse("M0LTE-1");
    private static readonly Callsign StationB = Callsign.Parse("G7XYZ-7");

    [Fact]
    public async Task Axping_From_A_To_B_Gets_Echoing_Responses_With_Zero_Loss()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (radioA, radioB) = InMemoryRadio.CreatePair();

        await using var listenerA = new Ax25Listener(new Packet.Kiss.KissModemTransport(radioA), new Ax25ListenerOptions { MyCall = StationA });
        await using var listenerB = new Ax25Listener(new Packet.Kiss.KissModemTransport(radioB), new Ax25ListenerOptions { MyCall = StationB });

        await listenerA.StartAsync(cts.Token);
        await listenerB.StartAsync(cts.Token);

        // A pings B. B's responder (built into every listener) answers each TEST
        // command with a TEST response echoing the probe tag; A correlates it.
        var result = await AxPinger.RunAsync(
            new ListenerAxPingChannel(listenerA),
            StationB,
            count: 3,
            perPingTimeout: TimeSpan.FromSeconds(5),
            clock: TimeProvider.System,
            ct: cts.Token);

        result.LossPct.Should().Be(0.0, "B answers every TEST command in-process — no loss");
        result.Replies.Should().HaveCount(3);
        result.Replies.Should().OnlyContain(r => !r.Timeout, "every probe must be answered");
        result.Replies.Should().OnlyContain(r => r.RttMs != null, "every answered probe has a measured RTT");

        // The TEST exchange is connectionless — neither station opened a session.
        listenerA.ActiveSessions.Should().BeEmpty("axping never opens a connection");
        listenerB.ActiveSessions.Should().BeEmpty("answering a TEST never opens a connection");
    }

    [Fact]
    public async Task Axping_To_A_Station_That_Is_Not_Answering_Reports_Full_Loss()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (radioA, _) = InMemoryRadio.CreatePair();

        // Only A is on the air — B's endpoint exists but no listener is attached,
        // so nothing answers (the LinBPQ-on-the-lab situation). A must report 100%
        // loss as a normal result, not an error.
        await using var listenerA = new Ax25Listener(new Packet.Kiss.KissModemTransport(radioA), new Ax25ListenerOptions { MyCall = StationA });
        await listenerA.StartAsync(cts.Token);

        var result = await AxPinger.RunAsync(
            new ListenerAxPingChannel(listenerA),
            StationB,
            count: 2,
            perPingTimeout: TimeSpan.FromMilliseconds(250),
            clock: TimeProvider.System,
            ct: cts.Token);

        result.LossPct.Should().Be(100.0, "no responder on the channel — every probe times out");
        result.Replies.Should().OnlyContain(r => r.Timeout);
    }
}
