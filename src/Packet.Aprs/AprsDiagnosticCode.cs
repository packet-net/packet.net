namespace Packet.Aprs;

/// <summary>
/// Stable identifiers for decoding diagnostics. Values are never renumbered; new codes are added
/// at the end.
/// </summary>
public enum AprsDiagnosticCode
{
    // ---- envelope

    /// <summary>The TNC2 header is not <c>SOURCE&gt;DEST[,PATH]:</c>.</summary>
    InvalidHeader = 1,

    /// <summary>An address is empty, too long, or has characters that cannot appear in one.</summary>
    InvalidAddress = 2,

    /// <summary>The destination address is empty (UAP §5.2).</summary>
    EmptyDestination = 3,

    /// <summary>The digipeater path has an empty entry (UAP §5.6).</summary>
    EmptyPathEntry = 4,

    /// <summary>More than one path entry is marked used with <c>*</c>; only the last used one should be (UAP §5.30).</summary>
    MultipleUsedMarkers = 5,

    /// <summary>The AX.25 frame is not a UI frame with PID 0xF0, or is too short (APRS12c §3).</summary>
    NotAprsFrame = 6,

    /// <summary>An AX.25 address is padded with NUL rather than spaces (UAP §5.29).</summary>
    NulPaddedAddress = 7,

    /// <summary>An AX.25 address has characters other than upper-case letters and digits.</summary>
    InvalidAx25AddressCharacters = 8,

    /// <summary>The AX.25 frame has more than 8 digipeater addresses.</summary>
    TooManyDigipeaters = 9,

    // ---- information field in general

    /// <summary>The information field ends with CR or LF (APRS12c §5, UAP §5.13).</summary>
    TrailingLineBreak = 20,

    /// <summary>Text that is not valid UTF-8 (UAP §5.16).</summary>
    NonUtf8Text = 21,

    /// <summary>The information field is shorter than its format requires.</summary>
    Truncated = 22,

    /// <summary>The first byte is not a data type identifier (APRS12c §20).</summary>
    NotAprs = 23,

    /// <summary>A reserved data type identifier with no defined format (APRS12c §5).</summary>
    ReservedDataType = 24,

    /// <summary>A format the spec marks obsolete or not recommended, e.g. raw NMEA or raw weather.</summary>
    ObsoleteFormat = 25,

    /// <summary>A value in a well-formed field is out of range and was dropped.</summary>
    OutOfRangeValue = 26,

    // ---- positions and timestamps

    /// <summary>A timestamp is malformed or out of range (APRS12c §6, UAP §5.8).</summary>
    InvalidTimestamp = 40,

    /// <summary>The position is missing or starts with something that cannot begin one.</summary>
    InvalidPosition = 41,

    /// <summary>The latitude is malformed (APRS12c §6, UAP §5.7).</summary>
    InvalidLatitude = 42,

    /// <summary>The longitude is malformed (APRS12c §6, UAP §5.7).</summary>
    InvalidLongitude = 43,

    /// <summary>A lower-case hemisphere letter (UAP §5.9).</summary>
    LowercaseHemisphere = 44,

    /// <summary>The symbol table identifier is not <c>/</c>, <c>\</c>, <c>0</c>-<c>9</c> or <c>A</c>-<c>Z</c>.</summary>
    InvalidSymbolTable = 45,

    /// <summary>The symbol code is not printable ASCII.</summary>
    InvalidSymbolCode = 46,

    /// <summary>A compressed position is malformed (APRS12c §9).</summary>
    InvalidCompressedPosition = 47,

    /// <summary>A <c>!DAO!</c> adds precision to an ambiguous position, which contradicts it.</summary>
    DaoWithAmbiguity = 48,

    /// <summary>A data extension (PHG, RNG, DFS) appears later in the comment (UAP §5.15).</summary>
    DataExtensionInComment = 49,

    // ---- objects and items

    /// <summary>The object name is empty or not printable ASCII.</summary>
    InvalidObjectName = 60,

    /// <summary>The object name is not padded to 9 characters.</summary>
    ObjectNameNotPadded = 61,

    /// <summary>An object report has no timestamp (APRS12c §11).</summary>
    ObjectWithoutTimestamp = 62,

    /// <summary>The item name is not 3-9 printable characters followed by <c>!</c> or <c>_</c>.</summary>
    InvalidItemName = 63,

    // ---- weather

    /// <summary>A weather report lacks a mandatory field (APRS12c §12).</summary>
    IncompleteWeather = 80,

    /// <summary>Text after the weather data; weather reports have no comment (UAP §2.7.1, §5.33).</summary>
    WeatherComment = 81,

    /// <summary>Positionless or raw weather data that could not be decoded.</summary>
    InvalidWeather = 82,

    // ---- Mic-E

    /// <summary>The destination address is not a valid Mic-E encoding (APRS12c §10).</summary>
    InvalidMicEDestination = 100,

    /// <summary>The Mic-E information field is malformed (APRS12c §10).</summary>
    InvalidMicEInformation = 101,

    /// <summary>Kenwood TM-D710 0xFF padding was removed (UAP §5.10).</summary>
    KenwoodFfPadding = 102,

    /// <summary>A Mic-E report without a device type prefix (UAP §5.4).</summary>
    MicEMissingDeviceType = 103,

    // ---- messages

    /// <summary>A message is malformed (APRS12c §14).</summary>
    InvalidMessage = 120,

    /// <summary>The addressee is not padded to 9 characters.</summary>
    UnpaddedAddressee = 121,

    /// <summary>An ack or rej carries a message ID of its own (UAP §5.32).</summary>
    MessageIdOnAck = 122,

    /// <summary>A telemetry metadata message (PARM/UNIT/EQNS/BITS) is malformed (APRS12c §13).</summary>
    InvalidTelemetryMetadata = 123,

    /// <summary>A directed query is malformed (APRS12c §15, UAP §5.18).</summary>
    InvalidQuery = 124,

    // ---- other types

    /// <summary>A telemetry report is malformed (APRS12c §13).</summary>
    InvalidTelemetry = 140,

    /// <summary>A status report is malformed (APRS12c §16).</summary>
    InvalidStatus = 141,

    /// <summary>A Maidenhead locator is malformed.</summary>
    InvalidLocator = 142,

    /// <summary>An NMEA sentence is malformed.</summary>
    InvalidNmea = 143,

    /// <summary>An NMEA sentence's checksum does not match, so the sentence is corrupt and is not decoded.</summary>
    NmeaChecksumMismatch = 144,

    /// <summary>A third-party header is malformed (APRS12c §17).</summary>
    InvalidThirdParty = 145,

    /// <summary>A general query is malformed (APRS12c §15).</summary>
    InvalidGeneralQuery = 146,

    /// <summary>A station capabilities report is malformed (APRS12c §15).</summary>
    InvalidCapabilities = 147,

    /// <summary>A user-defined packet is shorter than its 3-byte header (APRS12c §19).</summary>
    InvalidUserDefined = 148,

    /// <summary>An Agrelo DF report is malformed.</summary>
    InvalidAgreloDf = 149,

    /// <summary>A grid-locator status report lacks the mandatory space before its text (UAP §5.17).</summary>
    MissingSpaceAfterLocator = 150,

    /// <summary>A compressed position's type byte sets its unused high bits (APRS12c §9).</summary>
    CompressionTypeReservedBits = 151,

    /// <summary>A timestamped position report whose timestamp is missing or not timestamp-shaped (UAP §5.8).</summary>
    MalformedTimestamp = 152,

    /// <summary>A <c>!</c> position found after other text (obsolete TNC beacon rule).</summary>
    PositionNotAtStart = 153,

    /// <summary>A weather field is one character shorter or longer than its fixed width (UAP §5.31).</summary>
    NonStandardWeatherFieldWidth = 154,

    /// <summary>Wind sent as c/s fields in a position weather report instead of the DDD/SSS extension.</summary>
    WindFieldsInsteadOfExtension = 155,

    /// <summary>An uncompressed wind extension after a compressed weather position (UAP §5.33).</summary>
    WindExtensionAfterCompressed = 156,

    /// <summary>A Mic-E altitude after other status text instead of first (APRS12c §10).</summary>
    MicEAltitudeNotFirst = 157,

    /// <summary>Message text contains a <c>{</c> that does not start a valid message ID (APRS12c §14).</summary>
    BraceInMessageText = 158,

    /// <summary>A message addressee contains a space or <c>:</c> (APRS12c §14).</summary>
    InvalidAddresseeCharacters = 159,

    /// <summary>A bulletin addressee has a group name after a letter, e.g. <c>BLNCNET</c>; group bulletins use a digit (APRS12c §14).</summary>
    LetterGroupBulletin = 160,

    /// <summary>A <c>&lt;</c> station capabilities packet holds free text rather than TOKEN / TOKEN=VALUE items (APRS12c §15).</summary>
    FreeTextCapabilities = 161,
}
