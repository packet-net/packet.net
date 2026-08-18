using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Coverage for <see cref="Ax25Listener.UpdateSessionParameters"/> - the live
/// reseed of per-session AX.25 parameters on a RUNNING listener. The contract:
/// new sessions built after the reseed pick up the new values; sessions that
/// already exist keep the parameters (and object identity) they were built with;
/// the listener itself is never rebuilt.
/// </summary>
public class Ax25ListenerReseedTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCallA = new("G7XYZ", 7);
    private static readonly Callsign PeerCallB = new("M5ABC", 3);

    [Fact]
    public void CurrentSessionParameters_reflects_the_construction_time_options()
    {
        var modem = new LoopbackModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            N2 = 5,
            K = 3,
            T1V = TimeSpan.FromMilliseconds(4000),
            MaxCachedPeers = 17,
        });

        var sp = listener.CurrentSessionParameters;
        sp.N2.Should().Be(5);
        sp.K.Should().Be(3);
        sp.T1V.Should().Be(TimeSpan.FromMilliseconds(4000));
        sp.MaxCachedPeers.Should().Be(17);
    }

    [Fact]
    public async Task A_new_session_after_reseed_uses_the_new_params_an_existing_one_keeps_its_own()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            N2 = 5,
            K = 3,
            T1V = TimeSpan.FromMilliseconds(4000),
        });

        var accepted = new System.Collections.Concurrent.ConcurrentQueue<Ax25Session>();
        listener.SessionAccepted += (_, e) => accepted.Enqueue(e.Session);
        await listener.StartAsync();

        // First peer connects in BEFORE the reseed - built with N2=5, k=3, T1V=4 s.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        await WaitUntil(() => !accepted.IsEmpty, "first session accepted");
        accepted.TryDequeue(out var sessionA).Should().BeTrue();
        sessionA!.Context.N2.Should().Be(5);
        sessionA.Context.K.Should().Be(3);
        sessionA.Context.T1V.Should().Be(TimeSpan.FromMilliseconds(4000));

        // Live reseed: N2 5→9, k 3→7, T1V 4 s→9 s.
        listener.UpdateSessionParameters(new Ax25SessionParameters
        {
            N2 = 9,
            K = 7,
            T1V = TimeSpan.FromMilliseconds(9000),
        });

        // The existing session is untouched - same object, same params.
        sessionA.Context.N2.Should().Be(5, "an existing session keeps the params it was built with");
        sessionA.Context.K.Should().Be(3);
        sessionA.Context.T1V.Should().Be(TimeSpan.FromMilliseconds(4000));

        // A different peer connects in AFTER the reseed - built with the new params.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallB));
        await WaitUntil(() => !accepted.IsEmpty, "second session accepted");
        accepted.TryDequeue(out var sessionB).Should().BeTrue();
        sessionB!.Should().NotBeSameAs(sessionA);
        sessionB.Context.N2.Should().Be(9, "a new session picks up the reseeded N2");
        sessionB.Context.K.Should().Be(7, "a new session picks up the reseeded k");
        sessionB.Context.T1V.Should().Be(TimeSpan.FromMilliseconds(9000), "a new session picks up the reseeded T1V");

        // The first session is STILL untouched after the second was built.
        sessionA.Context.N2.Should().Be(5);
    }

    [Fact]
    public async Task Reseeding_back_to_defaults_clears_overrides_for_new_sessions()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            N2 = 5,
        });

        var accepted = new System.Collections.Concurrent.ConcurrentQueue<Ax25Session>();
        listener.SessionAccepted += (_, e) => accepted.Enqueue(e.Session);
        await listener.StartAsync();

        // Reseed with an all-null record → new sessions fall back to spec defaults.
        listener.UpdateSessionParameters(new Ax25SessionParameters());

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));
        await WaitUntil(() => !accepted.IsEmpty, "session accepted");
        accepted.TryDequeue(out var session).Should().BeTrue();
        session!.Context.N2.Should().Be(10, "a null N2 reseed restores the spec default (10)");
    }

    [Fact]
    public async Task UpdateSessionParameters_after_dispose_throws()
    {
        var modem = new LoopbackModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        await listener.StartAsync();
        await listener.DisposeAsync();

        var act = () => listener.UpdateSessionParameters(new Ax25SessionParameters { N2 = 3 });
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void UpdateSessionParameters_null_throws()
    {
        var modem = new LoopbackModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });
        var act = () => listener.UpdateSessionParameters(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private static async Task WaitUntil(Func<bool> condition, string reason)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }
        throw new TimeoutException($"condition not met within 2s: {reason}");
    }
}
