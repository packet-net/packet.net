namespace Packet.Aprs;

/// <summary>
/// Per-call configuration for APRS decoding: named tolerances for real-world packets that deviate
/// from APRS12c. Each flag, when on, lets the decoder accept one specific defect and record a
/// <see cref="AprsDiagnosticSeverity.Warning"/>; when off, the defect is an
/// <see cref="AprsDiagnosticSeverity.Error"/> and the data is not decoded.
/// </summary>
/// <remarks>
/// <para>
/// Spec philosophy, as for <c>Ax25ParseOptions</c>: <see cref="Strict"/> turns every tolerance off,
/// so only packets that follow the spec decode. <see cref="Lenient"/> turns them all on; it decodes
/// what the APRS network actually carries and says what was wrong, and it is what every decode
/// method uses when no options are passed. Anything the spec itself tells receivers to accept
/// (longer message text, variable-width telemetry values, ignoring the AX.25 C bits) is accepted
/// by both.
/// </para>
/// <para>
/// The inventory, with the spec rule each flag relaxes and the real-world source that needs it,
/// is in <c>docs/strict-vs-pragmatic-audit.md</c>. A new tolerance is a new flag here, defaulted
/// on, with a paired strict-rejects / lenient-accepts test; never a quietly widened parser.
/// </para>
/// </remarks>
public sealed record AprsParseOptions
{
    /// <summary>Every tolerance on. The default for all decode methods.</summary>
    public static AprsParseOptions Lenient { get; } = new();

    /// <summary>Every tolerance off: packets must follow APRS12c.</summary>
    public static AprsParseOptions Strict { get; } = new()
    {
        StripTrailingLineBreaks = false,
        AllowNonUtf8Text = false,
        AllowLowercaseHemisphere = false,
        AllowOutOfRangeValues = false,
        AllowObjectWithoutTimestamp = false,
        AllowShortObjectName = false,
        AllowIncompleteWeather = false,
        AllowWeatherComment = false,
        RecognizeDataExtensionInComment = false,
        AllowKenwoodFfPadding = false,
        AllowEmptyDestination = false,
        AllowEmptyPathEntry = false,
        AllowMultipleUsedMarkers = false,
        AllowNulPaddedAddress = false,
        AllowMessageIdOnAck = false,
        AllowUnpaddedAddressee = false,
        AllowInvalidAx25AddressCharacters = false,
        AllowMissingSpaceAfterLocator = false,
        AllowIncompleteTelemetry = false,
        AllowCompressionTypeReservedBits = false,
        AllowInvalidTimestamp = false,
        AllowMalformedTimestamp = false,
        AllowPositionNotAtStart = false,
        AllowNonStandardWeatherFieldWidths = false,
        AllowWindFieldsInPositionWeather = false,
        AllowWindExtensionAfterCompressed = false,
        AllowMicEAltitudeAnywhere = false,
        AllowDaoWithAmbiguity = false,
        AllowBraceInMessageText = false,
        AllowInvalidAddresseeCharacters = false,
        AllowLetterGroupBulletin = false,
        AllowFreeTextCapabilities = false,
    };

    /// <summary>
    /// Remove carriage returns and line feeds at the end of the information field. APRS12c §5 says
    /// not to send them; Kenwood radios add CR after acks (UAP §5.13) and many devices add one to
    /// comments (UAP §2.2). The raw bytes stay in <see cref="AprsPacket.Information"/>.
    /// </summary>
    public bool StripTrailingLineBreaks { get; init; } = true;

    /// <summary>
    /// Accept free text that is not valid UTF-8 (APRS 1.2 text is ASCII or UTF-8); such bytes are
    /// decoded as Latin-1. Driver: code page 437 / Latin-1 degree signs (UAP §5.16), Kenwood 0xFF
    /// bursts (UAP §5.10), IGates that corrupt UTF-8 (UAP §5.25).
    /// </summary>
    public bool AllowNonUtf8Text { get; init; } = true;

    /// <summary>Accept <c>n</c>/<c>s</c>/<c>e</c>/<c>w</c> hemispheres; the spec requires upper case (UAP §5.9).</summary>
    public bool AllowLowercaseHemisphere { get; init; } = true;

    /// <summary>
    /// Accept a well-formed field whose value is out of range (e.g. a course over 360) by dropping
    /// that one value.
    /// </summary>
    public bool AllowOutOfRangeValues { get; init; } = true;

    /// <summary>Accept an object report without a timestamp; APRS12c §11 says an object always has one
    /// (even the spec's own weather-object example omits it).</summary>
    public bool AllowObjectWithoutTimestamp { get; init; } = true;

    /// <summary>Accept an object name that is not space-padded to 9 characters (APRS12c §11).</summary>
    public bool AllowShortObjectName { get; init; } = true;

    /// <summary>Accept weather reports missing mandatory fields: the wind extension, gust or
    /// temperature (APRS12c §12 requires them, as dots if unknown).</summary>
    public bool AllowIncompleteWeather { get; init; } = true;

    /// <summary>Accept free text after the weather data; a complete weather report has no comment
    /// field (APRS12c §12, UAP §2.7.1). The text is returned as the comment.</summary>
    public bool AllowWeatherComment { get; init; } = true;

    /// <summary>
    /// Recognise PHG / RNG / DFS later in the comment rather than only straight after the symbol.
    /// Strictly such text is plain comment (UAP §5.15); with this off it is left in the comment
    /// rather than rejected.
    /// </summary>
    public bool RecognizeDataExtensionInComment { get; init; } = true;

    /// <summary>Remove the runs of 0xFF bytes (and stray 0x00 / 0x0F) that some Kenwood TM-D710 radios
    /// insert before the Mic-E device suffix (UAP §5.10).</summary>
    public bool AllowKenwoodFfPadding { get; init; } = true;

    /// <summary>Accept an empty destination address (UAP §5.2: some Anytone radios send six spaces).</summary>
    public bool AllowEmptyDestination { get; init; } = true;

    /// <summary>Skip an empty entry in the digipeater path, e.g. <c>APNU19,:</c> (UAP §5.6).</summary>
    public bool AllowEmptyPathEntry { get; init; } = true;

    /// <summary>Accept a TNC2 path with <c>*</c> after more than one entry; only the last used entry
    /// should carry it (UAP §5.30). Every entry up to the last <c>*</c> is taken as used.</summary>
    public bool AllowMultipleUsedMarkers { get; init; } = true;

    /// <summary>Accept AX.25 addresses padded with NUL instead of spaces (UAP §5.29).</summary>
    public bool AllowNulPaddedAddress { get; init; } = true;

    /// <summary>Accept an ack or rej with a message ID of its own appended, e.g. <c>ack1348{4205</c>
    /// (UAP §5.32); the extra ID is ignored.</summary>
    public bool AllowMessageIdOnAck { get; init; } = true;

    /// <summary>Accept a message addressee that is not space-padded to 9 characters before the
    /// second colon (APRS12c §14).</summary>
    public bool AllowUnpaddedAddressee { get; init; } = true;

    /// <summary>Accept an AX.25 frame address containing characters other than upper-case letters
    /// and digits, such as lower case (UAP §1.1). The address is kept as received.</summary>
    public bool AllowInvalidAx25AddressCharacters { get; init; } = true;

    /// <summary>Accept status text straight after a grid locator's symbol without the mandatory
    /// space (APRS12c §16, UAP §5.17).</summary>
    public bool AllowMissingSpaceAfterLocator { get; init; } = true;

    /// <summary>Accept a telemetry report with fewer than 5 analog values or without the 8 digital
    /// bits (APRS12c §13).</summary>
    public bool AllowIncompleteTelemetry { get; init; } = true;

    /// <summary>Accept a compressed position whose type byte sets the two unused high bits
    /// (APRS12c §9); they are ignored. Driver: UI-View32 compressed weather reports.</summary>
    public bool AllowCompressionTypeReservedBits { get; init; } = true;

    /// <summary>
    /// Accept a correctly shaped timestamp with a field out of range, such as <c>000000z</c>, day 0,
    /// or <c>204140z</c> (a clock time sent with the DHM suffix). The timestamp is kept as received
    /// with <see cref="AprsTimestamp.IsValid"/> false. Ham::APRS::FAP does the same.
    /// </summary>
    public bool AllowInvalidTimestamp { get; init; } = true;

    /// <summary>
    /// Decode a <c>/</c> or <c>@</c> position report whose timestamp is missing or garbled
    /// (<c>@252041_</c>, <c>@&lt;data&gt;z</c>), or an object whose seven timestamp characters are
    /// garbled (<c>111111x</c>), dropping the timestamp. Driver: misconfigured beacon texts (UAP §5.8).
    /// </summary>
    public bool AllowMalformedTimestamp { get; init; } = true;

    /// <summary>
    /// Find a <c>!</c> position within the first 40 characters of a packet that has no other data
    /// type: the original TNC beacon rule, abandoned in 2012 (APRS 1.1 corrections). The text
    /// before the <c>!</c> is dropped. Ham::APRS::FAP still applies this rule.
    /// </summary>
    public bool AllowPositionNotAtStart { get; init; } = true;

    /// <summary>
    /// Accept weather fields one character shorter or longer than the spec's fixed width when the
    /// digits clearly end there: <c>t45</c>, <c>h070</c>, <c>h100</c>, <c>b...</c> (APRS12c §12, UAP §5.31).
    /// A spec-width reading is always tried first, so this never changes a well-formed report.
    /// </summary>
    public bool AllowNonStandardWeatherFieldWidths { get; init; } = true;

    /// <summary>Accept wind as positionless-style <c>cDDDsSSS</c> fields in a position weather report
    /// instead of the <c>DDD/SSS</c> extension. Driver: ESP32 and Ecowitt gateway firmware.</summary>
    public bool AllowWindFieldsInPositionWeather { get; init; } = true;

    /// <summary>Accept an uncompressed <c>DDD/SSS</c> wind extension after a compressed weather position,
    /// which already carries wind in its cs bytes (UAP §5.33). Driver: LoRa APRS trackers.</summary>
    public bool AllowWindExtensionAfterCompressed { get; init; } = true;

    /// <summary>Recognise a Mic-E <c>xxx}</c> altitude later in the status text rather than only at its
    /// start (APRS12c §10). Driver: several radios put it at the end.</summary>
    public bool AllowMicEAltitudeAnywhere { get; init; } = true;

    /// <summary>Accept a <c>!DAO!</c> with extra precision on an ambiguous position, which contradict
    /// each other; the extra precision is ignored and the datum kept.</summary>
    public bool AllowDaoWithAmbiguity { get; init; } = true;

    /// <summary>
    /// Keep a <c>{</c> in message, bulletin or telemetry-metadata text when it does not start a valid
    /// message ID (1-5 letters or digits at the end, APRS12c §14): <c>cq{</c>, <c>IPINFO={...}</c>,
    /// or text appended after the ID. Driver: script-generated messages to self (<c>IPINFO={...}</c>)
    /// and an iGate path that appends signal reports after the ID.
    /// </summary>
    public bool AllowBraceInMessageText { get; init; } = true;

    /// <summary>
    /// Accept a message addressee with a space or <c>:</c> inside it (<c>CA4NDW -7</c>,
    /// <c>QRX B-10</c>). Trailing spaces are padding and always fine (APRS12c §14). Driver: hand-typed
    /// addressees, the BTECH UV-PRO's <c>QRX</c> messages, and RF bit errors.
    /// </summary>
    public bool AllowInvalidAddresseeCharacters { get; init; } = true;

    /// <summary>
    /// Read <c>BLN</c> + letter + name (<c>BLNCNET</c>, <c>BLNALUX</c>) as a group bulletin. APRS12c
    /// §14 defines only a digit before a group name, and a letter with no name for announcements.
    /// Driver: net and club announcements from APRS PropView, direwolf and Microsat beacons.
    /// </summary>
    public bool AllowLetterGroupBulletin { get; init; } = true;

    /// <summary>
    /// Accept a <c>&lt;</c> station capabilities packet whose items are free text rather than
    /// <c>TOKEN</c> / <c>TOKEN=VALUE</c> (APRS12c §15), keeping each comma-separated piece as a
    /// token. Driver: TNC ID beacons, node announcements and aprsd start-up notices sent with the
    /// wrong data type identifier.
    /// </summary>
    public bool AllowFreeTextCapabilities { get; init; } = true;
}
