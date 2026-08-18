using System.Net;
using System.Net.Sockets;

namespace Packet.Node.Core.Console;

/// <summary>
/// Wraps an accepted TCP socket as a line-based <see cref="INodeConnection"/> -
/// the local telnet dial-in. <c>telnet &lt;host&gt; &lt;port&gt;</c> lands
/// straight on the prompt: no callsign, no KISS, no AX.25.
/// </summary>
/// <remarks>
/// Telnet line discipline: on connect we send <c>WILL ECHO</c> +
/// <c>WILL SUPPRESS-GO-AHEAD</c> (<see cref="NegotiateAsync"/>) and echo received
/// input ourselves, so typing is visible on every client - including raw ones
/// (<c>plink -raw</c>, <c>nc</c>) that do no local echo of their own. Inbound IAC
/// (0xFF) command sequences are stripped so a client's option negotiation never
/// leaks control bytes into the command stream.
/// </remarks>
public sealed class TcpNodeConnection : INodeConnection
{
    private const byte Iac = 255;   // telnet "interpret as command"
    private const byte Sb = 250;    // subnegotiation begin
    private const byte Se = 240;    // subnegotiation end
    private const byte Will = 251;  // telnet WILL
    private const byte OptEcho = 1; // option: ECHO
    private const byte OptSga = 3;  // option: SUPPRESS-GO-AHEAD

    private readonly Socket socket;
    private readonly NetworkStream stream;
    private readonly byte[] readBuffer = new byte[2048];
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    // Telnet IAC parser state carried across reads.
    private bool inIac;
    private bool inSubneg;
    private int iacCommandBytesRemaining;

    // Server-side echo state, carried across reads.
    private int echoLineCol;      // printable chars on the current input line (for BS)
    private bool echoLastWasCr;   // coalesce CR-LF in the echoed stream

    // Outbound newline-normalisation state, carried across writes.
    private bool outLastWasCr;    // coalesce CR-LF in the normalised terminal output

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

    /// <summary>
    /// Send our telnet option negotiation - <c>WILL ECHO</c> + <c>WILL
    /// SUPPRESS-GO-AHEAD</c> - which puts a compliant client into
    /// character-at-a-time mode with its local echo off (we echo instead). Call
    /// once, before the banner. Raw clients ignore these few bytes and still get
    /// our echo.
    /// </summary>
    public async Task NegotiateAsync(CancellationToken cancellationToken = default)
    {
        byte[] negotiation = [Iac, Will, OptEcho, Iac, Will, OptSga];
        await WriteRawAsync(negotiation, cancellationToken).ConfigureAwait(false);
    }

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
                await EchoAsync(filtered, cancellationToken).ConfigureAwait(false);
                return filtered;
            }
            // The chunk was pure telnet negotiation - read again.
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Terminal output is newline-normalised (see <see cref="TelnetOutputNewlines"/>):
    /// a bare CR or lone LF is completed to CR-LF so relayed AX.25 content - a
    /// connected node's banner ends a line with a lone CR - advances the terminal
    /// instead of overtyping it. Idempotent on CR-LF; a prompt with no terminator
    /// is left untouched.
    /// </remarks>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        => WriteInternalAsync(bytes, normalizeNewlines: true, cancellationToken);

    // Write verbatim, no newline translation - for telnet control sequences (IAC
    // negotiation) and the server-side echo, which BuildEcho already CR-LF-ifies.
    private ValueTask WriteRawAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        => WriteInternalAsync(bytes, normalizeNewlines: false, cancellationToken);

    private async ValueTask WriteInternalAsync(ReadOnlyMemory<byte> bytes, bool normalizeNewlines, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        // Serialise writes: server-side echo (from the read path) and command
        // output / relayed data (from the service) can otherwise interleave on
        // the single stream during a connect-out relay. Normalisation runs under
        // the lock too, so outLastWasCr advances in write order.
        try
        {
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            ReadOnlyMemory<byte> payload = bytes;
            if (normalizeNewlines)
            {
                payload = TelnetOutputNewlines.NormalizeToCrlf(bytes.Span, ref outLastWasCr);
            }
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            completion.TrySetResult();
        }
        finally
        {
            writeLock.Release();
        }
    }

    // Echo received input back to the client. Printable bytes echo as-is; CR (or
    // a lone LF) becomes CR-LF; backspace / DEL erases the last echoed character
    // (only when there is one on the current line, so it can't chew into the
    // prompt). Best-effort - an echo write failure is swallowed by WriteAsync.
    private async ValueTask EchoAsync(ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        var echo = BuildEcho(input.Span);
        if (echo.Length > 0)
        {
            await WriteRawAsync(echo, ct).ConfigureAwait(false);
        }
    }

    private byte[] BuildEcho(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length + 2);
        foreach (var b in input)
        {
            switch (b)
            {
                case (byte)'\r':
                    output.Add((byte)'\r');
                    output.Add((byte)'\n');
                    echoLineCol = 0;
                    echoLastWasCr = true;
                    break;
                case (byte)'\n':
                    if (!echoLastWasCr)
                    {
                        output.Add((byte)'\r');
                        output.Add((byte)'\n');
                        echoLineCol = 0;
                    }
                    echoLastWasCr = false;
                    break;
                case 0x08:   // BS
                case 0x7f:   // DEL
                    if (echoLineCol > 0)
                    {
                        output.Add(0x08);
                        output.Add((byte)' ');
                        output.Add(0x08);
                        echoLineCol--;
                    }
                    echoLastWasCr = false;
                    break;
                default:
                    if (b >= 0x20)   // printable (incl. UTF-8 high bytes)
                    {
                        output.Add(b);
                        echoLineCol++;
                    }
                    // other control characters are not echoed
                    echoLastWasCr = false;
                    break;
            }
        }
        return output.ToArray();
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
                    if (b == Se)
                    {
                        inSubneg = false;   // IAC SE ends subnegotiation
                    }
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
                    case >= 251 and <= 254:   // WILL/WONT/DO/DONT - one option byte follows
                        iacCommandBytesRemaining = 1;
                        break;
                    default:
                        // Other 2-byte IAC commands (NOP, etc.) - nothing follows.
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
        writeLock.Dispose();
    }
}
