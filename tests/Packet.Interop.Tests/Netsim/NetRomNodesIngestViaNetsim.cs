using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Packet.NetRom.Routing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.NetRom;
using Xunit;
using Xunit.Abstractions;

namespace Packet.Interop.Tests.Netsim;

/// <summary>
/// Read-only NET/ROM interop: prove that pdn, attached to net-sim's afsk1200
/// channel, <b>hears a real reference node's NODES broadcast and builds a
/// routing-table entry from it</b> — through the exact production pipeline (a
/// real <see cref="Ax25Listener"/>'s frame-trace tap → <see cref="NetRomService"/>
/// → <see cref="NetRomRoutingTable"/> → snapshot). No transmit, no engine change.
/// </summary>
/// <remarks>
/// <para>
/// <b>What broadcasts what.</b> The interop stack runs two reference NET/ROM
/// nodes on the channel: XRouter (NODECALL <c>PN0XRT</c>, alias <c>PNXRT</c>, on
/// net-sim node d / 8103) and LinBPQ (<c>PN0TST</c>/<c>PNTST</c>, node c / 8102).
/// Observed behaviour of these two pinned images on the isolated test channel:
/// XRouter broadcasts a NODES frame (PID 0xCF, dest <c>NODES</c>) on its own
/// cadence regardless of table contents; LinBPQ broadcasts NODES only when its
/// routing table is non-empty, and on this deliberately-isolated topology (peers
/// reach only node a, not each other) BPQ has nothing to advertise, so it stays
/// silent. The fixture sets BPQ's <c>NODESINTERVAL=1</c> so that whenever it
/// <em>does</em> have content it broadcasts promptly; this test asserts the
/// reliably-observable XRouter ingest and records BPQ opportunistically.
/// </para>
/// <para>
/// <b>The assertion.</b> Hearing XRouter's NODES makes pdn record <c>PN0XRT</c>
/// as a directly-heard neighbour (alias <c>PNXRT</c>) with the assumed
/// default-port path quality and an assumed direct route — the canonical
/// processing heuristics 3 + 4. That is genuine cross-implementation evidence
/// that pdn parses a real NET/ROM node's on-the-wire broadcast and builds routing
/// state from it. Per <c>docs/plan.md</c> §7.1 the interop matrix is
/// environmentally flaky and non-blocking; this test is generous on timing and
/// fails only if pdn never hears a real NODES broadcast at all.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class NetRomNodesIngestViaNetsim
{
    private const string Host = "127.0.0.1";
    private const int OurKissPort = 8100;                       // net-sim node a — the shared "ours" endpoint
    private static readonly Callsign OurCall = new("PNTEST", 0);
    private static readonly Callsign XrouterCall = new("PN0XRT", 0);
    private static readonly Callsign BpqCall = new("PN0TST", 0);

    // With NODESINTERVAL=1 pinned in XROUTER.CFG, XRouter broadcasts NODES on a
    // steady ~75 s cadence (measured), so this window catches two broadcasts with
    // margin. LinBPQ's NODESINTERVAL is pinned to 1 min too, for whenever its table
    // is non-empty. Generous-but-bounded so a missed first broadcast still passes
    // and a genuinely-deaf node fails rather than hangs.
    private static readonly TimeSpan HearBudget = TimeSpan.FromSeconds(200);

    private readonly ITestOutputHelper output;

    public NetRomNodesIngestViaNetsim(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task Pdn_hears_a_reference_node_NODES_broadcast_and_learns_it_as_a_neighbour()
    {
        using var cts = new CancellationTokenSource(HearBudget + TimeSpan.FromSeconds(30));

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        // The production pipeline: a real listener on the channel, plus the
        // node-level NET/ROM service subscribed to its frame-trace tap. The
        // listener never transmits here (we don't connect to anyone) — it is a
        // pure promiscuous receiver, exactly the read-only slice.
        await using var listener = new Ax25Listener(kiss, new Ax25ListenerOptions { MyCall = OurCall });
        using var netRom = new NetRomService(new NetRomConfig { Enabled = true });
        netRom.AttachPort("vhf", OurCall, listener);
        await listener.StartAsync(cts.Token);

        // Wait until pdn has heard at least one real NODES broadcast and built a
        // neighbour entry from it.
        await WaitUntil(() => netRom.Snapshot().NeighbourCount > 0, HearBudget, cts.Token);

        var snap = netRom.Snapshot();
        output.WriteLine($"Learned {snap.NeighbourCount} neighbour(s), {snap.DestinationCount} destination(s):");
        foreach (var n in snap.Neighbours)
        {
            output.WriteLine($"  neighbour {n.Alias}:{n.Neighbour} port={n.PortId} qual={n.PathQuality}");
        }
        foreach (var d in snap.Destinations)
        {
            var routes = string.Join(", ", d.Routes.Select(r => $"via {r.Neighbour} q{r.Quality} obs{r.Obsolescence}"));
            output.WriteLine($"  dest {d.Alias}:{d.Destination} [{routes}]");
        }

        snap.NeighbourCount.Should().BeGreaterThan(0,
            "pdn must hear at least one reference node's NODES broadcast on the channel and record it as a neighbour");

        // XRouter is the reliably-broadcasting reference node — assert pdn learned
        // it specifically (alias PNXRT), with an assumed direct route.
        var xr = snap.Neighbours.SingleOrDefault(n => n.Neighbour == XrouterCall);
        xr.Should().NotBeNull("pdn should hear XRouter's NODES broadcast (PN0XRT) and learn it as a neighbour");
        xr!.Alias.Should().Be("PNXRT", "the neighbour entry carries XRouter's advertised alias");
        xr.PortId.Should().Be("vhf");
        snap.Destinations.Should().Contain(d => d.Destination == XrouterCall,
            "an assumed direct route to the heard originator is built (canonical heuristic 4)");

        // LinBPQ broadcasts only with a non-empty table; record (don't require) it,
        // so a future write-slice / richer-topology run surfaces it without this
        // read-only test going red on BPQ's environment-dependent silence.
        if (snap.Neighbours.Any(n => n.Neighbour == BpqCall))
        {
            output.WriteLine("Also heard LinBPQ (PN0TST) NODES.");
        }
        else
        {
            output.WriteLine("LinBPQ (PN0TST) did not broadcast NODES in-window (empty table on the isolated topology) — expected; not required.");
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan budget, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return;
            try { await Task.Delay(250, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }
}
