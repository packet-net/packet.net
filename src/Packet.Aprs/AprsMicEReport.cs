namespace Packet.Aprs;

/// <summary>
/// The Mic-E position comment carried in three destination-address bits (APRS12c §10 Mic-E Position
/// Comments). Standard comments mean the same to everyone; custom ones are user-defined.
/// </summary>
public enum AprsMicEMessage
{
    /// <summary>All three bits clear: emergency. Clients should alert loudly.</summary>
    Emergency,

    /// <summary>M0: Off Duty.</summary>
    OffDuty,

    /// <summary>M1: En Route.</summary>
    EnRoute,

    /// <summary>M2: In Service.</summary>
    InService,

    /// <summary>M3: Returning.</summary>
    Returning,

    /// <summary>M4: Committed.</summary>
    Committed,

    /// <summary>M5: Special.</summary>
    Special,

    /// <summary>M6: Priority.</summary>
    Priority,

    /// <summary>C0: Custom-0.</summary>
    Custom0,

    /// <summary>C1: Custom-1.</summary>
    Custom1,

    /// <summary>C2: Custom-2.</summary>
    Custom2,

    /// <summary>C3: Custom-3.</summary>
    Custom3,

    /// <summary>C4: Custom-4.</summary>
    Custom4,

    /// <summary>C5: Custom-5.</summary>
    Custom5,

    /// <summary>C6: Custom-6.</summary>
    Custom6,

    /// <summary>The bits mix standard and custom ones, which has no defined meaning. Decode only.</summary>
    Unknown,
}

/// <summary>
/// A Mic-E position report (APRS12c §10): latitude, message bits, hemispheres and longitude offset
/// packed into the destination address, and longitude, speed, course and symbol packed into the
/// first 9 bytes of the information field, followed by optional status text.
/// </summary>
/// <remarks>
/// Build one to send with <see cref="AprsPacket.CreateMicE"/>, which computes the destination.
/// The status text may begin with a device type code and end with a device suffix identifying
/// the radio (APRS12c §10, <see cref="AprsDeviceIdentification"/>); both are lifted out into
/// <see cref="TypeCode"/> and <see cref="DeviceSuffix"/>, a leading <c>xxx}</c> altitude into
/// <see cref="AprsPositionedData.AltitudeFeet"/>, and a leading <c>IO91SX/G</c> grid locator into
/// <see cref="MaidenheadLocator"/>.
/// </remarks>
public sealed record AprsMicEReport : AprsPositionedData
{
    /// <summary>The position comment (message) bits.</summary>
    public AprsMicEMessage Message { get; init; } = AprsMicEMessage.OffDuty;

    /// <summary>True for current GPS data (<c>`</c>), false for old data (<c>'</c>). The Kenwood
    /// TM-D700 sends <c>'</c> for current data (APRS12c §5).</summary>
    public bool IsCurrent { get; init; } = true;

    /// <summary>
    /// The device type code after the symbol, or null if none: <c>`</c> messaging-capable and
    /// <c>'</c> not (APRS 1.2), <c>&gt;</c> Kenwood handheld and <c>]</c> Kenwood mobile (legacy),
    /// space for the original Mic-E.
    /// </summary>
    public char? TypeCode { get; init; }

    /// <summary>The device suffix at the end of the status text, e.g. <c>_%</c> (Yaesu FTM-400DR),
    /// <c>=</c> (Kenwood TM-D710 after <c>]</c>), or empty.</summary>
    public string DeviceSuffix { get; init; } = "";

    /// <summary>
    /// A 4- or 6-character Maidenhead locator carried in the status text with the grid-square
    /// symbol, <c>IO91SX/G</c> (APRS12c §10 Maidenhead Locator in the Mic-E Status Text Field), or
    /// null. Held in upper case.
    /// </summary>
    public string? MaidenheadLocator { get; init; }

    /// <summary>The destination SSID, which originally selected one of 15 generic digipeater paths.
    /// Obsolete; normally 0 (APRS12c §4, UAP §1.2.1).</summary>
    public int DestinationSsid { get; init; }

    /// <summary>The five obsolete Rev 0 binary telemetry values (flag 0x1D), or null.</summary>
    public IReadOnlyList<int>? LegacyTelemetry { get; init => field = value is null ? null : Internal.EquatableList<int>.Of(value); }

    /// <summary>
    /// Whether the sender can receive APRS messages, from the type code: true for <c>`</c> and the
    /// Kenwood codes, false for <c>'</c>, null when unknown.
    /// </summary>
    public bool? MessagingCapable => TypeCode switch
    {
        '`' or '>' or ']' => true,
        '\'' => false,
        _ => null,
    };

    /// <summary>The sending device, looked up from <see cref="TypeCode"/> and <see cref="DeviceSuffix"/>.</summary>
    public AprsDevice? Device => AprsDeviceIdentification.FromMicE(TypeCode, DeviceSuffix);

    /// <inheritdoc/>
    public override char DataTypeIdentifier => IsCurrent ? '`' : '\'';

    internal override void Encode(Internal.InfoWriter writer) => Internal.MicECodec.WriteInformation(writer, this);
}
