using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
using Packet.Core;

namespace Packet.Ax25.Properties;

/// <summary>
/// Runtime invariants — properties that hold on a live <see cref="Ax25Session"/>
/// regardless of the input sequence.
/// </summary>
/// <remarks>
/// Phase 2 exit criterion: *"FsCheck property tests prove window invariants
/// (`V(A) ≤ V(S) ≤ V(A)+k`, no orphan transitions, no stuck Timer Recovery)"*.
/// The "no orphan transitions" half is covered statically by
/// <see cref="StateGraphInvariants"/>. This file covers the two
/// runtime invariants: the sliding-window bound and TimerRecovery exit.
/// </remarks>
public class SessionRuntimeInvariants
{
    [Property(MaxTest = 200)]
    public void Window_Invariant_Holds_Under_Arbitrary_Data_Requests(byte requestCountRaw)
    {
        // Number of DL-DATA-requests to post — clamped to a generous range so
        // even small N is meaningful while never running absurdly long.
        var requestCount = (requestCountRaw % 50) + 1;

        var rig = NewConnectedRig();

        for (int i = 0; i < requestCount; i++)
        {
            rig.Session.PostEvent(new DlDataRequest(new byte[] { (byte)i }));
            AssertWindowInvariant(rig.Context, $"after {i + 1} DL-DATA-request(s)");
        }
        AssertWindowInvariant(rig.Context, "after all requests");
    }

    [Property(MaxTest = 50)]
    public void TimerRecovery_Exits_To_Connected_Or_Disconnected_Within_N2_Plus_1_T1_Expiries(byte n2Raw)
    {
        // N2 ∈ [1, 10] — keep N2 bounded so tests stay fast. The invariant
        // is structural: regardless of N2's exact value, TimerRecovery must
        // exit within at most N2+1 T1 expiries (the +1 accounts for the
        // initial Connected → TimerRecovery transition consuming the first
        // expiry).
        var n2 = (n2Raw % 10) + 1;
        const int t1vMs = 100;

        var rig = NewConnectedRig(n2: n2, t1vMs: t1vMs);

        // Drive the session into TimerRecovery by posting a data request
        // (arms T1) and advancing past T1V. With no peer to ack, T1
        // expires and we land in TimerRecovery per figc4.4 t12.
        rig.Session.PostEvent(new DlDataRequest(new byte[] { 0xAA }));
        rig.Session.CurrentState.Should().Be("Connected");

        var seenTimerRecovery = false;
        var terminalStates = new HashSet<string> { "Connected", "Disconnected", "AwaitingConnection", "AwaitingV22Connection", "AwaitingRelease" };

        // Bound the loop at N2+2 T1 cycles (one to enter TR, then N2 to exit
        // — the +2 gives some slack, and we assert exit happens before reaching it).
        for (int i = 0; i <= n2 + 2; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(t1vMs + 10));

            if (rig.Session.CurrentState == "TimerRecovery")
            {
                seenTimerRecovery = true;
                continue;
            }

            // We've left TimerRecovery (or never entered, depending on the
            // path) — it must be one of the expected terminal states.
            terminalStates.Should().Contain(rig.Session.CurrentState,
                $"after {i + 1} T1 cycles, state should be a known non-TimerRecovery state");
            seenTimerRecovery.Should().BeTrue(
                "with no peer to ack the DL-DATA-request, the first T1 expiry must enter TimerRecovery");
            return;
        }

        // Loop fell through without exiting TimerRecovery within the bound.
        rig.Session.CurrentState.Should().NotBe("TimerRecovery",
            $"TimerRecovery must exit within N2+2={n2 + 2} T1 cycles; got stuck instead");
    }

    /// <summary>
    /// Regression for ax25sdl#53. In Timer Recovery, an RR response with F=1
    /// that acks every outstanding I-frame (N(r) = V(s)) must <b>complete
    /// recovery and return to Connected</b>. The figc4.5 recovery-complete
    /// decision is drawn after "V(a) := N(r)", so it tests V(s) = N(r); the
    /// table used to test the stale V(a), mis-routing to Invoke Retransmission
    /// so recovery never completed (RC → N2 → DM). The property above only
    /// asserts TimerRecovery <i>terminates</i> — and Disconnected counts as
    /// termination — so it could not catch this; this fact pins the success
    /// outcome specifically. Found on-air via packet.net#214.
    /// </summary>
    [Fact]
    public void Poll_Response_Acking_All_Outstanding_Completes_Recovery_To_Connected()
    {
        var rig = NewConnectedRig(t1vMs: 100);

        // Send one I-frame: V(s)=1, V(a)=0, T1 armed; stays Connected.
        rig.Session.PostEvent(new DlDataRequest(new byte[] { 0x42 }));
        rig.Session.CurrentState.Should().Be("Connected");
        rig.Context.VS.Should().Be(1);
        rig.Context.VA.Should().Be(0);

        // T1 expires with no ack → enter Timer Recovery (transmits an F/P=1 poll).
        rig.Time.Advance(TimeSpan.FromMilliseconds(110));
        rig.Session.CurrentState.Should().Be("TimerRecovery");

        // Peer replies RR, response, F=1, N(r)=1 — acks the outstanding frame.
        var rr = Ax25Frame.Rr(
            destination: rig.Context.Local,
            source:      rig.Context.Remote,
            nr:          1,
            isCommand:   false,
            pollFinal:   true);
        rig.Session.PostEvent(new RrReceived(rr));

        rig.Session.CurrentState.Should().Be("Connected",
            "an F=1 response that acks everything (N(r)=V(s)) completes recovery (ax25sdl#53)");
        rig.Context.VA.Should().Be(1, "V(a) catches up to N(r)");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sliding-window invariant: the count of unacked I-frames
    /// <c>(V(s) - V(a)) mod Modulus</c> must never exceed <c>K</c>.
    /// This is the AX.25 v2.2 §4.3.2.1 send-window constraint —
    /// crossing it would imply the runtime queued an I-frame outside
    /// the negotiated window.
    /// </summary>
    private static void AssertWindowInvariant(Ax25SessionContext ctx, string because)
    {
        var outstanding = (ctx.VS - ctx.VA + ctx.Modulus) % ctx.Modulus;
        outstanding.Should().BeLessThanOrEqualTo(ctx.K,
            $"V(s)-V(a) mod {ctx.Modulus} must be ≤ K={ctx.K} ({because}); was V(s)={ctx.VS}, V(a)={ctx.VA}, outstanding={outstanding}");
    }

    /// <summary>
    /// Rig: a single connected session with no peer wired. Used by the
    /// invariant properties — these test only sender-side state under
    /// arbitrary inputs; peer behaviour doesn't matter for the
    /// invariants under test.
    /// </summary>
    private sealed record Rig(
        Ax25Session Session,
        Ax25SessionContext Context,
        FakeTimeProvider Time,
        SystemTimerScheduler Scheduler);

    private static Rig NewConnectedRig(int? n2 = null, int? t1vMs = null)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };
        if (n2 is { } n2v)   ctx.N2 = n2v;
        if (t1vMs is { } t)  ctx.T1V = TimeSpan.FromMilliseconds(t);

        Ax25Session? sessionRef = null;
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame:    _ => { },
            sendUFrame:    _ => { },
            sendUiFrame:   _ => { },
            sendIFrame:    _ => { },
            sendUpward:    _ => { },
            sendLinkMux:   _ => { },
            sendInternal:  _ => { });

        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);
        if (dispatcher.Subroutines is DefaultSubroutineRegistry reg) reg.Wire(dispatcher, guards);

        var session = new Ax25Session(ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Disconnected"]          = DataLink_Disconnected.Transitions,
                ["AwaitingConnection"]    = DataLink_AwaitingConnection.Transitions,
                ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
                ["Connected"]             = DataLink_Connected.Transitions,
                ["AwaitingRelease"]       = DataLink_AwaitingRelease.Transitions,
                ["TimerRecovery"]         = DataLink_TimerRecovery.Transitions,
            },
            initialState: "Connected");
        sessionRef = session;
        return new Rig(session, ctx, time, scheduler);
    }

    private static Ax25Event TimerExpiry(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _    => throw new InvalidOperationException($"unexpected timer expiry '{name}'"),
    };
}
