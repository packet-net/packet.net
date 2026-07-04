using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Packet.Radio;
using Packet.Radio.Tait;

namespace Packet.Tune.Core;

/// <summary>Tunables for <see cref="SdmTuningLink"/>.</summary>
public sealed record SdmTuningLinkOptions
{
    /// <summary>Delivery attempts per telegram (first try + retries). Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Wait between delivery attempts. Default 2 s.</summary>
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum wait for the radio's over-air delivery receipt after a
    /// send. The radio itself waits ~6 s before reporting "not acknowledged",
    /// so this must exceed that. Default 10 s.</summary>
    public TimeSpan ReceiptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum wait for the channel to go quiet before transmitting
    /// (the link never keys over a busy channel). Default 30 s.</summary>
    public TimeSpan ChannelClearTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Poll interval while waiting for the channel to clear. Default 100 ms.</summary>
    public TimeSpan ChannelClearPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Fallback receive-buffer poll interval, for arrivals whose RING /
    /// FFSK-progress was missed. Default 1.5 s.</summary>
    public TimeSpan ReceivePollInterval { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Minimum gap between receiving a telegram and transmitting anything:
    /// our own radio's auto-acknowledgement of that telegram is still going
    /// out, and an SDM send (or any PTT) racing it wedges the TM8110's ack
    /// engine (see the class remarks). Default 2 s.
    /// </summary>
    public TimeSpan PostReceiveGuard { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// <see cref="ITuningLink"/> over a radio's own small-datagram side channel
/// (<see cref="IRadioSideChannel"/> — canonically Tait CCDI short data
/// messages): telegrams ride the radios' internal signalling modem at factory
/// deviation — fully independent of the TNC/modem under tune or negotiation,
/// so the coordination channel works at ANY deviation setting and in ANY
/// modem mode. No internet, no TNC involvement.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Reliability:</b> each send waits for the radio's over-air
///     delivery receipt (<see cref="IRadioSideChannel.DeliveryReceipt"/> —
///     for Tait, PROGRESS 1D, which requires SDM auto-acks enabled in both
///     radios' programming) and retries up to
///     <see cref="SdmTuningLinkOptions.MaxAttempts"/> times with
///     <see cref="SdmTuningLinkOptions.RetryBackoff"/> between attempts;
///     the receiver dedupes on the telegram sequence number, so a receipt
///     lost after successful delivery cannot double-deliver.</item>
///   <item><b>Half-duplex etiquette:</b> a send waits for the radio's own
///     DCD to show the channel clear before keying (never transmit over a
///     burst in flight).</item>
///   <item><b>Wire form:</b> telegrams are sent in the compact encoding
///     (<see cref="TuningTelegram.EncodeCompact"/>) to fit the channel's
///     <see cref="IRadioSideChannel.MaxPayloadLength"/> budget.</item>
///   <item><b>Receive:</b> hardware receive buffers are typically one-deep
///     with overwrite-on-arrival, so the link reads promptly on every
///     <see cref="IRadioSideChannel.DatagramArrived"/>, with a slow fallback
///     poll for missed events.</item>
/// </list>
/// <para>
/// <b>Hardware trap (TM8110, bench-found 2026-07-03):</b> keying the radio
/// through its data PTT line while its SDM auto-acknowledgement is pending
/// or in flight <em>wedges the radio's auto-ack engine</em> — from then on
/// it still receives SDMs (RING + buffer) but never acks again, so every
/// peer send reports "not delivered" until the radio is soft-reset (CCR
/// enter/exit) or power-cycled. Never transmit on a radio that has just
/// received a telegram until its ack has had time to go out —
/// <c>TuningSessionOptions.PreBurstDelay</c> exists precisely for this.
/// </para>
/// </remarks>
public sealed class SdmTuningLink : ITuningLink
{
    private readonly IRadioSideChannel channel;
    private readonly string peerId;
    private readonly SdmTuningLinkOptions options;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Channel<TuningTelegram> inbound = Channel.CreateUnbounded<TuningTelegram>();
    private readonly SemaphoreSlim arrivalSignal = new(0);
    private readonly CancellationTokenSource pumpCts = new();
    private readonly Task pumpLoop;
    private readonly HashSet<int> seenSequences = [];
    private readonly Queue<int> seenOrder = new();
    private readonly TaitSdmSideChannel? ownedAdapter;
    private TaskCompletionSource<bool>? pendingReceipt;
    private long lastArrivalTicks;
    private int disposed;

    /// <summary>
    /// Create over any <see cref="IRadioSideChannel"/> (see
    /// <see cref="Create(TaitCcdiRadio, string, SdmTuningLinkOptions?, bool)"/> for
    /// the live-Tait-radio form).
    /// </summary>
    /// <param name="channel">The radio's side channel.</param>
    /// <param name="peerId">The peer radio's side-channel identity (for Tait
    /// SDM, the 8-character data identity).</param>
    /// <param name="options">Tunables; null = defaults.</param>
    public SdmTuningLink(IRadioSideChannel channel, string peerId, SdmTuningLinkOptions? options = null)
        : this(channel, peerId, options, ownedAdapter: null)
    {
    }

    private SdmTuningLink(IRadioSideChannel channel, string peerId, SdmTuningLinkOptions? options, TaitSdmSideChannel? ownedAdapter)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrEmpty(peerId);
        this.channel = channel;
        this.peerId = peerId;
        this.options = options ?? new SdmTuningLinkOptions();
        this.ownedAdapter = ownedAdapter;
        channel.DatagramArrived += OnDatagramArrived;
        channel.DeliveryReceipt += OnDeliveryReceipt;
        pumpLoop = Task.Run(() => PumpInboundAsync(pumpCts.Token));
    }

    /// <summary>Create over a live <see cref="TaitCcdiRadio"/>. The radio's
    /// lifetime stays the caller's. Enable PROGRESS messages on the radio
    /// first — DCD, arrivals and receipts all ride on them.</summary>
    /// <param name="radio">The radio whose SDM side channel carries the telegrams.</param>
    /// <param name="peerId">The peer radio's 8-character SDM data identity.</param>
    /// <param name="options">Tunables; null = defaults.</param>
    /// <param name="extendedSdm">When <c>true</c>, allow telegrams over the 32-character plain-SDM
    /// budget to ride an extended SDM (up to 128 characters — natively split/reassembled by the
    /// radios). Needed for the richer <see cref="StationStatus"/> reply; leave <c>false</c> for the
    /// short mode-coordination / deviation telegrams. Default <c>false</c>.</param>
    public static SdmTuningLink Create(
        TaitCcdiRadio radio, string peerId, SdmTuningLinkOptions? options = null, bool extendedSdm = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerId);
        if (peerId.Length != TaitSdmSideChannel.IdentityLength)
        {
            throw new ArgumentException(
                $"Tait SDM identities are exactly {TaitSdmSideChannel.IdentityLength} characters", nameof(peerId));
        }
        var adapter = new TaitSdmSideChannel(
            radio, new TaitSdmSideChannelOptions { EnableExtendedSdm = extendedSdm });
        return new SdmTuningLink(adapter, peerId, options, adapter);
    }

    /// <summary>Diagnostic sink (attempt/receipt/retry lines). Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <inheritdoc/>
    /// <exception cref="TuningLinkException">No positive delivery receipt after
    /// <see cref="SdmTuningLinkOptions.MaxAttempts"/> attempts.</exception>
    public async Task SendAsync(TuningTelegram telegram, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telegram);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        string wire = telegram.EncodeCompact();
        if (wire.Length > channel.MaxPayloadLength)
        {
            throw new TuningLinkException(
                $"telegram '{wire}' is {wire.Length} chars — over the side channel's {channel.MaxPayloadLength}-character budget");
        }

        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? lastSendError = null;
        try
        {
            for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
            {
                await WaitOutPostReceiveGuardAsync(cancellationToken).ConfigureAwait(false);
                await WaitForChannelClearAsync(cancellationToken).ConfigureAwait(false);

                var receipt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingReceipt = receipt;
                try
                {
                    Log?.Invoke($"sdm-link: send attempt {attempt}/{options.MaxAttempts}: {wire}");
                    bool sent = true;
                    try
                    {
                        await channel.SendAsync(peerId, wire, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is TaitCcdiException or IOException or TimeoutException)
                    {
                        // The radio refused the datagram — busy / not-ready right after a prior
                        // transmission, or a programming rejection (e.g. SDM disabled, 0/06).
                        // Treat as a failed attempt and retry; surface the last such error if
                        // every attempt fails.
                        sent = false;
                        lastSendError = ex.Message;
                        Log?.Invoke($"sdm-link: send rejected by the radio ({ex.Message}) — will retry");
                    }

                    if (sent)
                    {
                        bool? acked;
                        try
                        {
                            acked = await receipt.Task.WaitAsync(options.ReceiptTimeout, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            acked = null;
                        }

                        if (acked == true)
                        {
                            Log?.Invoke("sdm-link: delivery receipt: acknowledged");
                            return;
                        }
                        Log?.Invoke(acked == false
                            ? "sdm-link: delivery receipt: NOT acknowledged — will retry"
                            : $"sdm-link: no delivery receipt within {options.ReceiptTimeout.TotalSeconds:0.#}s — will retry");
                    }
                }
                finally
                {
                    pendingReceipt = null;
                }

                if (attempt < options.MaxAttempts)
                {
                    await Task.Delay(options.RetryBackoff, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            sendGate.Release();
        }

        throw new TuningLinkException(
            $"SDM to {peerId} not acknowledged after {options.MaxAttempts} attempts: {wire}"
            + (lastSendError is null ? string.Empty : $" (last radio error: {lastSendError})"));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TuningTelegram> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var telegram in inbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return telegram;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        channel.DatagramArrived -= OnDatagramArrived;
        channel.DeliveryReceipt -= OnDeliveryReceipt;
        await pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await pumpLoop.ConfigureAwait(false);
        }
        catch
        {
        }
        inbound.Writer.TryComplete();
        ownedAdapter?.Dispose();
        pumpCts.Dispose();
        sendGate.Dispose();
        arrivalSignal.Dispose();
    }

    private void OnDatagramArrived(object? sender, EventArgs e)
    {
        if (arrivalSignal.CurrentCount == 0)
        {
            try
            {
                arrivalSignal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void OnDeliveryReceipt(object? sender, bool acknowledged) =>
        pendingReceipt?.TrySetResult(acknowledged);

    private async Task WaitOutPostReceiveGuardAsync(CancellationToken cancellationToken)
    {
        long arrival = Interlocked.Read(ref lastArrivalTicks);
        if (arrival == 0)
        {
            return;
        }
        var since = DateTimeOffset.UtcNow - new DateTimeOffset(arrival, TimeSpan.Zero);
        var wait = options.PostReceiveGuard - since;
        if (wait > TimeSpan.Zero)
        {
            // The radio is (or may be) transmitting its auto-ack for the
            // telegram we just received — sending now would pre-empt it.
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForChannelClearAsync(CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow;
        while (channel.ChannelBusy == true)
        {
            if (DateTimeOffset.UtcNow - start > options.ChannelClearTimeout)
            {
                throw new TuningLinkException(
                    $"channel still busy after {options.ChannelClearTimeout.TotalSeconds:0.#}s — refusing to transmit over it");
            }
            await Task.Delay(options.ChannelClearPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpInboundAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Prompt read on arrival events; slow fallback poll otherwise
                // (the buffer is one-deep — a missed event must not strand a
                // telegram until the next one overwrites it).
                await arrivalSignal.WaitAsync(options.ReceivePollInterval, cancellationToken)
                    .ConfigureAwait(false);

                string? message;
                try
                {
                    message = await channel.ReadBufferedAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or TaitCcdiException)
                {
                    Log?.Invoke($"sdm-link: buffer read failed ({ex.Message}) — will retry");
                    continue;
                }

                if (message is null)
                {
                    continue;
                }
                Interlocked.Exchange(ref lastArrivalTicks, DateTimeOffset.UtcNow.UtcTicks);
                if (!TuningTelegram.TryParse(message, out var telegram) || telegram is null)
                {
                    Log?.Invoke($"sdm-link: ignoring non-telegram SDM: '{message}'");
                    continue;
                }
                if (!MarkSeen(telegram.Sequence))
                {
                    Log?.Invoke($"sdm-link: duplicate seq {telegram.Sequence} dropped (transport retry)");
                    continue;
                }
                inbound.Writer.TryWrite(telegram);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            inbound.Writer.TryComplete();
        }
    }

    private bool MarkSeen(int sequence)
    {
        lock (seenSequences)
        {
            if (!seenSequences.Add(sequence))
            {
                return false;
            }
            seenOrder.Enqueue(sequence);
            while (seenOrder.Count > 64)
            {
                seenSequences.Remove(seenOrder.Dequeue());
            }
            return true;
        }
    }
}
