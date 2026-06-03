using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Packet.Core;
using Packet.Node.Core.Console;

namespace Packet.Node.Tests.Console;

public class NodeCommandParserTests
{
    [Theory]
    [InlineData("C M0LTE-1", "M0LTE-1")]
    [InlineData("CONNECT M0LTE-1", "M0LTE-1")]
    [InlineData("c m0lte-1", "M0LTE-1")]          // lowercase verb + call
    [InlineData("CONN G7XYZ", "G7XYZ")]           // unambiguous prefix
    [InlineData("  C   M0LTE   ", "M0LTE")]       // surrounding whitespace
    [InlineData("C M0LTE-1 via WIDE", "M0LTE-1")] // first token is the target
    public void Parses_connect_with_a_valid_callsign(string line, string expectedCall)
    {
        var cmd = NodeCommandParser.Parse(line);
        cmd.Should().BeOfType<ConnectCommand>()
            .Which.Target.Should().Be(Callsign.Parse(expectedCall));
    }

    [Theory]
    [InlineData("C")]            // missing callsign
    [InlineData("C  ")]
    [InlineData("CONNECT notacall!")]
    [InlineData("C M0LTE-99")]   // SSID out of range
    public void Connect_with_a_bad_or_missing_callsign_is_a_typed_error_not_an_exception(string line)
    {
        var cmd = NodeCommandParser.Parse(line);
        cmd.Should().BeOfType<MalformedConnect>();
    }

    [Theory]
    [InlineData("B")]
    [InlineData("BYE")]
    [InlineData("bye")]
    [InlineData("D")]
    [InlineData("DISCONNECT")]
    [InlineData("DISC")]
    public void Parses_bye_and_disconnect(string line)
    {
        NodeCommandParser.Parse(line).Should().BeOfType<ByeCommand>();
    }

    [Theory]
    [InlineData("N")]
    [InlineData("NODES")]
    [InlineData("nodes")]
    public void Parses_nodes(string line) => NodeCommandParser.Parse(line).Should().BeOfType<NodesCommand>();

    [Theory]
    [InlineData("I")]
    [InlineData("INFO")]
    public void Parses_info(string line) => NodeCommandParser.Parse(line).Should().BeOfType<InfoCommand>();

    [Theory]
    [InlineData("H")]
    [InlineData("HELP")]
    [InlineData("?")]
    public void Parses_help(string line) => NodeCommandParser.Parse(line).Should().BeOfType<HelpCommand>();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r")]
    public void Empty_line_is_EmptyCommand(string line) =>
        NodeCommandParser.Parse(line).Should().BeOfType<EmptyCommand>();

    [Theory]
    [InlineData("XYZZY")]
    [InlineData("frobnicate the gadget")]
    [InlineData("12345")]
    public void Unrecognised_verb_is_UnknownCommand(string line) =>
        NodeCommandParser.Parse(line).Should().BeOfType<UnknownCommand>();

    [Fact]
    public void Over_long_line_is_truncated_and_still_classified()
    {
        var huge = new string('Z', NodeCommandParser.MaxLineLength * 4);
        var cmd = NodeCommandParser.Parse(huge);
        cmd.Should().BeOfType<UnknownCommand>();
        ((UnknownCommand)cmd).Raw.Length.Should().BeLessThanOrEqualTo(NodeCommandParser.MaxLineLength);
    }

    [Fact]
    public void Null_line_is_EmptyCommand()
    {
        NodeCommandParser.Parse((string?)null).Should().BeOfType<EmptyCommand>();
    }

    // ── Properties (the fuzz contract, in-process) ──────────────────────

    [Property(MaxTest = 2000)]
    public void Never_throws_on_arbitrary_bytes(byte[] bytes)
    {
        bytes ??= [];
        var act = () => NodeCommandParser.Parse(bytes);
        act.Should().NotThrow();
    }

    [Property(MaxTest = 2000)]
    public void Never_throws_on_arbitrary_strings(string? text)
    {
        var act = () => NodeCommandParser.Parse(text);
        act.Should().NotThrow();
    }

    [Property(MaxTest = 2000)]
    public void A_parsed_Connect_always_carries_a_genuinely_valid_callsign(string? text)
    {
        // Whatever the input, if it parsed to Connect, the target must round-trip
        // through Callsign (no Connect ever holds garbage).
        if (NodeCommandParser.Parse(text) is ConnectCommand connect)
        {
            Callsign.TryParse(connect.Target.ToString(), out _).Should().BeTrue();
        }
    }

    [Property(MaxTest = 3000)]
    public void Random_non_command_text_never_becomes_a_spurious_Connect_or_Bye(NonNull<string> raw)
    {
        // A line that doesn't start with a command verb must not be misread as
        // Connect or Bye. We construct lines that can't be a verb prefix.
        var text = raw.Get;
        var firstTokenUpper = FirstToken(text).ToUpperInvariant();
        bool couldBeVerb =
            IsPrefixOf(firstTokenUpper, "CONNECT") || IsPrefixOf(firstTokenUpper, "BYE") ||
            IsPrefixOf(firstTokenUpper, "DISCONNECT") || IsPrefixOf(firstTokenUpper, "NODES") ||
            IsPrefixOf(firstTokenUpper, "INFO") || IsPrefixOf(firstTokenUpper, "HELP") ||
            firstTokenUpper == "?";

        var cmd = NodeCommandParser.Parse(text);
        if (!couldBeVerb)
        {
            cmd.Should().Match(c => c is UnknownCommand || c is EmptyCommand,
                "non-verb text must be Unknown or Empty, never a real command — got {0}", cmd.GetType().Name);
        }
    }

    private static string FirstToken(string s)
    {
        // Mirror the parser's TrimControl: strip leading/trailing control AND
        // whitespace before taking the first token, so the predicate classifies
        // the same line the parser sees (e.g. "\x1cb" trims to "b" = a Bye prefix).
        int start = 0, end = s.Length;
        while (start < end && (char.IsControl(s[start]) || char.IsWhiteSpace(s[start]))) start++;
        while (end > start && (char.IsControl(s[end - 1]) || char.IsWhiteSpace(s[end - 1]))) end--;
        var trimmed = s[start..end];
        int i = 0;
        while (i < trimmed.Length && !char.IsWhiteSpace(trimmed[i])) i++;
        return trimmed[..i];
    }

    private static bool IsPrefixOf(string candidate, string canonical) =>
        candidate.Length > 0 && candidate.Length <= canonical.Length &&
        canonical.StartsWith(candidate, StringComparison.Ordinal);
}
