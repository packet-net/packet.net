using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Console;
using Packet.Node.Core.Transports;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// #944: a connect to a station the port's transport has no route to fails at once with an
/// error naming the peer and the port, instead of timing out after N2 x T1 as "peer refused".
/// </summary>
public sealed class RouteAwareConnectTests
{
    private static readonly Callsign Node = new("M0LTE", 1);

    private sealed class NoRoutes : IRouteAwareTransport
    {
        public bool HasRouteTo(Callsign destination) => false;
    }

    [Fact]
    public async Task A_dial_with_no_route_fails_promptly_naming_the_peer_and_port_and_sends_nothing()
    {
        var (ours, theirs) = InMemoryRadio.CreatePair();
        await using var listener = new Ax25Listener(ours, new Ax25ListenerOptions { MyCall = Node }, TimeProvider.System);
        await listener.StartAsync();
        var connector = new Ax25OutboundConnector("axudp", listener, routes: new NoRoutes());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var dial = async () => await connector.ConnectAsync(new Callsign("SA6DAZ", 7), cts.Token);

        var ex = (await dial.Should().ThrowAsync<NoRouteToPeerException>()).Which;
        ex.Message.Should().Contain("SA6DAZ-7").And.Contain("axudp").And.Contain("peers entry");
        cts.IsCancellationRequested.Should().BeFalse("it failed immediately, not by timing out");

        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var heard = 0;
        try
        {
            await foreach (var _ in theirs.ReceiveAsync(quiet.Token))
            {
                heard++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        heard.Should().Be(0, "no SABM or XID left the port");
    }
}
