using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using static Packet.Ax25.Tests.Session.ListenerTestSupport;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// What <see cref="Ax25Listener.FrameTraced"/> reports for a frame received on a
/// modulo-128 link.
/// </summary>
/// <remarks>
/// The inbound pump has to parse before it can route (the addresses precede the
/// control field) and cannot know a frame's modulo until it has routed it, so its
/// routing parse is modulo-8. Dispatch re-reads the frame at the session's modulo;
/// the trace did not, so every received frame on a mod-128 link was traced at the
/// wrong modulo while transmitted frames were traced at the right one - an I frame
/// reported N(S) modulo 8, N(R) from the top bits of N(S), the second control octet
/// as its PID and a payload one octet too long, and an RR reported N(R) = 0 with a
/// one-octet information field (packet-net/packet.net#815).
/// </remarks>
public class Mod128TraceReadingTests
{
    private static readonly Callsign Call1 = new("M0LTE", 1);
    private static readonly Callsign Call2 = new("M0LTE", 2);

    [Fact]
    public async Task Received_extended_frames_trace_at_the_session_modulo()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = Call2 });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabme(Call2, Call1));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        session.Context.IsExtended.Should().BeTrue();

        var traced = new List<Ax25Frame>();
        listener.FrameTraced += (_, e) =>
        {
            if (e.Direction == FrameDirection.Received) { lock (traced) { traced.Add(e.Frame); } }
        };

        // An extended I frame: N(S)=9, N(R)=3, PID F0, 5 octets of payload.
        modem.InjectInbound(Ax25Frame.I(Call2, Call1, nr: 3, ns: 9,
            info: "hello"u8, pid: 0xF0, extended: true));
        // An extended RR response with N(R)=5.
        modem.InjectInbound(Ax25Frame.Rr(Call2, Call1, nr: 5, isCommand: false, pollFinal: false, extended: true));

        await WaitFor(() => { lock (traced) { return traced.Count >= 2; } }, TimeSpan.FromSeconds(2));

        Ax25Frame i, rr;
        lock (traced) { i = traced[0]; rr = traced[1]; }

        i.FrameType.Should().Be(Ax25FrameType.I);
        i.Ns.Should().Be(9, "the trace must read N(S) at the link's modulo");
        i.Nr.Should().Be(3, "the trace must read N(R) at the link's modulo");
        i.Pid.Should().Be(0xF0, "PID follows the 2-octet control field on a mod-128 link");
        i.Info.Length.Should().Be(5, "the payload is 5 octets, not 6");

        rr.FrameType.Should().Be(Ax25FrameType.Rr);
        rr.Nr.Should().Be(5, "the trace must read N(R) at the link's modulo");
        rr.Info.Length.Should().Be(0, "an RR has no information field; the second control octet is not payload");
    }
}
