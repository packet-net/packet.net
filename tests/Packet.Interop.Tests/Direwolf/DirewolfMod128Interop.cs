using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.Kiss;
using Xunit;

namespace Packet.Interop.Tests.Direwolf;

/// <summary>
/// v2.2 arc V5b — independent mod-128 (SABME / extended) interop against a
/// real Dire Wolf (WB2OSZ) 1.8.1 connected-mode engine. This is the
/// real-peer leg of V5b: our in-process conformance suites prove our stack
/// against our own SDL reading; Dire Wolf is the figure-faithful oracle
/// (<c>ax25_link.c</c> implements the figc4.x state machine, including
/// <c>state_5_awaiting_v22_connection</c> and <c>modulo == 128</c>) and the
/// <b>only</b> mod-128-capable peer in the matrix — LinBPQ FRMRs SABME
/// (packet.net#276), so the genuine mod-128 rows can only be proven here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Topology — why this peer is not on net-sim.</b> Dire Wolf's
/// connected-mode engine is reachable <i>only</i> over the AGW protocol with
/// a client app that has registered the called callsign on a real
/// (<c>MEDIUM_RADIO</c>, audio) channel — raw KISS bypasses
/// <c>ax25_link.c</c> entirely, and its one KISS-TCP-<i>client</i> mode
/// (<c>NCHANNEL</c>) is a <c>MEDIUM_NETTNC</c> channel on which every AGW
/// connected-mode request is hard-<c>assert</c>ed out (<c>chan &lt;
/// MAX_RADIO_CHANS</c>). net-sim, in turn, exposes no external audio ingress
/// in the pinned image, so an external Dire Wolf modem cannot join its RF
/// mixer. The <c>docker/direwolf</c> container therefore runs the issue's
/// two-direwolf audio-loopback path internally: two Dire Wolf radio channels
/// share one PulseAudio null sink as the simulated AFSK1200 RF channel.
/// <c>direwolf-gw</c> is a transparent KISS modem (we dial its KISS-TCP,
/// exposed on host <see cref="OurKissPort"/>) and <c>direwolf-resp</c> runs
/// the connected-mode engine, driven by the in-container AGW echo helper.
/// The AX.25 exchange under test is genuinely between our
/// <see cref="Ax25Session"/> and Dire Wolf's <c>ax25_link.c</c> over a real
/// 1200-baud AFSK modem link.
/// </para>
/// <para>
/// <b>The headline finding (verified against Dire Wolf 1.8.1 source + on the
/// wire).</b> Dire Wolf, as the responder, accepts an inbound SABME at
/// modulo-128 with <b>no config knob</b> for the incoming path — the opposite
/// of LinBPQ. <c>sabm_e_frame</c> in state 0 (disconnected) does
/// <c>if (extended) set_version_2_2(S)</c> — which sets <c>modulo = 128</c>
/// and <c>srej_enable = srej_single</c> — then answers UA. (<c>maxv22</c>
/// gates only the <i>outgoing</i> SABME-attempt count, not the incoming
/// path.) Confirmed on the wire: SABME → UA, then a two-octet-control,
/// 7-bit-N(S)/N(R) I-frame round-trip.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>; the
/// <c>direwolf</c> service goes healthy once both Dire Wolf instances are up
/// and the AGW echo helper has registered the called callsigns below.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class DirewolfMod128Interop
{
    private const string Host = "127.0.0.1";

    // direwolf-gw's KISS-TCP, exposed on the host. Distinct from net-sim's
    // 8100/8101 — Dire Wolf is its own self-contained RF (see class remarks),
    // so it does not share net-sim's listeners. Still in the serialised
    // Netsim collection because the docker stack as a whole is brought up /
    // torn down together and parallel runs would contend on docker.
    private const int OurKissPort = 8106;

    // Each test connects to a distinct called callsign — all registered by
    // the AGW echo helper via compose's AGW_REGISTER — so a torn-down link
    // from one test cannot be confused with another in the serialised run.
    // Callsign base is max 6 chars. Distinct per test case; all registered by
    // the AGW echo helper via compose's AGW_REGISTER.
    private static readonly Callsign Connect128 = new("PNDW28", 0);
    private static readonly Callsign ConnectBidi = new("PNDWBI", 0);
    private static readonly Callsign ConnectSrej = new("PNDWSR", 0);
    private static readonly Callsign ConnectSeg = new("PNDWSG", 0);
    private static readonly Callsign ConnectXid = new("PNDWXI", 0);

    private static readonly Callsign OurCall = new("PNTEST", 0);

    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan DataBudget = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(30);

    // Faster RR-ack turnaround on the shared half-duplex AFSK channel — same
    // rationale as the LinBPQ net-sim tests. T1/T3 keep spec defaults so
    // retransmit/recovery timing is unchanged.
    private static readonly TimeSpan AckTimer = TimeSpan.FromMilliseconds(700);

    // U-frame base control octets (P/F masked out) for wire assertions.
    private const int UaBase = 0x63;
    private const int DmBase = 0x0F;

    private static int UBase(Ax25Frame f) => f.Control & 0xEF;

    /// <summary>
    /// Case (a) — connect-extended. Our extended-preferred DL-CONNECT emits a
    /// SABME; Dire Wolf answers UA and runs the link at modulo-128. We assert
    /// we reach Connected, that the context is still extended (no FRMR
    /// fallback — Dire Wolf does <i>not</i> reject SABME the way LinBPQ does),
    /// and that a UA addressed to us appeared on the wire. Then DISC/UA tears
    /// it down. This is the cleanest possible proof of the headline: a real
    /// third-party stack establishing a mod-128 link with us over a real AFSK
    /// modem.
    /// </summary>
    [Fact]
    public async Task ExtendedConnect_DirewolfAnswersUaAtMod128_ThenDisconnects()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DisconnectBudget + TimeSpan.FromSeconds(15));
        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        var rig = BuildRig(local: OurCall, remote: Connect128, kiss: kiss);
        rig.Session.Context.IsExtended.Should().BeTrue("the rig must start extended so DL-CONNECT initiates a SABME");

        // `await using` so the pump is cancelled + awaited on EVERY exit path
        // (pass, assertion-failure, throw, timeout), not just at the happy-path end
        // — declared after `cts` so it disposes first. See InboundPumpScope.
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));
        await Task.Delay(500, cts.Token);

        rig.Session.PostEvent(new DlConnectRequest());

        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        connectConfirm.Should().NotBeNull("Dire Wolf must accept our SABME and reply UA, taking us to Connected at mod-128");
        rig.Session.CurrentState.Should().Be("Connected");
        rig.Session.Context.IsExtended.Should().BeTrue(
            "Dire Wolf accepts SABME at mod-128 (set_version_2_2) — there is no FRMR fallback as there is with LinBPQ");

        rig.Observed.Should().Contain(f => UBase(f) == UaBase,
            "Dire Wolf must answer our SABME with a UA addressed to us");
        rig.Observed.Should().NotContain(f => UBase(f) == DmBase,
            "Dire Wolf must NOT answer SABME with DM (it is v2.2-capable on the incoming path)");

        rig.Session.PostEvent(new DlDisconnectRequest());
        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps.Tasks, cts.Token);
        disconnectConfirm.Should().NotBeNull("Dire Wolf must reply UA to our DISC, taking us to Disconnected");
        rig.Session.CurrentState.Should().Be("Disconnected");
    }

    /// <summary>
    /// Case (b) — bidirectional mod-128 transfer. After the mod-128 connect,
    /// send a connected-data payload; the AGW echo helper bounces it back, so
    /// we receive our payload as a DL-DATA indication. Asserts the round-trip
    /// AND that Dire Wolf's I-frame carrying the echo used a <b>two-octet
    /// (extended) control field</b> with 7-bit N(S)/N(R) — the on-the-wire
    /// proof that the session is genuinely modulo-128, not modulo-8. (A mod-8
    /// I-frame has a single control octet.)
    /// </summary>
    [Fact]
    public async Task BidirectionalTransfer_Mod128_PayloadRoundTrips_With7BitSequencing()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DataBudget + DisconnectBudget + TimeSpan.FromSeconds(20));
        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        var rig = BuildRig(local: OurCall, remote: ConnectBidi, kiss: kiss);
        // `await using` so the pump is cancelled + awaited on EVERY exit path
        // (pass, assertion-failure, throw, timeout), not just at the happy-path end
        // — declared after `cts` so it disposes first. See InboundPumpScope.
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));
        await Task.Delay(500, cts.Token);

        rig.Session.PostEvent(new DlConnectRequest());
        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        connectConfirm.Should().NotBeNull("must complete the mod-128 handshake before data exchange");
        rig.Session.Context.IsExtended.Should().BeTrue("link must be mod-128");

        var payload = System.Text.Encoding.ASCII.GetBytes("mod128-roundtrip-via-direwolf");
        rig.Session.PostEvent(new DlDataRequest(payload, Ax25Frame.PidNoLayer3));

        var echo = await WaitForMatchingData(rig, payload, DataBudget, pumps.Tasks, cts.Token);
        echo.Should().NotBeNull("the AGW echo helper must bounce our connected-mode payload back to us");

        // Wire proof of mod-128: at least one I-frame from Dire Wolf addressed
        // to us must have a two-octet (extended) control field. ExtendedControl
        // is non-null only when the frame was parsed in extended mode AND is an
        // I/S frame; an I-frame additionally has a non-null Pid.
        rig.Observed.Should().Contain(
            f => f.Destination.Callsign.Equals(OurCall)
                 && f.IsExtendedControl
                 && f.Pid.HasValue,
            "Dire Wolf's echo I-frame must use a two-octet extended control field (modulo-128 sequencing on the wire)");

        rig.Session.CurrentState.Should().Be("Connected", "link must survive the data round-trip");

        rig.Session.PostEvent(new DlDisconnectRequest());
        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps.Tasks, cts.Token);
        disconnectConfirm.Should().NotBeNull("clean DISC/UA close after data exchange");
        rig.Session.CurrentState.Should().Be("Disconnected");
    }

    /// <summary>
    /// Case (c) — SREJ-128 loss recovery. Dire Wolf, as a v2.2 responder, runs
    /// single SREJ by default (<c>set_version_2_2</c> sets
    /// <c>srej_enable = srej_single</c>). To exercise SREJ deterministically on
    /// a medium that doesn't drop frames on its own, the rig silently drops
    /// exactly one of <i>our</i> outbound I-frames (N(S)=1) on the send side —
    /// simulating a frame lost on Dire Wolf's receive path. Dire Wolf then has
    /// a gap, sends us an <b>SREJ</b> requesting N(R)=1, and our figc4.5
    /// SREJ-received path selectively retransmits that single frame. Once Dire
    /// Wolf has the full in-order sequence it reassembles and the echo helper
    /// bounces all payloads back. Asserts every payload round-trips (recovery
    /// completed) AND that an SREJ from Dire Wolf appeared on the wire (the
    /// recovery genuinely went through SREJ, not a plain T1 retransmit).
    /// </summary>
    [Fact]
    public async Task SrejRecovery_Mod128_DirewolfSrejsDroppedIFrame_WeSelectivelyRetransmit()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DataBudget + DisconnectBudget + TimeSpan.FromSeconds(40));
        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        // SREJ on our side too so our SREJ-received selective-retransmit path
        // (figc4.5, single-frame) is the recovery mechanism, matching Dire
        // Wolf's srej_single. SABME alone doesn't negotiate SREJ for us — set
        // it explicitly, as the in-process SREJ conformance tests do.
        var rig = BuildRig(local: OurCall, remote: ConnectSrej, kiss: kiss,
            configure: ctx => { ctx.SrejEnabled = true; ctx.ImplicitReject = false; });

        // `await using` so the pump is cancelled + awaited on EVERY exit path
        // (pass, assertion-failure, throw, timeout), not just at the happy-path end
        // — declared after `cts` so it disposes first. See InboundPumpScope.
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));
        await Task.Delay(500, cts.Token);

        rig.Session.PostEvent(new DlConnectRequest());
        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        connectConfirm.Should().NotBeNull("must complete the mod-128 handshake first");

        // Arm the send-side drop: the FIRST time we transmit the I-frame with
        // N(S)=1, swallow it. Its retransmit (after Dire Wolf's SREJ) goes out
        // normally because we drop only once.
        var dropped = false;
        rig.DropOutboundIFrame = f =>
        {
            if (dropped || !f.Pid.HasValue) return false;
            if (f.Ns != 1) return false;
            dropped = true;
            return true;
        };

        // Three payloads → N(S) 0,1,2. We drop N(S)=1 on first send; Dire Wolf
        // receives 0 then 2 (gap at 1) → SREJ(1) → we retransmit frame 1.
        var payloads = new[]
        {
            System.Text.Encoding.ASCII.GetBytes("srej-seq-zero"),
            System.Text.Encoding.ASCII.GetBytes("srej-seq-one"),
            System.Text.Encoding.ASCII.GetBytes("srej-seq-two"),
        };
        foreach (var p in payloads) rig.Session.PostEvent(new DlDataRequest(p, Ax25Frame.PidNoLayer3));

        foreach (var p in payloads)
        {
            var got = await WaitForMatchingData(rig, p, DataBudget, pumps.Tasks, cts.Token);
            got.Should().NotBeNull(
                $"payload {System.Text.Encoding.ASCII.GetString(p)!} must be echoed back after SREJ recovery completes");
        }

        dropped.Should().BeTrue("the test must actually have dropped I-frame N(S)=1 for the recovery to be meaningful");
        rig.Observed.Should().Contain(f => IsSrej(f),
            "Dire Wolf must send an SREJ for the gap our dropped I-frame created (srej_single is on by default for a v2.2 responder)");

        rig.Session.PostEvent(new DlDisconnectRequest());
        await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps.Tasks, cts.Token);
    }

    /// <summary>
    /// Case (d) — segmentation round-trip, <b>both directions</b>. Now GREEN via
    /// the default-on <see cref="Ax25SessionQuirks.SegmentFirstCarriesL3Pid"/>
    /// quirk: our <see cref="SegmentationLayer"/> interoperates with Dire Wolf's
    /// segmentation format out of the box. AX.25 v2.2 Figure 6.2 draws a segment's
    /// info field as one F/X control octet (<c>FXXXXXXX</c>) followed directly by
    /// payload — with <b>no field carrying the original Layer-3 PID</b>. Dire Wolf
    /// 1.8.1 — the only known v2.2 segmenter — reads §6.6's "two-octet header"
    /// prose to mean its <b>first</b> segment carries an extra <b>inner-PID
    /// octet</b> (the original L3 PID) between the F/X octet and the data
    /// (<c>ax25_link.c</c> <c>dl_data_request</c> ~L1330–1410 / <c>dl_data_indication</c>
    /// ~L2010–2030: first segment <c>[F/X][original PID][data]</c>, subsequent
    /// <c>[F/X][data]</c>; the inner octet counts toward the segment budget via
    /// <c>DIVROUNDUP(len + 1, N1 − 1)</c>). The repo defaults to matching Dire
    /// Wolf's format (de-facto interop) and reproduces the figure-literal format
    /// only under <see cref="Ax25SessionQuirks.StrictlyFaithful"/>. With the quirk
    /// on (default) both directions are wire-compatible:
    /// <list type="bullet">
    /// <item>Forward (us → Dire Wolf): our segmenter emits Dire Wolf's format
    /// (our N1=64 forces several segments), Dire Wolf reassembles the exact
    /// payload and the echo helper bounces it back.</item>
    /// <item>Reverse (Dire Wolf → us): Dire Wolf re-segments the 220-byte echo
    /// (its PACLEN/N1 = 128 forces it), and our reassembler — now reading the
    /// first-segment inner-PID octet — reconstructs the exact original payload.</item>
    /// </list>
    /// We assert the exact-payload round-trip via our own receive-side
    /// <see cref="SegmentationLayer"/> reassembling Dire Wolf's segment I-frames.
    /// (The §6.6 / Figure 6.2 spec gap — the two-octet header loses the L3 PID and
    /// Dire Wolf fills it non-standardly — remains a candidate ax25spec
    /// clarification, deliberately not filed from this work.)
    /// </summary>
    [SkippableFact]
    public async Task Segmentation_Mod128_RoundTripsBothDirections_ViaDirewolfInnerPidQuirk()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DataBudget + DisconnectBudget + TimeSpan.FromSeconds(20));
        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        // N1 = 128 to MATCH Dire Wolf's responder PACLEN=128 (its config). N1 is a
        // symmetric link parameter, so our receiver must accept Dire Wolf's
        // ≤128-byte segments — set lower (e.g. 64) and the in-frame "info field ≤
        // N1" check (timer_recovery t22 / connected) trips DL-ERROR(O) +
        // re-establish on Dire Wolf's 128-byte echo segments. With N1=128 a >128B
        // payload still forces US to segment on the forward leg, and Dire Wolf's
        // PACLEN=128 forces it to segment the echo on the reverse leg — so both
        // directions exercise real segmentation while staying within N1.
        var rig = BuildRig(local: OurCall, remote: ConnectSeg, kiss: kiss,
            configure: ctx => { ctx.N1 = 128; ctx.K = 16; ctx.SegmenterReassemblerEnabled = true; });

        // Send-side and receive-side §6.6 shims over the same session context (so
        // both honour the default SegmentFirstCarriesL3Pid quirk). The send shim
        // emits Dire Wolf's format; the receive shim reassembles Dire Wolf's
        // segments back into the original payload.
        var sendSeg = new SegmentationLayer(rig.Session.Context);
        var recvSeg = new SegmentationLayer(rig.Session.Context);

        // `await using` so the pump is cancelled + awaited on EVERY exit path
        // (pass, assertion-failure, throw, timeout), not just at the happy-path end
        // — declared after `cts` so it disposes first. See InboundPumpScope.
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));
        await Task.Delay(500, cts.Token);

        rig.Session.PostEvent(new DlConnectRequest());
        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        connectConfirm.Should().NotBeNull("must complete the mod-128 handshake first");

        var payload = new byte[220];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('A' + (i % 26));

        var requests = sendSeg.BuildSendRequests(payload, Ax25Frame.PidNoLayer3);
        requests.Count.Should().BeGreaterThan(1, "the test payload must exceed N1 so our segmenter splits it into multiple I-frames");
        foreach (var req in requests) rig.Session.PostEvent(req);

        // Round-trip both directions: Dire Wolf reassembles our segments, the echo
        // helper bounces the payload, Dire Wolf re-segments the echo, and our
        // receive-side shim reassembles it back to the exact original payload. The
        // session surfaces each inbound 0x08 segment as its own DL-DATA indication
        // (PID 0x08); we feed them through recvSeg until it yields the reassembly.
        var reassembled = await WaitForReassembledPayload(rig, recvSeg, payload, DataBudget, pumps.Tasks, cts.Token);
        reassembled.Should().NotBeNull(
            "Dire Wolf must reassemble our segments (forward), echo the payload, re-segment it (reverse), and our " +
            "reassembler must reconstruct the exact original payload — the default inner-PID quirk makes both legs wire-compatible");

        rig.Session.PostEvent(new DlDisconnectRequest());
        await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps.Tasks, cts.Token);
    }

    /// <summary>
    /// Case (e) — XID parameter negotiation. Probe whether Dire Wolf responds
    /// to an XID command on the incoming-connect path. From the 1.8.1 source,
    /// Dire Wolf initiates XID only when it is the <i>connection initiator</i>
    /// (state 5 after sending SABME + receiving UA); as the responder it stays
    /// in Connected and only <i>answers</i> an XID command. So we connect
    /// mod-128, send an XID command, and assert Dire Wolf replies with an XID
    /// response (U-frame base 0xAF) addressed to us. The link stays up
    /// throughout. (If a future Dire Wolf changes the responder XID behaviour
    /// this test will surface it.)
    /// </summary>
    [Fact]
    public async Task XidNegotiation_Mod128_DirewolfAnswersXidCommand()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DataBudget + DisconnectBudget + TimeSpan.FromSeconds(20));
        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        var rig = BuildRig(local: OurCall, remote: ConnectXid, kiss: kiss);
        // `await using` so the pump is cancelled + awaited on EVERY exit path
        // (pass, assertion-failure, throw, timeout), not just at the happy-path end
        // — declared after `cts` so it disposes first. See InboundPumpScope.
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));
        await Task.Delay(500, cts.Token);

        rig.Session.PostEvent(new DlConnectRequest());
        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        connectConfirm.Should().NotBeNull("must complete the mod-128 handshake first");

        // Build and send an XID command (mod-128 / SREJ defaults) directly
        // over KISS — our Rx capabilities for Dire Wolf to negotiate against.
        var xidInfo = Packet.Ax25.Xid.XidInfoField.Encode(new Packet.Ax25.Xid.XidParameters
        {
            HdlcOptionalFunctions = Packet.Ax25.Xid.HdlcOptionalFunctions.Default,
            IFieldLengthRxBits = Packet.Ax25.Xid.XidParameters.OctetsToBits(rig.Session.Context.N1),
            WindowSizeRx = rig.Session.Context.K,
        });
        var xidFrame = Ax25Frame.Xid(
            destination: rig.Session.Context.Remote,
            source: rig.Session.Context.Local,
            info: xidInfo,
            isCommand: true,
            pollFinal: true);
        await kiss.SendAsync(port: 0, KissCommand.Data, xidFrame.ToBytes(), cts.Token);

        var sawXidResponse = await WaitForObserved(rig,
            f => f.Destination.Callsign.Equals(OurCall) && UBase(f) == 0xAF,
            DataBudget, pumps.Tasks, cts.Token);
        sawXidResponse.Should().BeTrue(
            "Dire Wolf must answer our XID command with an XID response (U-frame base 0xAF) — it responds to XID on the incoming path even though it only initiates XID when it is the connection initiator");

        rig.Session.CurrentState.Should().Be("Connected", "the link must survive the XID exchange");

        rig.Session.PostEvent(new DlDisconnectRequest());
        await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps.Tasks, cts.Token);
    }

    // ─── Wire predicates ──────────────────────────────────────────────────

    private static bool IsSrej(Ax25Frame f)
    {
        // SREJ is an S-frame. In extended mode the S-frame type lives in the
        // low nibble of the first control octet: 0b1101 = SREJ. Mask the
        // low byte: SREJ = 0x0D in bits 3..0 with bit0/1 = 01 (S-frame).
        if (!f.IsExtendedControl || f.Pid.HasValue) return false;   // must be an extended S-frame
        int low = f.Control & 0x0F;
        return low == 0x0D;
    }

    // ─── Rig (mirrors LinbpqViaNetsimExtendedMode.BuildRig) ────────────────

    private sealed record Rig(
        Ax25Session Session,
        SystemTimerScheduler Scheduler,
        ConcurrentQueue<DataLinkSignal> Signals,
        ConcurrentQueue<Ax25Frame> Observed,
        KissTcpClient Kiss)
    {
        /// <summary>
        /// Optional send-side loss hook for the SREJ test. When set, an
        /// outbound I-frame for which this returns true is NOT transmitted —
        /// simulating a frame lost on Dire Wolf's receive side, which makes
        /// Dire Wolf (srej_single) send us an SREJ and exercises our figc4.5
        /// SREJ-received selective-retransmit path. Returns false for every
        /// other frame.
        /// </summary>
        public Func<Ax25Frame, bool>? DropOutboundIFrame { get; set; }
    }

    private static Rig BuildRig(Callsign local, Callsign remote, KissTcpClient kiss, Action<Ax25SessionContext>? configure = null)
    {
        var scheduler = new SystemTimerScheduler(TimeProvider.System);
        var ctx = new Ax25SessionContext { Local = local, Remote = remote, IsExtended = true };
        configure?.Invoke(ctx);
        var signals = new ConcurrentQueue<DataLinkSignal>();
        var observed = new ConcurrentQueue<Ax25Frame>();

        Rig? rigRef = null;

        void SendBytes(ReadOnlyMemory<byte> bytes)
            => _ = kiss.SendAsync(port: 0, KissCommand.Data, bytes);

        // I-frame send path with an optional drop hook (SREJ test). The frame
        // is built once, optionally dropped, otherwise serialised and sent.
        void SendIFrame(Ax25Frame frame)
        {
            if (rigRef?.DropOutboundIFrame is { } drop && drop(frame)) return;
            SendBytes(frame.ToBytes());
        }

        Ax25Session? sessionRef = null;
        var subroutines = new DefaultSubroutineRegistry();
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame: spec => SendIFrame(spec.ToAx25Frame(ctx)),
            sendUpward: signals.Enqueue,
            sendLinkMux: _ => { },
            sendInternal: _ => { },
            subroutines: subroutines)
        {
            T2Duration = AckTimer,
        };

        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(ctx, scheduler, dispatcher, guards,
            transitionsByState: TransitionMap(),
            initialState: "Disconnected");
        sessionRef = session;
        rigRef = new Rig(session, scheduler, signals, observed, kiss);
        return rigRef;
    }

    private static async Task InboundPump(Rig rig, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<KissFrame> frames;
            try { frames = await rig.Kiss.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (System.IO.IOException) { return; }

            foreach (var f in frames)
            {
                if (f.Command != KissCommand.Data) continue;

                if (!Ax25Frame.TryParse(f.Payload, Ax25ParseOptions.Lenient,
                        rig.Session.Context.IsExtended, out var parsed))
                    continue;

                // The AFSK channel is broadcast — only react to frames
                // addressed to our local callsign.
                if (!parsed.Destination.Callsign.Equals(rig.Session.Context.Local)) continue;

                rig.Observed.Enqueue(parsed);
                rig.Session.PostEvent(Ax25FrameClassifier.Classify(parsed));
            }
        }
    }

    // ─── Wait helpers ──────────────────────────────────────────────────────

    private static async Task<DataLinkDataIndication?> WaitForMatchingData(
        Rig rig, byte[] expected, TimeSpan budget, IReadOnlyList<Task> bg, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            ThrowIfAnyFaulted(bg);
            while (rig.Signals.TryDequeue(out var sig))
            {
                if (sig is DataLinkDataIndication ind && ind.Info.Span.SequenceEqual(expected))
                    return ind;
            }
            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Drain DL-DATA indications the session raises and feed each through the
    /// receive-side <see cref="SegmentationLayer"/> shim (which reassembles 0x08
    /// segment series and passes non-segment indications through). Returns the
    /// reassembled payload once the shim yields one equal to <paramref name="expected"/>,
    /// or <c>null</c> on timeout. (Dire Wolf segments its 220-byte echo because its
    /// PACLEN=128, so the inbound arrives as several 0x08 indications.)
    /// </summary>
    private static async Task<byte[]?> WaitForReassembledPayload(
        Rig rig, SegmentationLayer recvSeg, byte[] expected, TimeSpan budget, IReadOnlyList<Task> bg, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            ThrowIfAnyFaulted(bg);
            while (rig.Signals.TryDequeue(out var sig))
            {
                if (sig is not DataLinkDataIndication ind) continue;
                var reassembled = recvSeg.OnDataIndication(ind);
                if (reassembled is not null && reassembled.Info.Span.SequenceEqual(expected))
                    return reassembled.Info.ToArray();
            }
            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static async Task<bool> WaitForObserved(
        Rig rig, Func<Ax25Frame, bool> predicate, TimeSpan budget, IReadOnlyList<Task> bg, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            ThrowIfAnyFaulted(bg);
            if (rig.Observed.Any(predicate)) return true;
            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return false; }
        }
        return rig.Observed.Any(predicate);
    }

    private static async Task<T?> WaitForSignal<T>(
        ConcurrentQueue<DataLinkSignal> signals, TimeSpan budget, IReadOnlyList<Task> bg, CancellationToken outer)
        where T : DataLinkSignal
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            ThrowIfAnyFaulted(bg);
            while (signals.TryDequeue(out var sig))
            {
                if (sig is T match) return match;
            }
            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static void ThrowIfAnyFaulted(IReadOnlyList<Task> tasks)
    {
        foreach (var t in tasks)
        {
            if (t.IsFaulted)
            {
                throw t.Exception?.GetBaseException()
                    ?? new InvalidOperationException("background task faulted with no exception attached");
            }
        }
    }

    private static Dictionary<string, IReadOnlyList<TransitionSpec>> TransitionMap() => new()
    {
        ["Disconnected"] = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"] = DataLink_AwaitingConnection.Transitions,
        ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
        ["Connected"] = DataLink_Connected.Transitions,
        ["AwaitingRelease"] = DataLink_AwaitingRelease.Transitions,
        ["TimerRecovery"] = DataLink_TimerRecovery.Transitions,
    };

    private static Ax25Event TimerExpiry(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _ => throw new InvalidOperationException($"unexpected timer expiry name '{name}'"),
    };
}
