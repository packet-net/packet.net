using Packet.Node.Core.Configuration;
using Packet.Rig;
using Packet.Rig.Flrig;
using Packet.Rig.Hamlib;

namespace Packet.Node.Core.Rigs;

/// <summary>
/// Builds a live <see cref="IRigControl"/> from a port's <see cref="PortRigConfig"/> — the
/// rig-side sibling of <see cref="Radios.IRadioControlFactory"/>. The port supervisor calls this
/// when a port with a <c>rig:</c> block comes up, and only this seam knows how each rig kind maps
/// onto a concrete backend (which lets component tests substitute a scripted rig instead of
/// dialling a real daemon).
/// </summary>
public interface IRigControlFactory
{
    /// <summary>
    /// Connect to the rig daemon described by <paramref name="rig"/> (capabilities and identity
    /// are probed as part of the connect). The returned rig is ready to poll; the caller owns its
    /// disposal.
    /// </summary>
    /// <param name="rig">The validated per-port rig attachment config.</param>
    /// <param name="timeProvider">Clock for the backend's command-timeout scheduling. Null uses
    /// the system clock.</param>
    /// <param name="cancellationToken">Cancels the connect/probe.</param>
    /// <exception cref="NotSupportedException">If the rig kind has no implementation in this
    /// build (unreachable for validated config — the validator and factory share
    /// <see cref="RigKinds"/>).</exception>
    Task<IRigControl> CreateAsync(
        PortRigConfig rig,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The production <see cref="IRigControlFactory"/>: <c>hamlib</c> →
/// <see cref="RigctldRig.ConnectAsync"/> (rigctld's network protocol), <c>flrig</c> →
/// <see cref="FlrigRig.ConnectAsync(FlrigRigOptions?, CancellationToken)"/>. Config defaults
/// resolve here: a null port becomes the kind's stock daemon port via
/// <see cref="RigKinds.DefaultPort"/>.
/// </summary>
public sealed class RigControlFactory : IRigControlFactory
{
    /// <summary>A shared default instance (the factory holds no state).</summary>
    public static RigControlFactory Instance { get; } = new();

    /// <inheritdoc/>
    public async Task<IRigControl> CreateAsync(
        PortRigConfig rig,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var port = rig.Port ?? RigKinds.DefaultPort(rig.Kind);

        if (RigKinds.Is(rig.Kind, RigKinds.Hamlib))
        {
            return await RigctldRig.ConnectAsync(new RigctldRigOptions
            {
                Host = rig.Host,
                Port = port,
                TimeProvider = timeProvider ?? TimeProvider.System,
            }, cancellationToken).ConfigureAwait(false);
        }

        if (RigKinds.Is(rig.Kind, RigKinds.Flrig))
        {
            return await FlrigRig.ConnectAsync(new FlrigRigOptions
            {
                Host = rig.Host,
                Port = port,
                TimeProvider = timeProvider ?? TimeProvider.System,
            }, cancellationToken).ConfigureAwait(false);
        }

        throw new NotSupportedException(
            $"rig kind '{rig.Kind}' has no IRigControl implementation in this build " +
            $"(expected one of: {string.Join(", ", RigKinds.Names)}).");
    }
}
