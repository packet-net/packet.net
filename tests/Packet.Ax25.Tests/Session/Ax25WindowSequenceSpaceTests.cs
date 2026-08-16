using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;
using Ax25Event = Packet.Ax25.Session.Ax25Event;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// The send window is bounded by the sequence space itself: at most
/// <c>Modulus - 1</c> I-frames may be outstanding. §4.2.4 sizes V(S) modulo the
/// link's modulus and §6.4.4.1 stops transmission at <c>V(S) = V(A) + k</c>, but
/// both transmit gates measure the outstanding count as
/// <c>(V(S) - V(A)) mod Modulus</c>, which can never reach the modulus - so a
/// <c>k</c> at or above it means "never full". The retransmit store
/// (<see cref="Ax25SessionContext.SentIFrames"/>) is keyed by the bare N(S), so
/// the wrapping frame overwrites a still-unacknowledged entry and a REJ then
/// retransmits the wrong payload under the right sequence number: silent
/// corruption, reachable from an operator-set <c>WindowSize</c> of 8..127 on a
/// mod-8 port (packet-net/packet.net#696).
/// <see cref="Ax25SessionContext.EffectiveWindow"/> is the single point every
/// gate reads, so the bound lives there.
/// </summary>
public class Ax25WindowSequenceSpaceTests
{
    private static readonly Callsign Local = new("M0LTE", 0);
    private static readonly Callsign Remote = new("G7XYZ", 7);

    [Theory]
    // mod-8, go-back-N: k at/over the modulus is bounded to Modulus-1 = 7.
    [InlineData(8, false, false, 7)]
    [InlineData(16, false, false, 7)]
    [InlineData(127, false, false, 7)]
    // ...and a legitimate mod-8 window is untouched.
    [InlineData(7, false, false, 7)]
    [InlineData(4, false, false, 4)]
    // mod-128, go-back-N: bounded at 127, not at the configured 128+.
    [InlineData(128, false, true, 127)]
    [InlineData(200, false, true, 127)]
    [InlineData(32, false, true, 32)]
    // SREJ takes the tighter half-modulus cap (ax25spec#13) first; the
    // sequence-space bound never loosens it.
    [InlineData(8, true, false, 4)]
    [InlineData(200, true, true, 64)]
    public void Effective_window_never_exceeds_the_sequence_space(
        int k, bool srej, bool extended, int expected)
    {
        var ctx = new Ax25SessionContext
        {
            Local = Local,
            Remote = Remote,
            K = k,
            SrejEnabled = srej,
            IsExtended = extended,
        };

        ctx.EffectiveWindow.Should().Be(expected);
    }

    [Fact]
    public void The_bound_applies_even_to_the_strictly_faithful_quirk_set()
    {
        // Ax25Spec13ClampSrejWindowToHalfModulus is a figure-interpretation quirk and
        // can be turned off; the sequence-space bound is arithmetic, so it cannot.
        var ctx = new Ax25SessionContext
        {
            Local = Local,
            Remote = Remote,
            Quirks = Ax25SessionQuirks.StrictlyFaithful,
            K = 8,
            SrejEnabled = true,
        };

        ctx.EffectiveWindow.Should().Be(7);
    }

    [Fact]
    public void Mod8_go_back_n_with_k8_keeps_seven_outstanding_and_rejects_retransmit_the_right_payloads()
    {
        // A single real session in Connected, mod-8, SREJ off, k = 8 (an
        // operator-set WindowSize an unfixed NodeConfigValidator would accept).
        var rig = BuildConnectedSession(k: 8);

        for (int i = 0; i < 12; i++)
        {
            rig.Session.PostEvent(new DlDataRequest(Payload(i)));
        }

        // Before the fix all 12 went out with N(S) = [0..7, 0, 1, 2, 3]: the window
        // gate (V(S) - V(A)) mod 8 can never report 8, so it never said "full".
        rig.Sent.Should().HaveCount(7, "at modulus 8 only Modulus-1 I-frames can be outstanding");
        rig.Sent.Select(f => f.Ns).Should().Equal([0, 1, 2, 3, 4, 5, 6]);
        rig.Sent.Select(f => f.Text).Should().Equal(
            [Text(0), Text(1), Text(2), Text(3), Text(4), Text(5), Text(6)]);
        rig.Context.VS.Should().Be(7);
        rig.Context.VA.Should().Be(0);

        // The peer rejects from N(R) = 2: V(A) := 2, retransmit from N(S) = 2 on.
        rig.Sent.Clear();
        rig.Session.PostEvent(new RejReceived(Ax25Frame.Rej(
            destination: Local, source: Remote, nr: 2, isCommand: false, pollFinal: false)));

        rig.Session.CurrentState.Should().Be("Connected");
        // Before the fix, N(S) 2 and 3 had been overwritten in SentIFrames by the
        // wrapped payloads 10 and 11, so the recovery put the wrong bytes on the air
        // under the right sequence numbers.
        rig.Sent.Take(5).Select(f => (f.Ns, f.Text)).Should().Equal(
            [((byte)2, Text(2)), ((byte)3, Text(3)), ((byte)4, Text(4)),
             ((byte)5, Text(5)), ((byte)6, Text(6))]);
        // The window still holds: at most 7 unacknowledged at any point.
        ((rig.Context.VS - rig.Context.VA + 8) % 8).Should().BeLessThanOrEqualTo(7);
    }

    private static byte[] Payload(int i) => System.Text.Encoding.ASCII.GetBytes(Text(i));

    private static string Text(int i) => $"payload-{i:D2}";

    private sealed record SentFrame(byte Ns, string Text);

    private sealed record Rig(Ax25Session Session, Ax25SessionContext Context, List<SentFrame> Sent);

    // One real session forced into Connected with the production bindings and the
    // packaged SDL transition tables - the smallest rig that exercises the transmit
    // gates and the N(S)-keyed retransmit store.
    private static Rig BuildConnectedSession(int k)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local = Local,
            Remote = Remote,
            K = k,
            SrejEnabled = false,
            ImplicitReject = true,
            IsExtended = false,
        };
        var sent = new List<SentFrame>();
        var subroutines = new DefaultSubroutineRegistry();
        Ax25Session? sessionRef = null;

        void RecordIFrame(Ax25Frame frame)
        {
            sent.Add(new SentFrame(
                (byte)((frame.Control >> 1) & 0x07),
                System.Text.Encoding.ASCII.GetString(frame.Info.Span)));
        }

        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame: _ => { },
            sendUFrame: _ => { },
            sendUiFrame: _ => { },
            sendIFrame: spec => RecordIFrame(spec.ToAx25Frame(ctx)),
            sendUpward: _ => { },
            sendLinkMux: _ => { },
            sendInternal: _ => { },
            subroutines: subroutines);

        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);
        subroutines.Wire(dispatcher, guards);

        var session = new Ax25Session(ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Disconnected"] = DataLink_Disconnected.Transitions,
                ["AwaitingConnection"] = DataLink_AwaitingConnection.Transitions,
                ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
                ["Connected"] = DataLink_Connected.Transitions,
                ["AwaitingRelease"] = DataLink_AwaitingRelease.Transitions,
                ["TimerRecovery"] = DataLink_TimerRecovery.Transitions,
            },
            initialState: "Connected");
        sessionRef = session;
        return new Rig(session, ctx, sent);
    }

    private static Ax25Event TimerExpiry(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _ => throw new InvalidOperationException($"unexpected timer expiry '{name}'"),
    };
}
