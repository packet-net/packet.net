using Packet.Radio.Tait.Ccdi;

namespace Packet.Radio.Tait;

/// <summary>
/// <see cref="IRadioSideChannel"/> over Tait CCDI Short Data Messages: datagrams ride the
/// radio's own internal FFSK modem at factory deviation — no TNC involved, and fully
/// independent of whatever mode/deviation the audio-path modem is in. Requires SDMs (and,
/// for <see cref="IRadioSideChannel.DeliveryReceipt"/>, SDM auto-acknowledgements) enabled
/// in both radios' programming, and PROGRESS messages enabled on this radio
/// (<see cref="TaitCcdiRadio.SetProgressMessagesAsync"/>) — PROGRESS carries carrier sense,
/// arrivals and delivery receipts.
/// </summary>
/// <remarks>
/// The plain-SDM payload budget is <see cref="PayloadBudget"/> characters and destination
/// identities are exactly <see cref="IdentityLength"/> characters ('*' wildcards per
/// character), per CCDI §1.9.8. The radio's receive buffer is one-deep with
/// overwrite-on-arrival, and these radios emit an FFSK-data PROGRESS on mere carrier rise —
/// both quirks are exactly the behaviour the <see cref="IRadioSideChannel"/> contract warns
/// consumers about.
/// </remarks>
public sealed class TaitSdmSideChannel : IRadioSideChannel, IDisposable
{
    /// <summary>Characters a plain Tait SDM can carry (CCDI §1.9.8).</summary>
    public const int PayloadBudget = 32;

    /// <summary>Length of a Tait SDM data identity (CCDI §1.9.8).</summary>
    public const int IdentityLength = 8;

    private readonly TaitCcdiRadio radio;

    /// <summary>Wrap the radio. The radio's lifetime stays the caller's —
    /// disposing this adapter only unhooks the event subscriptions.</summary>
    public TaitSdmSideChannel(TaitCcdiRadio radio)
    {
        ArgumentNullException.ThrowIfNull(radio);
        this.radio = radio;
        radio.RingReceived += OnRing;
        radio.ProgressReceived += OnProgress;
        radio.SdmDeliveryReceipt += OnReceipt;
    }

    /// <inheritdoc/>
    public int MaxPayloadLength => PayloadBudget;

    /// <inheritdoc/>
    public bool? ChannelBusy => radio.ChannelBusy;

    /// <inheritdoc/>
    public event EventHandler? DatagramArrived;

    /// <inheritdoc/>
    public event EventHandler<bool>? DeliveryReceipt;

    /// <inheritdoc/>
    public Task SendAsync(string destinationId, string payload, CancellationToken cancellationToken = default) =>
        radio.SendSdmAsync(destinationId, payload, leadInDelay: null, cancellationToken);

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
        DatagramArrived?.Invoke(this, EventArgs.Empty);

    private void OnProgress(object? sender, CcdiProgressMessage progress)
    {
        if (progress.Type == CcdiProgressType.FfskDataReceived)
        {
            DatagramArrived?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnReceipt(object? sender, TaitSdmReceipt receipt) =>
        DeliveryReceipt?.Invoke(this, receipt.Acknowledged);
}
