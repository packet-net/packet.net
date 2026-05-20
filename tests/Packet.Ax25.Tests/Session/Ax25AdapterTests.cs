using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Tests for <see cref="Ax25Adapter"/> — the glue between the state
/// machine and byte-level I/O. Covers the outbound serialisation path,
/// the inbound parse-and-classify path, and an end-to-end loopback
/// where two adapters exchange a SABM frame.
/// </summary>
public class Ax25AdapterTests
{
    private static IReadOnlyDictionary<string, IReadOnlyList<TransitionSpec>> RealTransitions => new Dictionary<string, IReadOnlyList<TransitionSpec>>
    {
        ["Disconnected"]         = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"]   = DataLink_AwaitingConnection.Transitions,
        ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
        ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
        ["Connected"]             = DataLink_Connected.Transitions,
    };

    private static Ax25SessionContext NewContext(string local, string remote) => new()
    {
        Local  = new Callsign(local, 0),
        Remote = new Callsign(remote, 0),
    };

    [Fact]
    public void Outbound_DL_UNIT_DATA_request_Produces_Bytes_With_Right_Addressing_And_Control()
    {
        var emittedBytes = new List<byte[]>();
        var ctx = NewContext(local: "M0LTE", remote: "G7XYZ");
        var bindings = new Dictionary<string, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctx, new SystemTimerScheduler(new FakeTimeProvider())),
            StringComparer.Ordinal)
        {
            ["P_eq_1"]            = () => false,
            ["able_to_establish"] = () => true,
        };
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);

        var adapter = new Ax25Adapter(
            context:      ctx,
            scheduler:    scheduler,
            transitions:  RealTransitions,
            initialState: "Disconnected",
            sendBytes:    b => emittedBytes.Add(b.ToArray()),
            bindings:     new Dictionary<string, Func<bool>>(Ax25SessionBindings.CreateDefault(ctx, scheduler), StringComparer.Ordinal)
            {
                ["P_eq_1"]            = () => false,
                ["able_to_establish"] = () => true,
            });

        adapter.Session.PostEvent(new DlUnitDataRequest("test"u8.ToArray(), Pid: 0xCF));

        emittedBytes.Should().ContainSingle();
        var bytes = emittedBytes[0];
        // 7 dest + 7 src + 1 control + 1 pid + 4 info = 20
        bytes.Length.Should().Be(20);
        bytes[14].Should().Be(Ax25Frame.ControlUi, "UI with P=0");
        bytes[15].Should().Be((byte)0xCF, "PID");
        bytes[16..].Should().Equal("test"u8.ToArray());
    }

    [Fact]
    public void Inbound_Bytes_Are_Parsed_Classified_And_Posted_To_Session()
    {
        var receivedFromSubroutine = new List<string>();
        var ctx = NewContext(local: "M0LTE", remote: "G7XYZ");
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var bindings = new Dictionary<string, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctx, scheduler), StringComparer.Ordinal)
        {
            ["P_eq_1"]            = () => false,
            ["able_to_establish"] = () => true,
        };
        var registry = new DefaultSubroutineRegistry();
        registry.Register("UI_Check", _ => receivedFromSubroutine.Add("UI_Check fired"));

        var adapter = new Ax25Adapter(
            context:      ctx,
            scheduler:    scheduler,
            transitions:  RealTransitions,
            initialState: "Disconnected",
            sendBytes:    _ => { },
            bindings:     bindings,
            subroutines:  registry);

        // Build an inbound UI frame with P=0 and feed its bytes.
        var inbound = Ax25Frame.Ui(
            destination: ctx.Local,
            source:      ctx.Remote,
            info:        "hello"u8,
            pid:         Ax25Frame.PidNoLayer3,
            isCommand:   true,
            pollFinal:   false);

        bool parsed = adapter.OnReceivedAx25Bytes(inbound.ToBytes());

        parsed.Should().BeTrue();
        // figc4.1 t12: UI received P=0 → UI_Check; no DM.
        receivedFromSubroutine.Should().ContainSingle().Which.Should().Be("UI_Check fired");
        adapter.Session.CurrentState.Should().Be("Disconnected");
    }

    [Fact]
    public void OnReceivedAx25Bytes_Returns_False_For_Malformed_Bytes()
    {
        var ctx = NewContext("M0LTE", "G7XYZ");
        var scheduler = new SystemTimerScheduler(new FakeTimeProvider());
        var adapter = new Ax25Adapter(
            context: ctx, scheduler: scheduler,
            transitions: RealTransitions, initialState: "Disconnected",
            sendBytes: _ => { });

        // Way too short to be a valid AX.25 frame (need at least 15 bytes).
        adapter.OnReceivedAx25Bytes(new byte[] { 0x01, 0x02 }).Should().BeFalse();
    }

    [Fact]
    public void Loopback_Two_Adapters_Exchange_SABM_When_Establish_Data_Link_Is_Wired()
    {
        // End-to-end style test. Side A's Establish_Data_Link subroutine
        // is wired to emit a SABM (the figc4.7 body we expect once that
        // page lands). Side A posts DL_CONNECT_request; the SABM bytes
        // flow into Side B, which classifies them as SabmReceived and
        // (figc4.1 t14) goes to AwaitingConnection.
        var time = new FakeTimeProvider();

        var ctxA = NewContext(local: "M0LTE", remote: "G7XYZ");
        var ctxB = NewContext(local: "G7XYZ", remote: "M0LTE");
        var schedA = new SystemTimerScheduler(time);
        var schedB = new SystemTimerScheduler(time);

        Ax25Adapter? a = null, b = null;
        void SendFromA(ReadOnlyMemory<byte> bytes) => b!.OnReceivedAx25Bytes(bytes.Span);
        void SendFromB(ReadOnlyMemory<byte> bytes) => a!.OnReceivedAx25Bytes(bytes.Span);

        // Wire Establish_Data_Link to do what figc4.7 will eventually do:
        //   RC := 0; emit SABM (P=1); start T1.
        var registryA = new DefaultSubroutineRegistry();
        registryA.Register("Establish_Data_Link", tx =>
        {
            tx.Session.RC = 0;
            // Use a UFrameSpec dispatch via the wire codec to mirror the
            // production path. The dispatcher's sendUFrame callback would
            // normally route to the adapter's serialiser, but here we
            // bypass it because subroutine bodies don't have access to
            // the dispatcher's sinks. Easiest path: directly serialise
            // and feed B.
            var sabm = Ax25Frame.Sabm(tx.Session.Remote, tx.Session.Local, pollBit: true);
            SendFromA(sabm.ToBytes());
            schedA.Arm("T1", tx.Session.T1V, () => { });
        });

        // P_eq_1 etc. — bindings the figures' predicates need.
        var bindingsA = new Dictionary<string, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctxA, schedA), StringComparer.Ordinal)
        {
            ["P_eq_1"]            = () => false,
            ["able_to_establish"] = () => true,
        };
        var bindingsB = new Dictionary<string, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctxB, schedB), StringComparer.Ordinal)
        {
            ["P_eq_1"]            = () => true,  // SABM came in with P=1
            ["able_to_establish"] = () => true,
        };

        a = new Ax25Adapter(ctxA, schedA, RealTransitions, "Disconnected",
            sendBytes: SendFromA, bindings: bindingsA, subroutines: registryA);
        b = new Ax25Adapter(ctxB, schedB, RealTransitions, "Disconnected",
            sendBytes: SendFromB, bindings: bindingsB);

        // Kick off.
        a.Session.PostEvent(new DlConnectRequest());

        // Side A: ran t03 — SRT/T1V init, Establish_Data_Link, set_layer_3_initiated, → AwaitingConnection.
        a.Session.CurrentState.Should().Be("AwaitingConnection");
        a.Context.Layer3Initiated.Should().BeTrue();
        a.Context.RC.Should().Be(0);
        schedA.IsRunning("T1").Should().BeTrue();

        // Side B: received SABM, figc4.1 t14 (able_to_establish=Yes) →
        // F := 1; SRT/T1V init; V(s):=0; V(r):=0; V(a):=0; Clear_Exception_Conditions;
        // DL_CONNECT_indication; UA; start_T3 → Connected.
        // Most of these depend on subroutine bodies that aren't wired;
        // what we can robustly verify is that B advanced to Connected
        // and emitted a UA back to A.
        b.Session.CurrentState.Should().Be("Connected");
    }
}
