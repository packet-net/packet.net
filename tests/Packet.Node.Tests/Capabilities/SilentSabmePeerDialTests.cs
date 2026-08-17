using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Capabilities;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Capabilities;

/// <summary>
/// The GB7RDG cutover blocker, closed at the node's dial-policy layer (#724): a peer that
/// <b>ignores</b> a SABME instead of rejecting it. On air, pdn sent 44 SABMEs and 0 SABMs to BPQ
/// partners, because only a DM or an FRMR degrades inside the engine and the per-peer capability
/// cache could only learn from dials that RETURNED.
/// </summary>
/// <remarks>
/// The peer here is a real <see cref="Ax25Listener"/> behind <see cref="SabmeDeafTransport"/>: it
/// never hears the SABME, and answers the SABM with a genuine UA. Every assertion is over that
/// real handshake on the in-memory radio, not a mock.
/// </remarks>
[Trait("Category", "Node")]
public sealed class SilentSabmePeerDialTests
{
    private const string PortId = "p1";
    private const string OtherPortId = "p2";
    private static readonly Callsign LocalCall = new("M0AAA", 1);
    private static readonly Callsign Target = new("M0BBB", 2);

    // U-frame control octets with the P/F bit masked out.
    private const byte SabmBase = 0x2F;
    private const byte SabmeBase = 0x6F;
    private static byte UBase(Ax25Frame f) => (byte)(f.Control & 0xEF);

    // A caller whose connect budget is short but with a full T1V of slack between the SDL giving
    // up (N2 x T1V) and ConnectAsync giving up ((N2+1) x T1V), so the "is the session back in
    // Disconnected by the time the dial throws?" question is answered by the protocol, not by a
    // race. The states are asserted explicitly below rather than assumed.
    private static Ax25Listener CallerListener(IAx25Transport transport) => new(transport, new Ax25ListenerOptions
    {
        MyCall = LocalCall,
        N2 = 1,
        T1V = TimeSpan.FromMilliseconds(400),
    }, TimeProvider.System);

    private static PortLinkConfig Link(LinkDialPreference dial, LinkPreConnectXid xid = LinkPreConnectXid.Off)
        => new() { Dial = dial, PreConnectXid = xid };

    // Stand up caller + SABME-deaf peer on one in-memory medium.
    private static async Task<(Ax25Listener Caller, SabmeDeafTransport PeerWire, EchoStation Peer, List<Ax25Frame> Sent)> RadioAsync()
    {
        var (a, b) = InMemoryRadio.CreatePair();
        var caller = CallerListener(a);
        var sent = new List<Ax25Frame>();
        caller.FrameTraced += (_, e) =>
        {
            if (e.Direction == FrameDirection.Transmitted)
            {
                lock (sent) { sent.Add(e.Frame); }
            }
        };
        await caller.StartAsync();

        var peerWire = new SabmeDeafTransport(b);
        var peer = new EchoStation(peerWire, Target, "ok");
        await peer.StartAsync();
        return (caller, peerWire, peer, sent);
    }

    private static byte[] TxBases(List<Ax25Frame> sent)
    {
        lock (sent) { return [.. sent.Select(UBase)]; }
    }

    [Fact]
    public async Task An_exhausted_extended_dial_leaves_the_cached_session_disconnected_and_a_fresh_v20_dial_sends_a_sabm()
    {
        // THE engine question behind the connector's immediate v2.0 retry: after ConnectAsync gives
        // up on a SABME nobody answered, is the cached session in a state where a fresh
        // ConnectAsync(extended: false) posts a NEW DL-CONNECT-request that sends a SABM? It is:
        // figc4.2/figc4.6 hit RC == N2 at N2 x T1V, emit DL-DISCONNECT-indication and return to
        // Disconnected, a full T1V before ConnectAsync's (N2+1) x T1V budget expires. So the retry
        // re-enters figc4.1 Establish Data Link cleanly on the SAME cached session (which keeps its
        // SRT/T1V history) rather than needing the session torn down and rebuilt.
        //
        // Note WHICH exception that produces, because it is why the connector's degrade trigger is
        // "the peer said nothing" rather than "we got a TimeoutException": the SDL's own give-up
        // arrives as a teardown signal, so ConnectAsync reports InvalidOperationException - the
        // very same exception a DM refusal produces - and the TimeoutException the budget would
        // eventually raise is never reached.
        var (caller, peerWire, peer, sent) = await RadioAsync();

        Func<Task> extended = () => caller.ConnectAsync(Target, LocalCall, extended: true, preConnectXidNegotiatesSrej: false);
        await extended.Should().ThrowAsync<InvalidOperationException>(
            "the peer is deaf to SABME, so the SDL exhausts N2 and tears the attempt down before the connect budget expires");

        peerWire.SabmesIgnored.Should().BeGreaterThan(0, "the SABME reached the peer's medium and was ignored");
        TxBases(sent).Should().AllSatisfy(c => c.Should().Be(SabmeBase), "the whole first dial was v2.2");

        var cached = caller.ActiveSessions.Single(s => s.Context.Remote.Equals(Target));
        cached.CurrentState.Should().Be("Disconnected",
            "the SDL gives up at N2 x T1V, a full T1V before ConnectAsync's (N2+1) x T1V budget - so the session is idle, not still awaiting a UA");

        // The immediate retry, on the same cached session.
        var session = await caller.ConnectAsync(Target, LocalCall, extended: false, preConnectXidNegotiatesSrej: false);

        session.Should().BeSameAs(cached, "the retry reuses the cached session (preserving its SRT/T1V history), it does not build a new one");
        session.Context.IsExtended.Should().BeFalse("the retry is a plain v2.0 link");
        TxBases(sent).Should().Contain(SabmBase, "the retry posted a new DL-CONNECT-request, which sent a SABM");
        peer.SawConnect.Should().BeTrue("the peer answered the SABM with a UA");

        await caller.DisposeAsync();
        await peer.DisposeAsync();
    }

    [Fact]
    public async Task Dial_v20_connects_first_time_with_no_sabme_at_all()
    {
        // The operator has declared the port v2.0 (the BPQ-facing port). No probe, no stall.
        var cache = new PeerCapabilityCache();
        var (caller, peerWire, peer, sent) = await RadioAsync();

        var connector = new Ax25OutboundConnector(
            PortId, caller, claim: null, localOverride: null, cache: cache,
            linkPolicy: () => Link(LinkDialPreference.V20));

        await using var connection = await connector.ConnectAsync(Target);

        peerWire.SabmesIgnored.Should().Be(0, "a v20 port never offers SABME, so there is nothing to ignore");
        TxBases(sent).Should().Contain(SabmBase).And.NotContain(SabmeBase);
        peer.SawConnect.Should().BeTrue();

        await caller.DisposeAsync();
        await peer.DisposeAsync();
    }

    [Fact]
    public async Task Auto_learns_the_silent_peer_and_still_connects_on_the_first_call()
    {
        // The default. The first CONNECT still succeeds - the connector retries v2.0 itself after
        // the SABME draws nothing - and the negative is remembered so the next one goes straight
        // there. This is the on-air behaviour the cutover needed.
        var cache = new PeerCapabilityCache();
        var (caller, peerWire, peer, sent) = await RadioAsync();

        var connector = new Ax25OutboundConnector(
            PortId, caller, claim: null, localOverride: null, cache: cache,
            linkPolicy: () => Link(LinkDialPreference.Auto));

        await using var connection = await connector.ConnectAsync(Target);

        peerWire.SabmesIgnored.Should().BeGreaterThan(0, "auto offers v2.2 first");
        TxBases(sent).Should().Contain(SabmBase, "and retries as v2.0 when the SABME draws no answer at all");
        peer.SawConnect.Should().BeTrue("so the operator's FIRST connect succeeds rather than just timing out");

        var rec = cache.All().Single();
        rec.PortId.Should().Be(PortId);
        rec.Peer.Should().Be(Target.ToString());
        rec.SupportsExtended.Should().BeFalse("the silent-to-SABME negative is learned - the gap that made this dial forever");
        rec.LastRefused.Should().NotBeNull();

        await caller.DisposeAsync();
        await peer.DisposeAsync();
    }

    [Fact]
    public async Task The_learned_negative_makes_the_next_dial_on_that_port_lead_with_a_sabm()
    {
        var cache = new PeerCapabilityCache();
        var (caller, peerWire, peer, _) = await RadioAsync();

        var connector = new Ax25OutboundConnector(
            PortId, caller, claim: null, localOverride: null, cache: cache,
            linkPolicy: () => Link(LinkDialPreference.Auto));

        await using (var first = await connector.ConnectAsync(Target)) { }
        int ignoredAfterFirst = peerWire.SabmesIgnored;
        ignoredAfterFirst.Should().BeGreaterThan(0);

        // Let the DISC the dispose sent finish before re-dialling; a second DL-CONNECT-request
        // posted while the release handshake is still in flight is a test artefact, not the
        // behaviour under test (a real operator re-connects seconds later, not microseconds).
        var session = caller.ActiveSessions.Single(x => x.Context.Remote.Equals(Target));
        await Wait.ForAsync(() => session.CurrentState == "Disconnected", "the first link releases");

        await using var second = await connector.ConnectAsync(Target);

        peerWire.SabmesIgnored.Should().Be(ignoredAfterFirst,
            "the second dial leads with a SABM: no further SABME is offered, so no further stall is paid");

        await caller.DisposeAsync();
        await peer.DisposeAsync();
    }

    [Fact]
    public async Task Dial_v22_never_falls_back_and_learns_nothing()
    {
        // A port pinned to v2.2 means "every station here is v2.2-capable", so a timeout is
        // "the station is off air", not "it ignores SABME". Degrading would be the silent
        // fallback AX.25 v2.2 6.3.1 forbids, and learning a negative from it would be wrong.
        var cache = new PeerCapabilityCache();
        var (caller, peerWire, peer, sent) = await RadioAsync();

        var connector = new Ax25OutboundConnector(
            PortId, caller, claim: null, localOverride: null, cache: cache,
            linkPolicy: () => Link(LinkDialPreference.V22));

        Func<Task> dial = async () => await connector.ConnectAsync(Target);
        await dial.Should().ThrowAsync<InvalidOperationException>("a v22 port offers SABME and only SABME");

        peerWire.SabmesIgnored.Should().BeGreaterThan(0);
        TxBases(sent).Should().NotContain(SabmBase, "no v2.0 fallback is attempted");
        peer.SawConnect.Should().BeFalse();
        cache.All().Should().BeEmpty("a v22 port learns no negative from a peer that did not answer");

        await caller.DisposeAsync();
        await peer.DisposeAsync();
    }

    [Fact]
    public async Task The_learned_negative_is_per_port_peer_pair()
    {
        // Capability is a property of the LINK, not the callsign: the same station reachable on a
        // second port is still worth the v2.2 probe there.
        var cache = new PeerCapabilityCache();
        var (caller, _, peer, _) = await RadioAsync();

        var connector = new Ax25OutboundConnector(
            PortId, caller, claim: null, localOverride: null, cache: cache,
            linkPolicy: () => Link(LinkDialPreference.Auto));
        await using var connection = await connector.ConnectAsync(Target);

        cache.PlanDial(PortId, Target.ToString(), PeerDialPolicy.UserConnect, PortLinkConfig.Default)
            .Extended.Should().BeFalse("the port that learned the negative dials v2.0");
        cache.PlanDial(OtherPortId, Target.ToString(), PeerDialPolicy.UserConnect, PortLinkConfig.Default)
            .Extended.Should().BeTrue("the same peer on another port has not been probed there, so it still gets the v2.2 offer");

        await caller.DisposeAsync();
        await peer.DisposeAsync();
    }

    [Fact]
    public async Task A_mod8_dial_that_throws_still_records_nothing()
    {
        // The pre-existing invariant, kept: only the extended-dial timeout is a capability signal.
        // A mod-8 dial into thin air says nothing about anything.
        var cache = new PeerCapabilityCache();
        var (a, _) = InMemoryRadio.CreatePair();   // no peer at all on the other endpoint
        var caller = CallerListener(a);
        await caller.StartAsync();

        var connector = new Ax25OutboundConnector(
            PortId, caller, claim: null, localOverride: null, cache: cache,
            linkPolicy: () => Link(LinkDialPreference.V20));

        Func<Task> dial = async () => await connector.ConnectAsync(Target);
        await dial.Should().ThrowAsync<Exception>();

        cache.All().Should().BeEmpty("no link of either version, and no SABME was offered, so there is no signal to record");

        await caller.DisposeAsync();
    }
}
