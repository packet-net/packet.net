using System.Net;
using System.Net.Sockets;

namespace Packet.Node.Core.Console;

/// <summary>
/// Wraps an accepted TCP socket as a line-based <see cref="INodeConnection"/> —
/// the local telnet dial-in. <c>telnet &lt;host&gt; &lt;port&gt;</c> lands
/// straight on the prompt: no callsign, no KISS, no AX.25.
/// </summary>
/// <remarks>
/// A minimal telnet-protocol filter strips inbound IAC (0xFF) command sequences
/// so a real telnet client's option negotiation doesn't leak control bytes into
/// the command stream; raw <c>nc</c> clients (which send none) are unaffected.
/// We send no telnet options of our own — line-at-a-time cooked mode is the
/// client default and is what we want.
/// </remarks>
public sealed class TcpNodeConnection : INodeConnection
{
    private const byte Iac = 255;   // telnet "interpret as command"
    private const byte Sb = 250;    // subnegotiation begin
    private const byte Se = 240;    // subnegotiation end

    private readonly Socket socket;
    private readonly NetworkStream stream;
    private readonly byte[] readBuffer = new byte[2048];
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    // Telnet IAC parser state carried across reads.
    private bool inIac;
    private bool inSubneg;
    private int iacCommandBytesRemaining;

    public TcpNodeConnection(Socket socket)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        stream = new NetworkStream(socket, ownsSocket: false);
        PeerId = DescribeRemote(socket);
    }

    /// <inheritdoc/>
    public string PeerId { get; }

    /// <inheritdoc/>
    public NodeTransportKind TransportKind => NodeTransportKind.Telnet;

    /// <inheritdoc/>
    public Task Completion => completion.Task;

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                completion.TrySetResult();
                return ReadOnlyMemory<byte>.Empty;
            }

            if (n == 0)
            {
                completion.TrySetResult();
                return ReadOnlyMemory<byte>.Empty;   // peer closed
            }

            var filtered = StripTelnet(readBuffer.AsSpan(0, n));
            if (filtered.Length > 0)
            {
                return filtered;
            }
            // The chunk was pure telnet negotiation — read again.
        }
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            completion.TrySetResult();
        }
    }

    // Remove telnet IAC command sequences from the byte stream, tracking state
    // across calls. Handles: IAC <cmd> <opt> (3-byte WILL/WONT/DO/DONT), IAC IAC
    // (escaped 0xFF → literal 0xFF), and IAC SB ... IAC SE subnegotiation.
    private byte[] StripTelnet(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length);
        foreach (var b in input)
        {
            if (inSubneg)
            {
                if (inIac)
                {
                    inIac = false;
                    if (b == Se) inSubneg = false;   // IAC SE ends subnegotiation
                }
                else if (b == Iac)
                {
                    inIac = true;
                }
                continue;
            }

            if (iacCommandBytesRemaining > 0)
            {
                iacCommandBytesRemaining--;
                continue;   // consuming the option byte(s) of a WILL/WONT/DO/DONT
            }

            if (inIac)
            {
                inIac = false;
                switch (b)
                {
                    case Iac:
                        output.Add(Iac);   // escaped 0xFF → literal
                        break;
                    case Sb:
                        inSubneg = true;
                        break;
                    case >= 251 and <= 254:   // WILL/WONT/DO/DONT — one option byte follows
                        iacCommandBytesRemaining = 1;
                        break;
                    default:
                        // Other 2-byte IAC commands (NOP, etc.) — nothing follows.
                        break;
                }
                continue;
            }

            if (b == Iac)
            {
                inIac = true;
                continue;
            }

            output.Add(b);
        }
        return output.ToArray();
    }

    private static string DescribeRemote(Socket s)
    {
        try
        {
            return s.RemoteEndPoint is IPEndPoint ep ? $"{ep.Address}:{ep.Port}" : "telnet";
        }
        catch
        {
            return "telnet";
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        completion.TrySetResult();
        await stream.DisposeAsync().ConfigureAwait(false);
        try { socket.Shutdown(SocketShutdown.Both); } catch { /* already closed */ }
        socket.Dispose();
    }
}
