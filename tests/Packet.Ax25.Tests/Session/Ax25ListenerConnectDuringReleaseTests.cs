using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;
using static Packet.Ax25.Tests.Session.ListenerTestSupport;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// #844: a dial to a peer whose previous link, from the same local callsign, is still being
/// torn down. The listener keeps one session per (local, remote) and reuses it, so the new dial
/// and the old link's release share it. The dial used to start on top of the release: the
/// release's DL-DISCONNECT confirm then reached the dial as a refusal, so the caller was told
/// the connect failed, and with the pre-connect XID the dial's DL-CONNECT request went in after
/// the release had finished, so the link came up anyway with nobody holding it. Seen in DAPPS's
/// soak on a slow simulated channel, where the old DISC waited a second or more for the air.
/// </summary>
public sealed class Ax25ListenerConnectDuringReleaseTests
{
    private static readonly Callsign CallA = new("N0AAA", 3);
    private static readonly Callsign CallB = new("N0BBB", 3);

    // Two modems wired by a polling pump, as PreConnectNegotiationTests does, with a hold on
    // the A-to-B direction: while held, A's frames wait, and they are delivered in order once
    // the hold is lifted. That is a slow channel's effect on the UA to B's DISC.
    private sealed class HeldWire : IDisposable
    {
        private readonly CancellationTokenSource cts = new();
        private volatile bool holdAToB;

        public HeldWire(LoopbackModem a, LoopbackModem b)
        {
            _ = Task.Run(async () =>
            {
                int ai = 0, bi = 0;
                while (!cts.IsCancellationRequested)
                {
                    while (!holdAToB && ai < a.SentFrames.Count)
                    {
                        b.InjectInboundRaw(a.SentFrames[ai++]);
                    }

                    while (bi < b.SentFrames.Count)
                    {
                        a.InjectInboundRaw(b.SentFrames[bi++]);
                    }

                    try { await Task.Delay(5, cts.Token); } catch (OperationCanceledException) { return; }
                }
            });
        }

        public bool HoldAToB { set => holdAToB = value; }

        public void Dispose()
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_dial_while_the_previous_link_is_still_releasing_waits_for_it_and_connects(bool preConnectXid)
    {
        var modemA = new LoopbackModem();
        var modemB = new LoopbackModem();
        await using var a = new Ax25Listener(modemA, new Ax25ListenerOptions { MyCall = CallA });
        await using var b = new Ax25Listener(modemB, new Ax25ListenerOptions { MyCall = CallB });

        Ax25Session? heldOnB = null;
        b.SessionAccepted += (_, e) => Volatile.Write(ref heldOnB, e.Session);
        var acceptedOnA = 0;
        a.SessionAccepted += (_, _) => Interlocked.Increment(ref acceptedOnA);
        await a.StartAsync();
        await b.StartAsync();
        a.AcceptIncoming = true;
        b.AcceptIncoming = true;
        using var wire = new HeldWire(modemA, modemB);

        // A calls B, and B holds the link it accepted, as the DAPPS daemon on B did.
        await a.ConnectAsync(CallB, CallA).WithTimeout(TimeSpan.FromSeconds(15));
        await WaitFor(() => Volatile.Read(ref heldOnB) is not null, TimeSpan.FromSeconds(5), "B accepted A's call");
        var onB = Volatile.Read(ref heldOnB)!;
        var acceptsBefore = Volatile.Read(ref acceptedOnA);

        // B's daemon restarts: its handle's DISC goes out, and A's UA is held on the channel,
        // so B's side of the link is still releasing...
        wire.HoldAToB = true;
        onB.PostEvent(new DlDisconnectRequest());
        await WaitFor(() => onB.CurrentState == "AwaitingRelease", TimeSpan.FromSeconds(5), "B is releasing");

        // ...when the new daemon dials A from the same callsign. By the time ConnectAsync hands
        // back its task, the dial has run as far as its first wait, so the old code has already
        // started (XID sent, or DL-CONNECT posted into the release) before the UA gets through.
        var dial = b.ConnectAsync(CallA, CallB, extended: true, preConnectXid);
        wire.HoldAToB = false;

        var session = await dial.WithTimeout(TimeSpan.FromSeconds(30));

        session.Should().BeSameAs(onB, "the listener keeps one session per (local, remote)");
        session.CurrentState.Should().Be("Connected");
        await WaitFor(() => Volatile.Read(ref acceptedOnA) > acceptsBefore, TimeSpan.FromSeconds(5),
            "A answered the new call, so the link B's caller holds is the one that is up");
    }

    [Fact]
    public async Task A_dial_cancelled_while_it_waits_for_the_release_fails_and_sends_nothing()
    {
        // The other outcome the issue allows: the dial fails, and then the node must not
        // connect. Cancelled while the release is still held up, it has put nothing on the air.
        var modemA = new LoopbackModem();
        var modemB = new LoopbackModem();
        await using var a = new Ax25Listener(modemA, new Ax25ListenerOptions { MyCall = CallA });
        await using var b = new Ax25Listener(modemB, new Ax25ListenerOptions { MyCall = CallB });
        Ax25Session? heldOnB = null;
        b.SessionAccepted += (_, e) => Volatile.Write(ref heldOnB, e.Session);
        await a.StartAsync();
        await b.StartAsync();
        a.AcceptIncoming = true;
        b.AcceptIncoming = true;
        using var wire = new HeldWire(modemA, modemB);

        await a.ConnectAsync(CallB, CallA).WithTimeout(TimeSpan.FromSeconds(15));
        await WaitFor(() => Volatile.Read(ref heldOnB) is not null, TimeSpan.FromSeconds(5), "B accepted A's call");
        var onB = Volatile.Read(ref heldOnB)!;

        wire.HoldAToB = true;
        onB.PostEvent(new DlDisconnectRequest());
        await WaitFor(() => onB.CurrentState == "AwaitingRelease", TimeSpan.FromSeconds(5), "B is releasing");
        var sentBeforeDial = modemB.OutboundFrameCount;

        using var cancel = new CancellationTokenSource();
        var dial = b.ConnectAsync(CallA, CallB, extended: true, preConnectXidNegotiatesSrej: true, cancel.Token);
        cancel.Cancel();

        var act = () => dial.WithTimeout(TimeSpan.FromSeconds(10));
        await act.Should().ThrowAsync<OperationCanceledException>();
        modemB.OutboundFrameCount.Should().Be(sentBeforeDial, "a dial still waiting for the release has sent nothing");
        onB.CurrentState.Should().Be("AwaitingRelease");
    }

    [Fact]
    public async Task A_dial_on_a_session_that_is_not_releasing_does_not_wait()
    {
        // The wait is only for a release in progress: a first dial to a silent peer goes
        // straight to its XID and SABM(E) as before.
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = CallB });
        await listener.StartAsync();

        var dial = listener.ConnectAsync(CallA, CallB, extended: true, preConnectXidNegotiatesSrej: false);

        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        modem.InjectInbound(Ax25Frame.Ua(CallB, CallA, finalBit: true));
        var session = await dial.WithTimeout(TimeSpan.FromSeconds(10));
        session.CurrentState.Should().Be("Connected");
    }
}
