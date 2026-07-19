using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// <see cref="Ax25Session.AttachConsumerWithReplay"/>: a consumer that attaches AFTER the session
/// has already emitted inbound DL-DATA (the outbound-connect case — the peer's connect banner
/// arrives before the wrapping <c>Ax25NodeConnection</c> can subscribe) still receives that early
/// data, exactly once, ahead of the live stream. This is the unit-level guard for the
/// banner-lost-on-connect regression that the AXUDP node-to-node test proves end-to-end.
/// </summary>
public sealed class Ax25SessionReplayTests
{
    // A no-op dispatcher: these tests only exercise RaiseDataLinkSignal / AttachConsumerWithReplay,
    // neither of which drives a transition, so the action dispatcher is never invoked.
    private sealed class NoOpDispatcher : IActionDispatcher
    {
        public void Execute(IEnumerable<ActionStep> actions, TransitionContext tx) { }
    }

    private static Ax25Session NewSession()
    {
        var scheduler = new SystemTimerScheduler(new FakeTimeProvider());
        var ctx = new Ax25SessionContext { Local = new Callsign("M0LTE", 0), Remote = new Callsign("G7XYZ", 7) };
        var guards = new GuardEvaluator(Ax25SessionBindings.CreateDefault(ctx, scheduler));
        return new Ax25Session(
            ctx, scheduler, new NoOpDispatcher(), guards,
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
    }

    private static DataLinkDataIndication Data(string text) =>
        new(System.Text.Encoding.ASCII.GetBytes(text), Ax25Frame.PidNoLayer3);

    [Fact]
    public void Data_emitted_before_attach_is_replayed_to_the_late_consumer()
    {
        var session = NewSession();
        // Peer's "banner" lands before any consumer is attached.
        session.RaiseDataLinkSignal(Data("BANNER>"));

        var seen = new List<string>();
        session.AttachConsumerWithReplay((_, sig) =>
        {
            if (sig is DataLinkDataIndication di)
            {
                seen.Add(System.Text.Encoding.ASCII.GetString(di.Info.Span));
            }
        });

        seen.Should().ContainSingle().Which.Should().Be("BANNER>", "the pre-subscribe banner is replayed exactly once");
    }

    [Fact]
    public void Live_data_after_attach_flows_and_is_not_duplicated_with_the_replay()
    {
        var session = NewSession();
        session.RaiseDataLinkSignal(Data("EARLY"));

        var seen = new List<string>();
        session.AttachConsumerWithReplay((_, sig) =>
        {
            if (sig is DataLinkDataIndication di)
            {
                seen.Add(System.Text.Encoding.ASCII.GetString(di.Info.Span));
            }
        });

        // A signal emitted after the attach goes live, once.
        session.RaiseDataLinkSignal(Data("LIVE"));

        seen.Should().Equal("EARLY", "LIVE");
    }

    [Fact]
    public void Only_data_indications_are_buffered_not_other_signals()
    {
        var session = NewSession();
        // A non-data signal before attach must not be buffered/replayed (it isn't inbound data).
        session.RaiseDataLinkSignal(new DataLinkConnectConfirm());

        var seen = new List<DataLinkSignal>();
        session.AttachConsumerWithReplay((_, sig) => seen.Add(sig));

        seen.Should().BeEmpty("only DL-DATA indications are replayed, not connect/other signals");
    }

    [Fact]
    public void A_redial_on_a_reused_session_replays_the_second_connections_banner()
    {
        // packet.net#659: Ax25Listener caches and REUSES an Ax25Session per (local, remote)
        // across connect/disconnect cycles. A one-shot buffer stays disarmed after the first
        // connection's consumer attaches, so the SECOND connect to the same peer replays
        // nothing and the banner is lost — even though the session L2-ACKs the I-frame. The
        // fresh DL-CONNECT-confirm on the re-dial must re-arm the buffer.
        var session = NewSession();

        // --- First connection: link up, banner arrives, consumer attaches + replays, detaches.
        session.RaiseDataLinkSignal(new DataLinkConnectConfirm());
        session.RaiseDataLinkSignal(Data("BANNER-1>"));

        var first = new List<string>();
        EventHandler<DataLinkSignal> firstConsumer = (_, sig) =>
        {
            if (sig is DataLinkDataIndication di)
            {
                first.Add(System.Text.Encoding.ASCII.GetString(di.Info.Span));
            }
        };
        session.AttachConsumerWithReplay(firstConsumer);
        first.Should().ContainSingle().Which.Should().Be("BANNER-1>", "the first connection's banner replays as before");
        session.DataLinkSignalEmitted -= firstConsumer;   // the wrapping connection is disposed

        // --- Second connection on the SAME cached session: re-dial, second banner pre-attach.
        session.RaiseDataLinkSignal(new DataLinkConnectConfirm());   // re-establish → must re-arm
        session.RaiseDataLinkSignal(Data("BANNER-2>"));

        var second = new List<string>();
        session.AttachConsumerWithReplay((_, sig) =>
        {
            if (sig is DataLinkDataIndication di)
            {
                second.Add(System.Text.Encoding.ASCII.GetString(di.Info.Span));
            }
        });

        second.Should().ContainSingle().Which.Should().Be("BANNER-2>",
            "the re-dial re-arms the early-inbound buffer, so the second connection's banner replays too (not the first's)");
    }

    [Fact]
    public void An_inbound_reconnect_indication_also_rearms_the_buffer()
    {
        // The symmetric inbound case: a peer that reconnects to us (DL-CONNECT-indication)
        // and immediately sends data must have that data replayed to the fresh wrapping
        // consumer, on a reused session, exactly like the outbound (confirm) path.
        var session = NewSession();

        session.RaiseDataLinkSignal(new DataLinkConnectIndication());
        session.RaiseDataLinkSignal(Data("HELLO-1"));
        EventHandler<DataLinkSignal> firstConsumer = (_, _) => { };
        session.AttachConsumerWithReplay(firstConsumer);
        session.DataLinkSignalEmitted -= firstConsumer;

        session.RaiseDataLinkSignal(new DataLinkConnectIndication());   // reconnect → re-arm
        session.RaiseDataLinkSignal(Data("HELLO-2"));

        var seen = new List<string>();
        session.AttachConsumerWithReplay((_, sig) =>
        {
            if (sig is DataLinkDataIndication di)
            {
                seen.Add(System.Text.Encoding.ASCII.GetString(di.Info.Span));
            }
        });

        seen.Should().ContainSingle().Which.Should().Be("HELLO-2",
            "an inbound reconnect indication re-arms the buffer just as an outbound connect confirm does");
    }

    [Fact]
    public void A_connect_confirm_rearm_discards_stale_pre_connect_data()
    {
        // Re-arming on connect creates a fresh buffer, so any data that predates the
        // connection boundary (a stray/duplicate frame from a prior lifecycle) cannot
        // leak into the new connection's consumer.
        var session = NewSession();
        session.RaiseDataLinkSignal(Data("STALE"));          // buffered under the initial arm
        session.RaiseDataLinkSignal(new DataLinkConnectConfirm());   // boundary → fresh buffer

        var seen = new List<string>();
        session.AttachConsumerWithReplay((_, sig) =>
        {
            if (sig is DataLinkDataIndication di)
            {
                seen.Add(System.Text.Encoding.ASCII.GetString(di.Info.Span));
            }
        });

        seen.Should().BeEmpty("data emitted before the connect boundary is not replayed to the post-connect consumer");
    }
}
