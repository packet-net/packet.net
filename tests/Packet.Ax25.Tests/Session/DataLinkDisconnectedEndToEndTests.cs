using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end integration tests against the **real** figc4.1
/// (Disconnected) transition table, driven through the **real**
/// <see cref="ActionDispatcher"/> with all its sinks and registry.
/// </summary>
/// <remarks>
/// <para>
/// The smoke tests (<see cref="DataLinkDisconnectedSmokeTests"/>) prove
/// that <see cref="Ax25Session.PostEvent"/> picks the right transition
/// and records the right verb sequence — they use a recording
/// dispatcher and never run the verbs. These tests run the same
/// transitions through the real dispatcher and assert observable
/// behaviour: emitted frames, raised signals, context mutations, state
/// transitions.
/// </para>
/// <para>
/// What this catches that the smoke tests can't:
/// <list type="bullet">
///   <item>Action verbs whose name is in the YAML but isn't wired in
///   the dispatcher — would throw <c>InvalidOperationException</c> on
///   real execution.</item>
///   <item>Context fields that should mutate but don't (e.g. wiring
///   <c>set_layer_3_initiated</c> to the wrong field).</item>
///   <item>Frame specs that should carry specific values (N(R), P/F)
///   but get the wrong defaults.</item>
///   <item>Subroutines that should be invoked but aren't.</item>
/// </list>
/// </para>
/// </remarks>
public class DataLinkDisconnectedEndToEndTests
{
    private sealed class TestRig
    {
        public required Ax25Session Session { get; init; }
        public required Ax25SessionContext Ctx { get; init; }
        public required SystemTimerScheduler Scheduler { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required List<SupervisoryFrameSpec> SFrames { get; init; }
        public required List<UFrameSpec> UFrames { get; init; }
        public required List<UiFrameSpec> UiFrames { get; init; }
        public required List<DataLinkSignal> Upward { get; init; }
        public required List<LinkMultiplexerSignal> LinkMux { get; init; }
        public required List<InternalSignal> InternalSignals { get; init; }
        public required List<string> SubroutineCalls { get; init; }
    }

    /// <summary>Build a session wired against the real figc4.1 + figc4.2 transition tables.</summary>
    private static TestRig NewRig(bool pEq1 = false, bool ableToEstablish = true)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };

        var sFrames = new List<SupervisoryFrameSpec>();
        var uFrames = new List<UFrameSpec>();
        var uiFrames = new List<UiFrameSpec>();
        var upward = new List<DataLinkSignal>();
        var linkMux = new List<LinkMultiplexerSignal>();
        var internalSignals = new List<InternalSignal>();
        var subroutineCalls = new List<string>();

        var registry = new DefaultSubroutineRegistry();
        foreach (var name in DefaultSubroutineRegistry.KnownSubroutines)
        {
            // Capture name in local for closure
            var captured = name;
            registry.Register(captured, _ => subroutineCalls.Add(captured));
        }

        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame:    sFrames.Add,
            sendUFrame:    uFrames.Add,
            sendUiFrame:   uiFrames.Add,
            sendUpward:    upward.Add,
            sendLinkMux:   linkMux.Add,
            sendInternal:  internalSignals.Add,
            subroutines:   registry);

        var bindings = new Dictionary<Ax25Guard, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctx, scheduler))
        {
            [Ax25Guard.PEq1]            = () => pEq1,
            [Ax25Guard.AbleToEstablish] = () => ableToEstablish,
        };
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(
            ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Disconnected"]       = DataLink_Disconnected.Transitions,
                ["AwaitingConnection"] = DataLink_AwaitingConnection.Transitions,
            },
            initialState: "Disconnected");

        return new TestRig
        {
            Session = session,
            Ctx = ctx,
            Scheduler = scheduler,
            Time = time,
            SFrames = sFrames,
            UFrames = uFrames,
            UiFrames = uiFrames,
            Upward = upward,
            LinkMux = linkMux,
            InternalSignals = internalSignals,
            SubroutineCalls = subroutineCalls,
        };
    }

    /// <summary>Stand-in frame for received-frame events that don't read frame fields.</summary>
    private static Ax25Frame Frame() => Ax25Frame.Ui(
        destination: new Callsign("M0LTE", 0),
        source:      new Callsign("G7XYZ", 7),
        info:        "x"u8);

    // ─── figc4.1 transitions, real dispatcher ──────────────────────────

    [Fact]
    public void t01_DL_DISCONNECT_request_Emits_DL_DISCONNECT_confirm_And_Stays_Disconnected()
    {
        var r = NewRig();

        r.Session.PostEvent(new DlDisconnectRequest());

        r.Session.CurrentState.Should().Be("Disconnected");
        r.Upward.Should().ContainSingle().Which.Should().BeOfType<DataLinkDisconnectConfirm>();
    }

    [Fact]
    public void t02_DL_UNIT_DATA_request_Emits_UI_Command_With_Payload_And_Pid()
    {
        var r = NewRig();
        var payload = "test-ui-payload"u8.ToArray();

        r.Session.PostEvent(new DlUnitDataRequest(payload, Pid: 0xCF));

        r.Session.CurrentState.Should().Be("Disconnected");
        r.UiFrames.Should().ContainSingle();
        var ui = r.UiFrames[0];
        ui.IsCommand.Should().BeTrue();
        ui.Pid.Should().Be((byte)0xCF);
        ui.Info.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void t03_DL_CONNECT_request_Initialises_SRT_T1V_Calls_Subroutine_Sets_Flag_Transitions_To_AwaitingConnection()
    {
        var r = NewRig();
        // Pre-corrupt so the assignments are observable as changes.
        r.Ctx.Srt = TimeSpan.FromMinutes(99);
        r.Ctx.T1V = TimeSpan.FromMinutes(99);
        r.Ctx.Layer3Initiated = false;

        r.Session.PostEvent(new DlConnectRequest());

        r.Ctx.Srt.Should().Be(TimeSpan.FromMilliseconds(3000), "SRT := Initial Default");
        r.Ctx.T1V.Should().Be(TimeSpan.FromMilliseconds(6000), "T1V := 2 * SRT");
        r.SubroutineCalls.Should().ContainSingle().Which.Should().Be("Establish_Data_Link");
        r.Ctx.Layer3Initiated.Should().BeTrue();
        r.Session.CurrentState.Should().Be("AwaitingConnection");
    }

    [Fact]
    public void t05_All_Other_Commands_Sets_F_From_P_And_Emits_DM_Response()
    {
        // figc4.1 t05: catch-all command → F := P; DM. The DM response's
        // F bit echoes the incoming command's P bit.
        var r = NewRig();

        // Build a UI command frame with P=1 — stands in for any unhandled
        // command frame the link multiplexer routes to all_other_commands.
        var triggerFrame = Ax25Frame.Ui(
            destination: new Callsign("M0LTE", 0),
            source:      new Callsign("G7XYZ", 7),
            info:        "x"u8,
            pollFinal:   true);

        r.Session.PostEvent(new AllOtherCommands(triggerFrame));

        r.Session.CurrentState.Should().Be("Disconnected");
        r.UFrames.Should().ContainSingle();
        var u = r.UFrames[0];
        u.Type.Should().Be(UFrameType.Dm);
        u.IsCommand.Should().BeFalse("DM is a response");
        u.PfBit.Should().BeTrue("F := P echoed incoming P=1");
    }

    [Fact]
    public void t06_All_Other_Primitives_From_Upper_Layer_Discards_Primitive_Stays_Disconnected()
    {
        var r = NewRig();

        r.Session.PostEvent(new AllOtherPrimitivesFromUpperLayer());

        r.Session.CurrentState.Should().Be("Disconnected");
        // discard_primitive is a no-op; no frames emitted, no signals raised.
        r.SFrames.Should().BeEmpty();
        r.UFrames.Should().BeEmpty();
        r.UiFrames.Should().BeEmpty();
        r.Upward.Should().BeEmpty();
    }

    [Fact]
    public void t07_Control_Field_Error_Raises_DL_ERROR_indication_L()
    {
        var r = NewRig();

        r.Session.PostEvent(new ControlFieldError());

        r.Session.CurrentState.Should().Be("Disconnected");
        r.Upward.Should().ContainSingle();
        var err = r.Upward[0].Should().BeOfType<DataLinkErrorIndication>().Subject;
        err.Code.Should().Be("L");
    }

    [Theory]
    [InlineData("M", typeof(InfoNotPermittedInFrame))]
    [InlineData("N", typeof(UOrSFrameLengthError))]
    public void figc4_1_Error_Catchall_Columns_Raise_The_Right_Letter_Code(string expectedCode, Type eventType)
    {
        var r = NewRig();
        var evt = (Ax25Event)Activator.CreateInstance(eventType)!;

        r.Session.PostEvent(evt);

        r.Session.CurrentState.Should().Be("Disconnected");
        r.Upward.Should().ContainSingle()
            .Which.Should().BeOfType<DataLinkErrorIndication>()
            .Which.Code.Should().Be(expectedCode);
    }

    [Fact]
    public void t10_UA_Received_While_Disconnected_Raises_DL_ERROR_C_D()
    {
        var r = NewRig();

        r.Session.PostEvent(new UaReceived(Frame()));

        r.Session.CurrentState.Should().Be("Disconnected");
        r.Upward.Should().ContainSingle()
            .Which.Should().BeOfType<DataLinkErrorIndication>()
            .Which.Code.Should().Be("C_D");
    }

    [Fact]
    public void t11_UI_Received_P_Equals_1_Calls_UI_Check_Then_Emits_DM_With_F_Bit()
    {
        var r = NewRig(pEq1: true);

        r.Session.PostEvent(new UiReceived(Frame()));

        r.Session.CurrentState.Should().Be("Disconnected");
        r.SubroutineCalls.Should().ContainSingle().Which.Should().Be("UI_Check");
        r.UFrames.Should().ContainSingle();
        var u = r.UFrames[0];
        u.Type.Should().Be(UFrameType.Dm);
        u.IsCommand.Should().BeFalse();
        u.PfBit.Should().BeTrue("F := 1 set Pending.PfBit; bare DM consumed it");
    }

    [Fact]
    public void t12_UI_Received_P_Equals_0_Calls_UI_Check_With_No_DM()
    {
        var r = NewRig(pEq1: false);

        r.Session.PostEvent(new UiReceived(Frame()));

        r.Session.CurrentState.Should().Be("Disconnected");
        r.SubroutineCalls.Should().ContainSingle().Which.Should().Be("UI_Check");
        r.UFrames.Should().BeEmpty("no DM response for P=0 UI");
    }

    [Fact]
    public void t13_DISC_Received_Emits_DM_With_F_Equal_To_Incoming_P()
    {
        // figc4.1 t13: DISC received → F := P; DM. The incoming frame's
        // P bit ends up on the DM response's F bit.
        var r = NewRig();

        // Build a DISC frame with P=1.
        var bytes = new byte[15];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        bytes[14] = 0x53;  // DISC control = 0x43 with P=1 → 0x53
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();

        r.Session.PostEvent(new DiscReceived(frame!));

        r.Session.CurrentState.Should().Be("Disconnected");
        r.UFrames.Should().ContainSingle();
        var u = r.UFrames[0];
        u.Type.Should().Be(UFrameType.Dm);
        u.IsCommand.Should().BeFalse();
        u.PfBit.Should().BeTrue("F := P echoed the incoming DISC's P=1");
    }
}
