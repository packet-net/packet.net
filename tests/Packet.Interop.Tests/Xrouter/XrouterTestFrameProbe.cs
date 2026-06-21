using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.Kiss;
using Packet.Node.Core.Api;
using Xunit;
using Xunit.Abstractions;

namespace Packet.Interop.Tests.Xrouter;

/// <summary>
/// Empirical probe: does XRouter answer a connectionless AX.25 TEST command
/// (§4.3.4.2) with a TEST response? We stand up an <see cref="Ax25Listener"/> on
/// net-sim node a (KISS-TCP 8100) and axping XRouter's NODECALL (PN0XRT) on node d
/// (8103) via <see cref="AxPinger"/> — the exact production initiator path. A
/// spec-compliant responder echoes the info field in a TEST response; a peer that
/// doesn't implement TEST simply never answers (100% loss).
/// </summary>
/// <remarks>
/// This is a one-shot finding-recorder, not a permanent behavioural contract — it
/// asserts nothing about XRouter (whether it answers TEST is XRouter's business,
/// not ours), it just logs the loss percentage so the result is captured. Tagged
/// Interop so it only runs against a live docker stack. Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class XrouterTestFrameProbe(ITestOutputHelper output)
{
    private const string Host        = "127.0.0.1";
    private const int    OurKissPort = 8100;
    private static readonly Callsign OurCall     = new("PNTEST", 0);
    private static readonly Callsign XrouterCall = new("PN0XRT", 0);

    [Fact]
    public async Task Probe_Xrouter_For_Test_Frame_Support()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);
        await using var listener = new Ax25Listener(kiss, new Ax25ListenerOptions { MyCall = OurCall });
        await listener.StartAsync(cts.Token);

        // Brief settle so net-sim's per-port queue is ready and XRouter's KISS dial
        // has landed before we put the first TEST command on the air.
        await Task.Delay(1000, cts.Token);

        var result = await AxPinger.RunAsync(
            new ListenerAxPingChannel(listener),
            XrouterCall,
            count: 3,
            perPingTimeout: TimeSpan.FromSeconds(10),
            clock: TimeProvider.System,
            ct: cts.Token);

        bool answersTest = result.LossPct < 100.0;
        output.WriteLine($"XROUTER TEST PROBE — loss {result.LossPct:F0}%, " +
            $"min/avg/max {result.MinMs}/{result.AvgMs}/{result.MaxMs} ms — " +
            $"XRouter {(answersTest ? "ANSWERS" : "does NOT answer")} TEST.");
        foreach (var reply in result.Replies)
        {
            output.WriteLine($"  probe #{reply.Seq}: {(reply.Timeout ? "TIMEOUT" : $"{reply.RttMs} ms")}");
        }

        // No assertion on XRouter's behaviour — the test exists to record the
        // finding. We only assert the probe itself ran cleanly (3 probes sent).
        result.Replies.Should().HaveCount(3, "the pinger should have sent all 3 probes");
    }
}
