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

    // ─── over-RF sysop verbs (auth part 4) ──────────────────────────────

    [Theory]
    [InlineData("SYSOP 123456", "123456", null)]
    [InlineData("sysop 123456", "123456", null)]
    [InlineData("SY 123456", "123456", null)]                 // unambiguous SY prefix
    [InlineData("SYSOP alice 654321", "alice", "654321")]     // telnet user+code form
    [InlineData("SYSOP", null, null)]                         // no args → service shows usage
    public void Parses_sysop_tokens(string line, string? t1, string? t2)
    {
        var cmd = NodeCommandParser.Parse(line).Should().BeOfType<SysopCommand>().Subject;
        cmd.Token1.Should().Be(t1);
        cmd.Token2.Should().Be(t2);
    }

    [Theory]
    [InlineData("SESSIONS")]
    [InlineData("sessions")]
    [InlineData("SE")]   // unambiguous SE prefix
    public void Parses_sessions(string line) =>
        NodeCommandParser.Parse(line).Should().BeOfType<SessionsCommand>();

    [Fact]
    public void Bare_S_is_ambiguous_and_not_a_sysop_or_sessions_command()
    {
        // "S" alone is a prefix of BOTH SYSOP and SESSIONS — it must NOT silently trigger
        // either (especially not the auth command). It falls through to Unknown.
        NodeCommandParser.Parse("S").Should().BeOfType<UnknownCommand>();
        NodeCommandParser.Parse("S 123456").Should().BeOfType<UnknownCommand>();
    }

    [Theory]
    [InlineData("KICK gb7rdg:M0LTE-1", "gb7rdg:M0LTE-1")]
    [InlineData("kick gb7rdg:M0LTE-1 extra", "gb7rdg:M0LTE-1")]
    public void Parses_kick(string line, string id) =>
        NodeCommandParser.Parse(line).Should().BeOfType<KickCommand>().Which.SessionId.Should().Be(id);

    [Theory]
    [InlineData("KICK")]
    [InlineData("KICK   ")]
    public void Kick_without_an_id_is_malformed(string line) =>
        NodeCommandParser.Parse(line).Should().BeOfType<MalformedKick>();

    [Theory]
    [InlineData("PORT gb7rdg UP", "gb7rdg", true)]
    [InlineData("PORT gb7rdg DOWN", "gb7rdg", false)]
    [InlineData("port gb7rdg down", "gb7rdg", false)]
    public void Parses_port_power(string line, string id, bool up)
    {
        var cmd = NodeCommandParser.Parse(line).Should().BeOfType<PortPowerCommand>().Subject;
        cmd.PortId.Should().Be(id);
        cmd.Up.Should().Be(up);
    }

    [Theory]
    [InlineData("PORT gb7rdg")]
    [InlineData("PORT gb7rdg SIDEWAYS")]
    public void Port_with_args_but_no_valid_state_is_malformed(string line) =>
        NodeCommandParser.Parse(line).Should().BeOfType<MalformedPort>();

    [Theory]
    [InlineData("PORTS")]
    [InlineData("ports")]
    [InlineData("PORT")]   // bare PORT lists too (PORT is a prefix of PORTS); the sysop form needs args
    [InlineData("PO")]     // unambiguous prefix of the PORT(S) family
    [InlineData("P")]
    public void Parses_ports_listing(string line) =>
        NodeCommandParser.Parse(line).Should().BeOfType<PortsCommand>();

    [Theory]
    [InlineData("RELOAD")]
    [InlineData("reload")]
    [InlineData("REL")]   // unambiguous prefix
    public void Parses_reload(string line) =>
        NodeCommandParser.Parse(line).Should().BeOfType<ReloadCommand>();

    // ─── MH — the MHeard log ────────────────────────────────────────────

    [Theory]
    [InlineData("MH")]
    [InlineData("mh")]
    [InlineData("MHE")]      // a prefix of MHEARD from the ≥2-char MH stem
    [InlineData("MHEARD")]
    public void Bare_mh_is_node_wide(string line) =>
        NodeCommandParser.Parse(line).Should().BeOfType<MhCommand>().Which.PortId.Should().BeNull();

    [Theory]
    [InlineData("MH vhf", "vhf")]
    [InlineData("mh hf", "hf")]
    [InlineData("MH vhf extra", "vhf")]   // first token after MH is the port; extras ignored
    public void Mh_with_a_port_is_per_port(string line, string port) =>
        NodeCommandParser.Parse(line).Should().BeOfType<MhCommand>().Which.PortId.Should().Be(port);

    [Fact]
    public void Bare_M_does_not_trigger_mh()
        // A single "M" is not enough — the MH stem needs ≥2 chars, so a lone M is just unknown.
        => NodeCommandParser.Parse("M").Should().BeOfType<UnknownCommand>();

    // ─── CAP — the per-peer capability cache ────────────────────────────

    [Theory]
    [InlineData("CAP")]
    [InlineData("caps")]
    [InlineData("CAPS")]
    [InlineData("CAPABILITIES")]
    [InlineData("CA")]    // unambiguous ≥2-char prefix of the CAP family
    [InlineData("CAPA")]  // a longer prefix of CAPABILITIES
    public void Bare_cap_lists_the_capability_cache(string line) =>
        NodeCommandParser.Parse(line).Should().BeOfType<CapabilitiesCommand>();

    [Fact]
    public void Bare_C_is_still_connect_not_cap()
    {
        // "C" alone is a prefix of BOTH CONNECT and the CAP family; it must stay CONNECT (and
        // therefore be MalformedConnect with no callsign), never become a CAP command.
        NodeCommandParser.Parse("C").Should().BeOfType<MalformedConnect>();
        NodeCommandParser.Parse("C M0LTE-1").Should().BeOfType<ConnectCommand>();
    }

    [Theory]
    [InlineData("CAP CLEAR gb7rdg:M0LTE-1", "gb7rdg:M0LTE-1")]
    [InlineData("cap clear gb7rdg:M0LTE-1", "gb7rdg:M0LTE-1")]
    [InlineData("CAPS CLEAR gb7rdg:M0LTE-1 extra", "gb7rdg:M0LTE-1")]  // extras after the id are ignored
    public void Parses_cap_clear(string line, string target) =>
        NodeCommandParser.Parse(line).Should().BeOfType<ClearCapabilityCommand>().Which.Target.Should().Be(target);

    [Theory]
    [InlineData("CAP CLEAR")]           // CLEAR with no id
    [InlineData("CAP CLEAR   ")]
    [InlineData("CAP FROBNICATE")]      // an unknown sub-verb
    [InlineData("CAP gb7rdg:M0LTE-1")]  // an arg that isn't CLEAR
    public void Cap_with_a_bad_argument_is_malformed(string line) =>
        NodeCommandParser.Parse(line).Should().BeOfType<MalformedCapability>();

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
            firstTokenUpper == "?" ||
            // The over-RF sysop verbs (auth part 4). SYSOP/SESSIONS require ≥2 chars (bare
            // "S" is ambiguous and falls through to Unknown — see the parser); KICK/PORT/
            // RELOAD use the usual unambiguous-prefix rule.
            (firstTokenUpper.Length >= 2 &&
                (IsPrefixOf(firstTokenUpper, "SYSOP") || IsPrefixOf(firstTokenUpper, "SESSIONS"))) ||
            // The CAP family: a ≥2-char CA-stem prefix of CAPABILITIES, or the plural "CAPS"
            // (mirrors the parser — bare "C" stays CONNECT, never CAP).
            (firstTokenUpper.Length >= 2 &&
                (IsPrefixOf(firstTokenUpper, "CAPABILITIES") || firstTokenUpper == "CAPS")) ||
            // MH — the MHeard log verb (#454). The parser accepts a ≥2-char prefix of MHEARD
            // (so MH/MHE/…/MHEARD all parse; bare "M" stays Unknown — see the parser).
            (firstTokenUpper.Length >= 2 && IsPrefixOf(firstTokenUpper, "MHEARD")) ||
            IsPrefixOf(firstTokenUpper, "KICK") || IsPrefixOf(firstTokenUpper, "PORTS") ||
            IsPrefixOf(firstTokenUpper, "RELOAD");

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
