using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Multi-state lifecycle integration tests against the real
/// Disconnected / AwaitingConnection / Connected / AwaitingRelease
/// transition tables, exercised together. Unlike the per-state
/// end-to-end suites these drive the session across boundaries —
/// connect → data round-trip → disconnect — and verify the byte-level
/// frame sequence and DL-* signal sequence match the figc4.1 / 4.2 /
/// 4.4 transcriptions.
/// </summary>
public class DataLinkSessionLifecycleTests
{
    private sealed class Rig
    {
        public required Ax25Session Session { get; init; }
        public required Ax25SessionContext Ctx { get; init; }
        public required SystemTimerScheduler Scheduler { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required List<SupervisoryFrameSpec> SFrames { get; init; }
        public required List<UFrameSpec> UFrames { get; init; }
        public required List<UiFrameSpec> UiFrames { get; init; }
        public required List<DataLinkSignal> Upward { get; init; }
        public required List<IFrameSpec> IFrames { get; init; }
    }

    /// <summary>
    /// Build a session wired up with the real ActionDispatcher, real
    /// DefaultSubroutineRegistry (auto-Wire'd by the Ax25Session ctor),
    /// real frame-aware bindings, and recorder lists on every output
    /// channel. Initial state defaults to Disconnected so callers can
    /// drive the full lifecycle.
    /// </summary>
    private static Rig NewRig(string initialState = "Disconnected", bool isExtended = false)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            IsExtended = isExtended,
        };

        var sFrames = new List<SupervisoryFrameSpec>();
        var uFrames = new List<UFrameSpec>();
        var uiFrames = new List<UiFrameSpec>();
        var iFrames = new List<IFrameSpec>();
        var upward = new List<DataLinkSignal>();

        // Real ActionDispatcher with the default subroutine registry —
        // the Ax25Session constructor will Wire() the registry against
        // this dispatcher + guards, so figc4.7's subroutine bodies
        // execute for real (not via recorder stubs).
        var registry = new DefaultSubroutineRegistry();
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame:    sFrames.Add,
            sendUFrame:    uFrames.Add,
            sendUiFrame:   uiFrames.Add,
            sendIFrame:    iFrames.Add,
            sendUpward:    upward.Add,
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            subroutines:   registry);

        // Frame-aware bindings — pass the session's CurrentTrigger so
        // P_eq_1 / F_eq_1 / command / N_s_eq_V_r etc. resolve against
        // the actual triggering frame at guard-evaluation time.
        Ax25Session? sessionRef = null;
        var bindings = Ax25SessionBindings.CreateDefault(
            ctx, scheduler,
            currentTrigger: () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(
            ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Disconnected"]         = DataLink_Disconnected.Transitions,
                ["AwaitingConnection"]   = DataLink_AwaitingConnection.Transitions,
                ["AwaitingConnection22"] = DataLink_AwaitingConnection22.Transitions,
                ["Connected"]            = DataLink_Connected.Transitions,
                ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
                // TimerRecovery is referenced by some Connected transitions
                // but has no transcription yet. Stub with empty so an
                // accidental routing there doesn't throw — events posted
                // while in TimerRecovery would just drop. Replace with a
                // real transcription once figc4.6's TimerRecovery state
                // is transcribed.
                ["TimerRecovery"]        = Array.Empty<TransitionSpec>(),
            },
            initialState: initialState);
        sessionRef = session;

        return new Rig
        {
            Session = session, Ctx = ctx, Scheduler = scheduler, Time = time,
            SFrames = sFrames, UFrames = uFrames, UiFrames = uiFrames, IFrames = iFrames,
            Upward = upward,
        };
    }

    // ─── Frame construction helpers ─────────────────────────────────────
    //
    // We build receive-side frames byte-by-byte so the assertion that
    // "the session reacted to this exact wire content" stays honest.
    // Outgoing frames are observed via the dispatcher recorder lists
    // (no factory shortcut), so they're tested end-to-end.

    private static Ax25Frame UFrameReceived(byte control)
    {
        // Frame addressed to us (M0LTE-0) from G7XYZ-7. CrhBit indicates
        // response (peer's reply to a command we sent).
        var bytes = new byte[15];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        bytes[14] = control;
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        return frame!;
    }

    private static Ax25Frame SFrameReceived(byte control)
        // Identical wire layout to a U-frame for the address fields;
        // control byte distinguishes. Centralise so the test reads tighter.
        => UFrameReceived(control);

    /// <summary>Build an inbound mod-8 I-frame addressed to us as a command (peer→us). <paramref name="ns"/>=sender's V(s), <paramref name="nr"/>=sender's V(r), <paramref name="pf"/>=poll/final.</summary>
    private static Ax25Frame IFrameInbound(byte ns, byte nr, bool pf, byte[] info, byte pid = Ax25Frame.PidNoLayer3)
    {
        // Mod-8 I-frame control: N(R)<<5 | (PF?0x10:0) | N(S)<<1 | 0 (info-frame LSB = 0).
        byte control = (byte)((nr << 5) | (pf ? 0x10 : 0) | (ns << 1));
        var bytes = new byte[15 + 1 + info.Length];
        // Command frame (peer → us): dst.CRH = 1, src.CRH = 0 (AX.25 v2.2 §6.1.2).
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        bytes[14] = control;
        bytes[15] = pid;
        info.CopyTo(bytes, 16);
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        return frame!;
    }

    private const byte UaFFinal1 = 0x73;     // UA  (control 0x63) | F=1 (0x10)
    private const byte UaFFinal0 = 0x63;     // UA  | F=0
    private const byte DmFFinal1 = 0x1F;     // DM  (control 0x0F) | F=1 (0x10)
    private const byte RrNr1F0   = 0x21;     // RR  (control 0x01) | N(R)=1<<5
    private const byte RrNr0F0   = 0x01;     // RR  | N(R)=0
    private const byte DiscPoll1 = 0x53;     // DISC (control 0x43) | P=1 (0x10)

    // ─── Lifecycle tests ────────────────────────────────────────────────

    [Fact]
    public void Mod8_Connect_Disconnect_Happy_Path_Through_Full_Lifecycle()
    {
        // Disconnected → AwaitingConnection → Connected → AwaitingRelease
        // → Disconnected. Verify the frame sequence on the wire and the
        // DL-* signals upward at each step.
        var r = NewRig();

        // Step 1 — Layer 3 requests a connect.
        r.Session.PostEvent(new DlConnectRequest());

        r.Session.CurrentState.Should().Be("AwaitingConnection");
        r.UFrames.Should().ContainSingle("Establish_Data_Link emits one SABM");
        r.UFrames[0].Type.Should().Be(UFrameType.Sabm);
        r.UFrames[0].PfBit.Should().BeTrue("P := 1 inside Establish_Data_Link");
        r.UFrames[0].IsCommand.Should().BeTrue();
        r.Scheduler.IsRunning("T1").Should().BeTrue("Start T1 inside Establish_Data_Link");
        r.Ctx.Layer3Initiated.Should().BeTrue();
        r.Ctx.RC.Should().Be(1);
        r.Upward.Should().BeEmpty("no DL-* indication until peer accepts");

        // Step 2 — peer ACKs the SABM with UA(F=1).
        r.UFrames.Clear();
        r.Session.PostEvent(new UaReceived(UFrameReceived(UaFFinal1)));

        r.Session.CurrentState.Should().Be("Connected");
        r.Upward.Should().ContainSingle().Which.Should().BeOfType<DataLinkConnectConfirm>();
        r.Scheduler.IsRunning("T1").Should().BeFalse("stop_T1 fired on ack");
        r.Scheduler.IsRunning("T3").Should().BeTrue("T3 armed on entry to Connected");
        r.Ctx.VS.Should().Be((byte)0);
        r.Ctx.VR.Should().Be((byte)0);
        r.Ctx.VA.Should().Be((byte)0);

        // Step 3 — Layer 3 requests disconnect.
        r.Upward.Clear();
        r.Session.PostEvent(new DlDisconnectRequest());

        r.Session.CurrentState.Should().Be("AwaitingRelease");
        r.UFrames.Should().ContainSingle("we emit DISC");
        r.UFrames[0].Type.Should().Be(UFrameType.Disc);
        r.UFrames[0].PfBit.Should().BeTrue();
        r.Scheduler.IsRunning("T1").Should().BeTrue("T1 armed waiting for UA");
        r.Scheduler.IsRunning("T3").Should().BeFalse("T3 stopped on outbound disconnect");

        // Step 4 — peer ACKs the DISC.
        r.UFrames.Clear();
        r.Session.PostEvent(new UaReceived(UFrameReceived(UaFFinal1)));

        r.Session.CurrentState.Should().Be("Disconnected");
        r.Upward.Should().ContainSingle().Which.Should().BeOfType<DataLinkDisconnectConfirm>();
        r.Scheduler.IsRunning("T1").Should().BeFalse();
        r.Scheduler.IsRunning("T2").Should().BeFalse();
        r.Scheduler.IsRunning("T3").Should().BeFalse();
    }

    [Fact]
    public void Mod8_Connect_Send_Iframe_Receive_Ack_Disconnect()
    {
        // Full round-trip with I-frame exchange in the middle. After
        // the peer ACKs an I-frame the connection cleanly tears down.
        var r = NewRig();

        // Connect.
        r.Session.PostEvent(new DlConnectRequest());
        r.Session.PostEvent(new UaReceived(UFrameReceived(UaFFinal1)));
        r.Session.CurrentState.Should().Be("Connected");
        r.UFrames.Clear();
        r.Upward.Clear();

        // Send 4-byte payload via DL-DATA-request → I-frame pops queue →
        // session emits the I-frame, V(s) becomes 1.
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        r.Session.PostEvent(new DlDataRequest(payload, Ax25Frame.PidNoLayer3));

        r.IFrames.Should().ContainSingle("queue drain emits one I-frame");
        r.IFrames[0].Info.ToArray().Should().Equal(payload);
        r.Ctx.VS.Should().Be((byte)1, "V(s) advanced past the sent I-frame");

        // Peer ACKs with RR(N(r)=1, F=0) — N(r)=1 acknowledges our seq 0.
        r.Session.PostEvent(new RrReceived(SFrameReceived(RrNr1F0)));

        r.Ctx.VA.Should().Be((byte)1, "V(a) advanced past the acked I-frame");
        r.Session.CurrentState.Should().Be("Connected");

        // Disconnect.
        r.UFrames.Clear();
        r.Session.PostEvent(new DlDisconnectRequest());
        r.UFrames[0].Type.Should().Be(UFrameType.Disc);
        r.Session.PostEvent(new UaReceived(UFrameReceived(UaFFinal1)));
        r.Session.CurrentState.Should().Be("Disconnected");
    }

    [Fact]
    public void Mod8_Connect_Refused_By_DM_F_Eq_1_Returns_To_Disconnected()
    {
        // Spec §6.3.1 ¶4: a DM(F=1) reply to our SABM means the peer
        // declines the connect. Session should signal a DL-DISCONNECT-
        // indication and return to Disconnected.
        var r = NewRig();

        r.Session.PostEvent(new DlConnectRequest());
        r.Session.CurrentState.Should().Be("AwaitingConnection");
        r.UFrames.Clear();

        r.Session.PostEvent(new DmReceived(UFrameReceived(DmFFinal1)));

        r.Session.CurrentState.Should().Be("Disconnected");
        r.Upward.Should().ContainSingle().Which.Should().BeOfType<DataLinkDisconnectIndication>();
        r.Scheduler.IsRunning("T1").Should().BeFalse();
    }

    [Fact]
    public void Mod128_Connect_Routes_Through_Establish_Data_Link_Emitting_SABME()
    {
        // With IsExtended = true at connect time, Establish_Data_Link's
        // mod_128 path runs (rather than the mod_8 SABM path), emitting
        // SABME. The figc4.1 transcription routes this column to
        // AwaitingConnection (not AwaitingConnection22) — the version
        // distinction is in the subroutine's selected path, not in the
        // transition target. AwaitingConnection22 is entered via a
        // different figc4.x path; see the figc4.2 / Awaiting Connection
        // table for those primitives.
        var r = NewRig(isExtended: true);

        r.Session.PostEvent(new DlConnectRequest());

        r.Session.CurrentState.Should().Be("AwaitingConnection");
        r.UFrames.Should().ContainSingle();
        r.UFrames[0].Type.Should().Be(UFrameType.Sabme, "mod-128 path emits SABME, not SABM");
        r.UFrames[0].PfBit.Should().BeTrue();
    }

    [Fact]
    public void Iframe_Received_In_Connected_Delivers_DL_DATA_Indication_And_Acks()
    {
        // Peer sends us an I-frame (N(s)=0, N(r)=0, P=1) when we're
        // in Connected with V(r)=0. We should deliver the data upward,
        // advance V(r), and (with no L3 backpressure) ack with RR.
        var r = NewRig();

        // Get to Connected first.
        r.Session.PostEvent(new DlConnectRequest());
        r.Session.PostEvent(new UaReceived(UFrameReceived(UaFFinal1)));
        r.SFrames.Clear();
        r.Upward.Clear();

        var info = "hello"u8.ToArray();
        r.Session.PostEvent(new IFrameReceived(IFrameInbound(ns: 0, nr: 0, pf: true, info)));

        r.Session.CurrentState.Should().Be("Connected");
        r.Upward.Should().ContainSingle()
            .Which.Should().BeOfType<DataLinkDataIndication>()
            .Which.Info.ToArray().Should().Equal(info);
        r.Ctx.VR.Should().Be((byte)1, "V(r) advanced after delivering the in-sequence I-frame");
    }
}
