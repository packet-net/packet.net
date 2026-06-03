using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end integration tests against the real figc4.3 (Awaiting
/// Release) transition table.
/// </summary>
public class DataLinkAwaitingReleaseEndToEndTests
{
    private static (Ax25Session session,
                    Ax25SessionContext ctx,
                    SystemTimerScheduler scheduler,
                    List<UFrameSpec> uFrames,
                    List<UiFrameSpec> uiFrames,
                    List<DataLinkSignal> upward,
                    List<string> subroutineCalls) NewRig(
        bool fEq1 = false,
        bool pEq1 = false,
        bool rcEqN2 = false)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };

        var uFrames = new List<UFrameSpec>();
        var uiFrames = new List<UiFrameSpec>();
        var upward = new List<DataLinkSignal>();
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
            subroutines:   registry);

        var bindings = new Dictionary<Ax25Guard, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctx, scheduler))
        {
            [Ax25Guard.FEq1]   = () => fEq1,
            [Ax25Guard.PEq1]   = () => pEq1,
            [Ax25Guard.RCEqN2] = () => rcEqN2,
        };
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(
            ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Disconnected"]     = DataLink_Disconnected.Transitions,
                ["AwaitingRelease"]  = DataLink_AwaitingRelease.Transitions,
            },
            initialState: "AwaitingRelease");

        return (session, ctx, scheduler, uFrames, uiFrames, upward, subroutineCalls);
    }

    private static Ax25Frame UaFrame(bool fBit)
    {
        var bytes = new byte[15];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        bytes[14] = (byte)(0x63 | (fBit ? 0x10 : 0));
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        return frame!;
    }

    [Fact]
    public void t01_DL_DISCONNECT_request_Emits_Expedited_DM_Stays_AwaitingRelease()
    {
        var (s, _, _, uFrames, _, _, _) = NewRig();

        s.PostEvent(new DlDisconnectRequest());

        s.CurrentState.Should().Be("AwaitingRelease");
        uFrames.Should().ContainSingle();
        var u = uFrames[0];
        u.Type.Should().Be(UFrameType.Dm);
        u.IsExpedited.Should().BeTrue();
    }

    [Fact]
    public void t02_T1_Expiry_RC_Eq_N2_Gives_Up_Returns_To_Disconnected()
    {
        var (s, _, _, _, _, upward, _) = NewRig(rcEqN2: true);

        s.PostEvent(new T1Expiry());

        s.CurrentState.Should().Be("Disconnected");
        upward.Should().HaveCount(2);
        upward[0].Should().BeOfType<DataLinkErrorIndication>().Which.Code.Should().Be("G");
        upward[1].Should().BeOfType<DataLinkDisconnectIndication>();
    }

    [Fact]
    public void t04_UA_Received_F_Eq_1_Confirms_Disconnect_Stops_T1_Returns_To_Disconnected()
    {
        var (s, _, scheduler, _, _, upward, _) = NewRig(fEq1: true);
        scheduler.Arm("T1", TimeSpan.FromSeconds(1), () => { });

        s.PostEvent(new UaReceived(UaFrame(fBit: true)));

        s.CurrentState.Should().Be("Disconnected");
        upward.Should().ContainSingle().Which.Should().BeOfType<DataLinkDisconnectConfirm>();
        scheduler.IsRunning("T1").Should().BeFalse();
    }

    [Fact]
    public void t07_DL_UNIT_DATA_request_Still_Sends_UI_While_Awaiting_Release()
    {
        var (s, _, _, _, uiFrames, _, _) = NewRig();
        var payload = "still-sending"u8.ToArray();

        s.PostEvent(new DlUnitDataRequest(payload));

        s.CurrentState.Should().Be("AwaitingRelease");
        uiFrames.Should().ContainSingle();
        uiFrames[0].Info.ToArray().Should().Equal(payload);
    }

    [Theory]
    [InlineData("L", typeof(ControlFieldError))]
    [InlineData("M", typeof(InfoNotPermittedInFrame))]
    [InlineData("N", typeof(UOrSFrameLengthError))]
    public void figc4_3_Error_Catchall_Columns_Raise_The_Right_Letter_Code(string expectedCode, Type eventType)
    {
        var (s, _, _, _, _, upward, _) = NewRig();
        var evt = (Ax25Event)Activator.CreateInstance(eventType)!;

        s.PostEvent(evt);

        s.CurrentState.Should().Be("AwaitingRelease");
        upward.Should().ContainSingle()
            .Which.Should().BeOfType<DataLinkErrorIndication>()
            .Which.Code.Should().Be(expectedCode);
    }
}
