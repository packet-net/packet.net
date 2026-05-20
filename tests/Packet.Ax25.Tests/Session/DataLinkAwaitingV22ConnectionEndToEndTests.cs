using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end integration tests against the real figc4.6 (Awaiting
/// V2.2 Connection) transition table.
/// </summary>
public class DataLinkAwaitingV22ConnectionEndToEndTests
{
    private static (Ax25Session session,
                    Ax25SessionContext ctx,
                    SystemTimerScheduler scheduler,
                    List<UFrameSpec> uFrames,
                    List<UiFrameSpec> uiFrames,
                    List<DataLinkSignal> upward,
                    List<InternalSignal> internalSignals,
                    List<string> subroutineCalls) NewRig(
        bool pEq1 = false,
        bool fEq1 = false,
        bool layer3Initiated = false,
        bool vsEqVa = false,
        bool rcEqN2 = false)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            Layer3Initiated = layer3Initiated,
        };

        var uFrames = new List<UFrameSpec>();
        var uiFrames = new List<UiFrameSpec>();
        var upward = new List<DataLinkSignal>();
        var internalSignals = new List<InternalSignal>();
        var subroutineCalls = new List<string>();

        var registry = new DefaultSubroutineRegistry();
        foreach (var name in DefaultSubroutineRegistry.KnownSubroutines)
        {
            var captured = name;
            registry.Register(captured, _ => subroutineCalls.Add(captured));
        }

        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame:    _ => { },
            sendUFrame:    uFrames.Add,
            sendUiFrame:   uiFrames.Add,
            sendUpward:    upward.Add,
            sendInternal:  internalSignals.Add,
            subroutines:   registry);

        var bindings = new Dictionary<string, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctx, scheduler), StringComparer.Ordinal)
        {
            ["P_eq_1"]     = () => pEq1,
            ["F_eq_1"]     = () => fEq1,
            ["V_s_eq_V_a"] = () => vsEqVa,
            ["RC_eq_N2"]   = () => rcEqN2,
        };
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(
            ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Disconnected"]          = DataLink_Disconnected.Transitions,
                ["AwaitingConnection"]    = DataLink_AwaitingConnection.Transitions,
                ["AwaitingV22Connection"]  = DataLink_AwaitingV22Connection.Transitions,
                ["Connected"]             = DataLink_Connected.Transitions,
            },
            initialState: "AwaitingV22Connection");

        return (session, ctx, scheduler, uFrames, uiFrames, upward, internalSignals, subroutineCalls);
    }

    [Fact]
    public void t01_DL_CONNECT_request_Discards_Queue_Sets_Layer_3_Initiated_Stays_AwaitingV22Connection()
    {
        var (s, ctx, _, _, _, _, _, _) = NewRig();
        ctx.IFrameQueue.Enqueue((new byte[] { 1 }, Ax25Frame.PidNoLayer3));
        ctx.Layer3Initiated = false;

        s.PostEvent(new DlConnectRequest());

        s.CurrentState.Should().Be("AwaitingV22Connection");
        ctx.IFrameQueue.Should().BeEmpty();
        ctx.Layer3Initiated.Should().BeTrue();
    }

    [Fact]
    public void t02_DL_UNIT_DATA_request_Emits_UI_Command_While_Negotiating_V22()
    {
        var (s, _, _, _, uiFrames, _, _, _) = NewRig();
        var payload = "negotiation-in-progress"u8.ToArray();

        s.PostEvent(new DlUnitDataRequest(payload));

        s.CurrentState.Should().Be("AwaitingV22Connection");
        uiFrames.Should().ContainSingle();
        uiFrames[0].Info.ToArray().Should().Equal(payload);
    }
}
