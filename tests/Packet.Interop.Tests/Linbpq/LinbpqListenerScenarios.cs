using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.Kiss;
using Xunit;

namespace Packet.Interop.Tests.Linbpq;

/// <summary>
/// Listener-side interop against LinBPQ — we listen on net-sim node a
/// (KISS-TCP 8100) under callsign <c>PNTEST</c>; BPQ initiates an
/// outbound <c>C PNTEST</c> via its node-prompt telnet listener on
/// 127.0.0.1:8010; the resulting SABM travels through net-sim's
/// AFSK1200 channel to our listener; our listener fires
/// <see cref="Ax25Listener.SessionAccepted"/>; we send a welcome
/// I-frame; BPQ disconnects; we tear down.
/// </summary>
/// <remarks>
/// <para>
/// This is the inverse of <see cref="LinbpqViaNetsimConnectedMode"/> —
/// that test has us initiate against BPQ as the acceptor; this one has
/// BPQ initiate against us as the acceptor. It validates that our
/// <see cref="Ax25Listener"/> is interoperable as the inbound-accept
/// side of a real third-party AX.25 stack, not just as the dialler.
/// </para>
/// <para>
/// The "BPQ-side trigger" is a telnet login to BPQ's node prompt
/// followed by typing <c>C PNTEST</c>. BPQ doesn't have a single-shot
/// CLI for issuing outbound L2 connects, so we re-purpose its
/// sysop-telnet interface as the driver. It's a fine pattern for one
/// outbound dial-out; a future fixture might expose this via the AGW
/// monitor / connect-from primitive once <c>Packet.Agw</c> grows that
/// surface.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class LinbpqListenerScenarios
{
    private const string Host          = "127.0.0.1";
    private const int    OurKissPort   = 8100;
    private const int    BpqTelnetPort = 8010;
    private static readonly Callsign OurCall = new("PNTEST", 0);

    private static readonly TimeSpan ConnectBudget    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(30);

    // Headroom for a local state mutation to settle after the signal that
    // implies it (SessionAccepted having fired, the DISC having been
    // acknowledged). Near-instant on an idle host; matters only under CPU
    // contention. WaitUntil returns as soon as the predicate holds.
    private static readonly TimeSpan StateSettleBudget = TimeSpan.FromSeconds(15);

    // Budget for each blocking read of a BPQ telnet prompt (user/password).
    // BPQ prints these promptly once the socket is up, but under host
    // contention the read can lag; a tight 5 s could spuriously give up and
    // send credentials into a not-yet-ready prompt. Generous and harmless —
    // ReadUntilAsync returns as soon as the needle is seen.
    private static readonly TimeSpan TelnetReadBudget = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Listener_Accepts_Connect_From_Linbpq()
    {
        using var cts = new CancellationTokenSource(
            ConnectBudget + DisconnectBudget + TimeSpan.FromSeconds(30));

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);
        await using var listener = new Ax25Listener(new Packet.Kiss.KissModemTransport(kiss), new Ax25ListenerOptions
        {
            MyCall = OurCall,
        });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bpqDisconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        listener.SessionAccepted += (_, e) =>
        {
            // BPQ's outbound source on the wire is its NODECALL with
            // an SSID assigned by BPQ; we accept any session — the
            // listener doesn't filter — and just record the first
            // one. The remote-side disconnection wires inline below.
            accepted.TrySetResult(e.Session);
            e.Session.DataLinkSignalEmitted += (_, sig) =>
            {
                if (sig is DataLinkDisconnectIndication or DataLinkDisconnectConfirm)
                {
                    bpqDisconnected.TrySetResult(true);
                }
            };
        };

        await listener.StartAsync(cts.Token);

        // Brief settle so the listener's pump is subscribed before we
        // tell BPQ to dial. BPQ's L2 connect retries on no UA, so a
        // small race here would still resolve, but waiting eliminates
        // the unnecessary retry budget burn.
        await Task.Delay(500, cts.Token);

        // Drive BPQ via its node-prompt telnet listener.
        await DriveBpqConnectAsync(OurCall, cts.Token);

        // BPQ now SABMs us; the listener accepts and goes Connected.
        var session = await accepted.Task.WaitAsync(cts.Token);
        await WaitUntil(() => session.CurrentState == "Connected", StateSettleBudget, cts.Token);
        session.CurrentState.Should().Be("Connected");

        // Send a welcome I-frame so BPQ has something to acknowledge.
        // Keep the payload small + non-empty; BPQ's node prompt
        // accepts arbitrary text and just echoes errors. Pid 0xF0 (no
        // L3) matches BPQ's own node-prompt protocol.
        session.PostEvent(new DlDataRequest(
            Encoding.ASCII.GetBytes("Packet.NET listener says hi\r"),
            Ax25Frame.PidNoLayer3));

        // Tell BPQ to drop the link from its side — type "B" (bye) at
        // the node prompt of the remote-end session it just opened.
        // We can't drive that easily without holding the same telnet
        // session; the more deterministic approach is to issue
        // DlDisconnectRequest from our side and observe BPQ's UA.
        session.PostEvent(new DlDisconnectRequest());

        // Wait for either DL-DISCONNECT-confirm (we initiated) or
        // -indication (BPQ initiated). Either is success here.
        await bpqDisconnected.Task.WaitAsync(DisconnectBudget, cts.Token);
        await WaitUntil(() => session.CurrentState == "Disconnected",
            StateSettleBudget, cts.Token);
        session.CurrentState.Should().Be("Disconnected");
    }

    /// <summary>
    /// Telnet into BPQ's sysop port, log in, and type
    /// <c>C &lt;callsign&gt;</c> to initiate an outbound L2 connect on
    /// BPQ's KISS-TCP port (netsim node c, port 8102 inside docker).
    /// </summary>
    private static async Task DriveBpqConnectAsync(Callsign target, CancellationToken ct)
    {
        using var telnet = new TcpClient();
        await telnet.ConnectAsync(Host, BpqTelnetPort, ct).ConfigureAwait(false);
        var stream = telnet.GetStream();

        // BPQ's telnet listener emits "user:" then "password:" prompts.
        // It accepts the answers terminated by \r. The configured
        // credentials in docker/linbpq/bpq32.cfg are admin/admin
        // (sysop).
        await ReadUntilAsync(stream, "user", TelnetReadBudget, ct);
        await WriteLineAsync(stream, "admin", ct);
        await ReadUntilAsync(stream, "password", TelnetReadBudget, ct);
        await WriteLineAsync(stream, "admin", ct);

        // After login, BPQ shows the node banner + a "PN0TST}" or
        // "Cmd:" style prompt. We don't need to wait for the exact
        // text — typing the C command at any point is accepted.
        // Settle briefly so BPQ has finished printing the banner.
        await Task.Delay(500, ct);

        // BPQ requires explicit port number for downlink connects:
        // "C <port> <callsign>". Bare "C PNTEST" returns
        // "Downlink connect needs port number — C P CALLSIGN".
        //
        // Port numbering follows the PORT-block order in bpq32.cfg:
        //   1 Telnet   (sysop)
        //   2 AXIP     (UDP listener)
        //   3 netsim   (KISS-TCP — the one with a route to net-sim node a)
        //
        // The PORTS command output confirms this (verified at test-write
        // time). If the cfg order ever changes, this number must follow.
        await WriteLineAsync(stream, $"C 3 {target}", ct);

        // Keep the telnet socket open for a beat so BPQ doesn't
        // abandon the command; then close. BPQ's outbound L2 session
        // is independent of the telnet session that initiated it.
        await Task.Delay(1500, ct);
    }

    /// <summary>Read from the stream until a substring is observed (case-insensitive).</summary>
    private static async Task ReadUntilAsync(NetworkStream stream, string needle, TimeSpan budget, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);
        var sb = new StringBuilder();
        var buf = new byte[256];
        while (!cts.IsCancellationRequested)
        {
            int n;
            try { n = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (n <= 0) return;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            if (sb.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase)) return;
        }
    }

    private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan budget, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return;
            try { await Task.Delay(50, cts.Token); } catch (OperationCanceledException) { return; }
        }
    }
}
