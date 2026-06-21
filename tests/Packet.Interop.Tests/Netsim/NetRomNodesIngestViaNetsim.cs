using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
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
/// <b>Two reference peers, both proven.</b> The interop stack runs two reference
/// NET/ROM nodes on the channel: XRouter (NODECALL <c>PN0XRT</c>, alias
/// <c>PNXRT</c>, on net-sim node d / 8103) and LinBPQ (<c>PN0TST</c>/<c>PNTST</c>,
/// node c / 8102). This test asserts pdn ingests <em>both</em>:
/// </para>
/// <list type="bullet">
/// <item><b>XRouter — ambient.</b> XRouter broadcasts a NODES frame (PID 0xCF,
/// dest <c>NODES</c>) on its own ~75 s cadence regardless of table contents, so
/// pdn hears it passively.</item>
/// <item><b>LinBPQ — provoked.</b> LinBPQ broadcasts NODES out a port only when
/// that port has a non-zero QUALITY <em>and</em> on its NODESINTERVAL cadence; on
/// this isolated topology its table is otherwise empty. We force an
/// <em>immediate</em> broadcast with the sysop <c>SENDNODES</c> command, after
/// authenticating with BPQ's real positional-challenge <c>PASSWORD</c> handshake
/// (see <see cref="BpqSysop"/>). This closes the BPQ-NODES gap that the #303
/// read-only slice left "recorded, not required".</item>
/// </list>
/// <para>
/// <b>The handshake (source-verified against LinBPQ 6.0.25.23, not guessed).</b>
/// BPQ's <c>PASSWORD</c> is a positional challenge, not a plain password. The bare
/// command <c>PASSWORD</c> makes BPQ reply with five 1-based character positions
/// (e.g. <c>5 3 6 3 3</c>) chosen at random — with repeats — from the configured
/// <c>PASSWORD=</c> text (uppercased; the fixture sets <c>WONTLISTEN</c>). The
/// caller answers <c>PASSWORD &lt;chars&gt;</c> with the characters at those
/// positions concatenated (here <c>LNINN</c>). BPQ sums the ASCII bytes of the
/// reply and compares to the sum it precomputed; a match flips the session to
/// authorised and unlocks the sysop command set (which includes <c>SENDNODES</c>).
/// We authenticate as a deliberately <em>non-sysop</em> telnet user
/// (<c>USER=netop</c>) so the genuine challenge runs — the <c>admin</c>/SYSOP user
/// is a Secure_Session and would shortcut <c>PASSWORD</c> to an instant Ok,
/// bypassing the very mechanism we want to prove. <c>SENDNODES</c> then emits an
/// immediate <c>PN0TST &gt; NODES</c> UI frame on the netsim port.
/// </para>
/// <para>
/// <b>The assertion.</b> Hearing each node's NODES makes pdn record it as a
/// directly-heard neighbour (with the node's advertised alias) carrying the
/// assumed default-port path quality and an assumed direct route — the canonical
/// processing heuristics 3 + 4. That is genuine cross-implementation evidence that
/// pdn parses a real NET/ROM node's on-the-wire broadcast and builds routing state
/// from it, for <em>both</em> reference peers.
/// </para>
/// <para>
/// <b>Determinism.</b> <c>SENDNODES</c> is immediate, so we wait for the specific
/// <c>PN0TST</c> neighbour with a bounded budget, re-issuing <c>SENDNODES</c> on a
/// short cadence inside the budget so a single frame lost on the simulated channel
/// doesn't fail the run (BPQ's pinned <c>NODESINTERVAL=1</c> is a further backstop).
/// Per <c>docs/plan.md</c> §7.1 the interop matrix is environmentally flaky and
/// non-blocking; if a local run flakes under box contention (the #47 lesson),
/// re-run in isolation rather than chasing a load artifact.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Trait("Group", "NetRom")]   // isolated from the timing-sensitive AX.25 tests — interop.yml runs the NET/ROM group against a freshly-recreated stack (see docs/plan.md §7.2)
[Collection(NetsimCollection.Name)]
public class NetRomNodesIngestViaNetsim
{
    private const string Host = "127.0.0.1";
    private const int OurKissPort = 8100;                       // net-sim node a — the shared "ours" endpoint
    private const int BpqTelnetPort = 8010;                     // LinBPQ node prompt
    private static readonly Callsign OurCall = new("PNTEST", 0);
    private static readonly Callsign XrouterCall = new("PN0XRT", 0);
    private static readonly Callsign BpqCall = new("PN0TST", 0);

    // The configured sysop password text (docker/linbpq/bpq32.cfg PASSWORD=).
    // BPQ uppercases it, so the challenge solves against this exact string.
    private const string BpqPasswordText = "WONTLISTEN";

    // XRouter broadcasts NODES on a steady ~75 s cadence (NODESINTERVAL=1 pinned
    // in XROUTER.CFG), so this window catches one ambient broadcast with margin.
    private static readonly TimeSpan XrouterHearBudget = TimeSpan.FromSeconds(200);

    // LinBPQ is provoked via SENDNODES (immediate), so its budget is much tighter
    // than XRouter's ambient cadence — generous-but-bounded so a single dropped
    // frame on the sim channel still passes (we re-trigger inside the budget) and
    // a genuinely-deaf node fails rather than hangs.
    private static readonly TimeSpan BpqHearBudget = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan BpqResendEvery = TimeSpan.FromSeconds(12);

    private readonly ITestOutputHelper output;

    public NetRomNodesIngestViaNetsim(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task Pdn_hears_both_reference_nodes_NODES_broadcasts_and_learns_them_as_neighbours()
    {
        using var cts = new CancellationTokenSource(XrouterHearBudget + BpqHearBudget + TimeSpan.FromSeconds(60));

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        // The production pipeline: a real listener on the channel, plus the
        // node-level NET/ROM service subscribed to its frame-trace tap. The
        // listener never transmits here (we don't connect to anyone) — it is a
        // pure promiscuous receiver, exactly the read-only slice.
        await using var listener = new Ax25Listener(new Packet.Kiss.KissModemTransport(kiss), new Ax25ListenerOptions { MyCall = OurCall });
        using var netRom = new NetRomService(new NetRomConfig { Enabled = true });
        netRom.AttachPort("vhf", OurCall, listener);
        await listener.StartAsync(cts.Token);

        // ── XRouter: ambient broadcast ───────────────────────────────────
        // Wait until pdn has heard at least one real NODES broadcast and built a
        // neighbour entry. XRouter's steady cadence makes this the reliable
        // first observation.
        await WaitUntil(() => netRom.Snapshot().Neighbours.Any(n => n.Neighbour == XrouterCall),
            XrouterHearBudget, cts.Token);

        DumpSnapshot(netRom.Snapshot(), "after XRouter window");

        var xr = netRom.Snapshot().Neighbours.SingleOrDefault(n => n.Neighbour == XrouterCall);
        xr.Should().NotBeNull("pdn should hear XRouter's NODES broadcast (PN0XRT) and learn it as a neighbour");
        xr!.Alias.Should().Be("PNXRT", "the neighbour entry carries XRouter's advertised alias");
        xr.PortId.Should().Be("vhf");
        netRom.Snapshot().Destinations.Should().Contain(d => d.Destination == XrouterCall,
            "an assumed direct route to the heard originator is built (canonical heuristic 4)");

        // ── LinBPQ: provoked via PASSWORD → SENDNODES ─────────────────────
        // Force an immediate NODES broadcast from the real LinBPQ and assert pdn
        // ingests BPQ's frame too. We re-trigger SENDNODES on a short cadence
        // inside the bounded budget so a single dropped frame on the simulated
        // channel doesn't fail the run.
        var heardBpq = await ProvokeAndHearBpqAsync(netRom, cts.Token);

        DumpSnapshot(netRom.Snapshot(), "after BPQ provoke");

        heardBpq.Should().BeTrue(
            "pdn must hear LinBPQ's NODES broadcast (PN0TST) after the PASSWORD->SENDNODES sysop handshake forces one onto the netsim channel");

        var bpq = netRom.Snapshot().Neighbours.SingleOrDefault(n => n.Neighbour == BpqCall);
        bpq.Should().NotBeNull("pdn should hear LinBPQ's NODES broadcast (PN0TST) and learn it as a neighbour");
        bpq!.Alias.Should().Be("PNTST", "the neighbour entry carries LinBPQ's advertised alias");
        bpq.PortId.Should().Be("vhf");
        netRom.Snapshot().Destinations.Should().Contain(d => d.Destination == BpqCall,
            "an assumed direct route to LinBPQ is built (canonical heuristic 4)");
    }

    /// <summary>
    /// Drive LinBPQ's sysop <c>SENDNODES</c> via the real positional-challenge
    /// <c>PASSWORD</c> handshake, then wait (bounded) for pdn to ingest the
    /// resulting <c>PN0TST</c> NODES broadcast. Re-triggers <c>SENDNODES</c> on a
    /// short cadence inside the budget for resilience against a dropped frame.
    /// </summary>
    private async Task<bool> ProvokeAndHearBpqAsync(NetRomService netRom, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(BpqHearBudget);

        var nextResend = DateTimeOffset.MinValue;   // trigger immediately on entry
        while (!cts.IsCancellationRequested)
        {
            if (netRom.Snapshot().Neighbours.Any(n => n.Neighbour == BpqCall))
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= nextResend)
            {
                try
                {
                    await BpqSysop.SendNodesAsync(Host, BpqTelnetPort, "netop", "netop", BpqPasswordText, output, cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A transient telnet hiccup must not sink the test outright —
                    // log and let the next resend tick retry within budget.
                    output.WriteLine($"SENDNODES trigger failed (will retry): {ex.GetType().Name}: {ex.Message}");
                }
                nextResend = DateTimeOffset.UtcNow + BpqResendEvery;
            }

            try { await Task.Delay(250, cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        return netRom.Snapshot().Neighbours.Any(n => n.Neighbour == BpqCall);
    }

    private void DumpSnapshot(NetRomRoutingSnapshot snap, string when)
    {
        output.WriteLine($"[{when}] {snap.NeighbourCount} neighbour(s), {snap.DestinationCount} destination(s):");
        foreach (var n in snap.Neighbours)
        {
            output.WriteLine($"  neighbour {n.Alias}:{n.Neighbour} port={n.PortId} qual={n.PathQuality}");
        }
        foreach (var d in snap.Destinations)
        {
            var routes = string.Join(", ", d.Routes.Select(r => $"via {r.Neighbour} q{r.Quality} obs{r.Obsolescence}"));
            output.WriteLine($"  dest {d.Alias}:{d.Destination} [{routes}]");
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

    /// <summary>
    /// Minimal IAC-aware telnet driver for LinBPQ's node prompt that performs the
    /// real positional-challenge sysop auth and issues <c>SENDNODES</c>.
    /// </summary>
    /// <remarks>
    /// The handshake (source-verified, see the class remarks): log in as a
    /// non-sysop user → <c>PASSWORD</c> (bare) → BPQ replies with five 1-based
    /// positions → answer <c>PASSWORD &lt;chars-at-those-positions&gt;</c> → BPQ
    /// replies <c>Ok</c> (authorised) → <c>SENDNODES</c> → <c>Ok</c> + an immediate
    /// NODES broadcast on every non-zero-QUALITY port (the netsim port carries
    /// QUALITY=192 in the fixture for exactly this).
    /// </remarks>
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

            // Log in as the non-sysop user (plain session → real challenge).
            await ReadUntilAsync(stream, "user", TimeSpan.FromSeconds(8), ct);
            await SendLineAsync(stream, user, ct);
            await ReadUntilAsync(stream, "password", TimeSpan.FromSeconds(8), ct);
            await SendLineAsync(stream, pass, ct);
            await ReadUntilAsync(stream, "Telnet Server", TimeSpan.FromSeconds(8), ct);

            // Bare PASSWORD → positional challenge.
            await SendLineAsync(stream, "PASSWORD", ct);
            var challenge = await ReadLineAfterPromptAsync(stream, TimeSpan.FromSeconds(6), ct);
            var positions = ParsePositions(challenge);
            var answer = SolveChallenge(positions, passwordText);
            output.WriteLine($"BPQ PASSWORD challenge {string.Join(' ', positions)} -> answer {answer}");

            // Answer goes back as an ARGUMENT to a second PASSWORD command — a
            // bare token would be parsed as an unknown command.
            await SendLineAsync(stream, "PASSWORD " + answer, ct);
            var authResp = await ReadLineAfterPromptAsync(stream, TimeSpan.FromSeconds(6), ct);
            if (!authResp.Contains("Ok", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"BPQ rejected the PASSWORD challenge answer: {authResp.Trim()}");
            }

            // Now authorised — force an immediate NODES broadcast.
            await SendLineAsync(stream, "SENDNODES", ct);
            var sendResp = await ReadLineAfterPromptAsync(stream, TimeSpan.FromSeconds(6), ct);
            if (!sendResp.Contains("Ok", StringComparison.Ordinal) ||
                sendResp.Contains("SYSOP", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"BPQ did not accept SENDNODES: {sendResp.Trim()}");
            }

            // Hold the socket open a beat so BPQ doesn't abandon the command, then
            // the using-blocks tear it down. The NODES broadcast is independent of
            // this telnet session.
            await Task.Delay(300, ct);
        }

        private static int[] ParsePositions(string challenge)
        {
            // The challenge line is "<prompt>} 5 3 6 3 3". The prompt prefix can
            // contain a digit (PN0TST), so take the LAST five integers.
            var nums = Regex.Matches(challenge, @"\d+").Select(m => int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
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
                // BPQ prints 1-based positions; the char summed is PWTEXT[p-1].
                // Positions are rand() % PWLen so always within range, but clamp
                // defensively rather than throw on a surprise.
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

        // Read until a substring is seen (IAC-stripped), returning the accumulated text.
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

        // Read a short reply that follows a command (BPQ echoes "<prompt>} <text>\r\n").
        // We read for a brief settle window and return whatever text arrived.
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
                // BPQ terminates a command reply with \r\n; stop once we have one.
                if (sb.ToString().Contains('\n', StringComparison.Ordinal)) break;
            }
            return sb.ToString();
        }

        // Strip IAC negotiation; reply WONT/DONT so BPQ doesn't wait on us.
        private static void AppendStripIac(NetworkStream stream, StringBuilder sb, byte[] buf, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (buf[i] != IAC)
                {
                    sb.Append((char)buf[i]);
                    continue;
                }
                if (i + 2 >= n) break;   // partial IAC at the tail — drop
                byte verb = buf[i + 1];
                byte opt = buf[i + 2];
                i += 2;
                byte reply = verb switch
                {
                    DO => WONT,
                    WILL => DONT,
                    _ => (byte)0,
                };
                if (reply != 0)
                {
                    _ = stream.WriteAsync(new byte[] { IAC, reply, opt }).AsTask();
                }
            }
        }
    }
}
