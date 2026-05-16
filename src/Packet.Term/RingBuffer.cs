namespace Packet.Term;

/// <summary>
/// A thread-safe bounded ring buffer of strings, snapshot-on-read.
/// Used by the frame-monitor and chat panes — both want "the last N
/// lines, latest at the bottom" semantics.
/// </summary>
public sealed class RingBuffer
{
    private readonly object lockObj = new();
    private readonly Queue<string> queue;
    private readonly int capacity;

    /// <summary>Construct an empty ring buffer with the given line cap.</summary>
    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
        queue = new Queue<string>(capacity);
    }

    /// <summary>Append <paramref name="line"/>, evicting the oldest entry if needed.</summary>
    public void Add(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (lockObj)
        {
            if (queue.Count >= capacity)
            {
                queue.Dequeue();
            }
            queue.Enqueue(line);
        }
    }

    /// <summary>Take a snapshot of the current buffer contents, oldest-first.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (lockObj)
        {
            return queue.ToArray();
        }
    }
}
