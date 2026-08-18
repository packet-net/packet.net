using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// The TX-complete→T1 seam (<see cref="Ax25ListenerOptions.RestartT1OnTxComplete"/>).
/// The SDL arms T1 at enqueue; behind a buffering TNC the frame may clear the air
/// seconds later, so with the option on the listener sends T1-arming frames in
/// ACKMODE and pushes a still-running T1's deadline out to (echo + T1V). These
/// tests drive the echo by hand (the LoopbackModem's <c>AckEchoGate</c>) under a
/// FakeTimeProvider, proving: the deadline genuinely extends; default behaviour
/// is byte-identical when the option is off; and a non-ACKMODE modem degrades
/// gracefully (one failed attempt, then plain sends).
/// </summary>
public class Ax25ListenerT1TxCompleteTests
{
    private static readonly Callsign LocalCall = Callsign.Parse("M0LTE-1");
    private static readonly Callsign PeerCall = Callsign.Parse("G7XYZ-2");
    private static readonly TimeSpan T1V = TimeSpan.FromSeconds(2);

    private static async Task<(Ax25Listener Listener, LoopbackModem Modem, FakeTimeProvider Time, Ax25Session Session)>
        ConnectedRigAsync(bool restartT1OnTxComplete, bool modemSupportsAck)
    {
        var time = new FakeTimeProvider();
        var modem = new LoopbackModem();
        if (modemSupportsAck)
        {
            modem.AckEchoGate = new SemaphoreSlim(0);
        }

        var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            T1V = T1V,
            RestartT1OnTxComplete = restartT1OnTxComplete,
        }, time);

        Ax25Session? session = null;
        listener.SessionAccepted += (_, e) => session = e.Session;
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        await ListenerTestSupport.WaitFor(
            () => session is { CurrentState: "Connected" },
            TimeSpan.FromSeconds(5), "inbound SABM must be accepted");
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(5)); // the UA

        return (listener, modem, time, session!);
    }

    [Fact]
    public async Task Echo_pushes_a_running_T1_deadline_out_to_TxComplete_plus_T1V()
    {
        var (listener, modem, time, session) = await ConnectedRigAsync(
            restartT1OnTxComplete: true, modemSupportsAck: true);
        await using var _ = listener;

        // One I-frame: goes out via ACKMODE (recorded, echo gated). T1 arms at
        // enqueue - deadline t0 + 2 s on the fake clock.
        listener.SendData(session, new byte[] { 0xAA });
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(5)); // UA + I

        // The echo arrives at t0+1.5 s - the frame only then cleared the air.
        time.Advance(TimeSpan.FromMilliseconds(1500));
        modem.AckEchoGate!.Release();
        await Task.Delay(250); // let the re-arm continuation run (real time; fake clock is ours)

        // Old deadline (t0+2 s) passes: T1 must NOT fire - the deadline moved to
        // (echo + T1V) = t0+3.5 s. A fire would put an enquiry on the modem.
        time.Advance(TimeSpan.FromMilliseconds(1000)); // t0+2.5 s
        await Task.Delay(250);
        modem.SentFrames.Count.Should().Be(2,
            "T1 now runs from TX-complete, so the enqueue-time deadline passing must not fire it");
        session.CurrentState.Should().Be("Connected");

        // The moved deadline passes: T1 fires for real (enquiry / TimerRecovery).
        time.Advance(TimeSpan.FromMilliseconds(1200)); // t0+3.7 s
        await ListenerTestSupport.WaitFor(
            () => modem.SentFrames.Count >= 3,
            TimeSpan.FromSeconds(5), "the re-armed T1 must still fire once (echo + T1V) passes");
        modem.AckEchoGate.Release(); // let the enquiry's own ack-send complete
    }

    [Fact]
    public async Task Option_off_T1_fires_on_the_enqueue_deadline_exactly_as_before()
    {
        var (listener, modem, time, session) = await ConnectedRigAsync(
            restartT1OnTxComplete: false, modemSupportsAck: true);
        await using var _ = listener;

        listener.SendData(session, new byte[] { 0xAA });
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(5));
        modem.AckEchoGate!.Release(); // irrelevant - the plain path never waits on it

        time.Advance(TimeSpan.FromMilliseconds(2100)); // past t0 + T1V
        await ListenerTestSupport.WaitFor(
            () => modem.SentFrames.Count >= 3,
            TimeSpan.FromSeconds(5), "with the option off, T1 runs from enqueue — the historical behaviour");
    }

    [Fact]
    public async Task A_modem_without_ackmode_degrades_to_plain_sends_after_one_attempt()
    {
        var (listener, modem, _, session) = await ConnectedRigAsync(
            restartT1OnTxComplete: true, modemSupportsAck: false);
        await using var _ = listener;

        // First I-frame: the ACKMODE attempt throws NotSupportedException inside
        // the send task; the listener latches ackmode-off and sends it plainly.
        listener.SendData(session, new byte[] { 0x01 });
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(5)); // UA + I (via fallback)

        // Second I-frame: the latch means it goes straight out the plain path.
        listener.SendData(session, new byte[] { 0x02 });
        await modem.SentFrames.WaitForCountAsync(3, TimeSpan.FromSeconds(5));
    }
}
