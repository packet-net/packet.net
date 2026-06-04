using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Packet.Ax25;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Xunit;

namespace Packet.Interop.Tests.Linbpq;

/// <summary>
/// Lightweight AXUDP smoke checks against the LinBPQ container's BPQAXIP (AXUDP)
/// listener. The real connected-mode AXUDP interop — a full SABM/UA + I-frame
/// session in both directions through the node host — lives in
/// <see cref="LinbpqViaAxudpConnectedMode"/>; this class just confirms the
/// container is up and that an AXUDP datagram in BPQ's required form is accepted.
/// </summary>
/// <remarks>
/// <para>
/// LinBPQ does NOT host a native KISS-TCP listener — that needs an external
/// softmodem (Direwolf / UZ7HO) bridged in — so KISS-TCP interop runs against
/// net-sim, which DOES expose KISS-TCP. BPQ's AX.25-over-IP path is the BPQAXIP
/// (AXIP / AXUDP) driver, exercised here and (for connected mode) in
/// <see cref="LinbpqViaAxudpConnectedMode"/>.
/// </para>
/// <para>
/// <b>FCS is mandatory on BPQAXIP/UDP</b> (source-verified in <c>bpqaxip.c</c>;
/// see <see cref="LinbpqViaAxudpConnectedMode"/> for the detail and the on-the-
/// wire regression guard). AXUDP always carries it, so the UI-frame smoke below
/// sends the FCS-bearing form. An FCS-less datagram would be silently dropped
/// by BPQ as "Invalid CRC" — sending one and asserting "no client-side
/// exception" (what a pre-#299 revision did) proved nothing, because the drop is
/// invisible to the sender. UDP is fire-and-forget, so this remains a framing /
/// liveness smoke; the connected-mode test is where acceptance is actually
/// asserted (BPQ's UA / I-frame replies).
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class LinbpqAxudpSmoke
{
    private const string Host = "127.0.0.1";
    private const int AxudpPort = 8093;
    private const int HttpPort  = 8008;

    [SkippableFact]
    public async Task LinBPQ_Container_Web_UI_Is_Reachable()
    {
        Skip.IfNot(await IsTcpPortReachable(Host, HttpPort),
            $"LinBPQ HTTP not reachable on {Host}:{HttpPort}. Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait linbpq'.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync($"http://{Host}:{HttpPort}/Node/NodeIndex.html");
        response.IsSuccessStatusCode.Should().BeTrue($"LinBPQ web UI returned {(int)response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("BPQ32 Node");
    }

    [SkippableFact]
    public async Task Sends_An_Fcs_Bearing_UI_Frame_Via_AXUDP_To_LinBPQ_Without_Error()
    {
        // The HTTP port serves as a liveness check — if it answers, LinBPQ is up
        // and the BPQAXIP driver is bound on its UDP port too (same daemon).
        Skip.IfNot(await IsTcpPortReachable(Host, HttpPort),
            $"LinBPQ not running (HTTP {Host}:{HttpPort} unreachable). Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait linbpq'.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new Packet.Axudp.AxudpSocket(localPort: 0);

        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source: new Callsign("PN0TST", 9),
            info: "Packet.NET v0 hello (AXUDP)"u8);

        // AXUDP always appends the 2-octet FCS — the form BPQAXIP/UDP requires (it
        // drops FCS-less datagrams as "Invalid CRC"). UDP is fire-and-forget; this is
        // a framing smoke. Real acceptance (UA / I-frame replies) is asserted in
        // LinbpqViaAxudpConnectedMode.
        await socket.SendAsync(new IPEndPoint(IPAddress.Loopback, AxudpPort), frame, cts.Token);
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
}
