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
    /// carrier-sense only). A <see cref="ReconnectingRadioControl"/> facade (head-end-bound radios,
    /// #576) gets a swap-following monitor that rebuilds the concrete monitor against each fresh
    /// inner driver, so health sampling and connection state survive a reconnect. Never returns
    /// null — an attached radio always has a status.
    /// </summary>
    public static IRadioStatusMonitor Create(
        string portId, PortRadioConfig config, IRadioControl radio, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(radio);

        return radio is ReconnectingRadioControl facade
            ? new SwappingRadioStatusMonitor(portId, config, facade, timeProvider)
            : CreateForDriver(portId, config, radio, timeProvider);
    }

    /// <summary>The concrete (non-facade) monitor for a driver — also what the swap-following
    /// monitor rebuilds per reconnect.</summary>
    internal static IRadioStatusMonitor CreateForDriver(
        string portId, PortRadioConfig config, IRadioControl radio, TimeProvider? timeProvider) =>
        radio is TaitCcdiRadio tait
            ? new TaitRadioStatusMonitor(portId, config, tait, timeProvider)
            : new GenericRadioStatusMonitor(portId, config, radio);
}

/// <summary>
/// The <see cref="IRadioStatusMonitor"/> for a radio behind the
/// <see cref="ReconnectingRadioControl"/> facade: it monitors whichever inner driver is live,
/// rebuilding the concrete monitor (for Tait, the health-sampling one) on every
/// <see cref="ReconnectingRadioControl.InnerChanged"/> swap. While the control channel is down the
/// last-built monitor projects honestly (a faulted connection state, no fresh health samples).
/// </summary>
internal sealed class SwappingRadioStatusMonitor : IRadioStatusMonitor
{
    private readonly string portId;
    private readonly PortRadioConfig config;
    private readonly ReconnectingRadioControl facade;
    private readonly TimeProvider? timeProvider;
    private volatile IRadioStatusMonitor current;
    private int disposed;

    public SwappingRadioStatusMonitor(
        string portId, PortRadioConfig config, ReconnectingRadioControl facade, TimeProvider? timeProvider)
    {
        this.portId = portId;
        this.config = config;
        this.facade = facade;
        this.timeProvider = timeProvider;
        current = RadioStatusMonitors.CreateForDriver(portId, config, facade.Inner, timeProvider);
        facade.InnerChanged += OnInnerChanged;
    }

    private void OnInnerChanged(object? sender, IRadioControl fresh)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        var old = current;
        current = RadioStatusMonitors.CreateForDriver(portId, config, fresh, timeProvider);
        _ = DisposeQuietlyAsync(old);
    }

    /// <inheritdoc/>
    public RadioStatus Snapshot() => current.Snapshot();

    private static async Task DisposeQuietlyAsync(IRadioStatusMonitor monitor)
    {
        try
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // best-effort — the monitor's driver is already gone
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        facade.InnerChanged -= OnInnerChanged;
        await current.DisposeAsync().ConfigureAwait(false);
    }
}
