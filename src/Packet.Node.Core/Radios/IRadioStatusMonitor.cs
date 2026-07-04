using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Radio;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios;

/// <summary>
/// A node-layer view of one port's attached radio: it owns whatever background sampling the
/// concrete radio supports (for Tait CCDI, a <see cref="TaitRadioHealthMonitor"/>), and projects the
/// current state as a serialisable <see cref="RadioStatus"/> on demand. The port supervisor creates
/// one when a radio attaches and disposes it on teardown (before the radio, so sampling stops first).
/// </summary>
/// <remarks>
/// Degrades cleanly by construction: a status monitor never touches the packet path, and a Snapshot
/// only reads already-captured state (no blocking serial I/O), so a faulted or silent radio yields a
/// <see cref="RadioStatus"/> with null health / <c>faulted</c> connection state rather than throwing.
/// </remarks>
public interface IRadioStatusMonitor : IAsyncDisposable
{
    /// <summary>Project the radio's current status. Non-blocking — reads captured state only.</summary>
    RadioStatus Snapshot();
}

/// <summary>Builds the right <see cref="IRadioStatusMonitor"/> for an attached radio.</summary>
public static class RadioStatusMonitors
{
    /// <summary>
    /// Create a status monitor for a just-opened <paramref name="radio"/> on
    /// <paramref name="portId"/>. A Tait CCDI radio gets the full health-sampling monitor; any other
    /// <see cref="IRadioControl"/> gets a basic monitor (attached / kind / control port /
    /// carrier-sense only). Never returns null — an attached radio always has a status.
    /// </summary>
    public static IRadioStatusMonitor Create(
        string portId, PortRadioConfig config, IRadioControl radio, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(radio);

        return radio is TaitCcdiRadio tait
            ? new TaitRadioStatusMonitor(portId, config, tait, timeProvider)
            : new GenericRadioStatusMonitor(portId, config, radio);
    }
}
