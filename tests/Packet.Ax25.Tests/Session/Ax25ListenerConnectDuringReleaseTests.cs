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
        private volatile bool dropAToB;

        public HeldWire(LoopbackModem a, LoopbackModem b)
        {
            _ = Task.Run(async () =>
            {
                int ai = 0, bi = 0;
                while (!cts.IsCancellationRequested)
                {
                    while (!holdAToB && ai < a.SentFrames.Count)
                    {
                        var frame = a.SentFrames[ai++];
                        if (!dropAToB)
                        {
                            b.InjectInboundRaw(frame);
                        }
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

        // Lose A's frames outright while set, rather than delay them.
        public bool DropAToB { set => dropAToB = value; }

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
    public async Task A_dial_polling_during_the_release_s_final_transition_does_not_take_its_teardown()
    {
        // The session moves CurrentState on before a transition's actions run, so while the
        // release's last transition is still raising its signals the state already says
        // Disconnected. Here the release gives up (A's answers are lost, so T1 runs out N2
        // times), and a subscriber is slow on the DL-ERROR that t02_t1_expiry_yes raises just
        // before DL-DISCONNECT indication. A dial that reads the state in that gap arms its
        // wait, then takes the release's DL-DISCONNECT as its own refusal (the #844 message),
        // with its SABM already sent. It has to see the state only once the transition is done.
        var modemA = new LoopbackModem();
        var modemB = new LoopbackModem();
        await using var a = new Ax25Listener(modemA, new Ax25ListenerOptions { MyCall = CallA });
        await using var b = new Ax25Listener(modemB, new Ax25ListenerOptions
        {
            MyCall = CallB,
            T1V = TimeSpan.FromMilliseconds(200),
            N2 = 2,
        });

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

        using var inGiveUp = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        var blocked = 0;
        onB.DataLinkSignalEmitted += (_, sig) =>
        {
            if (sig is DataLinkErrorIndication && Interlocked.Exchange(ref blocked, 1) == 0)
            {
                inGiveUp.Set();
                letGo.Wait(TimeSpan.FromSeconds(10));
            }
        };

        wire.DropAToB = true;
        onB.PostEvent(new DlDisconnectRequest());
        await WaitFor(() => onB.CurrentState == "AwaitingRelease", TimeSpan.FromSeconds(5), "B is releasing");

        var dial = b.ConnectAsync(CallA, CallB, extended: true, preConnectXidNegotiatesSrej: false);

        // Hold the release's final transition between its DL-ERROR and its DL-DISCONNECT for
        // several of the dial's 25 ms polls, then let A be heard again and the release finish.
        await Task.Run(() => inGiveUp.Wait(TimeSpan.FromSeconds(10))).WithTimeout(TimeSpan.FromSeconds(15));
        onB.CurrentState.Should().Be("Disconnected", "the state moves on before the transition's signals are raised");
        wire.DropAToB = false;
        await Task.Delay(150);
        letGo.Set();

        var session = await dial.WithTimeout(TimeSpan.FromSeconds(30));
        session.CurrentState.Should().Be("Connected");
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
