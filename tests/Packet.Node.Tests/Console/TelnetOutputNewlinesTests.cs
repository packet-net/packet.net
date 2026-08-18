using System.Text;
using Packet.Node.Core.Console;

namespace Packet.Node.Tests.Console;

/// <summary>
/// Outbound newline normalisation for the telnet connection - the companion to
/// <see cref="LineAssemblerTests"/> on the inbound side. A bare CR (the AX.25
/// line ending) renders as CR-LF so relayed node/BBS output advances the terminal
/// instead of overtyping it; existing CR-LF is left alone (idempotent); and
/// output with no terminator (a prompt) passes through verbatim so the cursor
/// stays put.
/// </summary>
public sealed class TelnetOutputNewlinesTests
{
    private static string Norm(string s, ref bool lastWasCr)
        => Encoding.ASCII.GetString(
            TelnetOutputNewlines.NormalizeToCrlf(Encoding.ASCII.GetBytes(s), ref lastWasCr));

    [Fact]
    public void Bare_CR_becomes_CRLF()
    {
        bool last = false;
        Norm("Type ? for Help\r", ref last).Should().Be("Type ? for Help\r\n");
        last.Should().BeTrue();
    }

    [Fact]
    public void Existing_CRLF_is_unchanged()
    {
        bool last = false;
        Norm("line\r\n", ref last).Should().Be("line\r\n");
        last.Should().BeFalse();
    }

    [Fact]
    public void Lone_LF_becomes_CRLF()
    {
        bool last = false;
        Norm("line\n", ref last).Should().Be("line\r\n");
    }

    [Fact]
    public void No_terminator_passes_through_so_prompts_keep_the_cursor()
    {
        bool last = false;
        Norm("GB7RDG>", ref last).Should().Be("GB7RDG>");
        last.Should().BeFalse();
    }

    [Fact]
    public void Two_bare_CR_lines_each_become_CRLF()
    {
        bool last = false;
        // The GB7RDG banner shape: CR-terminated lines back to back.
        Norm("...70cm. \rType ? for Help\r", ref last)
            .Should().Be("...70cm. \r\nType ? for Help\r\n");
    }

    [Fact]
    public void CRLF_split_across_chunks_does_not_double()
    {
        bool last = false;
        Norm("x\r", ref last).Should().Be("x\r\n");
        last.Should().BeTrue();
        // The LF opening the next chunk is the partner of the CR we already completed.
        Norm("\ny", ref last).Should().Be("y");
        last.Should().BeFalse();
    }

    [Fact]
    public void Non_newline_control_bytes_pass_through()
    {
        bool last = false;
        // A backspace echo sequence (BS, space, BS) must survive untouched.
        TelnetOutputNewlines.NormalizeToCrlf(new byte[] { 0x08, (byte)' ', 0x08 }, ref last)
            .Should().Equal(0x08, (byte)' ', 0x08);
    }
}
