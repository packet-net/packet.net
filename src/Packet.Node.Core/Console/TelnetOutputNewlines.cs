namespace Packet.Node.Core.Console;

/// <summary>
/// Normalises a byte stream bound for a telnet terminal so every line ending -
/// a bare CR (the AX.25 / packet convention), a lone LF, or a CR-LF - renders as
/// CR-LF. Stateful across calls (a CR that ends one chunk coalesces with an LF
/// that opens the next), so it can be applied chunk-by-chunk to a streamed
/// connection. The inverse of <see cref="LineAssembler"/>, which collapses the
/// same three terminators down to "one line" on the inbound side.
/// </summary>
/// <remarks>
/// A telnet terminal advances to a new line only on LF; a bare CR just returns
/// the cursor to column 0 of the current line. Relayed output from a connected
/// node/BBS uses bare-CR line endings - e.g. a banner ending <c>"…Help\r"</c> -
/// so without this the next thing written (or typed) overtypes that line. We only
/// <em>complete</em> CR/LF that were already sent; we never inject a break where
/// none was sent, so a prompt with no trailing terminator keeps the cursor in
/// place. Idempotent on input that is already CR-LF.
/// </remarks>
internal static class TelnetOutputNewlines
{
    /// <summary>
    /// Return <paramref name="input"/> with every line terminator rendered as
    /// CR-LF. <paramref name="lastWasCr"/> carries the CR-LF coalescing state
    /// across calls and is updated in place.
    /// </summary>
    public static byte[] NormalizeToCrlf(ReadOnlySpan<byte> input, ref bool lastWasCr)
    {
        // Worst case (all bare CR or LF) doubles the length.
        var output = new List<byte>(input.Length + 2);
        foreach (var b in input)
        {
            switch (b)
            {
                case (byte)'\r':
                    output.Add((byte)'\r');
                    output.Add((byte)'\n');
                    lastWasCr = true;
                    break;
                case (byte)'\n':
                    // An LF straight after a CR is the second half of a CR-LF we
                    // already emitted - swallow it. A lone LF becomes CR-LF.
                    if (!lastWasCr)
                    {
                        output.Add((byte)'\r');
                        output.Add((byte)'\n');
                    }
                    lastWasCr = false;
                    break;
                default:
                    output.Add(b);
                    lastWasCr = false;
                    break;
            }
        }
        return output.ToArray();
    }
}
