using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Multi-callsign coverage for <see cref="Ax25Listener"/> (the RHPv2 server's engine seam):
/// <see cref="Ax25Listener.AddLocalAlias"/> makes the listener answer inbound SABM/TEST for an
/// additional local callsign (the session's <c>Context.Local</c> = the alias), the session
/// cache keys by the (local, remote) pair so the same remote can hold links to MyCall and to
/// an alias simultaneously, and <see cref="Ax25Listener.ConnectAsync(Callsign, Callsign, CancellationToken)"/>
/// originates from an alias.
/// </summary>
public class Ax25ListenerLocalAliasTests
{
    private static readonly Callsign NodeCall = new("M9YYY", 0);
    private static readonly Callsign AppCall = new("M0LTE", 7);
    private static readonly Callsign Peer = new("G7AAA", 0);

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Sabm_To_A_Registered_Alias_Is_Accepted_With_Local_Set_To_The_Alias()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = NodeCall });
        listener.AddLocalAlias(AppCall);

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(AppCall, Peer));

        var session = await accepted.Task.WithTimeout(Budget);
        session.Context.Local.Should().Be(AppCall, "the session belongs to the alias the SABM addressed");
        session.Context.Remote.Should().Be(Peer);
        await ListenerTestSupport.WaitFor(() => session.CurrentState == "Connected", Budget);
    }

    [Fact]
    public async Task Sabm_To_An_Unregistered_Callsign_Is_Ignored()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = NodeCall });

        bool any = false;
        listener.SessionAccepted += (_, _) => any = true;
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(new Callsign("G0XXX", 3), Peer));

        // Deaf: no session, no UA/DM transmitted (frames not addressed to us are monitor-only).
        await Task.Delay(300);
        any.Should().BeFalse();
        modem.SentFrames.Count.Should().Be(0);
    }

    [Fact]
    public async Task Same_Remote_Holds_Distinct_Sessions_To_MyCall_And_To_An_Alias()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = NodeCall });
        listener.AddLocalAlias(AppCall);

        var sessions = new ConcurrentDictionary<Callsign, Ax25Session>();   // keyed by LOCAL
        listener.SessionAccepted += (_, e) => sessions[e.Session.Context.Local] = e.Session;
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(NodeCall, Peer));
        modem.InjectInbound(Ax25Frame.Sabm(AppCall, Peer));

        await ListenerTestSupport.WaitFor(() => sessions.Count == 2, Budget, "both locals accepted");
        sessions[NodeCall].Should().NotBeSameAs(sessions[AppCall],
            "the (local, remote) pair keys the cache — one remote, two locals, two sessions");
        await ListenerTestSupport.WaitFor(
            () => sessions[NodeCall].CurrentState == "Connected" && sessions[AppCall].CurrentState == "Connected", Budget);
    }

    [Fact]
    public async Task RemoveLocalAlias_Stops_New_Accepts_But_A_Live_Session_Keeps_Routing()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = NodeCall });
        listener.AddLocalAlias(AppCall);

        var accepted = new ConcurrentBag<Ax25Session>();
        listener.SessionAccepted += (_, e) => accepted.Add(e.Session);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(AppCall, Peer));
        await ListenerTestSupport.WaitFor(() => accepted.Count == 1, Budget);
        var live = accepted.First();
        await ListenerTestSupport.WaitFor(() => live.CurrentState == "Connected", Budget);

        listener.RemoveLocalAlias(AppCall);

        // The live link keeps working: its cache key admits the peer's frames (an RR here).
        int before = modem.SentFrames.Count;
        modem.InjectInbound(Ax25Frame.Rr(AppCall, Peer, nr: 0, isCommand: true, pollFinal: true));
        await ListenerTestSupport.WaitFor(() => modem.SentFrames.Count > before, Budget,
            "the live session must answer a P-bit RR (its key still routes)");

        // But a NEW station SABMing the deregistered alias is ignored.
        int sentBefore = modem.SentFrames.Count;
        modem.InjectInbound(Ax25Frame.Sabm(AppCall, new Callsign("G7BBB", 0)));
        await Task.Delay(300);
        accepted.Count.Should().Be(1, "no new session for a deregistered alias");
        modem.SentFrames.Count.Should().Be(sentBefore, "deaf to the new SABM — no UA, no DM");
    }

    [Fact]
    public async Task ConnectAsync_With_A_Local_Override_Originates_From_The_Alias()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = NodeCall });
        await listener.StartAsync();

        var connect = listener.ConnectAsync(Peer, AppCall);

        // The SABM on the wire must carry the alias as its SOURCE.
        await ListenerTestSupport.WaitFor(() => modem.SentFrames.Count >= 1, Budget);
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var sabm).Should().BeTrue();
        sabm!.Source.Callsign.Should().Be(AppCall, "the session originates from the alias");
        sabm.Destination.Callsign.Should().Be(Peer);

        // Peer answers UA addressed to the ALIAS — the (local, remote) key must route it.
        modem.InjectInbound(Ax25Frame.Ua(AppCall, Peer, finalBit: true));
        var session = await connect.WithTimeout(Budget);
        session.Context.Local.Should().Be(AppCall);
        session.CurrentState.Should().Be("Connected");
    }

    [Fact]
    public async Task A_Test_Command_To_An_Alias_Is_Answered_As_The_Alias()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = NodeCall });
        listener.AddLocalAlias(AppCall);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Test(AppCall, Peer, "ping"u8.ToArray(), isCommand: true, pollFinal: true));

        await ListenerTestSupport.WaitFor(() => modem.SentFrames.Count >= 1, Budget, "TEST response sent");
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var resp).Should().BeTrue();
        resp!.Source.Callsign.Should().Be(AppCall, "the station 'at' the alias answers, not the node");
        resp.Destination.Callsign.Should().Be(Peer);
    }

    [Fact]
    public async Task AddLocalAlias_Is_Refcounted()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = NodeCall });
        listener.AddLocalAlias(AppCall);
        listener.AddLocalAlias(AppCall);     // a second independent registration
        listener.RemoveLocalAlias(AppCall);  // balances one — STILL registered

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(AppCall, Peer));
        var session = await accepted.Task.WithTimeout(Budget);
        session.Context.Local.Should().Be(AppCall, "one registration remains, so the alias still answers");

        listener.RemoveLocalAlias(AppCall);
        listener.RemoveLocalAlias(AppCall);  // over-removal is harmless
    }
}
