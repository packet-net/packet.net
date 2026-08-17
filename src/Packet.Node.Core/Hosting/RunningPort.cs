using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Kiss.NinoTnc;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Radios;
using Packet.Radio;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// The <b>runtime half</b> of a live AX.25 port: its transport chain, its single
/// <see cref="Ax25Listener"/>, and the radio / rig handles that came up with it. One per
/// serving port, referenced from the port owner the <see cref="PortSupervisor"/> keeps for
/// every configured port (see <see cref="PortState"/> / <see cref="PortHealth"/>).
/// </summary>
/// <remarks>
/// <para>
/// It deliberately carries <b>no config</b>: the port's config baseline lives once, on the
/// owner (<c>PortSupervisor.GetPortConfig</c>), so "what is this port running on" has exactly
/// one answer (packet-net/packet.net#722 - it used to have two, maintained separately).
/// </para>
/// <para>
/// <b>Lifetime.</b> A consumer that got one from <c>PortSupervisor.GetPort</c> holds a
/// borrowed reference, not a lease: a reconcile, a restart or the running-state watchdog can
/// tear this port down while the reference is live. Teardown flips <see cref="IsAlive"/>
/// false <em>before</em> disposing anything, so a holder can re-check across an await instead
/// of writing into a disposed listener; <see cref="DisposeAsync"/> is idempotent. A full
/// borrow protocol is a follow-up (packet-net/packet.net#726).
/// </para>
/// </remarks>
public sealed class RunningPort : IAsyncDisposable
{
    private const int Alive = 0;
    private const int TearingDown = 1;
    private const int Disposed = 2;

    private int lifecycle;

    public required string Id { get; init; }

    /// <summary>
    /// False from the moment this port's teardown begins - set BEFORE the listener, transport,
    /// radio or rig are disposed. A consumer holding this port across an await re-checks it and
    /// gives up loudly rather than touching a half-disposed object.
    /// </summary>
    public bool IsAlive => Volatile.Read(ref lifecycle) == Alive;

    /// <summary>Mark this port as no longer usable (the supervisor calls it at the start of
    /// every teardown, before any disposal). Idempotent.</summary>
    internal void BeginTeardown() => Interlocked.CompareExchange(ref lifecycle, TearingDown, Alive);

    /// <summary>The neutral AX.25 transport this port runs over (a native KISS transport,
    /// optionally wrapped in the reconnect / pacing decorators; an AXUDP modem via the
    /// migration shim). May also expose <see cref="ITxCompletionTransport"/> /
    /// <see cref="ICsmaChannelParams"/> — consumers feature-detect with <c>is</c>.</summary>
    public required IAx25Transport Transport { get; init; }

    public required Ax25Listener Listener { get; init; }

    /// <summary>
    /// The carrier-sense source feeding this port's listener gate (OQ-012), when one
    /// exists: a radio with hardware DCD (<c>RadioCarrierSense</c>) or a transport that
    /// senses the channel itself (the in-process soundmodem; a future Nino KISS DCD
    /// extension). Null = no source, the always-clear gate. Surfaced port-level on
    /// <c>PortStatus.ChannelBusy</c> and the <c>pdn_port_channel_busy</c> metric.
    /// </summary>
    public ICarrierSense? CarrierSense { get; init; }

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

    /// <summary>The open rig-control (CAT) backend connection feeding the rig status poller, or
    /// null when this port has no rig attached (config absent, or the daemon was unreachable and
    /// the port degraded to running without it). Disposed after <see cref="RigStatus"/> — the
    /// poller reads it until stopped.</summary>
    public Packet.Rig.IRigControl? Rig { get; init; }

    /// <summary>The per-port rig status poller (frequency/mode/PTT + TX meters) driving
    /// <c>GET /api/v1/rigs</c>, <c>/ports/{id}/rig</c> and the <c>event: rig</c> SSE feed, or null
    /// when no rig is attached. Disposed BEFORE the rig it polls.</summary>
    public Rigs.IRigStatusMonitor? RigStatus { get; init; }

    /// <summary>The supervised node-managed <c>rigctld</c> when this port's <c>rig:</c> block is
    /// the <c>device</c>+<c>model</c> shape (and the daemon came up), or null (BYO daemon, no
    /// rig, or the daemon failed and the port degraded to running without a rig). Disposed
    /// <b>LAST</b> — every rig client (the status poller's connection AND a rig-backed radio's
    /// dedicated one) dials it until they are gone.</summary>
    public Rigs.ManagedRigDaemon? RigDaemon { get; init; }

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

    /// <inheritdoc/>
    /// <remarks>Idempotent: the first call tears the port down, later ones are no-ops (the
    /// supervisor disposes exactly once, but a stale holder must not be able to double-dispose
    /// a listener out from under a reconcile).</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref lifecycle, Disposed) == Disposed)
        {
            return;
        }

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
        // The rig pair is independent of the radio/modem chain; same discipline — the
        // poller stops before the backend it reads.
        if (RigStatus is not null)
        {
            await RigStatus.DisposeAsync().ConfigureAwait(false);
        }
        if (Rig is not null)
        {
            await Rig.DisposeAsync().ConfigureAwait(false);
        }
        // The node-managed rigctld goes LAST of all: the rig client above and a rig-backed
        // radio's dedicated connection (disposed with the radio, further up) both dial it.
        if (RigDaemon is not null)
        {
            await RigDaemon.DisposeAsync().ConfigureAwait(false);
        }
    }
}
