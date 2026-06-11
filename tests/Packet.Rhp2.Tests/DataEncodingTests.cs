using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Packet.Rhp2.Tests;

/// <summary>
/// Pins the binary-over-JSON convention: payload bytes map to the
/// <c>data</c> string via Latin-1 (one byte per char), NOT base64.
/// </summary>
public class DataEncodingTests
{
    [Fact]
    public void Binary_bytes_round_trip_through_the_wire_string()
    {
        // Spans the interesting ranges: NUL, ASCII boundary, high-bit
        // bytes 0x80/0xFF (where UTF-8 would multi-byte and corrupt
        // counts), and a control char the JSON layer must escape.
        var bytes = new byte[] { 0x00, 0x7F, 0x80, 0xFF, 0x01, 0x0A };

        var wire = RhpDataEncoding.ToWireString(bytes);
        var back = RhpDataEncoding.FromWireString(wire);

        back.Should().Equal(bytes);
    }

    [Fact]
    public void Each_byte_maps_to_exactly_one_char()
    {
        // The Latin-1 property the whole scheme rests on — and why the
        // 16-bit frame limit can be reasoned about in payload bytes.
        var bytes = new byte[] { 0x80, 0xA9, 0xFF };
        RhpDataEncoding.ToWireString(bytes).Length.Should().Be(bytes.Length);
    }

    [Fact]
    public void Empty_input_yields_empty_output_both_ways()
    {
        RhpDataEncoding.ToWireString([]).Should().BeEmpty();
        RhpDataEncoding.FromWireString(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Binary_payload_survives_a_full_message_round_trip()
    {
        // End-to-end: bytes → wire string → JSON (escaping applied) →
        // parse → wire string → bytes. This is the path real payloads take.
        var payload = new byte[] { 0x00, 0x0D, 0x0A, 0x22, 0x5C, 0x80, 0xFF };
        var sent = new SendMessage { Handle = 3, Data = RhpDataEncoding.ToWireString(payload) };

        var parsed = (SendMessage)RhpJson.Deserialize(RhpJson.Serialize(sent));

        RhpDataEncoding.FromWireString(parsed.Data!).Should().Equal(payload);
    }

    [Fact]
    public void Control_characters_are_json_escaped_on_the_wire()
    {
        // The spec's "control characters JSON-escaped" requirement is
        // discharged by the serializer, not by RhpDataEncoding — pin that
        // a CR really leaves as the two-character \r escape.
        var msg = new SendMessage { Handle = 1, Data = RhpDataEncoding.ToWireString([(byte)'i', 0x0D]) };
        var json = Encoding.UTF8.GetString(RhpJson.Serialize(msg));
        json.Should().Contain("\\r");
    }
}
