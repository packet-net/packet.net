using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Behavioural (real-dispatcher, real-codec) coverage of the connection-lifecycle
/// receive columns — figc4.3 (Awaiting Connection, the SABM/UA establishment wait)
/// and figc4.6/4.3 (Awaiting Release, the DISC/UA teardown wait). Both states were
/// thinly covered behaviourally (the transition-coverage ledger measured 4/25 and
/// 2/20); this drives a station <em>into</em> each transient state and exercises the
/// frames it can receive while waiting.
/// </summary>
/// <remarks>
/// A station is held in the transient state by dropping the peer's UA (so the
/// establish/release never completes), then the specific frame is delivered "from
/// the peer" via <see cref="TwoStationHarness.InjectFrameBytes"/> and the
/// real-dispatcher outcome asserted. The per-step invariant oracle is suspended
/// (these are connection-setup/teardown flows with no data in flight, outside the
/// reliable-delivery model).
/// </remarks>
public class ConnectionLifecycleConformanceTests
{
    /// <summary>A frame addressed to <paramref name="t"/> (as if its peer sent it).</summary>
    private static byte[] DmTo(TwoStationHarness.Endpoint t, bool finalBit) =>
        Ax25Frame.Dm(t.Context.Local, t.Context.Remote, finalBit).ToBytes();

    private static byte[] UaTo(TwoStationHarness.Endpoint t, bool finalBit) =>
        Ax25Frame.Ua(t.Context.Local, t.Context.Remote, finalBit).ToBytes();

    private static bool PeerSaw(TwoStationHarness.Endpoint peer, Func<Ax25Frame, bool> pred) =>
        peer.ReceivedFromPeer.Any(pred);

    private static bool IsSabm(Ax25Frame f) => (f.Control & 0xEF) == 0x2F;
    private static bool IsDisc(Ax25Frame f) => (f.Control & 0xEF) == 0x43;

    /// <summary>Drive A into AwaitingConnection: A issues DL-CONNECT (sends SABM),
    /// but B's UA is dropped so the establish never completes. Drop stays active so
    /// retransmitted SABMs are also un-acked.</summary>
    private static TwoStationHarness AInAwaitingConnection(int n2 = 4)
    {
        var h = TwoStationHarness.Build(k: 4, n2: n2);
        h.CheckAfterEachStep = false;
        h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && (f.Control & 0xEF) == 0x63; // B's UA
        h.A.Session.PostEvent(new DlConnectRequest());
        h.Settle();
        h.A.State.Should().Be("AwaitingConnection", "A is waiting for the UA that never arrives");
        return h;
    }

    /// <summary>Drive A into AwaitingRelease: connect, then A issues DL-DISCONNECT
    /// (sends DISC), but B's UA is dropped so the release never completes.</summary>
    private static TwoStationHarness AInAwaitingRelease(int n2 = 4)
    {
        var h = TwoStationHarness.Build(k: 4, n2: n2);
        h.Connect();
        h.CheckAfterEachStep = false;
        h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && (f.Control & 0xEF) == 0x63; // B's UA
        h.A.Session.PostEvent(new DlDisconnectRequest());
        h.Settle();
        h.A.State.Should().Be("AwaitingRelease", "A is waiting for the UA to its DISC");
        return h;
    }

    // ─── Awaiting Connection ────────────────────────────────────────────

    [Fact]
    public void AwaitingConnection_DM_with_final_abandons_the_connect()
    {
        var h = AInAwaitingConnection();

        // figc t03_dm_received_yes: peer refuses (DM, F=1) → discard, DL-DISCONNECT
        // indication, → Disconnected.
        h.InjectFrameBytes(h.A, DmTo(h.A, finalBit: true));

        h.A.State.Should().Be("Disconnected", "a DM(F=1) abandons the connection attempt");
        h.A.Signals.OfType<DataLinkDisconnectIndication>().Should().NotBeEmpty();
    }

    [Fact]
    public void AwaitingConnection_T1_timeout_retransmits_SABM_then_gives_up_at_N2()
    {
        var h = AInAwaitingConnection(n2: 3);
        h.B.ReceivedFromPeer.Clear();

        // Each T1 expiry while RC < N2 retransmits SABM (figc t05_t1_expiry_no);
        // when RC reaches N2 the attempt is abandoned (t05_t1_expiry_yes →
        // DL-ERROR(G) + DL-DISCONNECT indication → Disconnected).
        for (int r = 0; r < 6 && h.A.State == "AwaitingConnection"; r++) h.AdvanceT1();

        PeerSaw(h.B, IsSabm).Should().BeTrue("T1 expiry must retransmit the SABM");
        h.A.State.Should().Be("Disconnected", "after N2 unacked SABMs the connect attempt is abandoned");
        h.A.Signals.OfType<DataLinkErrorIndication>().Should().Contain(e => e.Code == "G",
            "N2 exhaustion in AwaitingConnection raises DL-ERROR (G)");
        h.A.Signals.OfType<DataLinkDisconnectIndication>().Should().NotBeEmpty();
    }

    // ─── Awaiting Release ───────────────────────────────────────────────

    [Fact]
    public void AwaitingRelease_UA_with_final_completes_the_release()
    {
        var h = AInAwaitingRelease();

        // figc t03_ua_received_yes: UA(F=1) → DL-DISCONNECT confirm, Stop T1,
        // → Disconnected.
        h.InjectFrameBytes(h.A, UaTo(h.A, finalBit: true));

        h.A.State.Should().Be("Disconnected", "the UA to our DISC completes the release");
        h.A.Signals.OfType<DataLinkDisconnectConfirm>().Should().NotBeEmpty();
    }

    [Fact]
    public void AwaitingRelease_DM_with_final_completes_the_release()
    {
        var h = AInAwaitingRelease();

        // figc t13_dm_received_yes: the peer is already gone (DM, F=1) → treat the
        // release as complete (DL-DISCONNECT confirm) → Disconnected.
        h.InjectFrameBytes(h.A, DmTo(h.A, finalBit: true));

        h.A.State.Should().Be("Disconnected", "a DM(F=1) also completes the release");
        h.A.Signals.OfType<DataLinkDisconnectConfirm>().Should().NotBeEmpty();
    }

    [Fact]
    public void AwaitingRelease_T1_timeout_retransmits_DISC_then_gives_up_at_N2()
    {
        var h = AInAwaitingRelease(n2: 3);
        h.B.ReceivedFromPeer.Clear();

        // T1 expiry while RC < N2 retransmits DISC (t02_t1_expiry_no); at N2 the
        // release is forced complete (t02_t1_expiry_yes → Disconnected).
        for (int r = 0; r < 6 && h.A.State == "AwaitingRelease"; r++) h.AdvanceT1();

        PeerSaw(h.B, IsDisc).Should().BeTrue("T1 expiry must retransmit the DISC");
        h.A.State.Should().Be("Disconnected", "after N2 unacked DISCs the release completes anyway");
    }
}
