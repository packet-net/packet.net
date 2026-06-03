using Packet.Ax25.Session;
using Packet.Kiss;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// A live AX.25 port the <see cref="PortSupervisor"/> owns: its modem, its
/// single <see cref="Ax25Listener"/>, and the <see cref="PortConfig"/> it was
/// built from (so the next reconcile can field-diff against it). One per
/// configured, enabled, successfully-started port.
/// </summary>
public sealed class RunningPort : IAsyncDisposable
{
    public required string Id { get; init; }

    /// <summary>The config snapshot this port was brought up from — the baseline
    /// the next reconcile diffs against to classify the change.</summary>
    public required PortConfig Config { get; init; }

    public required IKissModem Modem { get; init; }

    public required Ax25Listener Listener { get; init; }

    /// <summary>Whether the port reached a started state. A port whose transport
    /// failed to open is recorded as faulted (not started) so the reconcile can
    /// retry it on the next config change without disrupting healthy ports.</summary>
    public bool Started { get; init; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Listener.DisposeAsync().ConfigureAwait(false);
        switch (Modem)
        {
            case IAsyncDisposable ad:
                await ad.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable d:
                d.Dispose();
                break;
        }
    }
}
