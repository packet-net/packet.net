using System.Collections.Concurrent;
using System.Threading.Channels;
using Packet.Node.Core.Api;

namespace Packet.Node.Core.Rigs;

/// <summary>
/// The rig-status fan-out hub behind the <c>/api/v1/rigs/events</c> SSE stream: every
/// <see cref="RigStatusMonitor"/> publishes a <see cref="RigStatus"/> here after each poll tick,
/// and each SSE client holds a bounded subscription. Mirrors <c>NodeTelemetry</c>'s
/// subscribe/broadcast shape — DropOldest channels, so a slow consumer drops its oldest buffered
/// updates rather than back-pressuring the poll loops.
/// </summary>
public sealed class RigTelemetry
{
    private readonly ConcurrentDictionary<Guid, ChannelWriter<RigStatus>> subscribers = new();

    /// <summary>Publish a fresh status to every live subscriber. Non-blocking.</summary>
    public void Publish(RigStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        foreach (var writer in subscribers.Values)
        {
            writer.TryWrite(status);
        }
    }

    /// <summary>
    /// Open a live rig-status subscription. Returns a reader the SSE endpoint drains; the
    /// returned <see cref="IDisposable"/> unsubscribes (and completes the channel) on client
    /// disconnect.
    /// </summary>
    public IDisposable Subscribe(out ChannelReader<RigStatus> reader)
    {
        var channel = Channel.CreateBounded<RigStatus>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        subscribers[id] = channel.Writer;
        reader = channel.Reader;
        return new Subscription(this, id, channel.Writer);
    }

    /// <summary>Number of live subscribers (for tests).</summary>
    public int SubscriberCount => subscribers.Count;

    private sealed class Subscription(RigTelemetry owner, Guid id, ChannelWriter<RigStatus> writer) : IDisposable
    {
        public void Dispose()
        {
            if (owner.subscribers.TryRemove(id, out _))
            {
                writer.TryComplete();
            }
        }
    }
}
