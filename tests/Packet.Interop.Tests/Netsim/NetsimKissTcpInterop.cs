using System.Net.Http;
using System.Net.Sockets;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;
using Xunit;

namespace Packet.Interop.Tests.Netsim;

/// <summary>
/// KISS-TCP round-trip scenarios against the net-sim container.
/// </summary>
/// <remarks>
/// <para>
/// net-sim emulates an audio-domain RF link between two virtual TNCs and
/// exposes each TNC over a separate KISS-TCP port. We connect KISS clients
/// to both ends, transmit on one, observe on the other — which is the
/// closest a software-only test can get to a real RF link.
/// </para>
/// <para>
/// Bring the container up with:
/// <code>docker compose -f docker/compose.interop.yml up -d --wait netsim</code>
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class NetsimKissTcpInterop
{
    private const string Host = "127.0.0.1";
    private const int NodeAKissPort = 8100;
    private const int NodeBKissPort = 8101;
    private const int NetsimWebPort = 18080;

    [SkippableFact]
    public async Task UI_Frame_Sent_On_Node_A_Is_Received_On_Node_B()
    {
        Skip.IfNot(await IsNetsimHealthy(),
            $"net-sim not healthy on {Host}:{NetsimWebPort}. Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait netsim'.");

        // Generous: this covers KISS-connect + net-sim client registration + afsk1200 TX
        // (~150 ms on the wire) + the receive poll, over docker net-sim on a shared self-hosted
        // runner. 15 s was too tight under runner load and flaked as OperationCanceled mid-receive
        // (it even "failed" on a docs-only commit — proof of flakiness, not regression).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        await using var sender = await KissTcpClient.ConnectAsync(Host, NodeAKissPort, cts.Token);
        await using var receiver = await KissTcpClient.ConnectAsync(Host, NodeBKissPort, cts.Token);

        // Give net-sim a moment to register both clients before we transmit.
        // afsk1200 is slow — TX time for a small frame is ~150ms on the wire.
        await Task.Delay(200, cts.Token);

        var outbound = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("PN0TST", 9),
            info: "Packet.NET → net-sim"u8);

        await sender.SendAsync(port: 0, KissCommand.Data, outbound.ToBytes(), cts.Token);

        // Pull from the receiver until we get a Data frame from net-sim
        // matching our payload, or the cancellation token fires.
        Ax25Frame? decoded = null;
        while (!cts.IsCancellationRequested && decoded is null)
        {
            var frames = await receiver.ReceiveAsync(cts.Token);
            foreach (var f in frames)
            {
                if (f.Command != KissCommand.Data)
                {
                    continue;
                }

                if (!Ax25Frame.TryParse(f.Payload, out var parsed))
                {
                    continue;
                }

                if (parsed.Source.Callsign == new Callsign("PN0TST", 9))
                {
                    decoded = parsed;
                    break;
                }
            }
        }

        decoded.Should().NotBeNull("expected our UI frame to arrive on node b within the timeout");
        decoded.Destination.Callsign.Should().Be(new Callsign("APRS", 0));
        decoded.Source.Callsign.Should().Be(new Callsign("PN0TST", 9));
        decoded.Info.ToArray().Should().Equal("Packet.NET → net-sim"u8.ToArray());
    }

    private static async Task<bool> IsNetsimHealthy()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var resp = await http.GetAsync($"http://{Host}:{NetsimWebPort}/healthz");
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await resp.Content.ReadAsStringAsync();
            return body.Trim() == "ok";
        }
        catch
        {
            return false;
        }
    }
}
