using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// A transport fault must stop the listener cleanly. The inbound pump caught only
/// cancellation, so an enumerator fault (the serial pump and the NinoTNC dispatch
/// loop both complete their inbound channel with the terminal exception) killed
/// the pump silently: <c>IsRunning</c> stayed true, <c>StopAsync</c> rethrew the
/// IOException, and because <c>DisposeAsync</c> latched its disposed flag before
/// awaiting Stop, the lifecycle CTS and every session scheduler leaked
/// (packet-net/packet.net#696).
/// </summary>
public class Ax25ListenerPumpFaultTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);

    [Fact]
    public async Task A_transport_fault_stops_the_listener_without_throwing_out_of_stop_or_dispose()
    {
        var modem = new FaultingModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        await listener.StartAsync();
        await ListenerTestSupport.WaitFor(
            () => !listener.IsRunning, TimeSpan.FromSeconds(5),
            "the pump must mark the listener stopped when its transport faults");

        var stop = async () => await listener.StopAsync();
        await stop.Should().NotThrowAsync("a caller asking a faulted port to stop has got what it asked for");

        var dispose = async () => await listener.DisposeAsync();
        await dispose.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dispose_alone_is_enough_after_a_transport_fault()
    {
        // The leak path: DisposeAsync latches `disposed` before awaiting Stop, so
        // cleanup has to run in a finally or it never runs at all.
        var modem = new FaultingModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        await listener.StartAsync();
        await ListenerTestSupport.WaitFor(() => !listener.IsRunning, TimeSpan.FromSeconds(5));

        var dispose = async () => await listener.DisposeAsync();
        await dispose.Should().NotThrowAsync();

        // Disposal really happened: the listener refuses further work.
        var afterDispose = () => listener.MyCall.ToString();
        afterDispose.Should().NotThrow("MyCall is a plain read");
        var connect = async () => await listener.ConnectAsync(new Callsign("G7XYZ", 7));
        await connect.Should().ThrowAsync<ObjectDisposedException>();
    }

    /// <summary>A transport whose inbound stream faults the way a closed serial port does.</summary>
    private sealed class FaultingModem : IAx25Transport
    {
        private readonly bool alwaysFaults = true;

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (alwaysFaults)
            {
                throw new IOException("the port went away");
            }

            yield return new Ax25InboundFrame(ReadOnlyMemory<byte>.Empty, 0, DateTimeOffset.UtcNow);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
