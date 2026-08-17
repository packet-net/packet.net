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
    private const string Host = "127.0.0.1";
    private const int OurKissPort = 8100;
    private const int BpqTelnetPort = 8010;
    private static readonly Callsign OurCall = new("PNTEST", 0);

    // BPQ requires an explicit port number for downlink connects: "C <port> <callsign>".
    // Bare "C PNTEST" returns "Downlink connect needs port number - C P CALLSIGN".
    //
    // Port numbering follows the PORT-block order in docker/linbpq/bpq32.cfg:
    //   1 Telnet   (sysop)
    //   2 AXIP     (UDP listener)
    //   3 netsim   (KISS-TCP - the one with a route to net-sim node a)
    //
    // The PORTS command output confirms this (verified at test-write time). If the cfg
    // order ever changes, this number must follow.
    private const int NetsimPortNumber = 3;

    // Long enough to outlast BPQ's own L2 retry budget for this port (RETRIES=10 x
    // FRACK=3000 = 30 s in docker/linbpq/bpq32.cfg) plus margin, so a SABM that only gets
    // through on BPQ's last retry still counts, and a genuine "BPQ gave up" is reported as
    // such rather than being cut off mid-retry.
    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(45);
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

    // How long to listen for BPQ's immediate answer to the connect command. A command BPQ
    // accepts produces nothing here (it reports on the air, and later on this session), so
    // this budget is always spent in full - keep it short.
    private static readonly TimeSpan CommandEchoBudget = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Listener_Accepts_Connect_From_Linbpq()
    {
        using var cts = new CancellationTokenSource(
            ConnectBudget + DisconnectBudget + TimeSpan.FromSeconds(30));

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);
        await using var listener = new Ax25Listener(kiss, new Ax25ListenerOptions
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

        // Drive BPQ via its node-prompt telnet listener. The driver stays open for the
        // rest of the test, draining BPQ's side of the conversation into a transcript:
        // when the SABM doesn't arrive, what BPQ said is the whole diagnosis, and
        // closing the socket 1.5 s after typing the command threw it away (this test
        // failed once on CI with nothing but "A task was canceled" to go on - see
        // packet-net/packet.net#611).
        await using var bpq = await BpqNodeSession.LoginAsync(cts.Token);
        await bpq.ConnectDownlinkAsync(NetsimPortNumber, OurCall, cts.Token);

        // BPQ now SABMs us; the listener accepts and goes Connected.
        var acceptedInTime = await Task.WhenAny(accepted.Task, Delay(ConnectBudget, cts.Token)) == accepted.Task;
        acceptedInTime.Should().BeTrue(
            $"BPQ's 'C {NetsimPortNumber} {OurCall}' must reach our listener as a SABM within "
            + $"{ConnectBudget.TotalSeconds:F0} s. BPQ's node session said: {bpq.Transcript}");
        var session = await accepted.Task;

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
    /// A logged-in LinBPQ node session over telnet, used to make BPQ dial US. It keeps its
    /// socket open and keeps reading for as long as the test holds it, so everything BPQ
    /// says - the command's answer AND anything it reports later, e.g. its
    /// "Failure with &lt;call&gt;" once the L2 retry budget runs out - lands in
    /// <see cref="Transcript"/> and can be quoted in an assertion message.
    /// </summary>
    /// <remarks>
    /// Every step waits for the prompt that says BPQ is ready for it, rather than sleeping.
    /// The previous version typed the connect command 500 ms after sending the password,
    /// with no check that the login had completed and no look at what came back: a BPQ that
    /// was slow to finish logging us in could swallow the command, and the test's only
    /// symptom was a cancelled task 90 s later with nothing to diagnose it from.
    /// </remarks>
    private sealed class BpqNodeSession : IAsyncDisposable
    {
        private readonly TcpClient telnet;
        private readonly NetworkStream stream;
        private readonly StringBuilder transcript = new();
        private readonly CancellationTokenSource drainCts = new();
        private Task? drainTask;

        private BpqNodeSession(TcpClient telnet, NetworkStream stream)
        {
            this.telnet = telnet;
            this.stream = stream;
        }

        /// <summary>Everything BPQ has sent on this session, quoted for an assertion message.</summary>
        public string Transcript
        {
            get
            {
                lock (transcript)
                {
                    return "\"" + transcript.ToString().Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
                }
            }
        }

        /// <summary>
        /// Connect to BPQ's telnet listener and log in as the configured sysop user
        /// (admin/admin, see docker/linbpq/bpq32.cfg), waiting for each prompt in turn and
        /// finally for the node prompt ("PNTST:PN0TST}") that means BPQ is ready for a command.
        /// </summary>
        public static async Task<BpqNodeSession> LoginAsync(CancellationToken ct)
        {
            var telnet = new TcpClient();
            await telnet.ConnectAsync(Host, BpqTelnetPort, ct).ConfigureAwait(false);
            var session = new BpqNodeSession(telnet, telnet.GetStream());

            (await session.ReadUntilAsync("user", TelnetReadBudget, ct)).Should().BeTrue(
                $"BPQ's telnet listener must offer its user prompt. Saw: {session.Transcript}");
            await session.WriteLineAsync("admin", ct);
            (await session.ReadUntilAsync("password", TelnetReadBudget, ct)).Should().BeTrue(
                $"BPQ must offer its password prompt after the username. Saw: {session.Transcript}");
            await session.WriteLineAsync("admin", ct);

            // "Connected to PN0TST's Telnet Server" is BPQ's login-complete banner, and the
            // observable that says the next line will be read by its command interpreter
            // rather than by its login handler. Note it does NOT print its node prompt
            // ("PNTST:PN0TST}") here - that only follows a command - so the banner is the
            // signal to wait for. This is what the old fixed 500 ms sleep was standing in for.
            (await session.ReadUntilAsync(TelnetReadBudget, ct, "telnet server", "}")).Should().BeTrue(
                $"BPQ must finish the sysop login and be ready for a command. Saw: {session.Transcript}");
            return session;
        }

        /// <summary>
        /// Issue <c>C &lt;port&gt; &lt;callsign&gt;</c> - an outbound L2 connect on one of BPQ's
        /// ports - and assert BPQ did not refuse the command outright.
        /// </summary>
        public async Task ConnectDownlinkAsync(int port, Callsign target, CancellationToken ct)
        {
            await WriteLineAsync($"C {port} {target}", ct);

            // BPQ answers a command it won't run immediately ("Downlink connect needs port
            // number", "Invalid callsign", "Port not available", "Node busy"); a command it
            // WILL run stays silent here and reports later, so an empty read is the good
            // case. Give it a moment, then check what came back before starting the long
            // wait for a SABM that a refused command can never produce.
            await ReadUntilAsync("\u0000never\u0000", CommandEchoBudget, ct);
            var answer = Transcript;
            answer.Should().NotContainAny(["needs port number", "Invalid", "not available", "Busy from"],
                $"BPQ must accept the downlink connect command. It answered: {answer}");

            // Keep reading in the background for the rest of the test: a connect that fails
            // on the air reports "Failure with <call>" here ~30 s later (RETRIES x FRACK),
            // which is the difference between "BPQ never dialled" and "BPQ dialled and got
            // no answer" when this test next goes red.
            drainTask = Task.Run(() => DrainAsync(drainCts.Token), CancellationToken.None);
        }

        private async Task DrainAsync(CancellationToken ct)
        {
            var buf = new byte[256];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        return;
                    }

                    Append(Encoding.ASCII.GetString(buf, 0, n));
                }
            }
            catch (Exception)
            {
                // Socket closed / cancelled during teardown - the transcript keeps whatever
                // it had. A diagnostic reader must never fail the test it is diagnosing.
            }
        }

        private Task<bool> ReadUntilAsync(string needle, TimeSpan budget, CancellationToken ct)
            => ReadUntilAsync(budget, ct, needle);

        /// <summary>Read until any of the substrings is observed (case-insensitive). True if one was.</summary>
        private async Task<bool> ReadUntilAsync(TimeSpan budget, CancellationToken ct, params string[] needles)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);
            var buf = new byte[256];
            while (!cts.IsCancellationRequested)
            {
                int n;
                try { n = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
                if (n <= 0)
                {
                    return false;
                }

                var seen = Append(Encoding.ASCII.GetString(buf, 0, n));
                if (needles.Any(needle => seen.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private string Append(string text)
        {
            lock (transcript)
            {
                transcript.Append(text);
                return transcript.ToString();
            }
        }

        private async Task WriteLineAsync(string line, CancellationToken ct)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r");
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await drainCts.CancelAsync().ConfigureAwait(false);
            if (drainTask is { } t)
            {
                try { await t.ConfigureAwait(false); } catch (Exception) { /* teardown */ }
            }

            drainCts.Dispose();
            stream.Dispose();
            telnet.Dispose();
        }
    }

    /// <summary>A cancellable delay that completes (rather than throwing) on cancellation -
    /// so a <c>Task.WhenAny</c> race resolves to "the other thing didn't happen".</summary>
    private static async Task Delay(TimeSpan budget, CancellationToken ct)
    {
        try { await Task.Delay(budget, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan budget, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            if (condition())
            {
                return;
            }

            try { await Task.Delay(50, cts.Token); } catch (OperationCanceledException) { return; }
        }
    }
}
