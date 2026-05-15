using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.Kiss;
using Xunit;

namespace Packet.Interop.Tests.Rax25;

/// <summary>
/// Connected-mode interop against Thomas Habets's rax25 (Rust AX.25
/// engine) with net-sim providing the RF link between us. Our
/// <see cref="Ax25Session"/> attaches to net-sim's KISS-TCP on port
/// 8100 (node a); rax25's <c>async_server</c> example dials net-sim's
/// other listener on 8104 (node e) from inside docker, configured by
/// the <c>command</c> in <c>docker/compose.interop.yml</c>. The
/// afsk1200 sim bridges frames between the two endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Third third-party AX.25 stack in the netsim interop matrix
/// (LinBPQ — C / mature; XRouter — C / mature; rax25 — Rust / young).
/// Mirrors <c>LinbpqViaNetsimConnectedMode.Connect_Then_Disconnect_Against_Linbpq_Across_Netsim</c>
/// and <c>XrouterViaNetsimConnectedMode.Connect_Then_Disconnect_Against_Xrouter_Across_Netsim</c>
/// — minimum useful assertion (SABM/UA connect, DISC/UA disconnect)
/// against a Rust implementation as the remote peer.
/// </para>
/// <para>
/// <b>Scope is deliberately narrow.</b> rax25's README flags REJ and
/// SREJ as "untested / probably broken" and notes that segmentation
/// is not implemented. SABM/UA/DISC/UA — the only frames this test
/// exchanges — do NOT touch any of those paths. A future I-frame
/// round-trip test against rax25 may need to gate on, or avoid,
/// those features. Don't assume rax25-based assertions generalise.
/// </para>
/// <para>
/// <b>No CTEXT-equivalent needed.</b> rax25's <c>async_server</c> calls
/// <c>builder.accept()</c> which loops waiting for an incoming SABM
/// and replies UA when it arrives — no per-call configuration. After
/// the handshake it writes a short welcome banner ("Welcome to the
/// server!\n") as an I-frame, which our session will collect into the
/// signal queue but this test doesn't assert on. The DISC tears the
/// link down cleanly regardless.
/// </para>
/// <para>
/// rax25 registers itself with the SSID-suffixed call we pass via
/// <c>-s PN0RAX-1</c>, so our remote callsign is <c>PN0RAX-1</c>
/// (SSID 1), not bare <c>PN0RAX</c>.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// Compose's <c>depends_on netsim: healthy</c> ensures rax25's KISS
/// dial finds the listener on first attempt — rax25 doesn't retry the
/// dial, so this ordering matters.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class Rax25ViaNetsimConnectedMode
{
    private const string Host             = "127.0.0.1";
    private const int    OurKissPort      = 8100;
    private static readonly Callsign OurCall   = new("PNTEST", 0);
    private static readonly Callsign Rax25Call = new("PN0RAX", 1);

    private static readonly TimeSpan ConnectBudget    = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Connect_Then_Disconnect_Against_Rax25_Across_Netsim()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DisconnectBudget + TimeSpan.FromSeconds(15));

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        var rig = BuildRig(local: OurCall, remote: Rax25Call, kiss: kiss);

        var pumps = new[]
        {
            Task.Run(() => InboundPump(rig, cts.Token), cts.Token),
        };

        // Brief settle so net-sim's per-port TX queue is ready before
        // we fire SABM. rax25's KISS dial happens at container start;
        // compose's depends_on netsim: healthy plus this delay gives
        // it time to land in `accept()`'s wait loop.
        await Task.Delay(500, cts.Token);

        // ─── Connect ────────────────────────────────────────────────
        rig.Session.PostEvent(new DlConnectRequest());

        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps, cts.Token);
        connectConfirm.Should().NotBeNull("rax25 must accept SABM addressed to PN0RAX-1 and reply UA, taking us to Connected");
        rig.Session.CurrentState.Should().Be("Connected");

        // ─── Disconnect ─────────────────────────────────────────────
        rig.Session.PostEvent(new DlDisconnectRequest());

        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps, cts.Token);
        disconnectConfirm.Should().NotBeNull("rax25 must reply UA to our DISC, taking us to Disconnected");
        rig.Session.CurrentState.Should().Be("Disconnected");

        cts.Cancel();
        try { await Task.WhenAll(pumps); } catch (OperationCanceledException) { }
    }

    // ─── Rig ────────────────────────────────────────────────────────
    //
    // Same shape as LinbpqViaNetsimConnectedMode.BuildRig and
    // XrouterViaNetsimConnectedMode.BuildRig. Kept duplicated rather
    // than lifted into a shared helper because rax25-specific quirks
    // (the extended-mode reserved-bit handling, the lack of REJ/SREJ
    // support, custom T3 default) are likely to surface in later tests.

    private sealed record Rig(
        Ax25Session Session,
        SystemTimerScheduler Scheduler,
        ConcurrentQueue<DataLinkSignal> Signals,
        KissTcpClient Kiss);

    private static Rig BuildRig(Callsign local, Callsign remote, KissTcpClient kiss)
    {
        var scheduler = new SystemTimerScheduler(TimeProvider.System);
        var ctx = new Ax25SessionContext { Local = local, Remote = remote };
        var signals = new ConcurrentQueue<DataLinkSignal>();

        void SendBytes(ReadOnlyMemory<byte> bytes)
            => _ = kiss.SendAsync(port: 0, KissCommand.Data, bytes);

        Ax25Session? sessionRef = null;
        var subroutines = new DefaultSubroutineRegistry();
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame:   spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUpward:    signals.Enqueue,
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            subroutines:   subroutines);

        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(ctx, scheduler, dispatcher, guards,
            transitionsByState: TransitionMap(),
            initialState: "Disconnected");
        sessionRef = session;
        return new Rig(session, scheduler, signals, kiss);
    }

    private static async Task InboundPump(Rig rig, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<KissFrame> frames;
            try { frames = await rig.Kiss.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (System.IO.IOException) { return; }   // connection torn down

            foreach (var f in frames)
            {
                if (f.Command != KissCommand.Data) continue;
                if (!Ax25Frame.TryParse(f.Payload, out var parsed)) continue;

                // net-sim's afsk1200 channel is broadcast — every node
                // on the channel hears every TX. Filter to frames
                // addressed to our local callsign.
                if (!parsed.Destination.Callsign.Equals(rig.Session.Context.Local)) continue;

                rig.Session.PostEvent(Ax25FrameClassifier.Classify(parsed));
            }
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static Dictionary<string, IReadOnlyList<TransitionSpec>> TransitionMap() => new()
    {
        ["Disconnected"]         = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"]   = DataLink_AwaitingConnection.Transitions,
        ["AwaitingConnection22"] = DataLink_AwaitingConnection22.Transitions,
        ["Connected"]            = DataLink_Connected.Transitions,
        ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
        ["TimerRecovery"]        = Array.Empty<TransitionSpec>(),
    };

    private static Ax25Event TimerExpiry(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _    => throw new InvalidOperationException($"unexpected timer expiry name '{name}'"),
    };

    private static async Task<T?> WaitForSignal<T>(
        ConcurrentQueue<DataLinkSignal> signals,
        TimeSpan budget,
        IReadOnlyList<Task> backgroundTasks,
        CancellationToken outer) where T : DataLinkSignal
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            ThrowIfAnyFaulted(backgroundTasks);
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
}
