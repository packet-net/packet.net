using System.Text;
using Packet.Core;
using Packet.Node.Core.Console;

namespace Packet.Node.Tests.Console;

/// <summary>
/// The <c>C[onnect] [port] &lt;call&gt;</c> grammar (a 1-indexed port may precede the callsign,
/// XRouter/BPQ convention) and the in-memory <see cref="LoopbackNodeConnection"/> that backs a
/// local app crossconnect. Pure units — no supervisor.
/// </summary>
[Trait("Category", "Node")]
public sealed class ConnectParsingTests
{
    [Theory]
    // A leading integer with a callsign after it is a port.
    [InlineData("C 1 G0ABC-2", "G0ABC-2", 1)]
    [InlineData("CONNECT 3 M0LTE", "M0LTE", 3)]
    [InlineData("c 12 gb7rdg", "GB7RDG", 12)]            // range is the router's call, not the parser's
    // No leading port: a plain callsign (the router decides app-vs-default).
    [InlineData("C G0ABC-2", "G0ABC-2", null)]
    [InlineData("C GB7RDG-4", "GB7RDG-4", null)]
    // A single digit-leading token is a callsign, not a port (no second token).
    [InlineData("C 2E0ABC", "2E0ABC", null)]
    // A via-path / trailing extras after a bare callsign are ignored (unchanged behaviour).
    [InlineData("C M0LTE VIA RELAY", "M0LTE", null)]
    public void Connect_parses_optional_leading_port(string line, string expectedCall, int? expectedPort)
    {
        var cmd = Assert.IsType<ConnectCommand>(NodeCommandParser.Parse(line));
        Assert.Equal(Callsign.Parse(expectedCall), cmd.Target);
        Assert.Equal(expectedPort, cmd.Port);
    }

    [Theory]
    [InlineData("C")]                  // no argument at all
    [InlineData("C 1 NOTACALL!")]      // port present, target unparseable
    [InlineData("C @@@")]              // single bad token
    public void Connect_with_no_valid_target_is_malformed(string line)
    {
        Assert.IsType<MalformedConnect>(NodeCommandParser.Parse(line));
    }

    [Fact]
    public async Task Loopback_pair_carries_bytes_both_ways_and_labels_each_end()
    {
        var (appEnd, userEnd) = LoopbackNodeConnection.CreatePair(
            appPeerId: "G0AAA", appKind: NodeTransportKind.Ax25,
            userPeerId: "GB7RDG-4", userKind: NodeTransportKind.Ax25);

        // The app end is labelled with the caller; the user end with the app SSID.
        Assert.Equal("G0AAA", appEnd.PeerId);
        Assert.Equal("GB7RDG-4", userEnd.PeerId);

        // user → app
        await userEnd.WriteAsync("hi app"u8.ToArray());
        var atApp = await appEnd.ReadAsync();
        Assert.Equal("hi app", Encoding.UTF8.GetString(atApp.Span));

        // app → user
        await appEnd.WriteAsync("hi caller"u8.ToArray());
        var atUser = await userEnd.ReadAsync();
        Assert.Equal("hi caller", Encoding.UTF8.GetString(atUser.Span));
    }

    [Fact]
    public async Task Disposing_one_loopback_end_completes_both_and_EOFs_the_peer()
    {
        var (appEnd, userEnd) = LoopbackNodeConnection.CreatePair("G0AAA", NodeTransportKind.Ax25, "GB7RDG-4", NodeTransportKind.Ax25);

        await userEnd.DisposeAsync();   // the caller drops

        // Shared completion fires for both ends, and the app end reads EOF.
        await appEnd.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(userEnd.Completion.IsCompleted);
        var eof = await appEnd.ReadAsync();
        Assert.True(eof.IsEmpty);
    }
}
