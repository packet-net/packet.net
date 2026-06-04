using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Packet.Ax25;
using Packet.Core;
using Xunit;

namespace Packet.Interop.Tests.Xrouter;

/// <summary>
/// Interop scenarios against the XRouter container brought up by
/// <c>docker compose -f docker/compose.interop.yml up -d xrouter</c>.
/// </summary>
/// <remarks>
/// <para>
/// XRouter's AXUDP is a peer-pair link, not a generic listener. The
/// docker/xrouter/XROUTER.CFG configures the local UDP listener on
/// :8095 with a peer expected at 172.30.0.1:8094 — that's the docker
/// bridge gateway from inside the container, which is where
/// host-originated UDP traffic appears to come from.
/// </para>
/// <para>
/// Our test peer binds locally to UDP 8094 so its source endpoint
/// matches the configured peer, and sends AXUDP frames WITH FCS
/// (XRouter rejects FCS-less frames as "non-AXUDP" — verified during
/// the interop spike).
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
public class XrouterAxudpInterop
{
    private const string Host       = "127.0.0.1";
    private const int    AxudpPort  = 8095;
    private const int    PeerPort   = 8094;
    private const int    WebPort    = 8086;

    [SkippableFact]
    public async Task Xrouter_Container_Web_UI_Is_Reachable()
    {
        Skip.IfNot(await IsTcpPortReachable(Host, WebPort),
            $"XRouter web UI not reachable on {Host}:{WebPort}. Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait xrouter'.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync($"http://{Host}:{WebPort}/");
        response.IsSuccessStatusCode.Should().BeTrue($"XRouter web UI returned {(int)response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync();
        // XRouter self-identifies as "XrLin" on its web pages.
        body.Should().Contain("XrLin");
    }

    [SkippableFact]
    public async Task Sends_A_UI_Frame_With_Fcs_To_Xrouter_And_It_Is_Accepted_As_Valid_AXUDP()
    {
        Skip.IfNot(await IsTcpPortReachable(Host, WebPort),
            $"XRouter not reachable. Bring up the interop stack: 'docker compose -f docker/compose.interop.yml up -d --wait xrouter'.");

        var before = await GetAxudpValidCount();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // The peer port must match XROUTER.CFG's UDPREMOTE=8094, otherwise
        // XRouter classifies the inbound packets as unsolicited and drops them.
        using var socket = new Packet.Axudp.AxudpSocket(localPort: PeerPort);

        var frame = Ax25Frame.Ui(
            destination: new Callsign("APRS", 0),
            source:      new Callsign("PN0TST", 9),
            info:        "Packet.NET → XRouter (with FCS)"u8);

        await socket.SendAsync(new IPEndPoint(IPAddress.Loopback, AxudpPort), frame, cts.Token);

        // XRouter's stats counter takes a moment to tick, and the latency
        // varies under load — poll until it increments rather than betting
        // on a single fixed delay being long enough.
        var after = before;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            after = await GetAxudpValidCount();
            if (after > before) break;
            await Task.Delay(250, cts.Token);
        }

        after.Should().BeGreaterThan(before, "XRouter's 'valid AXUDP received' counter should increment");
    }

    /// <summary>
    /// Scrape XRouter's <c>stats axudp</c> output via the exec endpoint
    /// and pull out the "valid AXUDP received" count.
    /// </summary>
    private static async Task<int> GetAxudpValidCount()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var html = await http.GetStringAsync($"http://{Host}:{WebPort}/exec?cmd=stats+axudp");
        // The page renders as HTML; pull the AXUDP stats line out. Format
        // observed: "AXUDP stats: ... N valid AXUDP received ...".
        var match = System.Text.RegularExpressions.Regex.Match(html, @"(\d+)\s*valid AXUDP received");
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
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
