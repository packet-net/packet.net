using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Behavioural (real-dispatcher, real-codec) coverage of figc4.5 (Timer Recovery)
/// <b>receive columns</b> — supervisory and I frames arriving from the peer while a
/// station is recovering an unacked I-frame. The existing
/// <c>DataLinkTimerRecoveryIntegrationTests</c> cover the T1-driven spine (enter on
/// T1 expiry, N2 exhaustion → Disconnected, retransmit + recover when loss lifts),
/// and <c>ErrorRecoveryConformanceTests</c> cover FRMR/DM in TimerRecovery; this
/// fills the REJ / RNR / I / RR receive paths, which were only stub-covered.
/// </summary>
/// <remarks>
/// Each scenario drives station A into TimerRecovery with one outstanding,
/// unacked I-frame (drop A's I-frames, let T1 expire), then delivers a specific
/// frame "from B" via <see cref="TwoStationHarness.InjectFrameBytes"/> — a frame the
/// peer session wouldn't necessarily emit on cue — and asserts the spec-correct
/// reaction through the real <see cref="ActionDispatcher"/> plus the
/// <see cref="InvariantChecker"/> oracle.
/// </remarks>
public class TimerRecoveryConformanceTests
{
    /// <summary>Connect, then drive A into TimerRecovery holding
    /// <paramref name="outstanding"/> unacked I-frames (payloads 0x00, 0x01, …):
    /// drop A's I-frames so the submits are never acked, expire T1 (poll →
    /// TimerRecovery), then restore a clean channel. A is left with
    /// V(s)=<paramref name="outstanding"/>, V(a)=0.</summary>
    private static TwoStationHarness ConnectedWithAInTimerRecovery(byte outstanding = 1)
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();
        h.Link.Drop = f => f.Source.Callsign.Equals(h.A.Context.Local) && (f.Control & 0x01) == 0;
        for (byte i = 0; i < outstanding; i++) h.Submit(h.A, i);
        h.AdvanceT1();
        h.A.State.Should().Be("TimerRecovery", "a dropped I-frame's T1 must expire A into TimerRecovery");
        h.A.Context.VS.Should().Be(outstanding);
        h.A.Context.VA.Should().Be(0, "the outstanding I-frames are still unacked");
        h.Link.Drop = null;
        return h;
    }

    /// <summary>A supervisory frame addressed to <paramref name="target"/> (as if
    /// its peer sent it).</summary>
    private static byte[] RejTo(TwoStationHarness.Endpoint target, byte nr) =>
        Ax25Frame.Rej(target.Context.Local, target.Context.Remote, nr, isCommand: false, pollFinal: true).ToBytes();

    private static byte[] RnrTo(TwoStationHarness.Endpoint target, byte nr) =>
        Ax25Frame.Rnr(target.Context.Local, target.Context.Remote, nr, isCommand: false).ToBytes();

    private static byte[] RrTo(TwoStationHarness.Endpoint target, byte nr) =>
        Ax25Frame.Rr(target.Context.Local, target.Context.Remote, nr, isCommand: false).ToBytes();

    private static byte[] ITo(TwoStationHarness.Endpoint target, byte nr, byte ns, byte payload) =>
        Ax25Frame.I(target.Context.Local, target.Context.Remote, nr, ns, info: new[] { payload }, pollBit: false).ToBytes();

    [Fact]
    public void Rej_received_in_TimerRecovery_retransmits_and_recovers()
    {
        var h = ConnectedWithAInTimerRecovery();

        // B asks for retransmission from N(R)=0. figc4.5 REJ handling re-sends the
        // unacked I-frame; with the channel now clean it reaches B, which delivers
        // it and acks. The ack (F=0) advances V(a) but doesn't itself leave
        // TimerRecovery — that happens on the next poll/final cycle.
        h.InjectFrameBytes(h.A, RejTo(h.A, nr: 0));

        h.B.Delivered.Select(p => p[0]).Should().Equal(new byte[] { 0x00 },
            "the REJ must drive retransmission of the unacked I-frame, which B then delivers");
        h.A.Context.VA.Should().Be(1, "B's ack of the retransmit advances V(a)");

        // Complete the poll/final cycle (T1 → RR(P=1) → RR(F=1)): A confirms
        // everything is acked and returns to Connected.
        for (int r = 0; r < 8 && h.A.State != "Connected"; r++) h.AdvanceT1();
        h.A.State.Should().Be("Connected", "the poll/final cycle completes recovery back to Connected");
        h.AssertConverged();
    }

    [Fact]
    public void Rnr_received_in_TimerRecovery_registers_peer_busy()
    {
        var h = ConnectedWithAInTimerRecovery();

        // B reports it is busy. A must record peer-receiver-busy and hold off
        // retransmitting (the outstanding I-frame stays unacked until B clears).
        h.InjectFrameBytes(h.A, RnrTo(h.A, nr: 0));

        h.A.Context.PeerReceiverBusy.Should().BeTrue("RNR in TimerRecovery must set peer-receiver-busy");
        h.A.Context.VA.Should().Be(0, "nothing is acked, so the outstanding frame remains");
    }

    [Fact]
    public void I_received_in_TimerRecovery_delivers_peer_data_while_recovering()
    {
        var h = ConnectedWithAInTimerRecovery();

        // The injected I-frame is data B sends while A is recovering its own; it is
        // outside the harness's submitted/delivered accounting (it never went
        // through B.Submit), so suspend the reliable-delivery oracle for this step
        // and assert the delivery directly.
        h.CheckAfterEachStep = false;

        // In-sequence (N(s)=0 == A's V(r)): A accepts and delivers B's data even
        // while in TimerRecovery — recovery of A's own outstanding frame and
        // reception of the peer's data are independent directions.
        h.InjectFrameBytes(h.A, ITo(h.A, nr: 0, ns: 0, payload: 0xBB));

        h.A.Delivered.Select(p => p[0]).Should().Contain(0xBB,
            "A must deliver in-sequence data received from B even while in TimerRecovery");
        h.A.Context.VR.Should().Be(1, "V(r) advances over the received I-frame");
    }

    [Fact]
    public void Rr_received_in_TimerRecovery_partially_acks_and_stays()
    {
        // Two frames outstanding (V(s)=2, V(a)=0). A bare RR(N(R)=1) acks only the
        // first; the second is still in flight, so A advances V(a) but remains in
        // TimerRecovery (recovery isn't complete until V(a) catches V(s)).
        var h = ConnectedWithAInTimerRecovery(outstanding: 2);

        h.InjectFrameBytes(h.A, RrTo(h.A, nr: 1));

        h.A.Context.VA.Should().Be(1, "RR(N(R)=1) acknowledges the first of the two outstanding frames");
        h.A.Context.VS.Should().Be(2, "the second frame is still outstanding");
        h.A.State.Should().Be("TimerRecovery", "a partial ack does not complete recovery");
    }
}
