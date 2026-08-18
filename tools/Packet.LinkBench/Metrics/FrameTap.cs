using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.LinkBench.Metrics;

/// <summary>One observed frame on one endpoint's listener, either direction.</summary>
internal readonly record struct TracedFrame(DateTimeOffset At, FrameDirection Direction, Ax25Frame Frame);

/// <summary>
/// Captures an endpoint's full frame stream by tapping
/// <see cref="Ax25Listener.FrameTraced"/> (TX + RX, never filtered). The bench
/// owns both listeners, so two taps see every frame each engine emitted and
/// every frame each engine heard - the raw material for the §5 metrics.
/// </summary>
internal sealed class FrameTap
{
    private readonly List<TracedFrame> frames = [];
    private readonly object gate = new();

    public void Attach(Ax25Listener listener) =>
        listener.FrameTraced += (_, e) =>
        {
            lock (gate)
            {
                frames.Add(new TracedFrame(e.Timestamp, e.Direction, e.Frame));
            }
        };

    public IReadOnlyList<TracedFrame> Snapshot()
    {
        lock (gate)
        {
            // Stable order by timestamp: TX and RX append from different threads.
            return frames.OrderBy(f => f.At).ToList();
        }
    }
}
