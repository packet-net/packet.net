using Packet.Ax25;
using Packet.Core;
using Packet.Mcp.Decoding;

namespace Packet.Mcp.Tests;

public class FrameDecoderTests
{
    // dest + source address field (14 bytes) of a no-digipeater command frame,
    // borrowed from the strict UI factory so the E/C bits are spec-correct. Append
    // any control byte to get a minimal valid frame for classification tests.
    private static byte[] AddressField(string dest, string source)
    {
        var f = Ax25Frame.Ui(new Callsign(dest), new Callsign(source), ReadOnlySpan<byte>.Empty);
        return f.ToBytes()[..14];
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    [Fact]
    public void Decodes_a_UI_frame_with_addresses_type_and_text()
    {
        var frame = Ax25Frame.Ui(
            new Callsign("APRS"), new Callsign("M0LTE", 9),
            "hello"u8, pid: 0xF0, isCommand: true);

        var d = FrameDecoder.Decode(Hex(frame.ToBytes()));

        d.Framing.Should().Be("raw");
        d.Source.Should().Be("M0LTE-9");
        d.Destination.Should().Be("APRS");
        d.FrameClass.Should().Be("U");
        d.FrameType.Should().Be("UI");
        d.CommandResponse.Should().Be("command");
        d.Pid.Should().Be(0xF0);
        d.PidName.Should().Be("No layer 3");
        d.InfoText.Should().Be("hello");
        d.InfoLength.Should().Be(5);
        d.Modulo.Should().Be(8);
        d.Nr.Should().BeNull();
        d.Ns.Should().BeNull();
    }

    [Fact]
    public void Decodes_an_I_frame_with_sequence_numbers()
    {
        // A UI frame's layout (addr + control + pid + info) is a valid I-frame
        // layout once the control byte is an I control. 0x00 = I, N(S)=0, N(R)=0.
        var ui = Ax25Frame.Ui(new Callsign("GB7RDG"), new Callsign("M0LTE"), "data"u8);
        var bytes = ui.ToBytes();
        bytes[14] = 0x00; // control octet sits right after the 14-byte address field

        var d = FrameDecoder.Decode(Hex(bytes));

        d.FrameClass.Should().Be("I");
        d.FrameType.Should().Be("I");
        d.Ns.Should().Be(0);
        d.Nr.Should().Be(0);
        d.InfoText.Should().Be("data");
    }

    [Theory]
    [InlineData(0x2F, "U", "SABM")]
    [InlineData(0x6F, "U", "SABME")]
    [InlineData(0x43, "U", "DISC")]
    [InlineData(0x63, "U", "UA")]
    [InlineData(0x0F, "U", "DM")]
    [InlineData(0x87, "U", "FRMR")]
    [InlineData(0xAF, "U", "XID")]
    [InlineData(0xE3, "U", "TEST")]
    [InlineData(0x01, "S", "RR")]
    [InlineData(0x05, "S", "RNR")]
    [InlineData(0x09, "S", "REJ")]
    [InlineData(0x0D, "S", "SREJ")]
    public void Classifies_every_control_type(byte control, string cls, string type)
    {
        var bytes = new byte[15];
        AddressField("GB7RDG", "M0LTE").CopyTo(bytes, 0);
        bytes[14] = control;

        var d = FrameDecoder.Decode(Hex(bytes));

        d.FrameClass.Should().Be(cls);
        d.FrameType.Should().Be(type);
    }

    [Fact]
    public void Poll_final_bit_is_read()
    {
        var bytes = new byte[15];
        AddressField("GB7RDG", "M0LTE").CopyTo(bytes, 0);
        bytes[14] = 0x2F | 0x10; // SABM with P set

        FrameDecoder.Decode(Hex(bytes)).PollFinal.Should().BeTrue();
    }

    [Fact]
    public void Decodes_a_KISS_wrapped_frame_and_reports_the_port()
    {
        var frame = Ax25Frame.Ui(new Callsign("APRS"), new Callsign("M0LTE"), "k"u8);
        var ax25 = frame.ToBytes(); // chosen callsigns/info avoid 0xC0/0xDB, so no escaping needed

        // FEND | (port<<4)|Data | payload | FEND
        var kiss = new byte[ax25.Length + 3];
        kiss[0] = 0xC0;
        kiss[1] = 0x10; // port 1, command Data (0x0)
        ax25.CopyTo(kiss, 2);
        kiss[^1] = 0xC0;

        var d = FrameDecoder.Decode(Hex(kiss));

        d.Framing.Should().Be("kiss");
        d.KissPort.Should().Be(1);
        d.FrameType.Should().Be("UI");
        d.Source.Should().Be("M0LTE");
    }

    [Fact]
    public void Tolerates_separators_and_0x_prefixes_in_hex()
    {
        var frame = Ax25Frame.Ui(new Callsign("APRS"), new Callsign("M0LTE"), "x"u8);
        string plain = Hex(frame.ToBytes());
        // Re-space it with colons, 0x prefixes, and whitespace.
        string messy = string.Join(":", Enumerable.Range(0, plain.Length / 2)
            .Select(i => string.Concat("0x", plain.AsSpan(i * 2, 2))));

        var d = FrameDecoder.Decode("  " + messy + "\n");

        d.Source.Should().Be("M0LTE");
        d.FrameType.Should().Be("UI");
    }

    [Fact]
    public void Non_printable_info_renders_as_dots_but_hex_is_exact()
    {
        var frame = Ax25Frame.Ui(new Callsign("APRS"), new Callsign("M0LTE"), new byte[] { 0x01, 0x41, 0x02 });

        var d = FrameDecoder.Decode(Hex(frame.ToBytes()));

        d.InfoText.Should().Be(".A.");
        d.InfoHex.Should().Be("014102");
    }

    [Fact]
    public void Rejects_odd_length_hex()
    {
        Action act = () => FrameDecoder.Decode("ABC");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Rejects_bytes_too_short_to_be_a_frame()
    {
        Action act = () => FrameDecoder.Decode("0102030405");
        act.Should().Throw<ArgumentException>();
    }
}
