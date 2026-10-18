using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.NetRom.Routing;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Transports;
using Xunit;
using Xunit.Abstractions;

namespace Packet.Interop.Tests.Linbpq;

/// <summary>
/// <b>Multipoint AXUDP against three real LinBPQ AXIP peers at once</b> - the shape
/// GB7RDG uses after its BPQ-to-pdn cutover: one <c>axudp-multipoint</c> port per UDP
/// port, each with one LinBPQ partner, NET/ROM broadcasting and transit routing on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Topology.</b> pdn (<c>PNMPX</c>, alias <c>MPXNOD</c>) runs three
/// <see cref="AxudpMultipointFrameTransport"/>s bound to host UDP 8197 / 8198 / 8199,
/// each with ONE broadcast peer: the containers <c>pn-linbpq-axip-a/b/c</c>
/// (<c>PN1MPA</c>/<c>PN1MPB</c>/<c>PN1MPC</c>) at 172.30.0.20-22, listening on the same
/// port number as their pdn port and MAPping <c>PNMPX</c> back there with <c>B</c>, plus
/// <c>BROADCAST NODES</c> / <c>BROADCAST ID</c> (docker/linbpq/axip-peer-*.cfg). That is
/// GB7RDG's real arrangement: partner GB7BDH on 10094, GB7NDH on 10095, SA6DAZ on 10093.
/// pdn sends straight to each container's IP (not a published port), so the UDP source
/// port each side uses is the real one and BPQ's MAP matching sees pdn's real address.
/// </para>
/// <para>
/// <b>Why one port per UDP port.</b> A single pdn socket can send to partners on
/// different remote ports, but BPQ always sends to the port in its own MAP line, so a
/// partner that MAPs GB7RDG at 10094 never reaches a pdn listening only on 10093. The
/// 2026-10-17 lab (gb7rdg-migration survey/axip-validation.md) showed that shape one-way:
/// partners heard pdn, pdn never heard them. This test pins the working shape.
/// </para>
/// <para>
/// <b>What it asserts.</b> (1) pdn hears every partner's NODES on the right port;
/// (2) every partner learns pdn as a route; (3) every partner learns the OTHER two
/// partners through pdn (pdn's transit NODES reach all broadcast peers and BPQ accepts
/// them); (4) an L4 circuit pdn -&gt; each partner carries a command and its reply; (5) a
/// partner's user connects to pdn's alias (BPQ -&gt; pdn L4) and data round-trips; (6) a
/// partner's user reaches another partner THROUGH pdn (pdn as an L3 transit node).
/// </para>
/// <para>
/// Serialised into <see cref="NetsimCollection"/> and tagged <c>Group=NetRom</c> so it
/// runs in the clean-stack NET/ROM phase of <c>interop.yml</c>. Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Trait("Group", "NetRom")]
[Collection(NetsimCollection.Name)]
public class NetRomMultipointAxudpThreePeers
{
    private const string Host = "127.0.0.1";
    private static readonly Callsign OurCall = new("PNMPX", 0);
    private const string OurAlias = "MPXNOD";

    private sealed record Peer(string PortId, Callsign Call, string Alias, IPEndPoint Endpoint, int TelnetPort);

    private static readonly Peer[] Peers =
    [
        new("axip-8197", new Callsign("PN1MPA", 0), "MPA", new IPEndPoint(IPAddress.Parse("172.30.0.20"), 8197), 8311),
        new("axip-8198", new Callsign("PN1MPB", 0), "MPB", new IPEndPoint(IPAddress.Parse("172.30.0.21"), 8198), 8312),
        new("axip-8199", new Callsign("PN1MPC", 0), "MPC", new IPEndPoint(IPAddress.Parse("172.30.0.22"), 8199), 8313),
    ];

    private static readonly TimeSpan BroadcastEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NodesBudget = TimeSpan.FromSeconds(150);

    private readonly ITestOutputHelper output;

    public NetRomMultipointAxudpThreePeers(ITestOutputHelper output) => this.output = output;

    [SkippableFact]
    public async Task Three_axudp_multipoint_ports_interoperate_with_three_linbpq_peers_at_once()
    {
        foreach (var p in Peers)
        {
            Skip.IfNot(await IsTcpPortReachable(Host, p.TelnetPort),
                $"LinBPQ peer {p.Call} not reachable (telnet {Host}:{p.TelnetPort}). Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait'.");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));

        var modems = new List<AxudpMultipointFrameTransport>();
        var listeners = new List<Ax25Listener>();
        try
        {
            await using var netRom = new NetRomService(new NetRomConfig
            {
                Enabled = true,
                Broadcast = true,
                Routing = NetRomRouting.Transit,
                TransportTimeoutSeconds = 4,
                TransportRetries = 5,
            }, nodeAlias: OurAlias);

            var inboundEchoed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            netRom.RunInboundConsole = async (conn, ct) =>
            {
                await conn.WriteAsync(Encoding.ASCII.GetBytes("pdn-mpx\r"), ct);
                while (!ct.IsCancellationRequested)
                {
                    var data = await conn.ReadAsync(ct);
                    if (data.IsEmpty)
                    {
                        break;
                    }
                    var text = Encoding.ASCII.GetString(data.Span);
                    output.WriteLine($"inbound circuit RX: {text.Replace("\r", "\\r")}");
                    await conn.WriteAsync(Encoding.ASCII.GetBytes("ack:" + text), ct);
                    inboundEchoed.TrySetResult(text);
                }
            };

            foreach (var p in Peers)
            {
                var modem = new AxudpMultipointFrameTransport(
                    [new AxudpMultipointPeerEndpoint(p.Call, p.Endpoint, Broadcast: true)], p.Endpoint.Port);
                modems.Add(modem);
                var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = OurCall });
                listeners.Add(listener);
                // nodesBroadcast: per-port NODES are off by default (#929); GB7RDG sets it on each AXIP port.
                netRom.AttachPort(p.PortId, OurCall, listener, neighbourQuality: 50, nodesBroadcast: true);
                await listener.StartAsync(cts.Token);
            }

            using var broadcastCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var broadcaster = RunBroadcastLoop(netRom, broadcastCts.Token);
            try
            {
                // (1) pdn hears every partner, each on its own port. The partners broadcast
                // NODES every minute (NODESINTERVAL=1); SENDNODES would need the PASSWORD
                // challenge, and waiting one interval is simpler.
                var heardAll = await PollAsync(ct => Task.FromResult(Peers.All(p =>
                    netRom.Snapshot().Neighbours.Any(n => n.Neighbour == p.Call && n.PortId == p.PortId))),
                    NodesBudget, TimeSpan.FromSeconds(1), cts.Token);
                DumpSnapshot(netRom.Snapshot());
                heardAll.Should().BeTrue("pdn must hear each LinBPQ partner's NODES on that partner's own axudp-multipoint port");

                // (2) + (3) every partner learns pdn, and the other two partners THROUGH pdn.
                foreach (var p in Peers)
                {
                    var others = Peers.Where(o => o != p).Select(o => o.Call.Base).ToArray();
                    string last = "";
                    var learned = await PollAsync(async ct =>
                    {
                        last = await BpqAdmin.RunAsync(Host, p.TelnetPort, ["ROUTES", "NODES"], TimeSpan.FromSeconds(3), ct);
                        return last.Contains(OurCall.Base, StringComparison.Ordinal)
                               && others.All(o => last.Contains(o, StringComparison.Ordinal));
                    }, StepBudget, TimeSpan.FromSeconds(3), cts.Token);
                    output.WriteLine($"{p.Call} ROUTES+NODES: {Collapse(last)}");
                    learned.Should().BeTrue(
                        $"{p.Call} must learn pdn as a route and the other partners ({string.Join(", ", others)}) via pdn's NODES");
                }

                // (4) L4 circuit pdn -> each partner, a command and its reply.
                foreach (var p in Peers)
                {
                    var dest = netRom.Snapshot().ResolveDestination(p.Alias);
                    dest.Should().NotBeNull($"pdn resolves {p.Alias} as a NET/ROM destination");
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(30));
                    await using var conn = await netRom.ConnectCircuitAsync(dest!, OurCall, connectCts.Token);
                    await conn.WriteAsync(Encoding.ASCII.GetBytes("PORTS\r"), cts.Token);
                    var reply = await ReadAccumulateAsync(conn, "AXIP", TimeSpan.FromSeconds(20), cts.Token);
                    output.WriteLine($"pdn -> {p.Alias} PORTS reply: {Collapse(reply)}");
                    reply.Should().Contain(p.Call.Base, $"the L4 circuit to {p.Alias} reaches {p.Call}'s node prompt");
                    reply.Should().Contain("AXIP", $"the PORTS command typed over the circuit is answered by {p.Call}");
                }

                // (5) a partner's user connects to pdn's alias and data round-trips.
                var c = Peers[2];
                // Polled like step 6: a slow runner may need a second attempt before the
                // circuit is up and the reply is back inside the read windows.
                string session = "";
                var inboundOk = await PollAsync(async ct =>
                {
                    session = await BpqAdmin.RunAsync(Host, c.TelnetPort,
                        ["C " + OurAlias, "WAIT:6", "hello-mpx", "WAIT:4", "B"], TimeSpan.FromSeconds(2), ct);
                    return session.Contains("pdn-mpx", StringComparison.Ordinal)
                           && session.Contains("ack:hello-mpx", StringComparison.Ordinal);
                }, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5), cts.Token);
                output.WriteLine($"{c.Call} C {OurAlias}: {Collapse(session)}");
                inboundOk.Should().BeTrue($"{c.Call}'s user reaches pdn's alias and a typed line round-trips");
                session.Should().Contain("pdn-mpx", $"{c.Call}'s user reaches pdn's inbound console over an L4 circuit");
                inboundEchoed.Task.IsCompletedSuccessfully.Should().BeTrue("the line the BPQ user typed reached pdn over the circuit");
                session.Should().Contain("ack:hello-mpx", "pdn's reply to the BPQ user's line came back over the circuit");

                // (6) partner A's user reaches partner B THROUGH pdn (pdn as a transit node).
                var a = Peers[0];
                var b = Peers[1];
                string transit = "";
                var transitOk = await PollAsync(async ct =>
                {
                    transit = await BpqAdmin.RunAsync(Host, a.TelnetPort,
                        ["C " + b.Alias, "WAIT:8", "PORTS", "WAIT:4", "B"], TimeSpan.FromSeconds(2), ct);
                    return transit.Contains(b.Call.Base + "}", StringComparison.Ordinal);
                }, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5), cts.Token);
                output.WriteLine($"{a.Call} C {b.Alias} (via pdn): {Collapse(transit)}");
                transitOk.Should().BeTrue($"{a.Call}'s user connects to {b.Alias} through pdn and {b.Call} answers PORTS");
            }
            finally
            {
                await broadcastCts.CancelAsync();
                try { await broadcaster; } catch (OperationCanceledException) { /* expected */ }
            }
        }
        finally
        {
            foreach (var l in listeners)
            {
                await l.DisposeAsync();
            }
            foreach (var m in modems)
            {
                await m.DisposeAsync();
            }
        }
    }

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

    private static async Task<bool> PollAsync(Func<CancellationToken, Task<bool>> check, TimeSpan budget, TimeSpan every, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (await check(cts.Token))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
            catch (IOException) { /* telnet hiccup: retry */ }
            catch (SocketException) { /* telnet hiccup: retry */ }
            try { await Task.Delay(every, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return false;
    }

    private static async Task<string> ReadAccumulateAsync(INodeConnection conn, string needle, TimeSpan budget, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        var sb = new StringBuilder();
        while (!cts.IsCancellationRequested)
        {
            ReadOnlyMemory<byte> data;
            try { data = await conn.ReadAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            if (data.IsEmpty)
            {
                break;
            }
            sb.Append(Encoding.ASCII.GetString(data.Span));
            if (sb.ToString().Contains(needle, StringComparison.Ordinal))
            {
                break;
            }
        }
        return sb.ToString();
    }

    private void DumpSnapshot(NetRomRoutingSnapshot snap)
    {
        foreach (var n in snap.Neighbours)
        {
            output.WriteLine($"  neighbour {n.Alias}:{n.Neighbour} port={n.PortId} qual={n.PathQuality}");
        }
        foreach (var d in snap.Destinations)
        {
            output.WriteLine($"  dest {d.Alias}:{d.Destination} [{string.Join(", ", d.Routes.Select(r => $"via {r.Neighbour} q{r.Quality}"))}]");
        }
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

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
    /// Minimal IAC-aware driver for a LinBPQ node prompt as the <c>admin</c> user. Each line is sent and
    /// then read for <c>perLine</c>; a <c>WAIT:n</c> entry just reads for n seconds.
    /// Returns everything BPQ sent after login.
    /// </summary>
    private static class BpqAdmin
    {
        private const byte IAC = 255, DONT = 254, DO = 253, WILL = 251, WONT = 252;

        public static async Task<string> RunAsync(string host, int port, IReadOnlyList<string> lines, TimeSpan perLine, CancellationToken ct)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct);
            using var stream = tcp.GetStream();
            await ReadForAsync(stream, TimeSpan.FromSeconds(1.5), "user", ct);
            await SendAsync(stream, "admin", ct);
            await ReadForAsync(stream, TimeSpan.FromSeconds(2), "password", ct);
            await SendAsync(stream, "admin", ct);
            await ReadForAsync(stream, TimeSpan.FromSeconds(3), "Telnet Server", ct);
            var all = new StringBuilder();
            foreach (var line in lines)
            {
                if (line.StartsWith("WAIT:", StringComparison.Ordinal))
                {
                    all.Append(await ReadForAsync(stream, TimeSpan.FromSeconds(double.Parse(line[5..], System.Globalization.CultureInfo.InvariantCulture)), null, ct));
                    continue;
                }
                await SendAsync(stream, line, ct);
                all.Append(await ReadForAsync(stream, perLine, null, ct));
            }
            return all.ToString();
        }

        private static async Task SendAsync(NetworkStream stream, string line, CancellationToken ct)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r"), ct);
            await stream.FlushAsync(ct);
        }

        // Read for up to `budget`, stopping early once `needle` (if any) has been seen.
        private static async Task<string> ReadForAsync(NetworkStream stream, TimeSpan budget, string? needle, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);
            while (!cts.IsCancellationRequested)
            {
                int n;
                try { n = await stream.ReadAsync(buf, cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                if (n == 0)
                {
                    break;
                }
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] != IAC)
                    {
                        sb.Append((char)buf[i]);
                        continue;
                    }
                    if (i + 2 >= n)
                    {
                        break;
                    }
                    byte verb = buf[i + 1], opt = buf[i + 2];
                    i += 2;
                    byte reply = verb switch { DO => WONT, WILL => DONT, _ => (byte)0 };
                    if (reply != 0)
                    {
                        await stream.WriteAsync(new byte[] { IAC, reply, opt }, ct);
                    }
                }
                if (needle is not null && sb.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
            return sb.ToString();
        }
    }
}
