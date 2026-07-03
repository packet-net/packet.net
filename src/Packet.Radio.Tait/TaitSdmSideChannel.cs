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
/// character), per CCDI §1.9.8. With <see cref="TaitSdmSideChannelOptions.EnableExtendedSdm"/>
/// the budget rises to <see cref="ExtendedPayloadBudget"/> characters: payloads over 32
/// characters ride the extended SDM format (SFI 04), which the radios split and reassemble
/// natively — hardware-verified TM8110↔TM8110 (see
/// <see cref="TaitCcdiRadio.SendExtendedSdmAsync"/>). The radio's receive buffer is one-deep
/// with overwrite-on-arrival, and these radios emit an FFSK-data PROGRESS on mere carrier
/// rise — both quirks are exactly the behaviour the <see cref="IRadioSideChannel"/> contract
/// warns consumers about. (An extended arrival raises one FFSK-data PROGRESS per over-air
/// part before the completing RING; the mid-reassembly reads those spurious arrivals provoke
/// harmlessly return an empty buffer.)
/// </remarks>
public sealed class TaitSdmSideChannel : IRadioSideChannel, IDisposable
{
    /// <summary>Characters a plain Tait SDM can carry (CCDI §1.9.8).</summary>
    public const int PayloadBudget = 32;

    /// <summary>Characters an extended (SFI 04) Tait SDM can carry (CCDI §1.9.8).</summary>
    public const int ExtendedPayloadBudget = 128;

    /// <summary>Length of a Tait SDM data identity (CCDI §1.9.8).</summary>
    public const int IdentityLength = 8;

    private readonly TaitCcdiRadio radio;
    private readonly TaitSdmSideChannelOptions options;

    /// <summary>Wrap the radio. The radio's lifetime stays the caller's —
    /// disposing this adapter only unhooks the event subscriptions.</summary>
    public TaitSdmSideChannel(TaitCcdiRadio radio, TaitSdmSideChannelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(radio);
        this.radio = radio;
        this.options = options ?? new TaitSdmSideChannelOptions();
        radio.RingReceived += OnRing;
        radio.ProgressReceived += OnProgress;
        radio.SdmDeliveryReceipt += OnReceipt;
    }

    /// <inheritdoc/>
    public int MaxPayloadLength =>
        options.EnableExtendedSdm ? ExtendedPayloadBudget : PayloadBudget;

    /// <inheritdoc/>
    public bool? ChannelBusy => radio.ChannelBusy;

    /// <inheritdoc/>
    public event EventHandler? DatagramArrived;

    /// <inheritdoc/>
    public event EventHandler<bool>? DeliveryReceipt;

    /// <inheritdoc/>
    public Task SendAsync(string destinationId, string payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentException(
                $"payload of {payload.Length} characters exceeds the {MaxPayloadLength}-character budget",
                nameof(payload));
        }
        return payload.Length > PayloadBudget
            ? radio.SendExtendedSdmAsync(destinationId, payload, leadInDelay: null, cancellationToken)
            : radio.SendSdmAsync(destinationId, payload, leadInDelay: null, cancellationToken);
    }

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

/// <summary>Behavioural knobs for <see cref="TaitSdmSideChannel"/>.</summary>
public sealed record TaitSdmSideChannelOptions
{
    /// <summary>
    /// Allow payloads over 32 characters (up to
    /// <see cref="TaitSdmSideChannel.ExtendedPayloadBudget"/>) to ride the extended SDM
    /// format (SFI 04) and raise <see cref="TaitSdmSideChannel.MaxPayloadLength"/>
    /// accordingly. Hardware-proven TM8110↔TM8110 (CCDI 03.02, 2026-07-03: 100- and
    /// 128-character messages delivered, natively reassembled, receipts acknowledged), but
    /// default <c>false</c>: an extended message costs multiple over-air FFSK bursts per
    /// datagram (longer airtime per send, more to lose to one collision), and whether a
    /// given peer's programming accepts SFI 04 is a per-fleet question — opt in per
    /// deployment.
    /// </summary>
    public bool EnableExtendedSdm { get; init; }
}
