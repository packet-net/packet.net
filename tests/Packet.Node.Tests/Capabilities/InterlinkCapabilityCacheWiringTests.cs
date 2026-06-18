using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Packet.Node.Core.Capabilities;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.NetRom;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Capabilities;

/// <summary>
/// The NET/ROM interlink dial path consults the per-peer capability cache (PlanDial), threads
/// the resulting plan to the dial, and records the OUTCOME — but only on a RETURNED dial, never
/// on a throw. Drives <see cref="NetRomService.EnsureInterlinkForTestAsync"/> with the
/// claim-aware <see cref="NetRomService.OpenInterlink"/> hook capturing the plan it was handed
/// and returning a session with a controlled context, so the wiring is asserted deterministically
/// without standing up the full L4 circuit machinery.
/// </summary>
[Trait("Category", "Node")]
public sealed class InterlinkCapabilityCacheWiringTests
{
    private const string PortId = "p1";
    private static readonly Callsign NodeCall = new("GB7AAA", 0);
    private static readonly Callsign Neighbour = new("GB7NBR", 0);

    private static NetRomConfig InterlinkConfig() => new()
    {
        Enabled = true,
        Connect = true,   // routing role opens connected-mode interlinks
        TransportTimeoutSeconds = 2,
    };

    // A bare started listener over one InMemoryRadio endpoint, for AttachPort (its identity is
    // all EnsureInterlinkAsync's no-route fallback needs — it picks the first attachment).
    private static async Task<Ax25Listener> StartListenerAsync(IKissModem modem, Callsign myCall)
    {
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = myCall,
            N2 = TestAx25Timing.StationN2,
        }, TimeProvider.System);
        await listener.StartAsync();
        return listener;
    }

    // Build one REAL connected Ax25Session (two listeners over an InMemoryRadio pair complete a
    // handshake) so a test can hand it back from the OpenInterlink hook with a controlled context.
    private static async Task<(Ax25Session Session, IAsyncDisposable A, IAsyncDisposable B)> ConnectedSessionAsync()
    {
        var (eA, eB) = InMemoryRadio.CreatePair();
        var caller = await StartListenerAsync(eA, new Callsign("M0AAA", 1));
        var echo = new EchoStation(eB, new Callsign("M0BBB", 2), "ok");
        await echo.StartAsync();
        var session = await caller.ConnectAsync(new Callsign("M0BBB", 2));
        return (session, caller, echo);
    }

    [Fact]
    public async Task Pre_seeded_supports_extended_makes_the_interlink_dial_SABME()
    {
        var cache = new PeerCapabilityCache();
        // Learn the neighbour is extended-capable on this port (a returned SABME dial).
        cache.RecordOutcome(PortId, Neighbour.ToString(), dialedExtended: true, observedIsExtended: true,
            dialedPreConnectXid: false, observedSrejEnabled: false);

        var (session, a, b) = await ConnectedSessionAsync();
        await using var _ = a;
        await using var __ = b;

        var (eN, _) = InMemoryRadio.CreatePair();
        var portListener = await StartListenerAsync(eN, NodeCall);
        await using var svc = new NetRomService(InterlinkConfig(), TimeProvider.System,
            NullLogger<NetRomService>.Instance, store: null, capabilityCache: cache);
        svc.AttachPort(PortId, NodeCall, portListener);

        PeerDialPlan captured = default;
        svc.OpenInterlink = (_, _, plan, _) =>
        {
            captured = plan;
            return Task.FromResult(session);
        };

        await svc.EnsureInterlinkForTestAsync(Neighbour);

        captured.Extended.Should().BeTrue(
            "a neighbour learned extended-capable must be dialled SABME, overriding the conservative interlink default");

        await portListener.DisposeAsync();
    }

    [Fact]
    public async Task Pre_seeded_non_xid_answerer_makes_the_interlink_skip_the_pre_connect_xid()
    {
        var cache = new PeerCapabilityCache();
        // Learn the neighbour does NOT answer a pre-connect XID (a mod-8 dial that probed it).
        cache.RecordOutcome(PortId, Neighbour.ToString(), dialedExtended: false, observedIsExtended: false,
            dialedPreConnectXid: true, observedSrejEnabled: false);

        var (session, a, b) = await ConnectedSessionAsync();
        await using var _ = a;
        await using var __ = b;

        var (eN, _) = InMemoryRadio.CreatePair();
        var portListener = await StartListenerAsync(eN, NodeCall);
        await using var svc = new NetRomService(InterlinkConfig(), TimeProvider.System,
            NullLogger<NetRomService>.Instance, store: null, capabilityCache: cache);
        svc.AttachPort(PortId, NodeCall, portListener);

        PeerDialPlan captured = default;
        svc.OpenInterlink = (_, _, plan, _) =>
        {
            captured = plan;
            return Task.FromResult(session);
        };

        await svc.EnsureInterlinkForTestAsync(Neighbour);

        captured.Extended.Should().BeFalse("the neighbour is not known-extended, so the interlink stays mod-8");
        captured.PreConnectXid.Should().BeFalse("a learned non-XID-answerer must have its pre-connect XID skipped");

        await portListener.DisposeAsync();
    }

    [Fact]
    public async Task A_returned_dial_records_the_observed_outcome()
    {
        var cache = new PeerCapabilityCache();   // empty → miss → mod-8 + pre-connect XID plan

        var (session, a, b) = await ConnectedSessionAsync();
        await using var _ = a;
        await using var __ = b;
        // Control the observed link so RecordOutcome has deterministic values to learn. The
        // plan dials mod-8 + pre-connect XID (the miss default), so the XID dimension is probed
        // and SrejEnabled is what gets learned; the extended dimension stays unprobed (null).
        session.Context.IsExtended = false;
        session.Context.SrejEnabled = true;

        var (eN, _) = InMemoryRadio.CreatePair();
        var portListener = await StartListenerAsync(eN, NodeCall);
        await using var svc = new NetRomService(InterlinkConfig(), TimeProvider.System,
            NullLogger<NetRomService>.Instance, store: null, capabilityCache: cache);
        svc.AttachPort(PortId, NodeCall, portListener);

        svc.OpenInterlink = (_, _, _, _) => Task.FromResult(session);

        await svc.EnsureInterlinkForTestAsync(Neighbour);

        var rec = cache.All().Single();
        rec.PortId.Should().Be(PortId);
        rec.Peer.Should().Be(Neighbour.ToString());
        rec.SupportsSrejViaXid.Should().BeTrue("the pre-connect-XID dimension was probed and the link came back SREJ-enabled");
        rec.SupportsExtended.Should().BeNull("a mod-8 dial proves nothing about extended capability");

        await portListener.DisposeAsync();
    }

    [Fact]
    public async Task A_dial_that_throws_leaves_the_cache_unchanged()
    {
        var cache = new PeerCapabilityCache();

        var (eN, _) = InMemoryRadio.CreatePair();
        var portListener = await StartListenerAsync(eN, NodeCall);
        await using var svc = new NetRomService(InterlinkConfig(), TimeProvider.System,
            NullLogger<NetRomService>.Instance, store: null, capabilityCache: cache);
        svc.AttachPort(PortId, NodeCall, portListener);

        // The dial throws — no link of either version, so it carries no capability signal.
        svc.OpenInterlink = (_, _, _, _) => throw new IOException("neighbour did not answer");

        Func<Task> dial = async () => await svc.EnsureInterlinkForTestAsync(Neighbour);
        await dial.Should().ThrowAsync<IOException>();

        cache.All().Should().BeEmpty("a dial that throws must NOT record an outcome (the correctness hinge)");

        await portListener.DisposeAsync();
    }
}
