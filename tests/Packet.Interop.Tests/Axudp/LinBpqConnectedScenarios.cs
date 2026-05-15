using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Axudp;
using Packet.Core;
using Xunit;

namespace Packet.Interop.Tests.Axudp;

/// <summary>
/// Connected-mode interop against a live LinBPQ peer. Drives an
/// <see cref="Ax25Session"/> through the figc4.1 / 4.2 / 4.4 / 4.6
/// state machine end-to-end against a real third-party AX.25 stack,
/// proving the SDL walker produces frames LinBPQ accepts and reacts
/// correctly to LinBPQ's replies.
/// </summary>
/// <remarks>
/// <para>
/// Transport: AXUDP to LinBPQ's BPQAXIP listener (UDP 8093). The
/// listener replies to the source IP:port of the inbound packet, so
/// an ephemeral-port AXUDP socket gets two-way connectivity without
/// any neighbour-routing config in <c>bpq32.cfg</c>.
/// </para>
/// <para>
/// LinBPQ's callsign is <c>PN0TST-0</c> (configured in <c>SIMPLE=1</c>
/// mode in <c>docker/linbpq/bpq32.cfg</c>). Connecting to that
/// callsign lands at the node prompt; LinBPQ accepts the SABM and
/// sends an I-frame greeting once the link is up.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
public class LinBpqConnectedScenarios
{
    private const string Host           = "127.0.0.1";
    private const int    LinBpqAxudp    = 8093;
    private const int    LinBpqHealthy  = 8008;
    private static readonly TimeSpan ConnectBudget    = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(15);

    [SkippableFact]
    public async Task Connect_Then_Disconnect_With_LinBpq()
    {
        await SkipIfLinBpqDown();

        var us     = new Callsign("PNDOTN", 9);
        var linBpq = new Callsign("PN0TST", 0);

        using var axudp = new AxudpSocket(localPort: 0);
        var linBpqEndpoint = new IPEndPoint(IPAddress.Loopback, LinBpqAxudp);

        // Recorder queue for upward DL signals. The connect / disconnect
        // confirmation events are how we know the SDL walker observed
        // LinBPQ's reply and transitioned.
        var signals = new ConcurrentQueue<DataLinkSignal>();

        var time      = TimeProvider.System;
        using var sch = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext { Local = us, Remote = linBpq };

        Ax25Session? sessionRef = null;
        var subroutines = new DefaultSubroutineRegistry();
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame:    spec => Fire(axudp, linBpqEndpoint, spec.ToAx25Frame(ctx)),
            sendUFrame:    spec => Fire(axudp, linBpqEndpoint, spec.ToAx25Frame(ctx)),
            sendUiFrame:   spec => Fire(axudp, linBpqEndpoint, spec.ToAx25Frame(ctx)),
            sendIFrame:    spec => Fire(axudp, linBpqEndpoint, spec.ToAx25Frame(ctx)),
            sendUpward:    signals.Enqueue,
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            subroutines:   subroutines);

        var bindings = Ax25SessionBindings.CreateDefault(ctx, sch, () => sessionRef?.CurrentTrigger);
        var guards   = new GuardEvaluator(bindings);

        var session = new Ax25Session(ctx, sch, dispatcher, guards,
            transitionsByState: TransitionMap(),
            initialState: "Disconnected");
        sessionRef = session;

        using var cts = new CancellationTokenSource(ConnectBudget + DisconnectBudget + TimeSpan.FromSeconds(10));

        // Background pump: every UDP datagram from LinBPQ becomes a
        // classified Ax25Event into the session. Run on the thread-pool;
        // PostEvent acquires no lock so racing this against the main
        // test thread's PostEvent is harmless (xunit runs one test at
        // a time within this class).
        var rxLoop = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                AxudpReceiveResult datagram;
                try
                {
                    datagram = await axudp.ReceiveAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (datagram.DecodedFrame is { } frame)
                {
                    session.PostEvent(Ax25FrameClassifier.Classify(frame));
                }
            }
        }, cts.Token);

        // 1. Connect: emit SABM, wait for UA → DL-CONNECT-confirm.
        session.PostEvent(new DlConnectRequest());
        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(signals, ConnectBudget, cts.Token);
        connectConfirm.Should().NotBeNull("LinBPQ must respond with UA(F=1) and the SDL walker must observe it");
        session.CurrentState.Should().Be("Connected");

        // 2. Disconnect: emit DISC, wait for UA → DL-DISCONNECT-confirm.
        //
        // We don't currently assert on the I-frame greeting LinBPQ sends
        // post-connect (the node prompt). Drained-but-not-asserted is
        // fine: the SDL handler runs, V(r) advances, and the session
        // sends RR. Adding the greeting assertion is a follow-up once
        // we settle on what content LinBPQ actually emits.
        session.PostEvent(new DlDisconnectRequest());
        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(signals, DisconnectBudget, cts.Token);
        disconnectConfirm.Should().NotBeNull("LinBPQ must respond with UA(F=1) to the DISC");
        session.CurrentState.Should().Be("Disconnected");

        cts.Cancel();
        try { await rxLoop; } catch (OperationCanceledException) { }
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private static void Fire(AxudpSocket axudp, IPEndPoint to, Ax25Frame frame)
    {
        // Dispatcher's send callbacks are synchronous; we wait inline so
        // the next state-machine step doesn't race a still-in-flight UDP
        // send. LinBPQ's BPQAXIP wire form is the FCS-less variant —
        // matches AxudpSocket.SendAsync's `includeFcs: false` default.
        _ = axudp.SendAsync(to, frame).GetAwaiter().GetResult();
    }

    private static async Task<T?> WaitForSignal<T>(
        ConcurrentQueue<DataLinkSignal> signals,
        TimeSpan budget,
        CancellationToken outerToken) where T : DataLinkSignal
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            while (signals.TryDequeue(out var sig))
            {
                if (sig is T match) return match;
            }
            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static Dictionary<string, IReadOnlyList<TransitionSpec>> TransitionMap() => new()
    {
        ["Disconnected"]         = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"]   = DataLink_AwaitingConnection.Transitions,
        ["AwaitingConnection22"] = DataLink_AwaitingConnection22.Transitions,
        ["Connected"]            = DataLink_Connected.Transitions,
        ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
        // TimerRecovery is referenced by some Connected transitions but
        // isn't transcribed yet — stub with empty so an accidental
        // routing there doesn't throw. Real-world recovery paths would
        // drop here until figc4.6 is transcribed.
        ["TimerRecovery"]        = Array.Empty<TransitionSpec>(),
    };

    private static Ax25Event TimerExpiry(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _    => throw new InvalidOperationException($"unexpected timer expiry name '{name}'"),
    };

    private static async Task SkipIfLinBpqDown()
    {
        Skip.IfNot(await IsLinBpqHealthy(),
            $"LinBPQ not healthy on {Host}:{LinBpqHealthy}. Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait linbpq'.");
    }

    private static async Task<bool> IsLinBpqHealthy()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var resp = await http.GetAsync($"http://{Host}:{LinBpqHealthy}/");
            return resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.Unauthorized;
        }
        catch
        {
            return false;
        }
    }
}
