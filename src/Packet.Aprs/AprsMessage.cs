using Packet.Aprs.Internal;

namespace Packet.Aprs;

/// <summary>
/// Anything sent in APRS message format: data type identifier <c>:</c>, a 9-character addressee,
/// <c>:</c>, then text (APRS12c §14). Besides person-to-person messages this carries acks and
/// rejects, bulletins and announcements, NWS bulletins, telemetry metadata and directed queries,
/// each with its own subtype.
/// </summary>
public abstract record AprsMessage : AprsData
{
    private protected AprsMessage()
    {
    }

    /// <summary>
    /// Who it is for, without the padding: usually a callsign-SSID, but also a service
    /// (<c>WHO-IS</c>, <c>EMAIL</c>), a bulletin address (<c>BLN3</c>) or <c>NWS-WARN</c>. 1-9
    /// printable characters; APRS-IS names need not be AX.25-valid (UAP §2.5.2).
    /// </summary>
    public required string Addressee { get; init; }

    /// <summary>
    /// The message identifier after <c>{</c> (1-5 letters or digits). A text message with an ID asks
    /// for an acknowledgement. Null when there is none. Not used on acks, rejects and queries.
    /// </summary>
    public string? MessageId { get; init; }

    /// <inheritdoc/>
    public override char DataTypeIdentifier => ':';

    internal override void Encode(InfoWriter writer)
    {
        MessageCodec.ValidateAddressee(Addressee);
        writer.Char(':').Ascii(Addressee.PadRight(9)).Char(':');
        EncodeText(writer);
    }

    private protected abstract void EncodeText(InfoWriter writer);

    internal void WriteMessageId(InfoWriter writer, string? replyAck = null)
    {
        if (MessageId is null)
        {
            if (replyAck is not null)
            {
                throw new ArgumentException("a reply-ack needs a message ID", nameof(replyAck));
            }

            return;
        }

        MessageCodec.ValidateId(MessageId, nameof(MessageId));
        writer.Char('{').Ascii(MessageId);
        if (replyAck is not null)
        {
            if (replyAck.Length > 0)
            {
                MessageCodec.ValidateId(replyAck, nameof(replyAck));
            }

            writer.Char('}').Ascii(replyAck);
        }
    }
}

/// <summary>A person-to-person (or person-to-service) text message (APRS12c §14 Messages).</summary>
public sealed record AprsTextMessage : AprsMessage
{
    /// <summary>The message text: printable ASCII or UTF-8, no <c>{</c>. The spec limit is 67
    /// characters; longer text is accepted when decoding, as APRS12c asks, but not encoded.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// The reply-ack (APRS 1.1, APRS12c §14 New Message Number Format): with <see cref="AprsMessage.MessageId"/>
    /// set, a non-null value sends <c>{MM}AA</c>, acknowledging the other station's message AA in
    /// the same packet; an empty string sends <c>{MM}</c>, which just says "I understand reply-acks".
    /// Null for the original format.
    /// </summary>
    public string? ReplyAck { get; init; }

    /// <summary>True when the sender wants an acknowledgement (the message has an ID).</summary>
    public bool RequestsAck => MessageId is not null;

    private protected override void EncodeText(InfoWriter writer)
    {
        MessageCodec.ValidateText(Text, nameof(Text));
        writer.Utf8(Text);
        WriteMessageId(writer, ReplyAck);
    }
}

/// <summary>An acknowledgement: <c>ack</c> followed by the message ID being acknowledged (APRS12c §14).</summary>
public sealed record AprsMessageAck : AprsMessage
{
    /// <summary>The ID of the message being acknowledged.</summary>
    public required string AcknowledgedId { get; init; }

    /// <summary>For an ack of a reply-ack format ID <c>{MM}AA</c>, the <c>AA</c> part echoed back
    /// (<c>ackMM}AA</c>); match on <see cref="AcknowledgedId"/>. Null for a plain ack.</summary>
    public string? ReplyAck { get; init; }

    private protected override void EncodeText(InfoWriter writer)
    {
        if (MessageId is not null)
        {
            throw new ArgumentException("an ack must not carry its own message ID (UAP 5.32)", nameof(MessageId));
        }

        MessageCodec.ValidateId(AcknowledgedId, nameof(AcknowledgedId));
        writer.Ascii("ack").Ascii(AcknowledgedId);
        if (ReplyAck is not null)
        {
            writer.Char('}').Ascii(ReplyAck);
        }
    }
}

/// <summary>A rejection: <c>rej</c> followed by the message ID the station cannot accept (APRS12c §14).</summary>
public sealed record AprsMessageReject : AprsMessage
{
    /// <summary>The ID of the message being rejected.</summary>
    public required string RejectedId { get; init; }

    /// <summary>As for <see cref="AprsMessageAck.ReplyAck"/>.</summary>
    public string? ReplyAck { get; init; }

    private protected override void EncodeText(InfoWriter writer)
    {
        if (MessageId is not null)
        {
            throw new ArgumentException("a reject must not carry its own message ID", nameof(MessageId));
        }

        MessageCodec.ValidateId(RejectedId, nameof(RejectedId));
        writer.Ascii("rej").Ascii(RejectedId);
        if (ReplyAck is not null)
        {
            writer.Char('}').Ascii(ReplyAck);
        }
    }
}

/// <summary>Which kind of bulletin (APRS12c §14).</summary>
public enum AprsBulletinKind
{
    /// <summary><c>BLN0</c>-<c>BLN9</c>: a general bulletin.</summary>
    General,

    /// <summary><c>BLNA</c>-<c>BLNZ</c>: an announcement.</summary>
    Announcement,

    /// <summary><c>BLNnGROUP</c>: a bulletin to a named group.</summary>
    Group,
}

/// <summary>
/// A bulletin, announcement or group bulletin: a message whose addressee is <c>BLN</c> followed by
/// an identifier and optionally a group name (APRS12c §14). Not acknowledged.
/// </summary>
public sealed record AprsBulletin : AprsMessage
{
    /// <summary>The bulletin text: printable ASCII or UTF-8.</summary>
    public required string Text { get; init; }

    /// <summary>The line identifier: a digit for bulletins and group bulletins, a capital letter for announcements.</summary>
    public char Id => Addressee.Length > 3 ? Addressee[3] : '\0';

    /// <summary>The group name (1-5 characters) for a group bulletin, otherwise null.</summary>
    public string? GroupName => Addressee.Length > 4 ? Addressee[4..] : null;

    /// <summary>General bulletin, announcement or group bulletin.</summary>
    public AprsBulletinKind Kind => GroupName is not null ? AprsBulletinKind.Group
        : char.IsAsciiLetterUpper(Id) ? AprsBulletinKind.Announcement
        : AprsBulletinKind.General;

    /// <summary>A general bulletin (<paramref name="id"/> 0-9) or announcement (A-Z).</summary>
    public static AprsBulletin Create(char id, string text) => new() { Addressee = $"BLN{id}", Text = text };

    /// <summary>A group bulletin, e.g. <c>BLN4WX</c>.</summary>
    public static AprsBulletin CreateGroup(char id, string group, string text) => new() { Addressee = $"BLN{id}{group}", Text = text };

    private protected override void EncodeText(InfoWriter writer)
    {
        if (!MessageCodec.IsBulletinAddressee(Addressee))
        {
            throw new ArgumentException("a bulletin addressee is BLN, then 0-9 or A-Z, then an optional 1-5 character group name", nameof(Addressee));
        }

        if (GroupName is not null && !char.IsAsciiDigit(Id))
        {
            throw new ArgumentException("a group bulletin identifier is a digit (APRS12c ch. 14)", nameof(Addressee));
        }

        MessageCodec.ValidateText(Text, nameof(Text));
        writer.Utf8(Text);
        WriteMessageId(writer);
    }
}

/// <summary>
/// A US National Weather Service bulletin: a message addressed <c>NWS-xxxxx</c> (xxxxx is the
/// severity, e.g. WARN, WATCH, ADVIS), usually relayed from APRS-IS (APRS12c §14). Receivers do
/// not acknowledge it; any message ID is only a reference. The text layout is defined by
/// aprs-is.net/wx and is not parsed further.
/// </summary>
public sealed record AprsNwsBulletin : AprsMessage
{
    /// <summary>The bulletin text, e.g. <c>092010z,THUNDER_STORM,AR_ASHLEY</c>.</summary>
    public required string Text { get; init; }

    /// <summary>The severity after <c>NWS-</c> or <c>NWS_</c>, e.g. <c>WARN</c>.</summary>
    public string Severity => Addressee.Length > 4 ? Addressee[4..] : "";

    private protected override void EncodeText(InfoWriter writer)
    {
        if (!Addressee.StartsWith("NWS", StringComparison.Ordinal))
        {
            throw new ArgumentException("an NWS bulletin addressee starts NWS", nameof(Addressee));
        }

        Internal.Text.RequireNoLineBreaks(Text, nameof(Text));
        writer.Utf8(Text);
        WriteMessageId(writer);
    }
}

/// <summary>
/// A directed query: a message whose text is <c>?</c> and an upper-case query type, optionally
/// followed by a callsign (APRS12c §15). Never has a message ID and is never acknowledged.
/// </summary>
public sealed record AprsDirectedQuery : AprsMessage
{
    /// <summary>The query type: <c>APRSD</c>, <c>APRSH</c>, <c>APRSM</c>, <c>APRSO</c>, <c>APRSP</c>,
    /// <c>APRSS</c>, <c>APRST</c> or <c>PING?</c>. Unrecognised types should be ignored by the recipient.</summary>
    public required string QueryType { get; init; }

    /// <summary>The station asked about, for <c>APRSH</c>; otherwise null.</summary>
    public string? Target { get; init; }

    /// <summary>The query types APRS12c §15 defines for directed queries.</summary>
    public static IReadOnlyList<string> KnownTypes { get; } = ["APRSD", "APRSH", "APRSM", "APRSO", "APRSP", "APRSS", "APRST", "PING?"];

    private protected override void EncodeText(InfoWriter writer)
    {
        if (MessageId is not null)
        {
            throw new ArgumentException("a directed query never has a message ID (APRS12c ch. 15)", nameof(MessageId));
        }

        if (QueryType.Length == 0 || QueryType.Any(c => c is < '!' or > '~' or '{' || char.IsAsciiLetterLower(c)))
        {
            throw new ArgumentException("query type is upper case printable ASCII (APRS12c ch. 15)", nameof(QueryType));
        }

        writer.Char('?').Ascii(QueryType);
        if (Target is not null)
        {
            if (Target.Length is < 1 or > 9 || Target.Any(c => c is < '!' or > '~'))
            {
                throw new ArgumentException("query target is 1-9 printable characters", nameof(Target));
            }

            writer.Ascii(Target);
        }
    }
}
