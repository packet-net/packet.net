using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Packet.Rhp2;

/// <summary>
/// JSON codec for RHPv2 messages: strongly-typed <see cref="RhpMessage"/>
/// DTOs to/from the UTF-8 JSON payloads carried by <see cref="RhpFraming"/>.
/// </summary>
/// <remarks>
/// Two wire realities shape this codec (both pinned against real XRouter):
/// <list type="bullet">
/// <item><description>
/// Replies carry <c>errCode</c> / <c>errText</c> with capital C / T on
/// EVERY reply type — the published spec implies lowercase except on
/// <c>authReply</c>, but XRouter capitalises throughout. We emit the
/// capitalised form (so our output is byte-compatible with XRouter's) and
/// read case-insensitively (so the spec's lowercase form still parses).
/// </description></item>
/// <item><description>
/// Unrecognised <c>type</c> values must not kill the session — they map to
/// <see cref="UnknownMessage"/> so a newer XRouter can add messages freely.
/// Only a missing (or non-string) <c>type</c> is a protocol violation.
/// </description></item>
/// </list>
/// </remarks>
public static class RhpJson
{
    // WhenWritingNull makes every nullable DTO property an omit-when-null
    // wire field, matching XRouter, which never writes JSON nulls.
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    /// <summary>
    /// Serializes a message to its UTF-8 JSON wire form. The <c>type</c>
    /// discriminator is always the first key in the emitted object.
    /// </summary>
    public static byte[] Serialize<T>(T message) where T : RhpMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        // Serialize as the runtime type: the declared type may be the
        // abstract base (e.g. a heterogeneous outbound queue), and STJ
        // would otherwise emit only the base-class properties.
        return JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Options);
    }

    /// <summary>
    /// Parses a UTF-8 JSON frame payload into the concrete
    /// <see cref="RhpMessage"/> subclass selected by its <c>type</c> field.
    /// </summary>
    /// <returns>
    /// The typed message; an <see cref="UnknownMessage"/> carrying the raw
    /// JSON when the <c>type</c> value isn't recognised.
    /// </returns>
    /// <exception cref="RhpProtocolException">
    /// The payload is not a JSON object, or has no string <c>type</c> field.
    /// </exception>
    public static RhpMessage Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        // Parse to a node tree first: we need the discriminator before we
        // can pick a DTO, and unknown types keep the whole tree anyway.
        if (JsonNode.Parse(utf8Json) is not JsonObject obj)
        {
            throw new RhpProtocolException("RHP message is not a JSON object.");
        }

        if (obj["type"] is not JsonNode typeNode)
        {
            throw new RhpProtocolException("RHP message has no 'type' field.");
        }

        if (typeNode.GetValueKind() != JsonValueKind.String)
        {
            throw new RhpProtocolException("RHP message 'type' field is not a string.");
        }

        var type = typeNode.GetValue<string>();
        return type switch
        {
            RhpMessageType.Auth => To<AuthMessage>(obj),
            RhpMessageType.AuthReply => To<AuthReplyMessage>(obj),
            RhpMessageType.Open => To<OpenMessage>(obj),
            RhpMessageType.OpenReply => To<OpenReplyMessage>(obj),
            RhpMessageType.Socket => To<SocketMessage>(obj),
            RhpMessageType.SocketReply => To<SocketReplyMessage>(obj),
            RhpMessageType.Bind => To<BindMessage>(obj),
            RhpMessageType.BindReply => To<BindReplyMessage>(obj),
            RhpMessageType.Listen => To<ListenMessage>(obj),
            RhpMessageType.ListenReply => To<ListenReplyMessage>(obj),
            RhpMessageType.Connect => To<ConnectMessage>(obj),
            RhpMessageType.ConnectReply => To<ConnectReplyMessage>(obj),
            // The spec's connect example writes "ConnectReply" (PascalCase
            // typo); accept it on read, though we only ever emit camelCase.
            "ConnectReply" => To<ConnectReplyMessage>(obj),
            RhpMessageType.Send => To<SendMessage>(obj),
            RhpMessageType.SendReply => To<SendReplyMessage>(obj),
            RhpMessageType.SendTo => To<SendToMessage>(obj),
            RhpMessageType.SendToReply => To<SendToReplyMessage>(obj),
            RhpMessageType.Recv => To<RecvMessage>(obj),
            RhpMessageType.Accept => To<AcceptMessage>(obj),
            RhpMessageType.Status => To<StatusMessage>(obj),
            RhpMessageType.StatusReply => To<StatusReplyMessage>(obj),
            RhpMessageType.Close => To<CloseMessage>(obj),
            RhpMessageType.CloseReply => To<CloseReplyMessage>(obj),
            _ => new UnknownMessage(type, obj),
        };
    }

    private static T To<T>(JsonObject obj) where T : RhpMessage
        => obj.Deserialize<T>(Options)
           ?? throw new RhpProtocolException($"RHP message failed to bind as {typeof(T).Name}.");
}
