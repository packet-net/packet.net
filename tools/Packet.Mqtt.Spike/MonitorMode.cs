namespace Packet.Mqtt.Spike;

/// <summary>
/// Structured ingestion mode - parse each MQTT payload as an AX.25 frame
/// (optionally KISS-wrapped, depending on what `probe` revealed), gather
/// per-port stats, and log failures to JSONL for analysis.
/// </summary>
/// <remarks>
/// This mode is a stub until `probe` clarifies the payload format. The
/// fill-in happens once we know whether the broker delivers raw AX.25,
/// KISS-wrapped frames, JSON envelopes, or some other shape.
/// </remarks>
public static class MonitorMode
{
    public static Task<int> RunAsync(Options opts)
    {
        Console.Error.WriteLine("# monitor mode is not yet implemented.");
        Console.Error.WriteLine("# Run `probe` first to learn the wire format, then this stub gets filled in.");
        return Task.FromResult(2);
    }
}
