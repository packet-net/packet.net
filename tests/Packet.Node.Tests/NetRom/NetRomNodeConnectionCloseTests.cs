using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Transport;
using Packet.NetRom.Wire;
using Packet.Node.Core.NetRom;
using Xunit;

namespace Packet.Node.Tests.NetRom;

/// <summary>
/// packet.net#850 over NET/ROM: disposing a <see cref="NetRomNodeConnection"/> (a console or app
/// closing a routed session) closes the circuit only once the far end has acknowledged what was
/// written; <see cref="NetRomNodeConnection.AbortAsync"/> (sysop kill, shutdown) closes at once.
/// </summary>
public sealed class NetRomNodeConnectionCloseTests
{
    private static readonly Callsign ANode = new("GB7AAA", 0);
    private static readonly Callsign BNode = new("GB7BBB", 0);
    private static readonly Callsign User = new("M0LTE", 0);

    private static readonly byte[] Tail =
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(1, 6).Select(i => $"line {i} of the tail\r")));

    [Fact]
    public async Task Dispose_delivers_what_was_written_then_closes_the_circuit()
    {
        using var pair = new Pair();
        var connection = new NetRomNodeConnection(pair.ACircuit, BNode);

        pair.Hold = true;   // a slow path: nothing is delivered until released
        await connection.WriteAsync(Tail);
        await connection.DisposeAsync();

        connection.Completion.IsCompleted.Should().BeTrue("the local side ends at once");
        pair.ACircuit.State.Should().Be(NetRomCircuitState.Connected, "the circuit waits for the far end's acks");

        await connection.WriteAsync(Encoding.ASCII.GetBytes("too late\r"));

        pair.Hold = false;
        pair.Pump();

        pair.BReceivedWhenClosed.Should().Equal(Tail, "everything written before the close arrived before the disconnect");
        pair.ACircuit.State.Should().Be(NetRomCircuitState.Disconnected);
    }

    [Fact]
    public async Task Abort_closes_at_once()
    {
        using var pair = new Pair();
        var connection = new NetRomNodeConnection(pair.ACircuit, BNode);

        pair.Hold = true;
        await connection.WriteAsync(Tail);
        await connection.AbortAsync();

        pair.ACircuit.State.Should().Be(NetRomCircuitState.Disconnecting, "the Disconnect Request goes now");
        pair.Hold = false;
        pair.Pump();
        pair.ACircuit.State.Should().Be(NetRomCircuitState.Disconnected);
    }

    // Two circuit managers back to back with a connected circuit A->B. Datagrams queue on a wire
    // that is pumped explicitly; while held, nothing is pumped.
    private sealed class Pair : IDisposable
    {
        private readonly Queue<(CircuitManager To, NetRomPacket Packet)> wire = new();
        private readonly CircuitManager a;
        private readonly CircuitManager b;
        private readonly List<byte> bReceived = new();

        public Pair()
        {
            var time = new FakeTimeProvider();
            var opts = new NetRomCircuitOptions { WindowSize = 2 };
            a = new CircuitManager(ANode, opts, time);
            b = new CircuitManager(BNode, opts, time);
            a.SendPacket = p => wire.Enqueue((b, p));
            b.SendPacket = p => wire.Enqueue((a, p));
            b.IncomingCircuit += (_, e) =>
            {
                e.Circuit.DataReceived += d => bReceived.AddRange(d.ToArray());
                e.Circuit.Closed += _ => BReceivedWhenClosed = bReceived.ToArray();
                CircuitManager.AcceptIncoming(e);
            };

            ACircuit = a.OpenCircuit(BNode);
            ACircuit.Connect(User);
            Pump();
            ACircuit.State.Should().Be(NetRomCircuitState.Connected);
        }

        public NetRomCircuit ACircuit { get; }
        public bool Hold { get; set; }
        public byte[]? BReceivedWhenClosed { get; private set; }

        public void Dispose()
        {
            a.Dispose();
            b.Dispose();
        }

        public void Pump()
        {
            for (int guard = 0; guard < 10_000 && !Hold && wire.Count > 0; guard++)
            {
                var (to, packet) = wire.Dequeue();
                to.OnPacket(packet);
            }
        }
    }
}
