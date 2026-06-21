using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Axudp;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Transports;
using Xunit;

namespace Packet.Interop.Tests.Linbpq;

/// <summary>
/// Connected-mode AXUDP interop against the real LinBPQ container — the gap
/// PR #299 left open. The node host's <see cref="AxudpFrameTransport"/> (an
/// <c>IKissModem</c> presenting <see cref="AxudpSocket"/> over UDP) was only
/// ever exercised pdn↔pdn on loopback; AXUDP exists for real-peer interop and
/// LinBPQ is the de-facto reference, so these tests prove it actually talks to
/// BPQ through the SAME node-host machinery a deployed pdn uses
/// (<see cref="PortSupervisor"/> + <see cref="TransportFactory"/> →
/// <c>AxudpFrameTransport</c> → <c>Ax25Listener</c> → node console), in BOTH
/// directions, end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// <b>Topology.</b> LinBPQ's BPQAXIP driver binds UDP 8093 (published on the
/// host as <c>127.0.0.1:8093</c>). pdn binds a fixed host UDP port (8190) and
/// points its <see cref="AxudpTransport"/> at <c>127.0.0.1:8093</c>. BPQ's
/// <c>bpq32.cfg</c> AXIP port carries <c>AUTOADDQUIET</c> (auto-learn pdn's reply
/// route from the first inbound frame, for the pdn→BPQ leg) and a static
/// <c>MAP PNAX25-1 172.30.0.1 UDP 8190</c> (172.30.0.1 = the rfnet bridge
/// gateway = the host, where pdn binds — used for the BPQ→pdn leg, where there
/// is no prior inbound to auto-learn from).
/// </para>
/// <para>
/// <b>FCS is MANDATORY (source-verified, the headline finding).</b> The pinned
/// LinBPQ 6.0.25.23 BPQAXIP driver over UDP requires the 2-octet CRC-16/X.25
/// FCS on every datagram: <c>bpqaxip.c</c>'s UDP receive path computes the FCS
/// over the whole datagram and drops anything whose residue isn't <c>0xf0b8</c>
/// ("BPQAXIP Invalid CRC"), and its send path appends the FCS. There is no
/// per-MAP "no CRC" knob. AXUDP therefore unconditionally carries the FCS —
/// settled by a citation survey that found FCS-on is the de-facto AXIP/AXUDP wire
/// form everywhere (RFC 1226 + ax25ipd + BPQAXIP + XRouter + JNOS) and FCS-less is
/// pdn-only (the pre-#299 docs wrongly claimed FCS-less "matches BPQAXIP", and an
/// FCS-less datagram is in fact silently dropped by BPQ; the FCS-less opt-out
/// interoperated with nothing and was removed).
/// <see cref="FcsLess_Sabm_IsDropped_FcsBearing_Sabm_GetsUa"/> locks this in on
/// the wire so a regression (e.g. emitting FCS-less datagrams again) is caught.
/// </para>
/// <para>
/// Serialised into <see cref="NetsimCollection"/>: these tests talk to the same
/// BPQ daemon as the netsim-based LinBPQ tests (via its separate AXIP/UDP port)
/// and both bind host UDP 8190, so they must not run in parallel with each
/// other or with the netsim BPQ scenarios.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public sealed class LinbpqViaAxudpConnectedMode
{
    private const string Host          = "127.0.0.1";
    private const int    BpqAxudpPort  = 8093;   // BPQAXIP UDP listener (published)
    private const int    BpqHttpPort   = 8008;   // liveness probe
    private const int    BpqTelnetPort = 8010;   // node prompt (for BPQ→pdn dial-out)

    // The static MAP target in bpq32.cfg: BPQ originates connects to THIS exact
    // callsign + port (Direction B has no inbound for AUTOADDQUIET to learn from).
    private const int    PdnMappedPort = 8190;
    private static readonly Callsign PdnMappedCall = new("PNAX25", 1);

    // BPQ's AXIP port is the 2nd PORT block in bpq32.cfg (Telnet=1, AXIP=2,
    // netsim=3) — the order is fixed in the fixture, so the AXIP port number is
    // stable. BPQ's `C <port> <call>` dials out on that port.
    private const int    BpqAxipPortNum = 2;

    private static readonly Callsign BpqCall = new("PN0TST", 0);   // BPQ NODECALL

    // Directions A and the FCS guard don't need the static MAP — BPQ's AUTOADDQUIET
    // auto-learns their reply route from the inbound SABM (verified). They each use
    // a DISTINCT callsign and a DISTINCT, FIXED local port. Distinct callsigns make
    // BPQ track three independent links, so the shared daemon can't leak link/MH
    // state between tests; FIXED ports keep BPQ's AUTOADD cache valid across re-runs
    // (bpqaxip.c's auto-added ARP entry pins the call→port it first saw and never
    // updates it for AUTOADD entries, so a CHANGING ephemeral port would make BPQ
    // reply to a dead port on the second run — source-verified, learned the hard
    // way). The three ports are distinct so the serialised tests never contend.
    private const int    PdnDialOutPort = 8191;
    private const int    PdnFcsPort     = 8192;
    private static readonly Callsign PdnDialOutCall = new("PNAXDA", 1);   // Direction A
    private static readonly Callsign PdnFcsCall     = new("PNAXFC", 1);   // FCS guard

    /// <summary>
    /// Direction A — pdn → LinBPQ. A pdn node host with an AXUDP port dials BPQ's
    /// NODECALL through its own outbound connector (the exact path the node
    /// console's <c>Connect</c> command uses):
    /// SABM crosses the UDP tunnel, BPQ replies UA (the connect only returns on
    /// DL-CONNECT-confirm), then an I-frame round-trip — we send the <c>P</c>
    /// (ports) command and read BPQ's reply back over the tunnel — and a clean
    /// teardown on dispose. Exercises the full send/receive FCS handling,
    /// including BPQ's RR S-frame acks (which an unstripped FCS tail would break).
    /// </summary>
    [SkippableFact]
    public async Task Pdn_Connects_Out_To_Linbpq_Over_Axudp_And_Exchanges_IFrames()
    {
        Skip.IfNot(await IsTcpPortReachable(Host, BpqHttpPort),
            $"LinBPQ not reachable (HTTP {Host}:{BpqHttpPort}). Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait'.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Distinct callsign + distinct fixed local port: AUTOADDQUIET gives BPQ the
        // reply route (no static MAP needed) and this test is its own isolated BPQ
        // link; the fixed port keeps BPQ's AUTOADD cache valid across re-runs.
        await using var pdn = BuildPdn(call: PdnDialOutCall, localPort: PdnDialOutPort);
        await pdn.StartAsync(cts.Token);
        await WaitForAsync(() => pdn.RunningPortIds.Contains("axudp"),
            "pdn's AXUDP port should come up", cts.Token);

        var connector = pdn.ResolveDefaultConnector();
        connector.Should().NotBeNull("pdn has a running AXUDP port to dial out on");

        await using var toBpq = await connector!.ConnectAsync(BpqCall, cts.Token);
        // ConnectAsync only returns on DL-CONNECT-confirm, so reaching here proves
        // the SABM/UA handshake crossed the AXUDP+FCS tunnel.
        toBpq.TransportKind.Should().Be(NodeTransportKind.Ax25);
        toBpq.PeerId.Should().Contain("PN0TST", "the connected peer is BPQ's NODECALL");

        // ── I-frame round-trip ──────────────────────────────────────────
        // BPQ sends its CTEXT welcome banner as an eager I-frame right after UA.
        // Drain it (read until the indication stream goes quiet) so it can't be
        // mistaken for — or swallow the start of — our command's response, then
        // send "P\r" (the Ports command: short, deterministically non-empty, no
        // side effects on BPQ state) and read BPQ's reply.
        await DrainUntilQuietAsync(toBpq, quietFor: TimeSpan.FromSeconds(1.5), budget: TimeSpan.FromSeconds(15), cts.Token);

        await toBpq.WriteAsync(Encoding.ASCII.GetBytes("P\r"), cts.Token);
        var reply = await ReadUntilAsync(toBpq, needle: "Ports", TimeSpan.FromSeconds(20), cts.Token);

        reply.Should().Contain("Ports",
            "BPQ must reply to our Ports command with at least one I-frame over AXUDP");
        reply.Should().Contain("AXIP",
            "BPQ's ports list names its AXIP port — proving a real request/response I-frame round-trip over the AXUDP tunnel");
        // Dispose (below, via `await using`) drives DL-DISCONNECT → DISC/UA, a
        // clean teardown (verified on the wire in the raw-tap during development).
    }

    /// <summary>
    /// Direction B — LinBPQ → pdn. A pdn node host listens as PNAX25-1 on the
    /// AXUDP port; we then drive BPQ's own telnet node prompt to
    /// <c>C 2 PNAX25-1</c> (connect out on its AXIP port to pdn). BPQ
    /// originates the SABM over AXIP, pdn accepts it (its <c>SessionAccepted</c>
    /// fires and the supervisor runs the node console against the inbound
    /// session), and pdn's banner+prompt are relayed back to the BPQ telnet user.
    /// Proves the inbound (responder) leg end-to-end through the node host.
    /// </summary>
    [SkippableFact]
    public async Task Linbpq_Connects_In_To_Pdn_Over_Axudp_And_Reaches_The_Node_Prompt()
    {
        Skip.IfNot(await IsTcpPortReachable(Host, BpqHttpPort),
            $"LinBPQ not reachable (HTTP {Host}:{BpqHttpPort}). Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait'.");
        Skip.IfNot(await IsTcpPortReachable(Host, BpqTelnetPort),
            $"LinBPQ telnet not reachable on {Host}:{BpqTelnetPort}.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // BPQ originates this connect, so pdn MUST be at the static MAP target
        // (callsign + fixed port) — there's no inbound for AUTOADDQUIET to learn.
        await using var pdn = BuildPdn(
            call: PdnMappedCall,
            localPort: PdnMappedPort,
            banner: "Packet.NET node reached via AXUDP",
            prompt: "pdn> ");
        await pdn.StartAsync(cts.Token);
        await WaitForAsync(() => pdn.RunningPortIds.Contains("axudp"),
            "pdn's AXUDP port should come up", cts.Token);

        // Observe pdn accepting the inbound BPQ session directly off the listener.
        var accepted = new TaskCompletionSource<Callsign>(TaskCreationOptions.RunContinuationsAsynchronously);
        pdn.GetPort("axudp")!.Listener.SessionAccepted += (_, e) =>
            accepted.TrySetResult(e.Session.Context.Remote);

        // Drive BPQ's telnet node prompt to dial out over its AXIP port to pdn.
        using var telnet = await BpqTelnet.LoginAsync(Host, BpqTelnetPort, "admin", "admin", cts.Token);
        var relayed = await telnet.SendAndReadAsync(
            $"C {BpqAxipPortNum} {PdnMappedCall}",
            // pdn's banner+prompt are sent as one I-frame after the handshake.
            stopWhenContains: "pdn>",
            TimeSpan.FromSeconds(25),
            cts.Token);

        relayed.Should().Contain("reached via AXUDP",
            "BPQ's node user must see pdn's banner — proving BPQ connected IN over AXUDP and reached pdn's node prompt");
        relayed.Should().Contain("pdn>", "pdn's prompt is relayed back to the BPQ telnet user");

        var inboundRemote = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        inboundRemote.Should().Be(BpqCall,
            "pdn's listener accepted the inbound AXUDP session from BPQ's NODECALL");
    }

    /// <summary>
    /// The FCS finding, locked in on the wire (regression guard). Source-verified
    /// in <c>bpqaxip.c</c> and proven here against the live container: an FCS-less
    /// SABM is silently dropped by BPQ's BPQAXIP UDP receive path ("Invalid CRC")
    /// → no reply; the same SABM with the 2-octet CRC-16/X.25 FCS appended is
    /// accepted → BPQ answers UA. This is why AXUDP unconditionally carries the
    /// FCS; if anyone makes AXUDP emit FCS-less datagrams again, this fails.
    /// </summary>
    /// <remarks>
    /// The FCS-less leg is sent via <see cref="AxudpSocket.SendRawAsync"/> (the raw
    /// escape hatch, which appends no FCS) since <see cref="AxudpSocket.SendAsync"/>
    /// always appends the FCS. The FCS-bearing reply is received through
    /// <see cref="AxudpSocket.ReceiveAsync"/>, which strips + validates the FCS — so
    /// a non-null result already proves BPQ's reply carried a valid FCS.
    /// </remarks>
    [SkippableFact]
    public async Task FcsLess_Sabm_IsDropped_FcsBearing_Sabm_GetsUa()
    {
        Skip.IfNot(await IsTcpPortReachable(Host, BpqHttpPort),
            $"LinBPQ not reachable (HTTP {Host}:{BpqHttpPort}). Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait'.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Distinct callsign + distinct fixed port: an isolated BPQ link (AUTOADDQUIET
        // gives the reply route), so a prior test's BPQ state can't leak in; the
        // fixed port keeps BPQ's AUTOADD cache valid across re-runs.
        using var sock = new AxudpSocket(localPort: PdnFcsPort);
        var bpq = new IPEndPoint(IPAddress.Loopback, BpqAxudpPort);

        // Drain any stray datagram already queued before we start (belt + braces).
        await DrainDatagramsAsync(sock, TimeSpan.FromMilliseconds(300), cts.Token);

        // FCS-less SABM (raw send — no FCS appended) → BPQ drops it as "Invalid CRC"
        // → no reply.
        var sabm = Ax25Frame.Sabm(destination: BpqCall, source: PdnFcsCall, pollBit: true);
        await sock.SendRawAsync(bpq, sabm.ToBytes(), cts.Token);
        var fcsLessReply = await ReceiveOneAsync(sock, TimeSpan.FromSeconds(5), cts.Token);
        fcsLessReply.Should().BeNull(
            "BPQ's BPQAXIP UDP driver drops an FCS-less datagram as 'Invalid CRC' (bpqaxip.c) — no reply");

        // FCS-bearing SABM (SendAsync always appends the FCS) → BPQ accepts it →
        // replies UA. ReceiveAsync strips + validates the FCS, so a non-null body
        // already proves BPQ's UA carried a valid FCS (an unstripped tail would
        // otherwise make TryParse reject acks — the whole reason AXUDP strips it).
        await sock.SendAsync(bpq, sabm, cts.Token);
        var fcsReply = await ReceiveOneAsync(sock, TimeSpan.FromSeconds(10), cts.Token);
        fcsReply.Should().NotBeNull(
            "BPQ accepts the FCS-bearing SABM and replies with a UA whose FCS validates (ReceiveAsync would drop a bad-FCS datagram)");
        IsUa(fcsReply!).Should().BeTrue(
            $"BPQ's reply to the FCS-bearing SABM is a UA (got control 0x{ControlOf(fcsReply):X2})");

        // Cleanly close BPQ's now half-open link so it doesn't retransmit UA into
        // a later run (FCS-bearing, so BPQ accepts the DISC).
        await sock.SendAsync(bpq, Ax25Frame.Disc(destination: BpqCall, source: PdnFcsCall, pollBit: true), cts.Token);
        await DrainDatagramsAsync(sock, TimeSpan.FromMilliseconds(500), cts.Token);
    }

    // ── pdn node-host rig ───────────────────────────────────────────────

    private static PortSupervisor BuildPdn(Callsign call, int localPort, string? banner = null, string? prompt = null)
    {
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = call.ToString(), Alias = "PNAXU" },
            Services = banner is null && prompt is null
                ? new ServicesConfig()
                : new ServicesConfig
                {
                    Banner = banner ?? "Welcome to {node} ({call})",
                    Prompt = prompt ?? "{call}> ",
                },
            Ports =
            [
                new PortConfig
                {
                    Id = "axudp",
                    Enabled = true,
                    Transport = new AxudpTransport
                    {
                        Host = Host,
                        Port = BpqAxudpPort,
                        LocalPort = localPort,
                    },
                },
            ],
            // No telnet management listener — we drive BPQ's telnet, not pdn's.
            Management = new ManagementConfig { Telnet = new TelnetConfig { Enabled = false } },
        };

        return new PortSupervisor(
            new SingleConfigProvider(config),
            TransportFactory.Instance,
            TimeProvider.System,
            NullLoggerFactory.Instance);
    }

    // A fixed, never-changing config provider (no hot-reload path needed here).
    private sealed class SingleConfigProvider(NodeConfig config) : IConfigProvider
    {
        public NodeConfig Current => config;
        public IDisposable OnChange(Action<NodeConfig> listener) => Noop.Instance;
        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose() { }
        }
    }

    // ── wire helpers (FCS regression guard) ─────────────────────────────

    // Wait up to <paramref name="budget"/> for one valid datagram; null on timeout.
    // AxudpSocket.ReceiveAsync has already stripped + validated the FCS, so the
    // returned bytes are the bare AX.25 frame body (and a bad-FCS datagram never
    // surfaces — it's dropped inside ReceiveAsync).
    private static async Task<byte[]?> ReceiveOneAsync(AxudpSocket sock, TimeSpan budget, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        try
        {
            var r = await sock.ReceiveAsync(cts.Token);
            return r.RawFrame;
        }
        catch (OperationCanceledException) { return null; }
    }

    // Consume + discard any datagrams already queued (until quiet for the window).
    private static async Task DrainDatagramsAsync(AxudpSocket sock, TimeSpan quietFor, CancellationToken outer)
    {
        while (!outer.IsCancellationRequested)
        {
            var got = await ReceiveOneAsync(sock, quietFor, outer);
            if (got is null) return;
        }
    }

    private static byte ControlOf(byte[] body) =>
        Ax25Frame.TryParse(body, out var f) ? f.Control : (byte)0xFF;

    private static bool IsUa(byte[] body) =>
        Ax25Frame.TryParse(body, out var f) && (f.Control & 0xEF) == 0x63;   // UA, PF masked

    // ── helpers ─────────────────────────────────────────────────────────

    // Read + discard from the connection until it's been quiet for
    // <paramref name="quietFor"/> (used to consume BPQ's eager welcome banner
    // before issuing a command), or <paramref name="budget"/> elapses.
    private static async Task DrainUntilQuietAsync(INodeConnection conn, TimeSpan quietFor, TimeSpan budget, CancellationToken outer)
    {
        using var outerCts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        outerCts.CancelAfter(budget);
        while (!outerCts.IsCancellationRequested)
        {
            using var quietCts = CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);
            quietCts.CancelAfter(quietFor);
            try
            {
                var chunk = await conn.ReadAsync(quietCts.Token);
                if (chunk.Length == 0) return;   // EOF
            }
            catch (OperationCanceledException) when (quietCts.IsCancellationRequested && !outerCts.IsCancellationRequested)
            {
                return;   // quiet window elapsed with no new bytes — banner drained
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task<string> ReadUntilAsync(INodeConnection conn, string needle, TimeSpan budget, CancellationToken outer)
    {
        var sb = new StringBuilder();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var chunk = await conn.ReadAsync(cts.Token);
                if (chunk.Length == 0) break;   // EOF
                sb.Append(Encoding.ASCII.GetString(chunk.Span));
                if (sb.ToString().Contains(needle, StringComparison.Ordinal)) break;
            }
        }
        catch (OperationCanceledException) { }
        return sb.ToString();
    }

    private static async Task WaitForAsync(Func<bool> predicate, string because, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        while (!cts.IsCancellationRequested)
        {
            if (predicate()) return;
            await Task.Delay(100, cts.Token);
        }
        throw new TimeoutException($"timed out waiting: {because}");
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
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A minimal IAC-aware telnet client for driving BPQ's node prompt: enough to
    /// log in (user/password) and issue a line, filtering Telnet IAC negotiation
    /// (we reply WONT/DONT to every DO/WILL) so the option bytes don't pollute the
    /// text we assert on.
    /// </summary>
    private sealed class BpqTelnet : IDisposable
    {
        private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251;
        private readonly TcpClient tcp;
        private readonly NetworkStream stream;

        private BpqTelnet(TcpClient tcp)
        {
            this.tcp = tcp;
            stream = tcp.GetStream();
        }

        public static async Task<BpqTelnet> LoginAsync(string host, int port, string user, string pass, CancellationToken ct)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct);
            var t = new BpqTelnet(tcp);
            // BPQ prompts "user:" then "password:". Wait for each, answer, then
            // wait for the "Connected to … Telnet Server" acknowledgement.
            await t.ReadTextAsync(stopWhenContains: "user", TimeSpan.FromSeconds(8), ct);
            await t.SendLineAsync(user, ct);
            await t.ReadTextAsync(stopWhenContains: "password", TimeSpan.FromSeconds(8), ct);
            await t.SendLineAsync(pass, ct);
            await t.ReadTextAsync(stopWhenContains: "Telnet Server", TimeSpan.FromSeconds(8), ct);
            return t;
        }

        public async Task<string> SendAndReadAsync(string line, string stopWhenContains, TimeSpan budget, CancellationToken ct)
        {
            await SendLineAsync(line, ct);
            return await ReadTextAsync(stopWhenContains, budget, ct);
        }

        private async Task SendLineAsync(string line, CancellationToken ct)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r");
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        private async Task<string> ReadTextAsync(string stopWhenContains, TimeSpan budget, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buf = new byte[8192];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    int n;
                    try { n = await stream.ReadAsync(buf, cts.Token); }
                    catch (OperationCanceledException) { break; }
                    if (n == 0) break;   // peer closed
                    Append(sb, buf, n);
                    if (sb.ToString().Contains(stopWhenContains, StringComparison.Ordinal)) break;
                }
            }
            catch (IOException) { /* connection torn down — return what we have */ }
            return sb.ToString();
        }

        // Strip IAC negotiation; reply WONT/DONT to keep BPQ from waiting on us.
        private void Append(StringBuilder sb, byte[] buf, int n)
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
                    _ => 0,
                };
                if (reply != 0)
                {
                    // Best-effort, fire-and-forget IAC reply.
                    _ = stream.WriteAsync(new byte[] { IAC, reply, opt }).AsTask();
                }
            }
        }

        public void Dispose()
        {
            stream.Dispose();
            tcp.Dispose();
        }
    }
}
