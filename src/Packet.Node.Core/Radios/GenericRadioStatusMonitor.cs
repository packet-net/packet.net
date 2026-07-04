using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Radio;

namespace Packet.Node.Core.Radios;

/// <summary>
/// The <see cref="IRadioStatusMonitor"/> for a radio-control channel that isn't a Tait CCDI radio —
/// the common-subset <see cref="IRadioControl"/> surface only. It reports the radio as attached, the
/// configured kind and control device, and the last carrier-sense state, but has no identity, no
/// health sampling, and an <c>unknown</c> connection state (the common contract tracks none of
/// those). It owns nothing and disposing it is a no-op.
/// </summary>
public sealed class GenericRadioStatusMonitor : IRadioStatusMonitor
{
    private readonly string portId;
    private readonly PortRadioConfig config;
    private readonly IRadioControl radio;

    /// <summary>Wrap <paramref name="radio"/> for status projection on <paramref name="portId"/>.</summary>
    public GenericRadioStatusMonitor(string portId, PortRadioConfig config, IRadioControl radio)
    {
        this.portId = portId ?? throw new ArgumentNullException(nameof(portId));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.radio = radio ?? throw new ArgumentNullException(nameof(radio));
    }

    /// <inheritdoc/>
    public RadioStatus Snapshot() => new(
        PortId: portId,
        Attached: true,
        Kind: config.Kind,
        ControlPort: string.IsNullOrWhiteSpace(config.Port) ? null : config.Port,
        Serial: string.IsNullOrWhiteSpace(config.Serial) ? null : config.Serial,
        Identity: null,
        ConnectionState: "unknown",
        ChannelBusy: radio.ChannelBusy,
        Health: null);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
