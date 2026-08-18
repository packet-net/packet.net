using System.Text;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.Applications;

/// <summary>
/// The transport-agnostic core of the <c>pdn-app/1</c> wire: writes the connect header to the
/// app, then bridges a connected user's <see cref="INodeConnection"/> session to the app's
/// byte streams as line-oriented UTF-8 text. The same logic backs both local-session
/// transports - the spawn-per-connect process floor (<see cref="ExternalProcessApplication"/>,
/// streams = the child's stdio) and the long-running-socket rung
/// (<see cref="SocketApplication"/>, streams = a per-session socket) - so the wire is defined
/// once. See <c>docs/app-local-session-wire.md</c>.
/// </summary>
/// <remarks>
/// Two pumps run until the app closes its output (EOF on <paramref name="fromApp"/>) or the user
/// drops (both pumps race <see cref="INodeConnection.Completion"/>): inbound user lines →
/// <c>toApp</c> (terminator-normalised to a single <c>\n</c> via <see cref="LineAssembler"/>),
/// and the app's bytes → the user (decoded UTF-8 incrementally so a multi-byte char or a CR-LF
/// can split across reads, every <c>\n</c>/<c>\r</c>/<c>\r\n</c> translated to the transport's
/// newline). The bridge never closes the transport - the caller owns teardown (killing the
/// process / closing the socket), which is what finally signals EOF to the app.
/// </remarks>
internal static class AppSessionBridge
{
    // The newline fed to the app on its input - always a single LF, whatever the user's transport.
    private static readonly byte[] AppNewline = [(byte)'\n'];

    /// <summary>
    /// Build the <c>pdn-app/1</c> connect-header bytes. Every value is newline-sanitised so a
    /// value (notably user-supplied args) can't inject an extra header line.
    /// </summary>
    public static byte[] BuildHeader(string appId, NodeAppContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append("pdn-app: 1\n");
        sb.Append("id: ").Append(SanitiseValue(appId)).Append('\n');
        sb.Append("callsign: ").Append(SanitiseValue(ctx.Callsign)).Append('\n');
        sb.Append("transport: ").Append(TransportToken(ctx.Transport)).Append('\n');
        sb.Append("port: ").Append(string.IsNullOrEmpty(ctx.PortId) ? "-" : SanitiseValue(ctx.PortId)).Append('\n');
        sb.Append("sysop: ").Append(ctx.SysopElevated ? '1' : '0').Append('\n');
        sb.Append("args: ").Append(SanitiseValue(string.Join(' ', ctx.Args))).Append('\n');
        sb.Append('\n');   // blank line ends the header
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Write the header to <paramref name="toApp"/>, then bridge <paramref name="session"/> to the
    /// app until the app closes <paramref name="fromApp"/> (EOF) or the user drops. Returns then;
    /// the caller tears the transport down. The initial header write may throw (the app died
    /// instantly) - the caller maps that to an unavailable-app failure; the pumps themselves
    /// never throw on a normal close.
    /// </summary>
    public static async Task RunAsync(
        INodeConnection session, Stream toApp, Stream fromApp, byte[] header, CancellationToken cancellationToken = default)
    {
        // Best-effort header delivery. A one-shot app may have produced its output and already
        // closed its input by now (broken pipe), and a daemon may drop us - neither is fatal:
        // still forward whatever output the app produced, then return. The actual "app
        // unavailable" failures are the spawn / connect step, handled by the caller before here.
        try
        {
            await toApp.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await toApp.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // app closed its input before reading the header - proceed to drain its output.
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linked.Token;

        var feeder = PumpSessionToAppAsync(session, toApp, ct);
        var forwarder = PumpAppToSessionAsync(fromApp, session, ct);

        // The exchange ends when the app stops producing output (it exited / closed its output)
        // OR the user is gone (the forwarder also races session.Completion). Then cancel the
        // feeder; the caller's teardown closes the transport, which signals EOF to the app.
        await forwarder.ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        await SwallowAsync(feeder).ConfigureAwait(false);
    }

    // User → app: each completed inbound line (terminator stripped by the assembler) is written
    // followed by a single LF, so the app reads clean \n-terminated lines whatever the transport.
    private static async Task PumpSessionToAppAsync(INodeConnection session, Stream toApp, CancellationToken ct)
    {
        var assembler = new LineAssembler();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var readTask = session.ReadAsync(ct).AsTask();
                var done = await Task.WhenAny(readTask, session.Completion).ConfigureAwait(false);

                ReadOnlyMemory<byte> chunk;
                if (done != readTask)
                {
                    if (readTask.IsCompletedSuccessfully)
                    {
                        chunk = readTask.Result;   // a read also landed - deliver it, next loop sees the drop
                    }
                    else
                    {
                        break;   // peer gone before a read returned
                    }
                }
                else
                {
                    chunk = await readTask.ConfigureAwait(false);
                }

                if (chunk.IsEmpty)
                {
                    break;   // EOF
                }

                foreach (var line in assembler.Push(chunk))
                {
                    await toApp.WriteAsync(line, ct).ConfigureAwait(false);
                    await toApp.WriteAsync(AppNewline, ct).ConfigureAwait(false);
                }
                await toApp.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // teardown - normal.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The app closed its input / went away - nothing more to feed.
        }
    }

    // App → user: decode the app's bytes as UTF-8 (incrementally - a multi-byte char may split
    // across reads) and translate every \n / \r / \r-\n to the transport's newline (CR for AX.25
    // / NET-ROM, CR-LF for telnet). The \r-state carries across reads so a CR-LF on a buffer
    // boundary stays one line.
    private static async Task PumpAppToSessionAsync(Stream fromApp, INodeConnection session, CancellationToken ct)
    {
        var newline = session.TransportKind == NodeTransportKind.Telnet ? "\r\n" : "\r";
        var decoder = Encoding.UTF8.GetDecoder();
        var byteBuffer = new byte[2048];
        var charBuffer = new char[2048];
        bool pendingCr = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var readTask = fromApp.ReadAsync(byteBuffer.AsMemory(), ct).AsTask();
                var done = await Task.WhenAny(readTask, session.Completion).ConfigureAwait(false);
                if (done != readTask)
                {
                    break;   // user gone - stop forwarding
                }

                int n = await readTask.ConfigureAwait(false);
                if (n == 0)
                {
                    break;   // app closed its output (EOF)
                }

                int chars = decoder.GetChars(byteBuffer, 0, n, charBuffer, 0);
                var sb = new StringBuilder(chars + 16);
                for (int i = 0; i < chars; i++)
                {
                    char c = charBuffer[i];
                    if (c == '\r')
                    {
                        sb.Append(newline);
                        pendingCr = true;
                    }
                    else if (c == '\n')
                    {
                        if (!pendingCr)
                        {
                            sb.Append(newline);
                        }
                        pendingCr = false;
                    }
                    else
                    {
                        sb.Append(c);
                        pendingCr = false;
                    }
                }

                if (sb.Length > 0)
                {
                    await session.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // teardown - normal.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The session went away mid-write / the app transport faulted - done.
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { /* pump faults are handled in-pump; ignore here */ }
    }

    private static string TransportToken(NodeTransportKind kind) => kind switch
    {
        NodeTransportKind.Ax25 => "ax25",
        NodeTransportKind.NetRom => "netrom",
        NodeTransportKind.Telnet => "telnet",
        _ => "unknown",
    };

    // Strip control characters (notably CR/LF) from a header value so it stays on one line and
    // can't inject an extra header key.
    private static string SanitiseValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
