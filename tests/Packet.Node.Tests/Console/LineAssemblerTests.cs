using System.Linq;
using System.Text;
using Packet.Node.Core.Console;

namespace Packet.Node.Tests.Console;

/// <summary>
/// Line-editing behaviour of <see cref="LineAssembler"/> - in particular
/// backspace/DEL handling, which must stay in step with the telnet connection's
/// server-side echo so the parsed line matches what the user sees.
/// </summary>
public sealed class LineAssemblerTests
{
    [Fact]
    public void Backspace_erases_the_previous_buffered_character()
    {
        var a = new LineAssembler();
        // "AB" <BS> "C" <CR>  ->  the line is "AC"
        var line = a.Push(new byte[] { (byte)'A', (byte)'B', 0x08, (byte)'C', (byte)'\r' }).Single();
        Encoding.ASCII.GetString(line).Should().Be("AC");
    }

    [Fact]
    public void Del_erases_too_and_backspace_on_an_empty_line_is_a_noop()
    {
        var a = new LineAssembler();
        // <BS>(noop) 'X' <DEL>(erase X) 'Y' <LF>  ->  "Y"
        var line = a.Push(new byte[] { 0x08, (byte)'X', 0x7f, (byte)'Y', (byte)'\n' }).Single();
        Encoding.ASCII.GetString(line).Should().Be("Y");
    }

    [Fact]
    public void Backspace_does_not_cross_a_line_boundary()
    {
        var a = new LineAssembler();
        // "A" <CR> then <BS> 'B' <CR>: the BS after the terminator has nothing to
        // erase, so the second line is just "B".
        var lines = a.Push(new byte[] { (byte)'A', (byte)'\r', 0x08, (byte)'B', (byte)'\r' }).ToList();
        lines.Should().HaveCount(2);
        Encoding.ASCII.GetString(lines[0]).Should().Be("A");
        Encoding.ASCII.GetString(lines[1]).Should().Be("B");
    }

    [Fact]
    public void Cr_nul_enter_does_not_leak_a_nul_onto_the_next_line()
    {
        var a = new LineAssembler();
        // A telnet client that sends "CR alone" as CR-NUL (RFC 854). Two such lines
        // must yield exactly "a" and "b" - the NUL must NOT prepend to "b" (that leak
        // is what made a relayed "/quit" arrive at BPQ as "\0/quit" and be ignored).
        var lines = a.Push(new byte[] { (byte)'a', (byte)'\r', 0x00, (byte)'b', (byte)'\r', 0x00 }).ToList();
        lines.Should().HaveCount(2);
        Encoding.ASCII.GetString(lines[0]).Should().Be("a");
        Encoding.ASCII.GetString(lines[1]).Should().Be("b");
    }

    [Fact]
    public void A_stray_nul_is_dropped_mid_line()
    {
        var a = new LineAssembler();
        // NUL is telnet NOP - never line content, even mid-line.
        var line = a.Push(new byte[] { (byte)'a', 0x00, (byte)'b', (byte)'\r' }).Single();
        Encoding.ASCII.GetString(line).Should().Be("ab");
    }

    [Fact]
    public void Cr_nul_split_across_chunks_still_drops_the_nul()
    {
        var a = new LineAssembler();
        // The CR ends one chunk; the NUL leads the next. Cross-chunk, the NUL must
        // still be dropped rather than becoming the first byte of the next line.
        Encoding.ASCII.GetString(a.Push(new byte[] { (byte)'a', (byte)'\r' }).Single()).Should().Be("a");
        Encoding.ASCII.GetString(a.Push(new byte[] { 0x00, (byte)'b', (byte)'\r' }).Single()).Should().Be("b");
    }
}
