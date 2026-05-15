using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Packet.Agw.Tests;

public class AgwFrameTests
{
    [Fact]
    public void ToBytes_writes_header_then_body_in_lsb_first_order()
    {
        var frame = new AgwFrame(
            Port: 0,
            Kind: AgwCommandKind.Data,
            Pid: 0xF0,
            From: "M0LTE",
            To: "PN0TST",
            Data: Encoding.ASCII.GetBytes("Hello"));

        var bytes = frame.ToBytes();

        bytes.Should().HaveCount(AgwFrame.HeaderSize + 5);
        bytes[0].Should().Be(0, "port");
        bytes[4].Should().Be((byte)'D', "kind letter");
        bytes[6].Should().Be(0xF0, "pid");
        bytes[8..13].Should().BeEquivalentTo("M0LTE"u8.ToArray(), "from callsign ASCII");
        bytes[13..18].Should().AllBeEquivalentTo((byte)0, "from callsign NUL pad");
        bytes[18..24].Should().BeEquivalentTo("PN0TST"u8.ToArray(), "to callsign ASCII");
        bytes[28].Should().Be(5, "data length low byte");
        bytes[29..32].Should().AllBeEquivalentTo((byte)0, "data length high bytes");
        Encoding.ASCII.GetString(bytes[36..]).Should().Be("Hello");
    }

    [Fact]
    public void Parse_round_trips_a_data_frame()
    {
        var original = new AgwFrame(
            Port: 1,
            Kind: AgwCommandKind.Data,
            Pid: 0xF0,
            From: "MM3NDH",
            To: "GB7CHT-1",
            Data: Encoding.ASCII.GetBytes("ports\r"));

        var bytes = original.ToBytes();
        var parsed = AgwFrame.Parse(bytes, out int consumed);

        consumed.Should().Be(bytes.Length);
        parsed.Port.Should().Be(original.Port);
        parsed.Kind.Should().Be(original.Kind);
        parsed.Pid.Should().Be(original.Pid);
        parsed.From.Should().Be(original.From);
        parsed.To.Should().Be(original.To);
        parsed.Data.ToArray().Should().BeEquivalentTo(original.Data.ToArray());
    }

    [Fact]
    public void Parse_round_trips_an_empty_body()
    {
        var original = new AgwFrame(
            Port: 0,
            Kind: AgwCommandKind.RegisterCallsign,
            Pid: 0,
            From: "M0LTE",
            To: "",
            Data: ReadOnlyMemory<byte>.Empty);

        var parsed = AgwFrame.Parse(original.ToBytes(), out _);
        parsed.Data.Length.Should().Be(0);
        parsed.From.Should().Be("M0LTE");
        parsed.To.Should().BeEmpty();
    }

    [Fact]
    public void TryReadDataLength_reports_the_advertised_body_size_without_parsing_the_rest()
    {
        var frame = new AgwFrame(0, AgwCommandKind.Data, 0xF0, "A", "B", new byte[42]);
        var bytes = frame.ToBytes();

        AgwFrame.TryReadDataLength(bytes.AsSpan(0, AgwFrame.HeaderSize), out int dataLen)
            .Should().BeTrue();
        dataLen.Should().Be(42);
    }

    [Fact]
    public void TryReadDataLength_returns_false_for_a_truncated_header()
    {
        AgwFrame.TryReadDataLength(new byte[10], out int dataLen).Should().BeFalse();
        dataLen.Should().Be(0);
    }

    [Fact]
    public void Parse_throws_when_buffer_is_shorter_than_header()
    {
        var buffer = new byte[AgwFrame.HeaderSize - 1];
        var act = () => AgwFrame.Parse(buffer, out _);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_throws_when_body_is_truncated()
    {
        var frame = new AgwFrame(0, AgwCommandKind.Data, 0xF0, "A", "B", new byte[10]);
        var bytes = frame.ToBytes();
        var act = () => AgwFrame.Parse(bytes.AsSpan(0, bytes.Length - 1), out _);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Callsign_field_overflow_throws()
    {
        var act = () => new AgwFrame(0, AgwCommandKind.Data, 0xF0, "TOOLONGCALL11", "B", ReadOnlyMemory<byte>.Empty).ToBytes();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Read_trims_NUL_pad_from_callsigns()
    {
        // Craft a frame body with explicit NUL-padded callsigns to
        // confirm parsing strips them rather than leaving them in the
        // returned strings.
        var header = new byte[AgwFrame.HeaderSize];
        header[0] = 0;
        header[4] = AgwCommandKind.Data;
        header[6] = 0xF0;
        Encoding.ASCII.GetBytes("M0LTE\0\0\0\0\0".AsSpan(), header.AsSpan(8, 10));
        Encoding.ASCII.GetBytes("PN0TST\0\0\0\0".AsSpan(), header.AsSpan(18, 10));
        // data length 0, user 0

        var parsed = AgwFrame.Parse(header, out _);
        parsed.From.Should().Be("M0LTE");
        parsed.To.Should().Be("PN0TST");
    }
}
