using System.Globalization;
using System.Text.Json;

namespace Packet.Node.Core.Traffic;

/// <summary>
/// One frame of a portable traffic capture (SP-003, #178): when it was on the air, the port,
/// whether this node sent it, the AX.25 bytes in KISS form (addresses, control, PID and info,
/// no FCS), and the radio's per-frame signal readings when it had them.
/// </summary>
/// <param name="Truncated">True when the recorder capped the raw bytes (the traffic log keeps
/// at most <see cref="SqliteTrafficStore.RawCapBytes"/>); the frame then replays as far as
/// it goes and no further.</param>
public sealed record TrafficCaptureFrame(
    DateTimeOffset At,
    string Port,
    bool Transmitted,
    byte[] Frame,
    float? RssiDbm = null,
    float? SnrDb = null,
    float? NoiseFloorDbm = null,
    bool Truncated = false);

/// <summary>
/// The capture file: JSON lines, one object per frame, oldest first, so a capture can be
/// written as it is taken, cut with the usual text tools, and read by anything. Keys:
/// <c>ts</c> (ISO 8601, UTC, millisecond precision), <c>port</c>, <c>dir</c> (<c>rx</c> or
/// <c>tx</c>), <c>frame</c> (lowercase hex of the AX.25 bytes), and, when present,
/// <c>rssi</c>, <c>snr</c>, <c>noise</c> (dBm, dB, dBm) and <c>truncated</c>. A blank line or
/// a line starting with <c>#</c> is skipped, so a capture can carry notes.
/// </summary>
public static class TrafficCapture
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Write one frame as a line. The caller owns the writer's lifetime.</summary>
    public static void WriteFrame(TextWriter writer, TrafficCaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(frame);
        writer.WriteLine(FormatLine(frame));
    }

    /// <summary>Write every frame, one line each, in the order given.</summary>
    public static void Write(TextWriter writer, IEnumerable<TrafficCaptureFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        foreach (var frame in frames)
        {
            WriteFrame(writer, frame);
        }
    }

    /// <summary>The single line for a frame, without its newline.</summary>
    public static string FormatLine(TrafficCaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("ts", frame.At.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
            json.WriteString("port", frame.Port);
            json.WriteString("dir", frame.Transmitted ? "tx" : "rx");
            json.WriteString("frame", Convert.ToHexString(frame.Frame).ToLowerInvariant());
            if (frame.RssiDbm is { } rssi)
            {
                json.WriteNumber("rssi", rssi);
            }
            if (frame.SnrDb is { } snr)
            {
                json.WriteNumber("snr", snr);
            }
            if (frame.NoiseFloorDbm is { } noise)
            {
                json.WriteNumber("noise", noise);
            }
            if (frame.Truncated)
            {
                json.WriteBoolean("truncated", true);
            }
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Read a capture, one frame per line, in file order. A line that is not a
    /// capture frame throws <see cref="FormatException"/> naming its line number.</summary>
    public static IEnumerable<TrafficCaptureFrame> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }
            yield return ParseLine(line, lineNumber);
        }
    }

    /// <summary>Parse one capture line.</summary>
    public static TrafficCaptureFrame ParseLine(string line, int lineNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(line);
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var ts = root.GetProperty("ts").GetString() ?? throw new FormatException("ts is null");
            var at = DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var port = root.GetProperty("port").GetString() ?? throw new FormatException("port is null");
            var dir = root.GetProperty("dir").GetString();
            bool transmitted = dir switch
            {
                "tx" => true,
                "rx" => false,
                _ => throw new FormatException($"dir must be rx or tx, not '{dir}'"),
            };
            var hex = root.GetProperty("frame").GetString() ?? throw new FormatException("frame is null");
            var frame = Convert.FromHexString(hex);
            return new TrafficCaptureFrame(
                at, port, transmitted, frame,
                Optional(root, "rssi"), Optional(root, "snr"), Optional(root, "noise"),
                root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new FormatException($"capture line {lineNumber}: {ex.Message}", ex);
        }
    }

    /// <summary>A traffic-log row as a capture frame (the log's raw bytes are already KISS
    /// form; a row at the raw cap is marked truncated).</summary>
    public static TrafficCaptureFrame FromTrafficFrame(TrafficFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var bytes = new byte[frame.Raw.Count];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)frame.Raw[i];
        }
        return new TrafficCaptureFrame(
            frame.Timestamp, frame.PortId, frame.Direction == "tx", bytes,
            frame.RssiDbm, frame.SnrDb, frame.NoiseFloorDbm,
            Truncated: bytes.Length >= SqliteTrafficStore.RawCapBytes);
    }

    private static float? Optional(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
}
