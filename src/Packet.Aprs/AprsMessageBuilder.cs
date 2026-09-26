namespace Packet.Aprs;

/// <summary>A message to another station, from <see cref="AprsStation.Message(string, string)"/> (APRS12c §14).</summary>
public sealed class AprsMessageBuilder : AprsBuilder
{
    private readonly string addressee;
    private readonly string text;
    private string? id;
    private string? replyAck;

    internal AprsMessageBuilder(AprsStation station, string addressee, string text)
        : base(station)
    {
        ArgumentNullException.ThrowIfNull(addressee);
        ArgumentNullException.ThrowIfNull(text);
        this.addressee = addressee;
        this.text = text;
    }

    /// <summary>A message ID (1-5 letters or digits), which asks the other station to acknowledge it.</summary>
    public AprsMessageBuilder WithId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        this.id = id;
        return this;
    }

    /// <summary>
    /// Uses the reply-ack format (APRS12c §14 New Message Number Format), which needs
    /// <see cref="WithId"/>: acknowledges the other station's message <paramref name="theirId"/> in
    /// the same packet, or with none just says this station understands reply-acks.
    /// </summary>
    public AprsMessageBuilder ReplyAck(string theirId = "")
    {
        ArgumentNullException.ThrowIfNull(theirId);
        replyAck = theirId;
        return this;
    }

    /// <inheritdoc/>
    public override AprsTextMessage ToData() => new() { Addressee = addressee, Text = text, MessageId = id, ReplyAck = replyAck };
}

/// <summary>An acknowledgement or rejection, from <see cref="AprsStation.Ack(string, string)"/> or
/// <see cref="AprsStation.Reject(string, string)"/> (APRS12c §14).</summary>
public sealed class AprsAckBuilder : AprsBuilder
{
    private readonly string addressee;
    private readonly string messageId;
    private readonly bool reject;
    private string? replyAck;

    internal AprsAckBuilder(AprsStation station, string addressee, string messageId, bool reject)
        : base(station)
    {
        ArgumentNullException.ThrowIfNull(addressee);
        ArgumentNullException.ThrowIfNull(messageId);
        this.addressee = addressee;
        this.messageId = messageId;
        this.reject = reject;
    }

    /// <summary>For a message that came in reply-ack format (<c>{MM}AA</c>), echoes back its <c>AA</c> part.</summary>
    public AprsAckBuilder ReplyAck(string theirAck)
    {
        ArgumentNullException.ThrowIfNull(theirAck);
        replyAck = theirAck;
        return this;
    }

    /// <inheritdoc/>
    public override AprsMessage ToData() => reject
        ? new AprsMessageReject { Addressee = addressee, RejectedId = messageId, ReplyAck = replyAck }
        : new AprsMessageAck { Addressee = addressee, AcknowledgedId = messageId, ReplyAck = replyAck };
}

/// <summary>A status report, from <see cref="AprsStation.Status(string)"/> (APRS12c §16).</summary>
public sealed class AprsStatusBuilder : AprsBuilder
{
    private readonly string text;
    private AprsTimestamp? timestamp;
    private string? locator;
    private AprsSymbol? symbol;
    private AprsBeamHeading? beam;

    internal AprsStatusBuilder(AprsStation station, string text)
        : base(station)
    {
        ArgumentNullException.ThrowIfNull(text);
        this.text = text;
    }

    /// <summary>When the status was set, sent as day, hours and minutes UTC. Not with <see cref="Locator"/>.</summary>
    public AprsStatusBuilder Timestamp(AprsTimestamp timestamp)
    {
        this.timestamp = timestamp;
        return this;
    }

    /// <summary>When the status was set, sent as day, hours and minutes UTC (<c>DDHHMMz</c>).</summary>
    public AprsStatusBuilder Timestamp(DateTime utc) => Timestamp(AprsTimestamp.FromDateTime(utc));

    /// <summary>A 4- or 6-character Maidenhead locator and a symbol at the start, e.g. <c>IO91SX/-</c>.</summary>
    public AprsStatusBuilder Locator(string maidenheadLocator, AprsSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(maidenheadLocator);
        locator = maidenheadLocator;
        this.symbol = symbol;
        return this;
    }

    /// <summary>A meteor-scatter beam heading and power at the end (<c>^HP</c>).</summary>
    public AprsStatusBuilder Beam(AprsBeamHeading beam)
    {
        this.beam = beam;
        return this;
    }

    /// <inheritdoc/>
    public override AprsStatusReport ToData() => new()
    {
        Text = text,
        Timestamp = timestamp,
        MaidenheadLocator = locator,
        Symbol = symbol,
        BeamHeading = beam,
    };
}

/// <summary>
/// A telemetry report, from <see cref="AprsStation.Telemetry(int)"/> (APRS12c §13): five analog
/// values and eight digital bits. The channel names, units and scaling go in separate messages:
/// <see cref="AprsStation.TelemetryNames(string[])"/> and the methods after it.
/// </summary>
public sealed class AprsTelemetryBuilder : AprsBuilder
{
    private readonly string sequence;
    private decimal?[] analog = [null, null, null, null, null];
    private byte digital;
    private string comment = "";

    internal AprsTelemetryBuilder(AprsStation station, int sequence)
        : base(station)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        this.sequence = sequence.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Up to 5 analog values, A1 first; any not given are sent empty.</summary>
    public AprsTelemetryBuilder Analog(params decimal[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length > 5)
        {
            throw new ArgumentException("a telemetry report carries at most 5 analog values (APRS12c ch. 13)", nameof(values));
        }

        analog = [.. values.Select(v => (decimal?)v), .. Enumerable.Repeat<decimal?>(null, 5 - values.Length)];
        return this;
    }

    /// <summary>The 8 digital channels; bit 0 is B1.</summary>
    public AprsTelemetryBuilder Digital(byte bits)
    {
        digital = bits;
        return this;
    }

    /// <summary>The 8 digital channels as sent, B1 first, e.g. <c>"10000000"</c> for B1 on.</summary>
    public AprsTelemetryBuilder Digital(string bits)
    {
        ArgumentNullException.ThrowIfNull(bits);
        if (bits.Length != 8 || bits.Any(c => c is not ('0' or '1')))
        {
            throw new ArgumentException("the digital channels are 8 characters, each 0 or 1, B1 first", nameof(bits));
        }

        digital = 0;
        for (int i = 0; i < 8; i++)
        {
            digital |= (byte)(bits[i] == '1' ? 1 << i : 0);
        }

        return this;
    }

    /// <summary>Free text after the values.</summary>
    public AprsTelemetryBuilder Comment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        comment = text;
        return this;
    }

    /// <inheritdoc/>
    public override AprsTelemetryReport ToData() => new() { Sequence = sequence, Analog = analog, Digital = digital, Comment = comment };
}
