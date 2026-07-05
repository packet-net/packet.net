using System.Net;
using System.Net.Sockets;
using Packet.Kiss;

namespace Packet.Kiss.NinoTnc.Tests;

/// <summary>
/// Drives <see cref="NinoTncSerialPort"/> over the TCP-backed <c>ISerialPortIo</c>
/// (<c>NinoTncSerialPort.OpenTcp</c> → <c>KissSerialModem.OpenTcp</c> → <c>TcpSerialPortIo</c>)
/// against a loopback <see cref="TcpListener"/> standing in for the split-station head-end,
/// proving the full-control NinoTNC surface (here GETVER) round-trips over a raw socket —
/// distinct from the generic control-less <c>kiss-tcp</c> transport. Mirrors the scripted-IO
/// style of <see cref="NinoTncSerialPortTests"/>.
/// </summary>
public class NinoTncSerialPortTcpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OpenTcp_runs_GETVER_over_the_socket_and_returns_the_version()
    {
        using var head = new LoopbackHeadEnd();
        await using var nino = await NinoTncSerialPort.OpenTcp("127.0.0.1", head.Port);
        var server = await head.Accepted.WaitAsync(Timeout);

        var versionTask = nino.GetVersionAsync(Timeout);

        // Head-end: consume the GETVER command frame, then reply with the bare ASCII version on
        // raw KISS command byte 0xE0 (port 14 + command 0), exactly as the firmware does.
        await ReceiveSomeAsync(server);
        await server.SendAsync(KissEncoder.Encode(14, KissCommand.Data, "3.41"u8.ToArray()).AsMemory());

        (await versionTask).Should().Be("3.41");
    }

    private static async Task ReceiveSomeAsync(Socket socket)
    {
        var buffer = new byte[256];
        await socket.ReceiveAsync(buffer.AsMemory()).AsTask().WaitAsync(Timeout);
    }

    /// <summary>A loopback <see cref="TcpListener"/> standing in for the split-station head-end:
    /// the accepted socket is the head-end side the test scripts KISS frames onto.</summary>
    private sealed class LoopbackHeadEnd : IDisposable
    {
        private readonly TcpListener listener;

        public LoopbackHeadEnd()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Accepted = listener.AcceptSocketAsync();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public Task<Socket> Accepted { get; }

        public void Dispose()
        {
            listener.Stop();
            if (Accepted.IsCompletedSuccessfully)
            {
                Accepted.Result.Dispose();
            }
        }
    }
}
