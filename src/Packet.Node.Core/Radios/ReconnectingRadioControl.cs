using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Radio;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios;

/// <summary>
/// The stable <see cref="IRadioControl"/> facade that gives a head-end-bound radio-control channel
/// reconnect supervision (#576) — the control-plane sibling of
/// <see cref="Transports.ReconnectingKissModem"/>. Everything on the port holds THIS object for the
/// port's whole life (the RSSI-tagging transport, the carrier-sense gate, the status monitor); when
/// the inner driver faults — the head-end bounced, the socket died, the radio stopped answering
/// probes — the facade disposes the dead driver and re-opens through the
/// <see cref="IRadioControlFactory"/> with capped exponential backoff, swapping the fresh driver in
/// underneath without disturbing any consumer.
/// </summary>
/// <remarks>
/// <para>
/// Each reopen attempt resolves the head-end afresh (a new <see cref="HeadEndDeviceResolver"/> from
/// the live config + discovery), so a re-addressed head-end or a device whose raw-pipe TCP port
/// moved after a replug is picked up; the factory's create path also re-clocks the line to the
/// CONFIGURED CCDI rate and re-runs the progress-messages enable, so DCD events flow again without
/// any operator action. While the link is down, <see cref="ChannelBusy"/> reads <c>null</c> (the
/// faulted driver cleared it — unknown fails the CSMA gate open) and RSSI polls fail fast; AX.25
/// traffic on the co-located data pipe is unaffected.
/// </para>
/// <para>
/// Consumers that need the concrete driver (tuning, hail, the capability doctor) must not cache the
/// inner across operations — they resolve the LIVE driver per operation via
/// <see cref="RadioControls.LiveTait"/>. <see cref="InnerChanged"/> announces each swap (the
/// status monitor rebuilds its health sampling against the fresh driver on it).
/// </para>
/// </remarks>
public sealed partial class ReconnectingRadioControl : IRadioControl
{
    private readonly string portId;
    private readonly PortRadioConfig config;
    private readonly IRadioControlFactory factory;
    private readonly Func<HeadEndDeviceResolver> resolverFactory;
    private readonly ILogger logger;
    private readonly TimeProvider time;
    private readonly TimeSpan minBackoff;
    private readonly TimeSpan maxBackoff;
    private readonly CancellationTokenSource lifecycle = new();
    private readonly object swapGate = new();

    private volatile IRadioControl inner;
    private int reconnecting;
    private int disposed;

    /// <summary>
    /// Wrap <paramref name="initial"/> (an already-open driver — the eager first open stays with
    /// the caller so first-open fault isolation is unchanged). <paramref name="resolverFactory"/>
    /// builds a fresh head-end resolver per reopen attempt so the address/inventory is re-resolved
    /// from live config each time.
    /// </summary>
    public ReconnectingRadioControl(
        IRadioControl initial,
        string portId,
        PortRadioConfig config,
        IRadioControlFactory factory,
        Func<HeadEndDeviceResolver> resolverFactory,
        ILogger<ReconnectingRadioControl>? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? minBackoff = null,
        TimeSpan? maxBackoff = null)
    {
        inner = initial ?? throw new ArgumentNullException(nameof(initial));
        this.portId = portId ?? throw new ArgumentNullException(nameof(portId));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.resolverFactory = resolverFactory ?? throw new ArgumentNullException(nameof(resolverFactory));
        this.logger = logger ?? NullLogger<ReconnectingRadioControl>.Instance;
        time = timeProvider ?? TimeProvider.System;
        this.minBackoff = minBackoff ?? TimeSpan.FromSeconds(1);
        this.maxBackoff = maxBackoff ?? TimeSpan.FromSeconds(30);
        Attach(initial);
        // A fault that landed BETWEEN the factory open and this subscription fired its
        // ConnectionStateChanged with no listener (the driver only raises on a transition) —
        // re-check now so a driver that died mid-bring-up is reopened, not adopted dead.
        if (initial is TaitCcdiRadio { ConnectionState: TaitConnectionState.Faulted })
        {
            KickReconnect();
        }
    }

    /// <summary>The live inner driver. Swapped on reconnect — do not cache across operations
    /// (resolve per operation via <see cref="RadioControls.Live"/>).</summary>
    public IRadioControl Inner => inner;

    /// <summary>Raised after a reopen swaps a fresh driver in (the argument). Handlers run on the
    /// reconnect task — keep them fast; rebuild against the new driver, don't block.</summary>
    public event EventHandler<IRadioControl>? InnerChanged;

    /// <inheritdoc/>
    /// <remarks>Re-raised from whichever inner driver is live, across swaps.</remarks>
    public event EventHandler<CarrierSenseChange>? CarrierSenseChanged;

    /// <inheritdoc/>
    public RadioCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc/>
    /// <remarks><c>null</c> while the control channel is down (the faulted driver clears its
    /// busy state — unknown fails the CSMA gate open).</remarks>
    public bool? ChannelBusy => inner.ChannelBusy;

    /// <inheritdoc/>
    public ValueTask<float> ReadRssiDbmAsync(CancellationToken cancellationToken = default) =>
        inner.ReadRssiDbmAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask SetTransmitterAsync(bool transmit, CancellationToken cancellationToken = default) =>
        inner.SetTransmitterAsync(transmit, cancellationToken);

    private void Attach(IRadioControl radio)
    {
        radio.CarrierSenseChanged += ForwardCarrierSense;
        if (radio is TaitCcdiRadio tait)
        {
            tait.ConnectionStateChanged += OnConnectionStateChanged;
        }
    }

    private void Detach(IRadioControl radio)
    {
        radio.CarrierSenseChanged -= ForwardCarrierSense;
        if (radio is TaitCcdiRadio tait)
        {
            tait.ConnectionStateChanged -= OnConnectionStateChanged;
        }
    }

    private void ForwardCarrierSense(object? sender, CarrierSenseChange e) =>
        CarrierSenseChanged?.Invoke(this, e);

    // Fires on the driver's pump/watchdog — never block here. Single-flight: one reconnect loop
    // at a time; a fresh fault during a swap is caught by the post-loop re-check below.
    private void OnConnectionStateChanged(object? sender, TaitConnectionState state)
    {
        if (state != TaitConnectionState.Faulted || Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        KickReconnect();
    }

    private void KickReconnect()
    {
        if (Interlocked.Exchange(ref reconnecting, 1) != 0)
        {
            return;
        }
        LogRadioFaulted(portId, config.HeadEndId, config.DeviceId);
        _ = Task.Run(() => ReconnectLoopAsync(lifecycle.Token));
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        try
        {
            var dead = inner;
            Detach(dead);
            await DisposeQuietlyAsync(dead).ConfigureAwait(false);

            var backoff = minBackoff;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // The factory re-resolves the head-end (fresh resolver: live config address /
                    // mDNS + a fresh inventory fetch, so a moved tcpPort is found), re-clocks the
                    // line to the CONFIGURED baud, and re-enables progress messages — the full
                    // bring-up, not a bare re-dial.
                    var fresh = await factory
                        .CreateAsync(config, time, resolverFactory(), ct).ConfigureAwait(false);
                    bool adopted = false;
                    lock (swapGate)
                    {
                        if (Volatile.Read(ref disposed) == 0)
                        {
                            Attach(fresh);
                            inner = fresh;
                            adopted = true;
                        }
                    }
                    if (!adopted)
                    {
                        await DisposeQuietlyAsync(fresh).ConfigureAwait(false);
                        return;
                    }
                    LogRadioReopened(portId, config.HeadEndId, config.DeviceId);
                    InnerChanged?.Invoke(this, fresh);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Includes HttpClient's TaskCanceledException timeout (an OCE with OUR token
                    // not cancelled — the black-holed-head-end case): it must take the backoff
                    // path, not be mistaken for shutdown.
                    LogRadioReopenFailed(portId, (int)Math.Ceiling(backoff.TotalSeconds), ex.Message);
                }

                try
                {
                    await Task.Delay(backoff, time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                backoff = TimeSpan.FromTicks(Math.Clamp(backoff.Ticks * 2, minBackoff.Ticks, maxBackoff.Ticks));
            }
        }
        finally
        {
            Volatile.Write(ref reconnecting, 0);
            // A fault that landed between the swap-in and the flag reset was dropped by the
            // single-flight guard — re-check so a dead fresh driver is not left unsupervised.
            if (Volatile.Read(ref disposed) == 0 &&
                inner is TaitCcdiRadio { ConnectionState: TaitConnectionState.Faulted })
            {
                KickReconnect();
            }
        }
    }

    private static async ValueTask DisposeQuietlyAsync(IRadioControl radio)
    {
        try
        {
            await radio.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // best-effort — the driver is being discarded
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await lifecycle.CancelAsync().ConfigureAwait(false);
        IRadioControl current;
        lock (swapGate)
        {
            current = inner;
        }
        Detach(current);
        await DisposeQuietlyAsync(current).ConfigureAwait(false);
        lifecycle.Dispose();
    }

    [LoggerMessage(EventId = 5201, Level = LogLevel.Warning,
        Message = "Port {PortId}: radio control channel (head-end '{HeadEndId}' device '{DeviceId}') faulted; reopening with backoff.")]
    private partial void LogRadioFaulted(string portId, string headEndId, string deviceId);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Information,
        Message = "Port {PortId}: radio control channel (head-end '{HeadEndId}' device '{DeviceId}') reopened; DCD/RSSI flowing again.")]
    private partial void LogRadioReopened(string portId, string headEndId, string deviceId);

    [LoggerMessage(EventId = 5203, Level = LogLevel.Warning,
        Message = "Port {PortId}: radio control reopen failed ({Reason}); retrying in {Seconds}s.")]
    private partial void LogRadioReopenFailed(string portId, int seconds, string reason);
}

/// <summary>
/// How consumers resolve the concrete radio driver behind a port's <see cref="IRadioControl"/>
/// handle, which may be the stable <see cref="ReconnectingRadioControl"/> facade (head-end-bound
/// radios) or the bare driver (local-serial radios). Resolve per operation — never cache the
/// unwrapped driver across a facade swap.
/// </summary>
public static class RadioControls
{
    /// <summary>The live driver behind <paramref name="radio"/>: the facade's current inner, or
    /// <paramref name="radio"/> itself when it is not the reconnect facade.</summary>
    public static IRadioControl? Live(IRadioControl? radio) =>
        radio is ReconnectingRadioControl facade ? facade.Inner : radio;

    /// <summary>The live <see cref="TaitCcdiRadio"/> behind <paramref name="radio"/>, or
    /// <c>null</c> when the port's radio (facade-wrapped or bare) is not a Tait CCDI driver.</summary>
    public static TaitCcdiRadio? LiveTait(IRadioControl? radio) => Live(radio) as TaitCcdiRadio;
}
