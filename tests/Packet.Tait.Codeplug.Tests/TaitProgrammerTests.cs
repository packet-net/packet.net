using AwesomeAssertions;
using Packet.Tait.Codeplug;
using Xunit;

namespace Packet.Tait.Codeplug.Tests;

/// <summary>
/// Drives <see cref="TaitProgrammer"/> against a mock radio scripted from the real captured
/// interrogate exchange (radio s/n 19925328, capture3 / session-1). This proves the transport
/// state machine end to end - command sequence, lock-step prompts, record parsing, identity
/// decode - with no hardware.
/// </summary>
public class TaitProgrammerTests
{
    private static Dictionary<string, string> InterrogateScript() => new()
    {
        ["^"] = "v",
        ["#"] = ">",
        ["ld"] = "{C05}\r>",
        ["d00"] = "{C01}\r>",
        ["r00"] =
            "000010544D414231322D423130305F3032303147\r" +
            "000115514D4131465F7374645F30322E31382E30302E303076\r" +
            "000209303039342C303038362E\r" +
            "0003040000FFAA50\r" +
            "000405544D4143359D\r" +
            "00050831393932353332384C\r" +
            "00060830313032303030306F\r>",
        ["p01"] = ">",
        ["r27"] = "2700025E0079\r>",
        ["p00"] = ">",
        ["r2F"] = "2F0002560079\r>",
    };

    [Fact]
    public void Interrogate_decodes_the_captured_identity()
    {
        var radio = new ScriptedRadio(InterrogateScript());
        using var programmer = new TaitProgrammer(radio);

        TaitIdentity id = programmer.Interrogate();

        id.Model.Should().Be("TMAB12-B100_0201");
        id.Firmware.Should().Be("QMA1F_std_02.18.00.00");
        id.Serial.Should().Be("19925328");
        id.Versions.Should().Be("0094,0086");
    }

    [Fact]
    public void Interrogate_transmits_the_captured_command_sequence()
    {
        var radio = new ScriptedRadio(InterrogateScript());
        using var programmer = new TaitProgrammer(radio);

        programmer.Interrogate();

        // ^ and # open the session; ld/d00 handshake; then the interrogate reads.
        radio.CommandsSeen.Should().Equal("^", "#", "ld", "d00", "r00", "p01", "r27", "p00", "r2F");
    }

    [Fact]
    public void Read_section_parses_the_streamed_records()
    {
        var radio = new ScriptedRadio(InterrogateScript());
        using var programmer = new TaitProgrammer(radio);

        IReadOnlyList<CodeplugRecord> records = programmer.ReadSection(0x27);

        records.Should().ContainSingle();
        records[0].Section.Should().Be(0x27);
        records[0].ToWireLine().Should().Be("2700025E0079");
    }

    [Fact]
    public void WriteRecords_writes_only_the_given_records_in_a_block()
    {
        // Write session: the interrogate replies plus r22, and a fallback that acks the write
        // block commands (b / i<arg> / w<record> / e) with the prompt.
        Dictionary<string, string> script = InterrogateScript();
        script["r22"] = "22000100DD\r>";
        var radio = new ScriptedRadio(
            script,
            fallback: cmd => cmd is "b" or "e" || cmd.StartsWith('i')
                || cmd.StartsWith('w') ? ">" : null);
        using var programmer = new TaitProgrammer(radio);

        var record = new CodeplugRecord(0x27, 0, new byte[] { 0x5E, 0x00 });
        int written = programmer.WriteRecords(new[] { record });

        written.Should().Be(1);
        radio.CommandsSeen.Should().ContainInOrder("b", "i53380146", "w2700025E0079", "e");
        radio.CommandsSeen.Count(c => c.StartsWith("w2700", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void WriteRecords_never_writes_the_read_only_identity()
    {
        Dictionary<string, string> script = InterrogateScript();
        script["r22"] = "22000100DD\r>";
        var radio = new ScriptedRadio(
            script,
            fallback: cmd => cmd is "b" or "e" || cmd.StartsWith('i')
                || cmd.StartsWith('w') ? ">" : null);
        using var programmer = new TaitProgrammer(radio);

        // Only a section-0 (identity) record: nothing should be written.
        int written = programmer.WriteRecords(new[] { new CodeplugRecord(0x00, 0, new byte[] { 0x00 }) });

        written.Should().Be(0);
        radio.CommandsSeen.Should().NotContain("b");
    }
}
