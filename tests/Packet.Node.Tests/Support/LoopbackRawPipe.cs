using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A loopback <see cref="TcpListener"/> standing in for a head-end's raw byte pipe (the transport
/// the head-end factory branches dial after resolving the inventory). Exposes the assigned
/// <see cref="Port"/> (put it in the stub inventory's <c>tcpPort</c>) and the accepted socket, plus a
/// tiny CCDI responder that answers each command with a prompt so a <c>TaitCcdiRadio.OpenTcp</c> +
/// <c>SetProgressMessagesAsync</c> handshake completes over the socket.
/// </summary>
public sealed class LoopbackRawPipe : IDisposable
{
    private readonly TcpListener listener;

    public LoopbackRawPipe()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Accepted = listener.AcceptSocketAsync();
    }

    /// <summary>The ephemeral loopback port the pipe listens on.</summary>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>The accepted client socket (the head-end side the test scripts bytes onto).</summary>
    public Task<Socket> Accepted { get; }

    /// <summary>
    /// Answer every inbound CCDI command with a <c>.</c> prompt (0x2E) — the transaction-complete
    /// marker <c>TaitCcdiRadio</c> waits for on a prompt-completed command like the progress-enable
    /// the radio factory issues. Runs until the socket closes. Await the returned task after tearing
    /// the radio down.
    /// </summary>
    public Task RespondCcdiPromptsAsync() => Task.Run(async () =>
    {
        var socket = await Accepted.ConfigureAwait(false);
        var buffer = new byte[256];
        while (true)
        {
            int read;
            try
            {
                read = await socket.ReceiveAsync(buffer.AsMemory()).ConfigureAwait(false);
            }
            catch
            {
                break; // socket torn down — done
            }
            if (read == 0)
            {
                break;
            }
            try
            {
                await socket.SendAsync(Encoding.Latin1.GetBytes(".").AsMemory()).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
        }
    });

    public void Dispose()
    {
        listener.Stop();
        if (Accepted.IsCompletedSuccessfully)
        {
            Accepted.Result.Dispose();
        }
    }
}
