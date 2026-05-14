namespace Packet.Aprs;

/// <summary>
/// A decoded APRS Mic-E report per APRS101 §10. Mic-E packs position,
/// speed, course, symbol, message-bits and digi-path into both the
/// AX.25 destination-address field and the AX.25 information field;
/// see <see cref="AprsMicEDecoder"/> for the encoding details.
/// </summary>
/// <param name="Latitude">Decimal-degree latitude, positive north.</param>
/// <param name="Longitude">Decimal-degree longitude, positive east.</param>
/// <param name="SpeedKnots">Decoded speed 0–799 kn.</param>
/// <param name="CourseDegrees">
/// Decoded course 0–360 °. Per §10 a value of 0 means "unknown or
/// indefinite" and 360 means "due north".
/// </param>
/// <param name="SymbolTable">
/// Symbol-table identifier byte from info[8]. Same convention as
/// <see cref="AprsPosition.SymbolTable"/>: <c>/</c>, <c>\</c>, or an
/// overlay character.
/// </param>
/// <param name="SymbolCode">Symbol code byte from info[7].</param>
/// <param name="MessageType">Decoded standard / custom / emergency message identifier.</param>
/// <param name="Comment">
/// Free-form bytes after the 9-byte information-field header — may
/// contain altitude, status text, Maidenhead locator, telemetry. The
/// decoder doesn't interpret it.
/// </param>
public readonly record struct AprsMicE(
    double Latitude,
    double Longitude,
    int SpeedKnots,
    int CourseDegrees,
    char SymbolTable,
    char SymbolCode,
    MicEMessageType MessageType,
    string Comment);

/// <summary>
/// Mic-E message-bits result per APRS101 §10. The 3 message-identifier
/// bits in the destination address bytes 1–3 select one of 15
/// pre-defined types plus an Emergency code.
/// </summary>
public enum MicEMessageType
{
    /// <summary>All three message bits zero — the emergency code.</summary>
    Emergency,
    StandardM0OffDuty,
    StandardM1EnRoute,
    StandardM2InService,
    StandardM3Returning,
    StandardM4Committed,
    StandardM5Special,
    StandardM6Priority,
    CustomC0,
    CustomC1,
    CustomC2,
    CustomC3,
    CustomC4,
    CustomC5,
    CustomC6,
    /// <summary>
    /// Mix of "1 (Std)" and "1 (Custom)" message-bit characters in the
    /// destination — spec §10 explicitly calls this out as "unknown".
    /// </summary>
    Unknown,
}
