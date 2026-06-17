using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Listener-level coverage of the §6.6 segmentation seam wired into
/// <see cref="Ax25Listener"/>: <see cref="Ax25Listener.SendData"/> routes an
/// over-N1 payload through the per-session <see cref="SegmentationLayer"/> on
/// send (so it leaves the modem as several PID-0x08 I-frames), rejects an
/// over-N1 payload cleanly when the segmenter is not negotiated, and the
/// receive-side reassembler is wired into the upward-signal fan-out.
/// </summary>
public class Ax25ListenerSegmentationTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall  = new("G7XYZ", 7);

    /// <summary>Accept an inbound SABM and return the Connected session, with the
    /// session's context customised via <paramref name="configure"/> before any
    /// events flow (so SegmenterReassemblerEnabled / N1 are set in time).</summary>
    private static async Task<(Ax25Listener Listener, LoopbackModem Modem, Ax25Session Session)>
        AcceptedSession(Action<Ax25SessionContext> configure)
    {
        var modem = new LoopbackModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            ConfigureSession = s => configure(s.Context),
        });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));   // the UA
        session.CurrentState.Should().Be("Connected");
        return (listener, modem, session);
    }

    [Fact]
    public async Task SendData_segments_an_over_N1_payload_into_PID_0x08_iframes_on_the_wire()
    {
        var (listener, modem, session) = await AcceptedSession(ctx =>
        {
            ctx.N1 = 64;
            ctx.K = 16;
            ctx.SegmenterReassemblerEnabled = true;
        });
        await using var _ = listener;

        var ua = modem.SentFrames.Count;   // frames already sent (the UA)
        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();   // 5 segments at N1=64

        // A non-default L3 PID so we can see it carried as the first-segment inner PID.
        listener.SendData(session, payload, Ax25Frame.PidNetRom);

        // Five I-frames, each carrying PID 0x08, should hit the modem.
        await modem.SentFrames.WaitForCountAsync(ua + 5, TimeSpan.FromSeconds(2));
        var iFrames = modem.SentFrames.SnapshotList().Skip(ua)
            .Select(b => { Ax25Frame.TryParse(b.Span, out var f); return f!; })
            .ToList();

        iFrames.Should().HaveCount(5);
        iFrames.Should().OnlyContain(f => Ax25FrameClassifier.Classify(f) is IFrameReceived,
            "each segment goes out as a normal I-frame");
        iFrames.Should().OnlyContain(f => f.Pid == Ax25Frame.PidSegmented,
            "every segment I-frame carries the segmented PID 0x08");

        // Default quirk (SegmentFirstCarriesL3Pid on): the FIRST segment's info
        // field is [F/X = First|count][inner-PID = original L3 PID][data…]. So the
        // first segment's second info octet is the original L3 PID, and subsequent
        // segments do NOT carry it.
        var firstInfo = iFrames[0].Info.ToArray();
        (firstInfo[0] & Segmenter.FirstBit).Should().NotBe(0, "segment 0 must be the First segment");
        firstInfo[1].Should().Be(Ax25Frame.PidNetRom,
            "the first segment carries the original L3 PID as the inner-PID octet (Dire Wolf's default format)");
    }

    [Fact]
    public async Task SendData_under_StrictlyFaithful_emits_the_figure_literal_format_without_an_inner_PID()
    {
        var (listener, modem, session) = await AcceptedSession(ctx =>
        {
            ctx.N1 = 64;
            ctx.K = 16;
            ctx.SegmenterReassemblerEnabled = true;
            ctx.Quirks = Ax25SessionQuirks.StrictlyFaithful;
        });
        await using var _ = listener;

        var ua = modem.SentFrames.Count;
        // payload[0] = 0, distinct from PidNetRom (0xCF), so we can tell the
        // first segment's second octet is payload, not an inner PID.
        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

        listener.SendData(session, payload, Ax25Frame.PidNetRom);

        await modem.SentFrames.WaitForCountAsync(ua + 5, TimeSpan.FromSeconds(2));
        var iFrames = modem.SentFrames.SnapshotList().Skip(ua)
            .Select(b => { Ax25Frame.TryParse(b.Span, out var f); return f!; })
            .ToList();

        iFrames.Should().HaveCount(5, "figure-literal: 300 bytes / (N1-1=63) = 5 segments (no inner-PID octet stealing a slot)");
        iFrames.Should().OnlyContain(f => f.Pid == Ax25Frame.PidSegmented);

        var firstInfo = iFrames[0].Info.ToArray();
        (firstInfo[0] & Segmenter.FirstBit).Should().NotBe(0, "segment 0 must be the First segment");
        firstInfo[1].Should().Be((byte)0,
            "figure-literal: the byte after the F/X octet is the first PAYLOAD byte (payload[0]=0), not an inner-PID octet");
    }

    [Fact]
    public async Task A_configured_N1_via_session_parameters_drives_segmentation_for_new_sessions()
    {
        // The per-port PACLEN path (packet.net#458): N1 is carried on the live-reseed
        // Ax25SessionParameters record (not Ax25ListenerOptions — that is parity-tracked),
        // seeded into a NEW session's context at build time. Prove a configured N1=64
        // (set via UpdateSessionParameters, exactly as the node-host PortSupervisor does
        // after constructing the listener) actually drives segmentation on the wire.
        var modem = new LoopbackModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            // The segmenter must be negotiated for the over-N1 SDU to segment rather
            // than throw; N1 itself comes from the session parameters below, not here.
            ConfigureSession = s => s.Context.SegmenterReassemblerEnabled = true,
        });
        await using var _ = listener;

        // Reseed N1 BEFORE the session is built (the node-host post-construction reseed).
        listener.UpdateSessionParameters(new Ax25SessionParameters { N1 = 64, K = 16 });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));   // the UA

        // The new session picked up the configured N1.
        session.Context.N1.Should().Be(64, "the per-port N1 seeds the new session's context");

        var ua = modem.SentFrames.Count;
        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();   // 5 segments at N1=64
        listener.SendData(session, payload, Ax25Frame.PidNetRom);

        await modem.SentFrames.WaitForCountAsync(ua + 5, TimeSpan.FromSeconds(2));
        var iFrames = modem.SentFrames.SnapshotList().Skip(ua)
            .Select(b => { Ax25Frame.TryParse(b.Span, out var f); return f!; })
            .ToList();
        iFrames.Should().HaveCount(5, "300 bytes segmented at the configured N1=64 → 5 I-frames");
        iFrames.Should().OnlyContain(f => f.Pid == Ax25Frame.PidSegmented);
    }

    [Fact]
    public async Task SendData_passes_a_within_N1_payload_as_a_single_iframe()
    {
        var (listener, modem, session) = await AcceptedSession(ctx =>
        {
            ctx.N1 = 256;
            ctx.SegmenterReassemblerEnabled = true;
        });
        await using var _ = listener;

        var ua = modem.SentFrames.Count;
        listener.SendData(session, new byte[100], Ax25Frame.PidNoLayer3);

        await modem.SentFrames.WaitForCountAsync(ua + 1, TimeSpan.FromSeconds(2));
        Ax25Frame.TryParse(modem.SentFrames[ua].Span, out var f).Should().BeTrue();
        f!.Pid.Should().Be(Ax25Frame.PidNoLayer3, "a within-N1 payload passes through with its L3 PID, unsegmented");
    }

    [Fact]
    public async Task SendData_rejects_an_over_N1_payload_when_the_segmenter_is_not_negotiated()
    {
        var (listener, _, session) = await AcceptedSession(ctx =>
        {
            ctx.N1 = 256;
            ctx.SegmenterReassemblerEnabled = false;   // v2.0 / not negotiated
        });
        await using var _ = listener;

        var act = () => listener.SendData(session, new byte[300]);

        act.Should().Throw<InvalidOperationException>(
            "an over-N1 payload on a non-segmenter session must be rejected cleanly")
            .WithMessage("*segmenter/reassembler has not been negotiated*");
    }

    [Fact]
    public async Task SendData_rejects_a_session_the_listener_does_not_own()
    {
        var (listener, _, _) = await AcceptedSession(_ => { });
        await using var _ = listener;

        // A session this listener never built (never driven — just constructable).
        var alien = new Ax25SessionContext { Local = LocalCall, Remote = new Callsign("M5ABC", 3) };
        var scheduler = new SystemTimerScheduler(TimeProvider.System);
        var dispatcher = new ActionDispatcher(onTimerExpiry: _ => { }, sendSFrame: _ => { });
        var guards = new GuardEvaluator(Ax25SessionBindings.CreateDefault(alien, scheduler));
        var alienSession = new Ax25Session(
            alien, scheduler, dispatcher, guards,
            new Dictionary<string, IReadOnlyList<Sdl.TransitionSpec>>
            {
                ["Disconnected"] = Array.Empty<Sdl.TransitionSpec>(),
            },
            "Disconnected");

        var act = () => listener.SendData(alienSession, new byte[10]);
        act.Should().Throw<ArgumentException>().WithMessage("*not owned by this listener*");
    }

    /// <summary>
    /// Accept an inbound SABM and return the Connected session, additionally
    /// wiring a DL-DATA-indication observer onto the session's signal stream
    /// (before any events flow) so the test can see exactly what the receive-side
    /// segmentation seam delivers upward.
    /// </summary>
    private static async Task<(Ax25Listener Listener, LoopbackModem Modem, Ax25Session Session,
        ConcurrentQueue<DataLinkDataIndication> Delivered)>
        AcceptedSessionObservingData(Action<Ax25SessionContext> configure)
    {
        var delivered = new ConcurrentQueue<DataLinkDataIndication>();
        var modem = new LoopbackModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            ConfigureSession = s =>
            {
                configure(s.Context);
                s.DataLinkSignalEmitted += (_, sig) =>
                {
                    if (sig is DataLinkDataIndication d) delivered.Enqueue(d);
                };
            },
        });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));   // the UA
        session.CurrentState.Should().Be("Connected");
        return (listener, modem, session, delivered);
    }

    /// <summary>
    /// The receive-path hardening (FINDINGS.md 2026-06-03): a malformed PID-0x08
    /// segment arriving off the wire through the real <see cref="Ax25Listener"/>
    /// pump is dropped cleanly at the <see cref="SegmentationLayer"/> seam — no
    /// DL-DATA indication surfaces, the pump survives, and (the reset half) a valid
    /// segmented series delivered immediately afterwards still reassembles intact.
    /// This proves the fix does not lean on the listener's inbound catch-all.
    /// </summary>
    [Fact]
    public async Task Receive_drops_a_malformed_segment_off_the_wire_then_reassembles_a_following_valid_series()
    {
        var (listener, modem, _, delivered) = await AcceptedSessionObservingData(ctx =>
        {
            ctx.N1 = 16;
            ctx.SegmenterReassemblerEnabled = true;
            ctx.Quirks = Ax25SessionQuirks.StrictlyFaithful;   // figure-literal: no inner-PID octet
        });
        await using var _ = listener;

        // A malformed segment off the wire: a non-First (First bit clear) segment
        // with no in-progress series. Carried as a normal I-frame, PID 0x08, N(S)=0.
        // The I-frame is sequence-valid (so V(R) advances), but its info field is a
        // protocol-violating segment — it must be dropped at the seam, delivering
        // nothing upward, without throwing through the pump.
        modem.InjectInbound(Ax25Frame.I(LocalCall, PeerCall, nr: 0, ns: 0,
            info: new byte[] { 0x05, 0xAA, 0xBB }, pid: Ax25Frame.PidSegmented));

        // A valid single-segment series immediately afterwards (N(S)=1): First bit
        // set, remaining 0, one data byte 0x42. If the malformed segment had
        // poisoned the reassembler — or thrown through the pump and wedged it —
        // this would not reassemble.
        modem.InjectInbound(Ax25Frame.I(LocalCall, PeerCall, nr: 0, ns: 1,
            info: new byte[] { Segmenter.FirstBit | 0, 0x42 }, pid: Ax25Frame.PidSegmented));

        await ListenerTestSupport.WaitFor(() => !delivered.IsEmpty, TimeSpan.FromSeconds(2),
            "the valid series after the dropped malformed segment must reassemble and surface");

        delivered.Should().HaveCount(1,
            "only the valid series is delivered — the malformed segment surfaced nothing");
        delivered.TryPeek(out var ind).Should().BeTrue();
        ind!.Info.ToArray().Should().Equal(new byte[] { 0x42 },
            "the reassembled payload is exactly the valid series' data — no leakage from the malformed one");
        ind.Pid.Should().Be(SegmentationLayer.FigureLiteralReassembledPid);
    }
}
