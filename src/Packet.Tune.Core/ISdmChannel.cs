using Packet.Radio.Tait;
using Packet.Radio.Tait.Ccdi;

namespace Packet.Tune.Core;

/// <summary>
/// The minimal short-data-message surface <see cref="SdmTuningLink"/> needs
/// from a radio: send an SDM, read the (one-deep) receive buffer, know
/// whether the channel is busy, and hear about arrivals + delivery receipts.
/// <see cref="TaitSdmChannel"/> adapts a live <see cref="TaitCcdiRadio"/>;
/// tests supply fakes.
/// </summary>
public interface ISdmChannel
{
    /// <summary>Whether the radio currently reports the channel busy
    /// (<c>null</c> = unknown, e.g. PROGRESS messages not enabled).</summary>
    bool? ChannelBusy { get; }

    /// <summary>Something SDM-shaped arrived at the radio (a RING or an
    /// FFSK-data PROGRESS) — read the buffer promptly, it is one-deep and
    /// overwritten by the next arrival. May fire spuriously (these radios
    /// emit an FFSK-data PROGRESS on mere carrier rise); readers must
    /// tolerate an empty buffer.</summary>
    event EventHandler? MessageArrived;

    /// <summary>An over-air delivery receipt for a previously sent SDM:
    /// <c>true</c> = the destination radio acknowledged, <c>false</c> = no
    /// acknowledgement within the radio's configured wait.</summary>
    event EventHandler<bool>? DeliveryReceipt;

    /// <summary>Transmit one SDM to the 8-character destination identity.</summary>
    Task SendAsync(string destinationId, string message, CancellationToken cancellationToken = default);

    /// <summary>Read and clear the radio's buffered received SDM
    /// (<c>null</c> when nothing is buffered).</summary>
    Task<string?> ReadBufferedAsync(CancellationToken cancellationToken = default);
}

/// <summary><see cref="ISdmChannel"/> over a live <see cref="TaitCcdiRadio"/>.
/// Requires PROGRESS messages enabled on the radio
/// (<see cref="TaitCcdiRadio.SetProgressMessagesAsync"/>) for
/// <see cref="ChannelBusy"/>, arrivals and receipts to flow.</summary>
public sealed class TaitSdmChannel : ISdmChannel, IDisposable
{
    private readonly TaitCcdiRadio radio;

    /// <summary>Wrap the radio. The radio's lifetime stays the caller's —
    /// disposing this adapter only unhooks the event subscriptions.</summary>
    public TaitSdmChannel(TaitCcdiRadio radio)
    {
        ArgumentNullException.ThrowIfNull(radio);
        this.radio = radio;
        radio.RingReceived += OnRing;
        radio.ProgressReceived += OnProgress;
        radio.SdmDeliveryReceipt += OnReceipt;
    }

    /// <inheritdoc/>
    public bool? ChannelBusy => radio.ChannelBusy;

    /// <inheritdoc/>
    public event EventHandler? MessageArrived;

    /// <inheritdoc/>
    public event EventHandler<bool>? DeliveryReceipt;

    /// <inheritdoc/>
    public Task SendAsync(string destinationId, string message, CancellationToken cancellationToken = default) =>
        radio.SendSdmAsync(destinationId, message, leadInDelay: null, cancellationToken);

    /// <inheritdoc/>
    public Task<string?> ReadBufferedAsync(CancellationToken cancellationToken = default) =>
        radio.ReadBufferedSdmAsync(cancellationToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        radio.RingReceived -= OnRing;
        radio.ProgressReceived -= OnProgress;
        radio.SdmDeliveryReceipt -= OnReceipt;
    }

    private void OnRing(object? sender, CcdiRingMessage ring) =>
        MessageArrived?.Invoke(this, EventArgs.Empty);

    private void OnProgress(object? sender, CcdiProgressMessage progress)
    {
        if (progress.Type == CcdiProgressType.FfskDataReceived)
        {
            MessageArrived?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnReceipt(object? sender, TaitSdmReceipt receipt) =>
        DeliveryReceipt?.Invoke(this, receipt.Acknowledged);
}
