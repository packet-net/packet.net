using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// LRU eviction of the per-peer session cache. It used to drop
/// <c>lruOrder.First</c> unconditionally - no state check, no upward signal - and
/// LRU position is refreshed only by frame activity, so a quiet established link
/// was the natural victim of a flood of first-contact frames from distinct (or
/// spoofed) callsigns, each of which claims a slot of its own. The consumer was
/// left holding a session whose timers had been disposed and told nothing
/// (packet-net/packet.net#696). Eviction now prefers a session that is not on a
/// live link, and a live one that must go gets a DL-DISCONNECT-indication.
/// </summary>
public class Ax25ListenerEvictionTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);

    private static Callsign Peer(int i) => new($"G7XY{(char)('A' + i)}", 1);

    private static async Task<Ax25Session> AcceptInbound(Ax25Listener listener, LoopbackModem modem, Callsign peer)
    {
        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAccepted(object? _, Ax25SessionEventArgs e)
        {
            if (e.Session.Context.Remote.Equals(peer))
            {
                accepted.TrySetResult(e.Session);
            }
        }

        listener.SessionAccepted += OnAccepted;
        try
        {
            modem.InjectInbound(Ax25Frame.Sabm(LocalCall, peer));
            var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
            session.CurrentState.Should().Be("Connected");
            return session;
        }
        finally
        {
            listener.SessionAccepted -= OnAccepted;
        }
    }

    [Fact]
    public async Task First_contact_frames_evict_each_other_not_an_established_link()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            MaxCachedPeers = 2,
        });
        await listener.StartAsync();

        var established = await AcceptInbound(listener, modem, Peer(0));

        // A run of pre-SABM XID commands from distinct callsigns: each stages a
        // cached session of its own, in Disconnected.
        for (var i = 1; i <= 6; i++)
        {
            modem.InjectInbound(Ax25Frame.Xid(LocalCall, Peer(i), [], isCommand: true, pollFinal: true));
        }

        await Task.Delay(400);

        listener.ActiveSessions.Count.Should().BeLessThanOrEqualTo(2, "the cap still holds");
        listener.ActiveSessions.Should().Contain(established,
            "a live QSO must outlive a flood of first-contact frames");
        established.CurrentState.Should().Be("Connected");
    }

    [Fact]
    public async Task Evicting_a_live_session_tells_its_consumer()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            MaxCachedPeers = 1,
        });
        await listener.StartAsync();

        var first = await AcceptInbound(listener, modem, Peer(0));
        var disconnected = new TaskCompletionSource<DataLinkSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.DataLinkSignalEmitted += (_, sig) =>
        {
            if (sig is DataLinkDisconnectIndication)
            {
                disconnected.TrySetResult(sig);
            }
        };

        // A second peer connects; with room for one, the live first session is the
        // only candidate left.
        var second = await AcceptInbound(listener, modem, Peer(1));

        (await disconnected.Task.WithTimeout(TimeSpan.FromSeconds(2))).Should().BeOfType<DataLinkDisconnectIndication>();
        listener.ActiveSessions.Should().ContainSingle().Which.Should().Be(second,
            "the newly-accepted session must be the one that stayed cached");
    }
}
