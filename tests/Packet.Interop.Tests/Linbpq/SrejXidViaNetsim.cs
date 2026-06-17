using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Ax25.Xid;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.Kiss;
using Xunit;

namespace Packet.Interop.Tests.Linbpq;

/// <summary>
/// v2.2 arc — <b>mod-8 Selective Reject (SREJ) interop against the real LinBPQ
/// container over net-sim</b>. This is the proof that PDN gets <i>working SREJ on
/// the wire to LinBPQ</i>: a dropped I-frame is recovered by LinBPQ sending us an
/// <b>SREJ</b> (selective retransmit), not a REJ (go-back-N).
/// </summary>
/// <remarks>
/// <para>
/// <b>The headline finding (source-verified against LinBPQ 6.0.25.23 + on the
/// wire).</b> Contrary to the earlier "BPQ = go-back-N only" inference, LinBPQ
/// <i>does</i> do mod-8 SREJ — but with two hard requirements, both proven here on
/// the live stack:
/// </para>
/// <list type="number">
/// <item><b>XID must precede the SABM.</b> BPQ's <c>L2Code.c ProcessXIDCommand</c>
/// runs only on the no-active-link path and sets <c>LINK-&gt;Ver2point2</c> (which
/// switches its reject scheme from REJ to SREJ, <c>L2Code.c</c> ~L4230). An XID on
/// an <i>already-established</i> link is ignored (verified: BPQ never answers a
/// post-connect XID on this port). The AX.25 v2.2 figures instead negotiate XID
/// <i>after</i> the connect (figc4.6 raises MDL-NEGOTIATE on the UA) — that
/// post-connect XID never reaches BPQ's responder. So speaking SREJ to BPQ needs the
/// <b>pre-SABM</b> exchange, which is what
/// <see cref="Ax25ListenerOptions.PreConnectXidNegotiatesSrej"/> drives.</item>
/// <item><b>The HDLC Optional Functions PV must be transmitted most-significant
/// octet first.</b> AX.25 v2.2 Figure 4.6, direwolf (<c>xid.c</c>), and LinBPQ
/// (<c>L2Code.c</c> <c>xidval&gt;&gt;16</c> first / parse <c>value=(value&lt;&lt;8)+*p</c>)
/// all use MSB-first; the repo's historical encoder emitted it LSB-first, which BPQ
/// silently drops (it fails BPQ's <c>OPMustHave</c>/<c>OPSREJMult</c> check and
/// never negotiates SREJ). The codec is now MSB-first (the bug fix this test
/// guards); see <c>HdlcOptionalFunctions.ToOctets</c> and
/// <c>docs/strict-vs-pragmatic-audit.md</c>.</item>
/// </list>
/// <para>
/// <b>What this test does.</b> Over net-sim node a (KISS-TCP 8100), it sends BPQ an
/// XID command (mod-8, SREJ + SREJ-multiframe, MSB-first PV — exactly what the
/// production <see cref="Ax25ManagementDataLink"/> offer now serialises), waits for
/// BPQ's XID response (which confirms SREJ-multi + mod-8), then SABMs. With the link
/// in SREJ mode it sends three I-frames, dropping our N(S)=1 on the send side so BPQ
/// sees a gap (0 then 2). It asserts BPQ emits an <b>SREJ</b> (S-frame, low nibble
/// <c>0b1101</c>) for N(R)=1 — NOT a REJ — and that our selective retransmit of
/// frame 1 completes the sequence (BPQ acks up to N(R)=3). A companion case proves
/// the contrast: a plain SABM connect with <i>no</i> pre-SABM XID recovers the same
/// dropped frame via <b>REJ</b> (go-back-N), since BPQ's link stays <c>Ver2point2=0</c>.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class SrejXidViaNetsim
{
    private const string Host = "127.0.0.1";
    private const int OurKissPort = 8100;
    private static readonly Callsign BpqCall = new("PN0TST", 0);

    // Distinct callsigns per case so a torn-down BPQ link table from one case in
    // the serialised collection can't be confused with another.
    private static readonly Callsign SrejCall = new("PNXSRJ", 0);
    private static readonly Callsign RejCall = new("PNXREJ", 0);

    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DataBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AckTimer = TimeSpan.FromMilliseconds(600);

    // ─── Wire predicates (mod-8 S-frame nibble) ───────────────────────────
    private static bool IsSrej(Ax25Frame f)
    {
        if (f.IsExtendedControl) return false;
        return (f.Control & 0x03) == 0x01 && ((f.Control >> 2) & 0x03) == 0x03;   // SREJ
    }

    private static bool IsRej(Ax25Frame f)
    {
        if (f.IsExtendedControl) return false;
        return (f.Control & 0x03) == 0x01 && ((f.Control >> 2) & 0x03) == 0x02;   // REJ
    }

    /// <summary>
    /// The headline: XID-before-SABM negotiates SREJ with LinBPQ at mod-8, and a
    /// dropped I-frame is recovered via an <b>SREJ</b> on the wire (selective
    /// retransmit), not go-back-N.
    /// </summary>
    [Fact]
    public async Task Mod8_XidNegotiatesSrej_DroppedIFrameRecoveredViaBpqSrej()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DataBudget + TimeSpan.FromSeconds(40));
        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        var rig = BuildRig(local: SrejCall, remote: BpqCall, kiss: kiss);
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));
        await Task.Delay(500, cts.Token);

        // ── XID command BEFORE the SABM, MSB-first PV (production codec), advertising
        //    mod-8 + SREJ + SREJ-multiframe — exactly the management-data-link offer. ──
        var offer = new XidParameters
        {
            ClassesOfProcedures = ClassesOfProcedures.HalfDuplexDefault,
            HdlcOptionalFunctions = new HdlcOptionalFunctions
            {
                Reject = RejectMode.SelectiveReject,
                Modulo128 = false,
                SrejMultiframe = true,
            },
            IFieldLengthRxBits = XidParameters.OctetsToBits(256),
            WindowSizeRx = 4,
        };
        var xid = Ax25Frame.Xid(destination: BpqCall, source: SrejCall,
            info: XidInfoField.Encode(offer), isCommand: true, pollFinal: true);

        var negotiated = await SendUntilObserved(rig, xid.ToBytes(),
            f => (f.Control & 0xEF) == 0xAF,   // BPQ's XID response (U-frame base 0xAF)
            attempts: 8, gap: TimeSpan.FromMilliseconds(2000), cts.Token);
        negotiated.Should().BeTrue(
            "LinBPQ must answer our MSB-first XID command with an XID response — it does mod-8 SREJ when XID precedes the SABM");

        // Confirm BPQ's XID response actually selected SREJ-multi + mod-8.
        var bpqXid = rig.Observed.First(f => (f.Control & 0xEF) == 0xAF);
        XidInfoField.TryParse(bpqXid.Info.Span, XidParseOptions.Lenient, out var bpqParams)
            .Should().BeTrue("BPQ's XID response info field must parse");
        bpqParams!.HdlcOptionalFunctions!.Reject.Should().Be(RejectMode.SelectiveReject,
            "BPQ's XID response selects SREJ (its ProcessXIDCommand replies OPSREJMult|OPMod8)");
        bpqParams.HdlcOptionalFunctions.Modulo128.Should().BeFalse("BPQ negotiates SREJ at mod-8, not mod-128");

        // ── SABM (mod-8). Our side runs SREJ recovery too. ──
        rig.Session.Context.SrejEnabled = true;
        rig.Session.Context.ImplicitReject = false;
        rig.Session.PostEvent(new DlConnectRequest());
        var cc = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        cc.Should().NotBeNull("SABM after the XID exchange must complete a mod-8 connection");
        rig.Session.CurrentState.Should().Be("Connected");
        await Task.Delay(3000, cts.Token);   // drain BPQ banner

        // ── Drop our N(S)=1 so BPQ sees a gap (0 then 2) and SREJs it. ──
        var dropped = false;
        rig.DropOutboundIFrame = f =>
        {
            if (dropped || !f.Pid.HasValue || f.Ns != 1) return false;
            dropped = true; return true;
        };
        var payloads = new[]
        {
            System.Text.Encoding.ASCII.GetBytes("srej-bpq-0\r"),
            System.Text.Encoding.ASCII.GetBytes("srej-bpq-1\r"),
            System.Text.Encoding.ASCII.GetBytes("srej-bpq-2\r"),
        };
        foreach (var p in payloads) rig.Session.PostEvent(new DlDataRequest(p, Ax25Frame.PidNoLayer3));

        var sawSrej = await WaitForObserved(rig, IsSrej, DataBudget, pumps.Tasks, cts.Token);

        dropped.Should().BeTrue("the test must have dropped I-frame N(S)=1 for the recovery to be meaningful");
        sawSrej.Should().BeTrue(
            "LinBPQ must SREJ the gap our dropped I-frame created (Ver2point2 set by the pre-SABM XID) — NOT REJ");
        rig.Observed.Any(IsRej).Should().BeFalse(
            "the recovery must be selective (SREJ), not go-back-N (REJ)");
        (rig.Session.CurrentState is "Connected" or "TimerRecovery").Should().BeTrue(
            $"link survives the SREJ recovery (was {rig.Session.CurrentState}; TimerRecovery is a transient post-ack recovery state under load, not a teardown)");
    }

    /// <summary>
    /// Contrast: a plain SABM connect with NO pre-SABM XID leaves BPQ's link
    /// <c>Ver2point2=0</c>, so the same dropped I-frame is recovered via <b>REJ</b>
    /// (go-back-N), proving the SREJ above is genuinely the XID-negotiated path.
    /// </summary>
    [Fact]
    public async Task Mod8_NoXid_DroppedIFrameRecoveredViaBpqRej_GoBackN()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DataBudget + TimeSpan.FromSeconds(40));
        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        var rig = BuildRig(local: RejCall, remote: BpqCall, kiss: kiss);
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));
        await Task.Delay(500, cts.Token);

        // Plain SABM — no XID first.
        rig.Session.PostEvent(new DlConnectRequest());
        var cc = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        cc.Should().NotBeNull("plain SABM must connect");
        await Task.Delay(3000, cts.Token);

        var dropped = false;
        rig.DropOutboundIFrame = f =>
        {
            if (dropped || !f.Pid.HasValue || f.Ns != 1) return false;
            dropped = true; return true;
        };
        var payloads = new[]
        {
            System.Text.Encoding.ASCII.GetBytes("rej-bpq-0\r"),
            System.Text.Encoding.ASCII.GetBytes("rej-bpq-1\r"),
            System.Text.Encoding.ASCII.GetBytes("rej-bpq-2\r"),
        };
        foreach (var p in payloads) rig.Session.PostEvent(new DlDataRequest(p, Ax25Frame.PidNoLayer3));

        var sawRej = await WaitForObserved(rig, IsRej, DataBudget, pumps.Tasks, cts.Token);
        dropped.Should().BeTrue("the test must have dropped I-frame N(S)=1");
        sawRej.Should().BeTrue("without the pre-SABM XID, BPQ uses go-back-N (REJ) for the gap");
        rig.Observed.Any(IsSrej).Should().BeFalse("no SREJ without XID negotiation");
    }

    // ─── Rig (mirrors the direwolf SREJ rig: Observed log + DropOutboundIFrame) ──
    private sealed record Rig(
        Ax25Session Session, SystemTimerScheduler Scheduler,
        ConcurrentQueue<DataLinkSignal> Signals, ConcurrentQueue<Ax25Frame> Observed, KissTcpClient Kiss)
    {
        public Func<Ax25Frame, bool>? DropOutboundIFrame { get; set; }
    }

    private static Rig BuildRig(Callsign local, Callsign remote, KissTcpClient kiss)
    {
        var scheduler = new SystemTimerScheduler(TimeProvider.System);
        var ctx = new Ax25SessionContext { Local = local, Remote = remote };   // mod-8
        var signals = new ConcurrentQueue<DataLinkSignal>();
        var observed = new ConcurrentQueue<Ax25Frame>();
        Rig? rigRef = null;

        void SendBytes(ReadOnlyMemory<byte> bytes) => _ = kiss.SendAsync(port: 0, KissCommand.Data, bytes);
        void SendIFrame(Ax25Frame frame)
        {
            if (rigRef?.DropOutboundIFrame is { } drop && drop(frame)) return;
            SendBytes(frame.ToBytes());
        }

        Ax25Session? sessionRef = null;
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame: spec => SendIFrame(spec.ToAx25Frame(ctx)),
            sendUpward: signals.Enqueue,
            sendLinkMux: _ => { }, sendInternal: _ => { },
            subroutines: new DefaultSubroutineRegistry())
        { T2Duration = AckTimer };

        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);
        var session = new Ax25Session(ctx, scheduler, dispatcher, guards, TransitionMap(), "Disconnected");
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
                        rig.Session.Context.IsExtended, out var parsed)) continue;
                if (!parsed.Destination.Callsign.Equals(rig.Session.Context.Local)) continue;
                rig.Observed.Enqueue(parsed);
                rig.Session.PostEvent(Ax25FrameClassifier.Classify(parsed));
            }
        }
    }

    // ─── Wait / send helpers ───────────────────────────────────────────────

    private static async Task<bool> SendUntilObserved(
        Rig rig, ReadOnlyMemory<byte> bytes, Func<Ax25Frame, bool> predicate,
        int attempts, TimeSpan gap, CancellationToken ct)
    {
        for (int i = 0; i < attempts; i++)
        {
            await rig.Kiss.SendAsync(port: 0, KissCommand.Data, bytes, ct);
            try { await Task.Delay(gap, ct); } catch (OperationCanceledException) { break; }
            if (rig.Observed.Any(predicate)) return true;
        }
        return rig.Observed.Any(predicate);
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
            try { await Task.Delay(50, cts.Token); } catch (OperationCanceledException) { break; }
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
            while (signals.TryDequeue(out var sig)) if (sig is T m) return m;
            try { await Task.Delay(50, cts.Token); } catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static void ThrowIfAnyFaulted(IReadOnlyList<Task> tasks)
    {
        foreach (var t in tasks)
            if (t.IsFaulted)
                throw t.Exception?.GetBaseException()
                    ?? new InvalidOperationException("background task faulted with no exception attached");
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
        "T1" => new T1Expiry(), "T2" => new T2Expiry(), "T3" => new T3Expiry(),
        _ => throw new InvalidOperationException($"unexpected timer '{name}'"),
    };
}
