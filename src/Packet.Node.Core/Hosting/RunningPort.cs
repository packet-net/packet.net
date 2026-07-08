using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Kiss.NinoTnc;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Radios;
using Packet.Radio;

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

    /// <summary>
    /// When a radio-control attachment is active (<see cref="PortConfig.Radio"/>), the
    /// modem transport underneath the RSSI-tagging wrapper — the KISS/CSMA-capable
    /// transport <see cref="Transport"/> decorates. The tagging wrapper does NOT own
    /// what it wraps, so this port disposes it explicitly (after the wrapper, before
    /// the radio). Null when no radio is attached (then <see cref="Transport"/> IS the
    /// modem chain).
    /// </summary>
    public IAx25Transport? InnerTransport { get; init; }

    /// <summary>The open radio control channel feeding the RSSI-tagging wrapper, or
    /// null when this port has no radio attached (config absent, or the radio failed
    /// to open and the port degraded to running without metadata). Disposed LAST —
    /// the wrapper's sampler and the health monitor poll it until they are disposed.</summary>
    public IRadioControl? Radio { get; init; }

    /// <summary>The per-port radio status/health monitor (identity, connection state, carrier-sense,
    /// latest health sample) driving <c>GET /api/v1/radios</c> and <c>/ports/{id}/radio</c>, or null
    /// when no radio is attached. Owns its own sampling (a Tait health monitor); disposed BEFORE the
    /// radio it polls, AFTER the modem chain.</summary>
    public IRadioStatusMonitor? RadioStatus { get; init; }

    /// <summary>
    /// The transport to feature-detect KISS/CSMA capabilities on
    /// (<c>ICsmaChannelParams</c> / <c>ITxCompletionTransport</c>): the modem chain
    /// beneath the RSSI-tagging wrapper when a radio is attached, else
    /// <see cref="Transport"/> itself. The tagging wrapper deliberately does not
    /// forward those interfaces, so KISS-param application must target this.
    /// </summary>
    public IAx25Transport ModemTransport => InnerTransport ?? Transport;

    /// <summary>
    /// The NinoTNC serial port underneath the modem chain, captured before any pacing /
    /// reconnect decorator hides it — or <c>null</c> when this port's modem is not a NinoTNC
    /// (a serial-KISS / kiss-tcp / AXUDP modem exposes no NinoTNC diagnostics). The capability
    /// doctor (<c>GET /api/v1/ports/{id}/doctor</c>) issues GETVER/GETALL/GETRSSI against it —
    /// and, on an explicit interrupt, the transmitting probes. <b>Not owned here</b>: the modem
    /// chain (<see cref="ModemTransport"/>) owns and disposes it.
    /// </summary>
    public NinoTncSerialPort? NinoTnc { get; init; }

    /// <summary>
    /// The reconnect decorator's live link state (<c>IsReconnecting</c>) when this port's transport
    /// chain contains one (kiss-tcp / nino-tnc-tcp ports), captured before later decorators hide it
    /// — like <see cref="NinoTnc"/>. Null for a transport with no reconnect supervision (local
    /// serial, AXUDP). Feeds <c>pdn_port_transport_reconnecting{port}</c> (#583); <b>not owned
    /// here</b> — it IS (part of) the modem chain, which owns its own disposal.
    /// </summary>
    public Transports.ITransportLinkState? LinkState { get; init; }

    /// <summary>Whether the port reached a started state. A port whose transport
    /// failed to open is recorded as faulted (not started) so the reconcile can
    /// retry it on the next config change without disrupting healthy ports.</summary>
    public bool Started { get; init; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Order matters: listener first (it consumes the outermost transport), then the
        // outermost transport (when radio-tagged, disposing the node tap cascades into the
        // RSSI-tagging wrapper and stops its sampler), then the modem chain the wrapper didn't
        // own, then the radio-status/health monitor, then the radio itself LAST — both the RSSI
        // sampler and the health monitor poll the radio, so the radio must outlive them.
        await Listener.DisposeAsync().ConfigureAwait(false);
        await Transport.DisposeAsync().ConfigureAwait(false);
        if (InnerTransport is not null)
        {
            await InnerTransport.DisposeAsync().ConfigureAwait(false);
        }
        if (RadioStatus is not null)
        {
            await RadioStatus.DisposeAsync().ConfigureAwait(false);
        }
        if (Radio is not null)
        {
            await Radio.DisposeAsync().ConfigureAwait(false);
        }
    }
}
