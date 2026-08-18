namespace Packet.Node.Core.Console;

/// <summary>
/// Reassembles a byte stream into lines, bounded so a peer that never sends a
/// line terminator can't drive unbounded buffering. Fed chunk-by-chunk from a
/// <see cref="INodeConnection"/>; yields complete lines split on CR, LF, or
/// CR-LF. A line that reaches <see cref="MaxLineLength"/> without a terminator
/// is flushed as-is (and the overflow tail discarded up to the next terminator)
/// so the parser still sees a bounded line and the buffer never grows.
/// </summary>
/// <remarks>
/// Line-terminator handling is permissive on purpose: telnet clients send
/// CR-LF, raw TCP tools send LF, and AX.25 terminals send a bare CR. All three
/// resolve to "one line". An empty line (a lone terminator) is yielded as an
/// empty string so the console re-prompts. A telnet "CR alone" arrives as
/// CR-NUL (RFC 854); the NUL (also telnet's NOP) is dropped, never treated as
/// content - otherwise it would prepend to the following line.
/// </remarks>
public sealed class LineAssembler
{
    /// <summary>Cap on a single buffered line; mirrors
    /// <see cref="NodeCommandParser.MaxLineLength"/>.</summary>
    public int MaxLineLength { get; }

    private readonly List<byte> buffer = new();
    private bool overflowing;        // dropping bytes until the next terminator
    private bool lastWasCr;          // for CR-LF coalescing across chunks

    public LineAssembler(int maxLineLength = NodeCommandParser.MaxLineLength)
    {
        MaxLineLength = maxLineLength > 0 ? maxLineLength : NodeCommandParser.MaxLineLength;
    }

    /// <summary>
    /// Feed a chunk of inbound bytes; returns every complete line the chunk
    /// completed (possibly none, possibly several). Each returned value is the
    /// raw line bytes with the terminator stripped.
    /// </summary>
    public IEnumerable<byte[]> Push(ReadOnlyMemory<byte> chunk)
    {
        var lines = new List<byte[]>();
        var span = chunk.Span;
        foreach (var b in span)
        {
            // Telnet sends NUL both as NOP and as the second byte of "CR alone"
            // (CR-NUL, RFC 854) - it is never line content, so drop it. Without
            // this, a CR-NUL "Enter" leaves the NUL behind to prepend to the next
            // line, so a relayed command such as "/quit" reaches the peer as
            // "\0/quit" and isn't recognised.
            if (b == 0)
            {
                continue;
            }

            // Coalesce CR-LF: an LF immediately after a CR is swallowed (the CR
            // already ended the line).
            if (b == (byte)'\n' && lastWasCr)
            {
                lastWasCr = false;
                continue;
            }
            lastWasCr = b == (byte)'\r';

            if (b == (byte)'\r' || b == (byte)'\n')
            {
                lines.Add(buffer.ToArray());
                buffer.Clear();
                overflowing = false;
                continue;
            }

            // Backspace / DEL: line editing - erase the last buffered byte, so the
            // assembled line stays in step with the connection's server-side echo.
            if (b == 0x08 || b == 0x7f)
            {
                if (!overflowing && buffer.Count > 0)
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }
                continue;
            }

            if (overflowing)
            {
                continue;   // dropping the overflow tail until the next terminator
            }

            if (buffer.Count >= MaxLineLength)
            {
                // Flush what we have as a (truncated) line and drop the rest of
                // this over-long line until its terminator arrives.
                lines.Add(buffer.ToArray());
                buffer.Clear();
                overflowing = true;
                continue;
            }

            buffer.Add(b);
        }
        return lines;
    }
}
