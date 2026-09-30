using Packet.Ax25;
using Packet.Core;
using Packet.Node.Core.Traffic;

namespace Packet.Node.Tests.Traffic;

/// <summary>The portable capture format (SP-003, #178): JSON lines that round-trip every
/// field, tolerate notes, and name the line that is not a frame.</summary>
[Trait("Category", "Node")]
public sealed class TrafficCaptureTests
{
    private static readonly Callsign A = new("M0AAA", 1);
    private static readonly Callsign B = new("M0BBB", 2);
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 20, 0, 0, 123, TimeSpan.Zero);

    [Fact]
    public void A_frame_round_trips_with_its_signal_readings_and_without_them()
    {
        var sabm = Ax25Frame.Sabm(B, A).ToBytes();
        var withSignal = new TrafficCaptureFrame(T0, "vhf-1", Transmitted: false, sabm, RssiDbm: -95.3f, SnrDb: 12.5f, NoiseFloorDbm: -107.8f);
        var plain = new TrafficCaptureFrame(T0 + TimeSpan.FromMilliseconds(250), "vhf-1", Transmitted: true, Ax25Frame.Ua(A, B, finalBit: true).ToBytes(), Truncated: true);

        var text = new StringWriter();
        TrafficCapture.Write(text, [withSignal, plain]);
        var lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("{\"ts\":\"2026-09-30T20:00:00.123Z\",\"port\":\"vhf-1\",\"dir\":\"rx\",\"frame\":\"");
        lines[0].Should().Contain("\"rssi\":-95.3").And.Contain("\"snr\":12.5").And.Contain("\"noise\":-107.8").And.NotContain("truncated");
        lines[1].Should().Contain("\"dir\":\"tx\"").And.Contain("\"truncated\":true").And.NotContain("rssi");

        var back = TrafficCapture.Read(new StringReader(text.ToString())).ToList();
        back.Should().HaveCount(2);
        back[0].Should().BeEquivalentTo(withSignal);
        back[1].Should().BeEquivalentTo(plain);
        back[0].At.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Notes_and_blank_lines_are_skipped_and_a_bad_line_is_named()
    {
        var good = TrafficCapture.FormatLine(new TrafficCaptureFrame(T0, "p", false, Ax25Frame.Sabm(B, A).ToBytes()));
        var frames = TrafficCapture.Read(new StringReader($"# a note\n\n{good}\n")).ToList();
        frames.Should().HaveCount(1);

        var act = () => TrafficCapture.Read(new StringReader($"{good}\n{{\"ts\":\"2026-09-30T20:00:00Z\",\"port\":\"p\",\"dir\":\"sideways\",\"frame\":\"00\"}}\n")).ToList();
        act.Should().Throw<FormatException>().WithMessage("capture line 2:*sideways*");
    }

    [Fact]
    public void A_traffic_log_row_becomes_a_frame_and_a_row_at_the_raw_cap_is_marked_truncated()
    {
        var raw = Enumerable.Range(0, SqliteTrafficStore.RawCapBytes).Select(i => i & 0xFF).ToArray();
        var frame = TrafficCapture.FromTrafficFrame(new TrafficFrame(
            1, T0, "vhf-1", "tx", "M0AAA-1", "M0BBB-2", "I", 0, 0, 0, 0, 0xF0, 3000, raw, -90f, null, null));
        frame.Transmitted.Should().BeTrue();
        frame.Frame.Should().HaveCount(SqliteTrafficStore.RawCapBytes);
        frame.Truncated.Should().BeTrue();
        frame.RssiDbm.Should().Be(-90f);

        var whole = TrafficCapture.FromTrafficFrame(new TrafficFrame(
            2, T0, "vhf-1", "rx", "M0AAA-1", "M0BBB-2", "RR", null, 0, 1, 0x11, null, 0, [0x01, 0x02], null, null, null));
        whole.Transmitted.Should().BeFalse();
        whole.Truncated.Should().BeFalse();
        whole.Frame.Should().Equal(0x01, 0x02);
    }
}
