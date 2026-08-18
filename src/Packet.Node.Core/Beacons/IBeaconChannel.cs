using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Node.Core.Beacons;

/// <summary>
/// The minimal seam the <see cref="BeaconService"/> transmits an ID beacon through: a
/// way to <em>send</em> a connectionless UI frame. <see cref="Ax25Listener"/> satisfies
/// this directly (see <see cref="ListenerBeaconChannel"/>); a test fake can satisfy it
/// without a live radio, so the scheduler is unit-testable on a
/// <c>FakeTimeProvider</c> without standing up a modem.
/// </summary>
/// <remarks>
/// The beacon only ever calls the public <see cref="SendUiAsync"/> - it never mutates
/// the supervisor's live port set, so (like the axping channel) it does not need, and
/// must not take, the host's <c>RunExclusiveAsync</c> gate. <see cref="SendUiAsync"/> is
/// safe to call concurrently with the listener's inbound pump (it builds + writes one UI
/// frame to the modem; the NET/ROM NODES origination already sends UI frames the same way).
/// </remarks>
public interface IBeaconChannel
{
    /// <summary>This station's identity (the beacon UI frame's source callsign).</summary>
    Callsign MyCall { get; }

    /// <summary>Send a connectionless UI frame (the ID beacon) to
    /// <paramref name="destination"/> carrying <paramref name="info"/>. Mirrors
    /// <see cref="Ax25Listener.SendUiAsync"/>.</summary>
    Task SendUiAsync(Callsign destination, ReadOnlyMemory<byte> info, byte pid = Ax25Frame.PidNoLayer3, CancellationToken ct = default);
}

/// <summary>
/// Adapts a live <see cref="Ax25Listener"/> to <see cref="IBeaconChannel"/> - the
/// production channel the supervisor hands the beacon service per port. Pure
/// delegation; holds no state.
/// </summary>
public sealed class ListenerBeaconChannel(Ax25Listener listener) : IBeaconChannel
{
    private readonly Ax25Listener listener = listener ?? throw new ArgumentNullException(nameof(listener));

    /// <inheritdoc/>
    public Callsign MyCall => listener.MyCall;

    /// <inheritdoc/>
    public Task SendUiAsync(Callsign destination, ReadOnlyMemory<byte> info, byte pid = Ax25Frame.PidNoLayer3, CancellationToken ct = default)
        => listener.SendUiAsync(destination, info, pid, ct);
}
