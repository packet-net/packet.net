using System.Text;
using Packet.Node.Core.Console;

namespace Packet.Node.Tests.Console;

/// <summary>
/// The in-memory duplex used by the browser command console. The key behaviour under test is the
/// <c>normalizeAppOutputToCrlf</c> seam: the appEnd is the terminal-bound direction, so its writes
/// must complete bare-CR / lone-LF endings to CR-LF (so a relayed BBS/node greeting advances the
/// xterm line instead of leaving the cursor at column 0 - the reported iPhone symptom), exactly as
/// the real telnet listener's TcpNodeConnection does. The userEnd (keystroke/input direction) must
/// stay a raw pipe. Default construction stays raw on both ends for the other callers.
/// </summary>
public sealed class LoopbackNodeConnectionTests
{
    private static async Task<string> ReadAllAsciiAsync(INodeConnection c, int reads)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < reads; i++)
        {
            var chunk = await c.ReadAsync(CancellationToken.None).ConfigureAwait(true);
            if (chunk.IsEmpty)
            {
                break;
            }

            sb.Append(Encoding.ASCII.GetString(chunk.Span));
        }
        return sb.ToString();
    }

    [Fact]
    public async Task AppEnd_normalizes_bare_CR_output_to_CRLF_when_requested()
    {
        var (appEnd, userEnd) = LoopbackNodeConnection.CreatePair(
            "console", NodeTransportKind.Telnet, "console", NodeTransportKind.Telnet,
            normalizeAppOutputToCrlf: true);

        // A connected BBS greeting + an unterminated prompt, AX.25-style (bare CR endings).
        await appEnd.WriteAsync(Encoding.ASCII.GetBytes("Welcome to GB7XYZ\rEnter your call: "),
            CancellationToken.None);

        var got = await ReadAllAsciiAsync(userEnd, 1);
        // The line advances (CR-LF), and the unterminated prompt keeps the cursor in place.
        got.Should().Be("Welcome to GB7XYZ\r\nEnter your call: ");
    }

    [Fact]
    public async Task AppEnd_normalization_is_idempotent_on_existing_CRLF()
    {
        var (appEnd, userEnd) = LoopbackNodeConnection.CreatePair(
            "console", NodeTransportKind.Telnet, "console", NodeTransportKind.Telnet,
            normalizeAppOutputToCrlf: true);

        // The node's own replies are already CR-LF - they must not become CR-CR-LF.
        await appEnd.WriteAsync(Encoding.ASCII.GetBytes("ports\r\n"), CancellationToken.None);

        (await ReadAllAsciiAsync(userEnd, 1)).Should().Be("ports\r\n");
    }

    [Fact]
    public async Task UserEnd_input_is_never_normalized()
    {
        var (appEnd, userEnd) = LoopbackNodeConnection.CreatePair(
            "console", NodeTransportKind.Telnet, "console", NodeTransportKind.Telnet,
            normalizeAppOutputToCrlf: true);

        // The keystroke direction (Enter = a bare CR) stays raw; the command service's LineAssembler
        // owns inbound line splitting. Normalising input here would be wrong.
        await userEnd.WriteAsync(Encoding.ASCII.GetBytes("ports\r"), CancellationToken.None);

        (await ReadAllAsciiAsync(appEnd, 1)).Should().Be("ports\r");
    }

    [Fact]
    public async Task Default_pair_is_a_raw_pipe_on_both_ends()
    {
        var (appEnd, userEnd) = LoopbackNodeConnection.CreatePair(
            "a", NodeTransportKind.Ax25, "b", NodeTransportKind.Ax25);

        await appEnd.WriteAsync(Encoding.ASCII.GetBytes("x\ry\r"), CancellationToken.None);
        (await ReadAllAsciiAsync(userEnd, 1)).Should().Be("x\ry\r");
    }
}
