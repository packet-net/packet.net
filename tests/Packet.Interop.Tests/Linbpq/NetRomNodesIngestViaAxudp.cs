using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Transports;
using Xunit;
using Xunit.Abstractions;

namespace Packet.Interop.Tests.Linbpq;

/// <summary>
/// <b>Tier 2 (frame-perfect, modem-less) NET/ROM NODES interop vs real LinBPQ
/// over AXUDP.</b> The AXUDP analog of <see cref="NetRomNodesIngestViaNetsim"/>:
/// it asserts the same protocol behaviour — pdn ingests LinBPQ's on-the-wire
/// NODES broadcast and learns it as a neighbour — but over a BPQAXIP/UDP tunnel
/// (<see cref="AxudpKissModem"/>) instead of net-sim's software-AFSK channel, so
/// it is deterministic and load-insensitive (no audio decode to glitch under CPU
/// contention). See <c>docs/plan.md</c> §7 for the three-tier layering.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transport.</b> pdn runs a real <see cref="Ax25Listener"/> over an
/// <see cref="AxudpKissModem"/> bound to a fixed host UDP port, pointed at BPQ's
/// BPQAXIP/UDP listener (127.0.0.1:8093) — the exact <c>IKissModem</c> seam a
/// deployed pdn AXUDP port uses. A <see cref="NetRomService"/> taps the listener's
/// <c>FrameTraced</c> stream (read-only; no engine change), parses the NODES UI
/// frame, and builds the routing table.
/// </para>
/// <para>
/// <b>How BPQ broadcasts NODES over a BPQAXIP/UDP port (the headline finding —
/// source-verified in <c>bpqaxip.c</c>, then proven on the wire).</b> Unlike a
/// broadcast RF/KISS port, BPQAXIP is point-to-point: its TX path
/// (<c>bpqaxip.c</c> <c>AXIP_TX</c> → <c>SendFrame</c>) sends a frame only to ARP/MAP
/// entries whose callsign matches the frame's <em>destination</em> — and a NET/ROM
/// NODES UI frame is addressed to the pseudo-destination <c>NODES</c>, which matches
/// no per-station MAP. So a stock AXIP port silently drops its own NODES broadcast
/// (we verified BPQ sends pdn <em>zero</em> NODES datagrams with a stock MAP). BPQAXIP
/// gates broadcast delivery behind two explicit config items: a
/// <c>BROADCAST NODES</c> line registering <c>NODES</c> as a broadcast address on the
/// port, and the per-MAP <c>B</c> flag marking an entry as a broadcast recipient.
/// The fixture therefore carries <c>BROADCAST NODES</c> + <c>MAP PNL4AX-1 … B</c>
/// on the AXIP port (see <c>docker/linbpq/bpq32.cfg</c>); with those, BPQ emits its
/// <c>PN0TST &gt; NODES</c> UI frame to pdn over UDP exactly as it does over RF.
/// (This is the AXUDP counterpart of the <c>QUALITY=192</c> the net-sim port needs —
/// a per-port NODES-advertising enablement, not a protocol change.)
/// </para>
/// <para>
/// <b>Provoke + AUTOADD ordering.</b> pdn first broadcasts its own NODES so BPQ's
/// <c>AUTOADDQUIET</c> learns pdn's reply route on the AXIP port (the static MAP
/// pins the same call→port). We then force an immediate NODES with the
/// source-verified positional-challenge <c>PASSWORD</c> → <c>SENDNODES</c> sysop
/// handshake (the <see cref="NetRomNodesIngestViaNetsim"/> driver, reused here),
/// re-triggering on a short cadence inside a bounded budget. Because this is a
/// reliable UDP tunnel (no channel loss, no half-duplex contention), the budget is
/// far tighter than the net-sim version's.
/// </para>
/// <para>
/// Serialised into <see cref="NetsimCollection"/>: it talks to the same shared BPQ
/// daemon as the other BPQ interop tests and binds a fixed host UDP port, so it
/// must not run in parallel with them. Tagged <c>Group=NetRom</c> so it runs in
/// the clean-stack-fenced NET/ROM phase of <c>interop.yml</c> (a fresh BPQ → no
/// learned-state carryover), isolated from the timing-sensitive AX.25 tests.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Trait("Group", "NetRom")]
[Collection(NetsimCollection.Name)]
public class NetRomNodesIngestViaAxudp
{
    private const string Host = "127.0.0.1";
    private const int BpqAxudpPort = 8093;    // BPQAXIP/UDP listener (published)
    private const int BpqHttpPort = 8008;     // liveness probe
    private const int BpqTelnetPort = 8010;   // node prompt (SENDNODES driver)

    // pdn binds the static-MAP target port + uses the static-MAP target callsign so
    // BPQ has a stable reply route (the AUTOADD cache pins call→port across re-runs).
    private const int PdnLocalPort = 8194;
    private static readonly Callsign OurCall = new("PNL4AX", 1);
    private const string OurAlias = "PNL4A";
    private static readonly Callsign BpqCall = new("PN0TST", 0);

    private const string BpqPasswordText = "WONTLISTEN";   // docker/linbpq/bpq32.cfg PASSWORD=

    // A reliable UDP tunnel — no channel loss/half-duplex — so the budget is tight.
    private static readonly TimeSpan HearBpqBudget = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ResendEvery = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BroadcastEvery = TimeSpan.FromSeconds(2);

    private readonly ITestOutputHelper output;

    public NetRomNodesIngestViaAxudp(ITestOutputHelper output) => this.output = output;

    [SkippableFact]
    public async Task Pdn_hears_real_linbpq_NODES_over_axudp_and_learns_it_as_a_neighbour()
    {
        Skip.IfNot(await IsTcpPortReachable(Host, BpqHttpPort),
            $"LinBPQ not reachable (HTTP {Host}:{BpqHttpPort}). Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait'.");

        using var cts = new CancellationTokenSource(HearBpqBudget + TimeSpan.FromSeconds(45));

        await using var modem = new AxudpKissModem(new IPEndPoint(IPAddress.Loopback, BpqAxudpPort), PdnLocalPort);
        // A real listener over the AXUDP tunnel + the read-only NET/ROM service tap —
        // the exact production pipeline, point-to-point over UDP rather than the RF sim.
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = OurCall });
        await using var netRom = new NetRomService(new NetRomConfig
        {
            Enabled = true,
            // Broadcast so BPQ's AUTOADDQUIET learns pdn's reply route on the AXIP port
            // (the static MAP pins it; broadcasting keeps the AUTOADD entry warm).
            Broadcast = true,
        }, nodeAlias: OurAlias);
        netRom.AttachPort("axudp", OurCall, listener);
        await listener.StartAsync(cts.Token);

        // Keep originating pdn's NODES so BPQ keeps a fresh route + AUTOADD entry to us.
        using var broadcastCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var broadcaster = RunBroadcastLoop(netRom, broadcastCts.Token);

        try
        {
            var heardBpq = await ProvokeAndHearBpqAsync(netRom, cts.Token);

            DumpSnapshot(netRom.Snapshot(), "after BPQ provoke (AXUDP)");

            heardBpq.Should().BeTrue(
                "pdn must hear LinBPQ's NODES broadcast (PN0TST) over the AXUDP tunnel after the " +
                "PASSWORD->SENDNODES sysop handshake forces one — BPQ delivers NODES to the AXIP MAP " +
                "entry because the fixture marks it a broadcast recipient (BROADCAST NODES + MAP … B)");

            var bpq = netRom.Snapshot().Neighbours.SingleOrDefault(n => n.Neighbour == BpqCall);
            bpq.Should().NotBeNull("pdn learns LinBPQ (PN0TST) as a directly-heard neighbour from its NODES");
            bpq!.Alias.Should().Be("PNTST", "the neighbour entry carries LinBPQ's advertised alias");
            bpq.PortId.Should().Be("axudp");
            netRom.Snapshot().Destinations.Should().Contain(d => d.Destination == BpqCall,
                "an assumed direct route to LinBPQ is built (canonical heuristic 4) — same as the net-sim tier, minus the audio");
        }
        finally
        {
            await broadcastCts.CancelAsync();
            try { await broadcaster; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    // ── pdn NODES origination loop (keeps BPQ's AUTOADD/route to us fresh) ─────
    private async Task RunBroadcastLoop(NetRomService netRom, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { netRom.BroadcastNodes(); }
            catch (Exception ex) { output.WriteLine($"broadcast failed (continuing): {ex.Message}"); }
            try { await Task.Delay(BroadcastEvery, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── provoke BPQ SENDNODES (bounded, re-trigger) + wait until we've heard it ─
    private async Task<bool> ProvokeAndHearBpqAsync(NetRomService netRom, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(HearBpqBudget);
        var nextResend = DateTimeOffset.MinValue;   // trigger immediately on entry
        while (!cts.IsCancellationRequested)
        {
            if (netRom.Snapshot().Neighbours.Any(n => n.Neighbour == BpqCall))
            {
                return true;
            }
            if (DateTimeOffset.UtcNow >= nextResend)
            {
                try { await BpqSysop.SendNodesAsync(Host, BpqTelnetPort, "netop", "netop", BpqPasswordText, output, cts.Token); }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                catch (Exception ex) { output.WriteLine($"SENDNODES trigger failed (will retry): {ex.GetType().Name}: {ex.Message}"); }
                nextResend = DateTimeOffset.UtcNow + ResendEvery;
            }
            try { await Task.Delay(250, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return netRom.Snapshot().Neighbours.Any(n => n.Neighbour == BpqCall);
    }

    private void DumpSnapshot(Packet.NetRom.Routing.NetRomRoutingSnapshot snap, string when)
    {
        output.WriteLine($"[{when}] {snap.NeighbourCount} neighbour(s), {snap.DestinationCount} destination(s):");
        foreach (var n in snap.Neighbours)
        {
            output.WriteLine($"  neighbour {n.Alias}:{n.Neighbour} port={n.PortId} qual={n.PathQuality}");
        }
    }

    private static async Task<bool> IsTcpPortReachable(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    /// <summary>
    /// IAC-aware telnet driver for LinBPQ's node prompt: the source-verified
    /// positional-challenge sysop auth (log in as the non-sysop <c>netop</c> user →
    /// bare <c>PASSWORD</c> → five 1-based positions → answer the chars there →
    /// <c>Ok</c>) then <c>SENDNODES</c> to force an immediate broadcast. Mirrors the
    /// <c>BpqSysop</c> driver in <see cref="NetRomNodesIngestViaNetsim"/>.
    /// </summary>
    private static class BpqSysop
    {
        private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251;

        public static async Task SendNodesAsync(
            string host, int port, string user, string pass, string passwordText,
            ITestOutputHelper output, CancellationToken ct)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct);
            using var stream = tcp.GetStream();

            await ReadUntilAsync(stream, "user", TimeSpan.FromSeconds(8), ct);
            await SendLineAsync(stream, user, ct);
            await ReadUntilAsync(stream, "password", TimeSpan.FromSeconds(8), ct);
            await SendLineAsync(stream, pass, ct);
            await ReadUntilAsync(stream, "Telnet Server", TimeSpan.FromSeconds(8), ct);

            await SendLineAsync(stream, "PASSWORD", ct);
            var challenge = await ReadLineAfterPromptAsync(stream, TimeSpan.FromSeconds(6), ct);
            var positions = ParsePositions(challenge);
            var answer = SolveChallenge(positions, passwordText);
            output.WriteLine($"BPQ PASSWORD challenge {string.Join(' ', positions)} -> answer {answer}");

            await SendLineAsync(stream, "PASSWORD " + answer, ct);
            var authResp = await ReadLineAfterPromptAsync(stream, TimeSpan.FromSeconds(6), ct);
            if (!authResp.Contains("Ok", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"BPQ rejected the PASSWORD challenge answer: {authResp.Trim()}");
            }

            await SendLineAsync(stream, "SENDNODES", ct);
            var sendResp = await ReadLineAfterPromptAsync(stream, TimeSpan.FromSeconds(6), ct);
            if (!sendResp.Contains("Ok", StringComparison.Ordinal) || sendResp.Contains("SYSOP", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"BPQ did not accept SENDNODES: {sendResp.Trim()}");
            }
            await Task.Delay(300, ct);
        }

        private static int[] ParsePositions(string challenge)
        {
            var nums = Regex.Matches(challenge, @"\d+").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            if (nums.Length < 5)
            {
                throw new InvalidOperationException($"Could not parse 5 challenge positions from: {challenge.Trim()}");
            }
            return nums[^5..];
        }

        private static string SolveChallenge(int[] positions, string passwordText)
        {
            var sb = new StringBuilder(positions.Length);
            foreach (var p in positions)
            {
                int idx = Math.Clamp(p - 1, 0, passwordText.Length - 1);
                sb.Append(passwordText[idx]);
            }
            return sb.ToString();
        }

        private static async Task SendLineAsync(NetworkStream stream, string line, CancellationToken ct)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r");
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        private static async Task<string> ReadUntilAsync(NetworkStream stream, string needle, TimeSpan budget, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);
            while (!cts.IsCancellationRequested)
            {
                int n;
                try { n = await stream.ReadAsync(buf, cts.Token); }
                catch (OperationCanceledException) { break; }
                if (n == 0) break;
                AppendStripIac(stream, sb, buf, n);
                if (needle.Length > 0 && sb.ToString().Contains(needle, StringComparison.Ordinal)) break;
            }
            return sb.ToString();
        }

        private static async Task<string> ReadLineAfterPromptAsync(NetworkStream stream, TimeSpan budget, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);
            while (!cts.IsCancellationRequested)
            {
                int n;
                try { n = await stream.ReadAsync(buf, cts.Token); }
                catch (OperationCanceledException) { break; }
                if (n == 0) break;
                AppendStripIac(stream, sb, buf, n);
                if (sb.ToString().Contains('\n', StringComparison.Ordinal)) break;
            }
            return sb.ToString();
        }

        private static void AppendStripIac(NetworkStream stream, StringBuilder sb, byte[] buf, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (buf[i] != IAC)
                {
                    sb.Append((char)buf[i]);
                    continue;
                }
                if (i + 2 >= n) break;
                byte verb = buf[i + 1];
                byte opt = buf[i + 2];
                i += 2;
                byte reply = verb switch { DO => WONT, WILL => DONT, _ => (byte)0 };
                if (reply != 0)
                {
                    _ = stream.WriteAsync(new byte[] { IAC, reply, opt }).AsTask();
                }
            }
        }
    }
}
