using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Capabilities;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Capabilities;

/// <summary>
/// The user-CONNECT dial path (<see cref="Ax25OutboundConnector"/>) consults the per-peer
/// capability cache to pick the dial version + pre-connect-XID probe, then records the OUTCOME of
/// a RETURNED dial — never on a throw. Asserted over a real handshake on the in-memory radio: the
/// pre-seeded plan is observed on the wire (the peer's first received frame), and the cache state
/// is checked after a returned dial and after a dial that throws.
/// </summary>
[Trait("Category", "Node")]
public sealed class Ax25OutboundConnectorCapabilityCacheWiringTests
{
    private const string PortId = "p1";
    private static readonly Callsign LocalCall = new("M0AAA", 1);
    private static readonly Callsign Target = new("M0BBB", 2);

    // U-frame control octets (P/F masked out) — mirrors the Ax25 listener wire tests.
    private const byte SabmBase = 0x2F;
    private const byte XidBase = 0xAF;
    private static byte UBase(Ax25Frame f) => (byte)(f.Control & 0xEF);

    private static Ax25Listener CallerListener(IAx25Transport transport) => new(transport, new Ax25ListenerOptions
    {
        MyCall = LocalCall,
        N2 = 1,
        // Tight T1V so a dial to a dead peer fails its (N2+1)·T1V budget fast (no peer answers).
        T1V = TimeSpan.FromMilliseconds(80),
    }, TimeProvider.System);

    [Fact]
    public async Task A_pre_seeded_record_steers_the_dial_version_and_xid_probe()
    {
        var cache = new PeerCapabilityCache();
        // Fully-known mod-8 non-XID-answerer ⇒ PlanDial(UserConnect) = extended:false, no XID:
        // learn non-extended (offered SABME, degraded) AND non-XID-answerer (probed, no answer).
        cache.RecordOutcome(PortId, Target.ToString(), dialedExtended: true, observedIsExtended: false,
            dialedPreConnectXid: false, observedSrejEnabled: false);
        cache.RecordOutcome(PortId, Target.ToString(), dialedExtended: false, observedIsExtended: false,
            dialedPreConnectXid: true, observedSrejEnabled: false);

        var (eA, eB) = InMemoryRadio.CreatePair();
        var caller = CallerListener(eA);
        await caller.StartAsync();
        var echo = new EchoStation(eB, Target, "ok");
        await echo.StartAsync();

        // Capture the FIRST frame the caller transmits — it proves what the connector dialled
        // (tap the caller's own TX: same wire, no dependence on the peer's RX timing).
        var firstReceived = new TaskCompletionSource<Ax25Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        caller.FrameTraced += (_, e) =>
        {
            if (e.Direction == FrameDirection.Transmitted)
            {
                firstReceived.TrySetResult(e.Frame);
            }
        };

        var connector = new Ax25OutboundConnector(PortId, caller, claim: null, localOverride: null, cache: cache);
        await using var connection = await connector.ConnectAsync(Target);

        var first = await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        UBase(first).Should().Be(SabmBase,
            "a known mod-8 non-XID-answerer must be dialled with a plain SABM — no SABME, no pre-connect XID");
        UBase(first).Should().NotBe(XidBase);

        await caller.DisposeAsync();
        await echo.DisposeAsync();
    }

    [Fact]
    public async Task A_returned_dial_records_the_observed_outcome()
    {
        var cache = new PeerCapabilityCache();   // miss → optimistic SABME plan (UserConnect)

        var (eA, eB) = InMemoryRadio.CreatePair();
        var caller = CallerListener(eA);
        await caller.StartAsync();
        var echo = new EchoStation(eB, Target, "ok");
        await echo.StartAsync();

        var connector = new Ax25OutboundConnector(PortId, caller, claim: null, localOverride: null, cache: cache);
        await using var connection = await connector.ConnectAsync(Target);

        // The dial RETURNED a session, so an outcome was recorded for (port, target). The plan
        // offered SABME (the UserConnect miss default), so the extended dimension was probed.
        var rec = cache.All().Single();
        rec.PortId.Should().Be(PortId);
        rec.Peer.Should().Be(Target.ToString());
        rec.SupportsExtended.Should().NotBeNull("the SABME dial probed the extended dimension, so it was learned");

        await caller.DisposeAsync();
        await echo.DisposeAsync();
    }

    [Fact]
    public async Task A_dial_that_throws_leaves_the_cache_unchanged()
    {
        var cache = new PeerCapabilityCache();

        var (eA, _) = InMemoryRadio.CreatePair();   // no peer on the other endpoint ⇒ no UA ⇒ throw
        var caller = CallerListener(eA);
        await caller.StartAsync();

        var connector = new Ax25OutboundConnector(PortId, caller, claim: null, localOverride: null, cache: cache);

        Func<Task> dial = async () => await connector.ConnectAsync(Target);
        await dial.Should().ThrowAsync<Exception>("no peer answers, so the connect exhausts its budget and throws");

        cache.All().Should().BeEmpty("a dial that throws must NOT record an outcome (no link ⇒ no signal)");

        await caller.DisposeAsync();
    }
}
