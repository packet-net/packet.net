using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// A live AX.25 port the <see cref="PortSupervisor"/> owns: its transport, its
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

    /// <summary>The neutral AX.25 transport this port runs over (a native KISS transport,
    /// optionally wrapped in the reconnect / pacing decorators; an AXUDP modem via the
    /// migration shim). May also expose <see cref="ITxCompletionTransport"/> /
    /// <see cref="ICsmaChannelParams"/> — consumers feature-detect with <c>is</c>.</summary>
    public required IAx25Transport Transport { get; init; }

    public required Ax25Listener Listener { get; init; }

    /// <summary>Whether the port reached a started state. A port whose transport
    /// failed to open is recorded as faulted (not started) so the reconcile can
    /// retry it on the next config change without disrupting healthy ports.</summary>
    public bool Started { get; init; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Listener.DisposeAsync().ConfigureAwait(false);
        await Transport.DisposeAsync().ConfigureAwait(false);
    }
}
