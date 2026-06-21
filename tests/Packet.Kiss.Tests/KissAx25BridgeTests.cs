using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Kiss;

namespace Packet.Kiss.Tests;

public class KissAx25BridgeTests
{
    private static readonly Callsign Local  = new("M0LTE", 0);
    private static readonly Callsign Remote = new("G7XYZ", 7);

    /// <summary>Captures outbound sends; leaves the inbound stream empty (the default).</summary>
    private sealed class FakeModem : IAx25Transport
    {
        public List<byte[]> Sent { get; } = new();

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        {
            Sent.Add(ax25.ToArray());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<TransitionSpec>> Transitions => new Dictionary<string, IReadOnlyList<TransitionSpec>>
    {
        ["Disconnected"]         = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"]   = DataLink_AwaitingConnection.Transitions,
        ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
        ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
        ["Connected"]            = DataLink_Connected.Transitions,
    };

    private static Ax25SessionContext NewContext() => new()
    {
        Local  = Local,
        Remote = Remote,
    };

    [Fact]
    public void Outbound_DL_UNIT_DATA_request_Reaches_Modem_As_KISS_Send()
    {
        var modem = new FakeModem();
        var ctx = NewContext();
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);

        var adapter = KissAx25Bridge.CreateOutbound(
            modem, ctx, scheduler, Transitions, "Disconnected",
            bindings: new Dictionary<Ax25Guard, Func<bool>>(
                Ax25SessionBindings.CreateDefault(ctx, scheduler))
            {
                [Ax25Guard.PEq1] = () => false,
            });

        adapter.Session.PostEvent(new DlUnitDataRequest("hello"u8.ToArray(), Pid: Ax25Frame.PidNoLayer3));

        modem.Sent.Should().ContainSingle();
        // Bytes are AX.25 body bytes; a real KISS transport's SendAsync wraps
        // them in KISS internally (KissEncoder handles flags/escapes/command byte).
        var bytes = modem.Sent[0];
        bytes[14].Should().Be(Ax25Frame.ControlUi);
    }

    [Fact]
    public void Inbound_Ax25FrameReceivedEvent_Posts_Classified_Event_To_Session()
    {
        var modem = new FakeModem();
        var ctx = NewContext();
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);

        var subroutineCalls = new List<string>();
        var registry = new DefaultSubroutineRegistry();
        registry.Register("UI_Check", _ => subroutineCalls.Add("UI_Check"));

        var adapter = KissAx25Bridge.CreateOutbound(
            modem, ctx, scheduler, Transitions, "Disconnected",
            subroutines: registry);

        // Simulate the KISS driver receiving a UI frame from the wire.
        var inboundFrame = Ax25Frame.Ui(
            destination: Local, source: Remote,
            info: "incoming"u8,
            pollFinal: false);
        var fakeRaw = new KissFrame(Port: 0, Command: KissCommand.Data,
            Payload: inboundFrame.ToBytes());
        var evt = new Ax25FrameReceivedEvent(fakeRaw, inboundFrame);

        bool routed = KissAx25Bridge.RouteInboundToAdapter(evt, adapter);

        routed.Should().BeTrue();
        // figc4.1 t12: UI_received P=0 → UI_Check subroutine, no DM emission.
        subroutineCalls.Should().ContainSingle().Which.Should().Be("UI_Check");
        modem.Sent.Should().BeEmpty("no DM response for P=0 UI");
    }

    [Fact]
    public void Inbound_AckModeDataReceivedEvent_Is_Routed_If_Payload_Parses_As_Ax25()
    {
        var modem = new FakeModem();
        var ctx = NewContext();
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);

        var subroutineCalls = new List<string>();
        var registry = new DefaultSubroutineRegistry();
        registry.Register("UI_Check", _ => subroutineCalls.Add("UI_Check"));

        var adapter = KissAx25Bridge.CreateOutbound(
            modem, ctx, scheduler, Transitions, "Disconnected",
            subroutines: registry);

        var inboundFrame = Ax25Frame.Ui(
            destination: Local, source: Remote, info: "x"u8);
        var ax25Bytes = inboundFrame.ToBytes();
        // ACKMODE frame has 2-byte seq tag prefix + AX.25 bytes; but the
        // event's Ax25Payload is supposed to be the *unwrapped* AX.25
        // bytes (the seq-tag has been stripped). So we pass ax25Bytes
        // straight in.
        var fakeRaw = new KissFrame(Port: 0, Command: (KissCommand)0x0C,
            Payload: ax25Bytes);
        var evt = new AckModeDataReceivedEvent(fakeRaw, SequenceTag: 42, Ax25Payload: ax25Bytes);

        bool routed = KissAx25Bridge.RouteInboundToAdapter(evt, adapter);

        routed.Should().BeTrue();
        subroutineCalls.Should().ContainSingle().Which.Should().Be("UI_Check");
    }

    [Fact]
    public void Inbound_UnknownInboundEvent_Is_Not_Routed()
    {
        var modem = new FakeModem();
        var ctx = NewContext();
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var adapter = KissAx25Bridge.CreateOutbound(
            modem, ctx, scheduler, Transitions, "Disconnected");

        var fakeRaw = new KissFrame(Port: 0, Command: KissCommand.SetHardware,
            Payload: new byte[] { 0x42 });
        var evt = new UnknownInboundEvent(fakeRaw);

        bool routed = KissAx25Bridge.RouteInboundToAdapter(evt, adapter);

        routed.Should().BeFalse();
        modem.Sent.Should().BeEmpty();
    }

    [Fact]
    public void End_To_End_Loopback_Two_Bridges_Exchange_SABM_Via_Fake_Modem_Pair()
    {
        // Two adapters, two fake modems wired so each modem's send
        // becomes the other adapter's inbound. Verifies the full path:
        //   adapter A sendBytes → modem A.SendAsync (captures)
        //   → loopback delivery → adapter B.OnReceivedAx25Frame
        //   → BOTH sides reach Connected: the loopback is synchronous, so
        //   B's UA arrives back at A while A's DL-CONNECT dispatch is still
        //   in flight; PostEvent's run-to-completion queue (#327) defers it
        //   and dispatches it right after t03 commits, completing A's
        //   connect in the same drain.
        //
        // (Before #327 this test pinned the re-entrancy corruption: the UA
        // dispatched INLINE mid-transition, advanced A to Connected, and
        // then t03's commit clobbered the state back to AwaitingConnection
        // — the previous assertion here. That was the artifact, not the
        // contract.)
        var time = new FakeTimeProvider();
        var ctxA = new Ax25SessionContext { Local = Local,  Remote = Remote };
        var ctxB = new Ax25SessionContext { Local = Remote, Remote = Local  };
        var schedA = new SystemTimerScheduler(time);
        var schedB = new SystemTimerScheduler(time);

        Ax25Adapter? aRef = null, bRef = null;

        var modemA = new LoopbackModem(bytes => bRef!.OnReceivedAx25Bytes(bytes.Span));
        var modemB = new LoopbackModem(bytes => aRef!.OnReceivedAx25Bytes(bytes.Span));

        var registryA = new DefaultSubroutineRegistry();
        registryA.Register("Establish_Data_Link", tx =>
        {
            tx.Session.RC = 0;
            var sabm = Ax25Frame.Sabm(tx.Session.Remote, tx.Session.Local, pollBit: true);
            // Use the modem to send so the bridge's outbound path is
            // exercised. Synchronously: the LoopbackModem is in-process.
            modemA.SendAsync(sabm.ToBytes(), default).GetAwaiter().GetResult();
            schedA.Arm("T1", tx.Session.T1V, () => { });
        });

        var bindingsA = new Dictionary<Ax25Guard, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctxA, schedA, () => aRef?.Session.CurrentTrigger))
        {
            [Ax25Guard.AbleToEstablish] = () => true,
        };
        var bindingsB = new Dictionary<Ax25Guard, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctxB, schedB, () => bRef?.Session.CurrentTrigger))
        {
            [Ax25Guard.AbleToEstablish] = () => true,
        };

        aRef = KissAx25Bridge.CreateOutbound(modemA, ctxA, schedA, Transitions, "Disconnected",
            bindings: bindingsA, subroutines: registryA);
        bRef = KissAx25Bridge.CreateOutbound(modemB, ctxB, schedB, Transitions, "Disconnected",
            bindings: bindingsB);

        aRef.Session.PostEvent(new DlConnectRequest());

        aRef.Session.CurrentState.Should().Be("Connected",
            "the synchronously-looped UA is deferred and dispatched after t03 commits — A's connect completes in one drain");
        bRef.Session.CurrentState.Should().Be("Connected");
    }

    /// <summary>Loopback transport that hands off sent bytes to a delivery callback.</summary>
    private sealed class LoopbackModem : IAx25Transport
    {
        private readonly Action<ReadOnlyMemory<byte>> deliver;
        public LoopbackModem(Action<ReadOnlyMemory<byte>> deliver) => this.deliver = deliver;

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        {
            deliver(ax25);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
