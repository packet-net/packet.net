using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Packet.Rhp2;

/// <summary>
/// Base class for every RHPv2 message. Concrete subclasses correspond
/// one-to-one with the <c>type</c> discriminators in <see cref="RhpMessageType"/>.
/// </summary>
/// <remarks>
/// The discriminator is a constructor-set read-only property rather than
/// an abstract override so the <c>[JsonPropertyName]</c> /
/// <c>[JsonPropertyOrder]</c> attributes live on exactly one declaration —
/// System.Text.Json resolves attributes on the declaring member, and an
/// attribute-less override in a subclass would silently shed them.
/// <c>JsonPropertyOrder(-1)</c> puts <c>type</c> ahead of every
/// default-ordered (0) property, so it is always the first key in emitted
/// JSON — XRouter dispatches on it without buffering the whole object.
/// </remarks>
public abstract class RhpMessage
{
    private protected RhpMessage(string type) => Type = type;

    /// <summary>The wire <c>type</c> discriminator. Always emitted first.</summary>
    [JsonPropertyName("type")]
    [JsonPropertyOrder(-1)]
    public string Type { get; }

    /// <summary>
    /// Request correlation id. Optional: a client that omits it gets no
    /// success reply (the server then only replies on error). Echoed back
    /// verbatim on replies.
    /// </summary>
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    /// <summary>
    /// Server-assigned sequence number on asynchronous notifications
    /// (<c>recv</c>, <c>accept</c>, server-initiated <c>status</c> /
    /// <c>close</c>).
    /// </summary>
    [JsonPropertyName("seqno")]
    public int? Seqno { get; set; }
}

/// <summary>
/// Carrier for a message whose <c>type</c> we don't recognise. Holding the
/// raw JSON instead of throwing keeps the codec forward-compatible: a newer
/// XRouter can introduce message types without killing the session.
/// </summary>
public sealed class UnknownMessage : RhpMessage
{
    /// <summary>Wraps an unrecognised message, lifting <c>id</c> / <c>seqno</c> if present.</summary>
    public UnknownMessage(string type, JsonObject raw) : base(type)
    {
        ArgumentNullException.ThrowIfNull(raw);
        Raw = raw;

        // Surface the correlation fields so generic reply-matching logic
        // can still route unknown messages without poking at the JSON.
        if (raw["id"] is JsonNode idNode)
        {
            Id = idNode.GetValue<int>();
        }

        if (raw["seqno"] is JsonNode seqnoNode)
        {
            Seqno = seqnoNode.GetValue<int>();
        }
    }

    /// <summary>The complete original JSON object, untouched.</summary>
    [JsonIgnore]
    public JsonObject Raw { get; }
}
