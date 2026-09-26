using System.Text.Json.Nodes;

namespace Packet.Aprs.Tests.Vectors;

/// <summary>
/// Builds Packet.Aprs data from the neutral form, for encode cases. Covers the types and fields
/// the encode cases use so far, and throws for anything else rather than ignore a field, so a
/// case can never pass because part of it was skipped.
/// </summary>
internal static class NeutralReader
{
    public static AprsData Data(JsonObject d)
    {
        string type = (string)d["type"]!;
        return type switch
        {
            "position" => Position(d),
            "message" => Message(d),
            _ => throw new NotSupportedException($"the vector runner cannot build '{type}' data yet"),
        };
    }

    private static AprsPositionReport Position(JsonObject d)
    {
        Only(d, "type", "messaging", "latitude", "longitude", "ambiguity", "symbol", "compressed", "course_degrees", "speed_knots", "altitude_feet", "comment");
        return new AprsPositionReport
        {
            MessagingCapable = (bool?)d["messaging"] ?? false,
            Position = new AprsPosition((double)d["latitude"]!, (double)d["longitude"]!, (int?)d["ambiguity"] ?? 0),
            Symbol = AprsSymbol.Parse((string)d["symbol"]!),
            IsCompressed = (bool?)d["compressed"] ?? false,
            CourseDegrees = (int?)d["course_degrees"],
            SpeedKnots = (double?)d["speed_knots"],
            AltitudeFeet = (double?)d["altitude_feet"],
            Comment = (string?)d["comment"] ?? "",
        };
    }

    private static AprsTextMessage Message(JsonObject d)
    {
        Only(d, "type", "addressee", "text", "message_id", "reply_ack");
        return new AprsTextMessage
        {
            Addressee = (string)d["addressee"]!,
            Text = (string?)d["text"] ?? "",
            MessageId = (string?)d["message_id"],
            ReplyAck = (string?)d["reply_ack"],
        };
    }

    private static void Only(JsonObject d, params string[] known)
    {
        string[] unknown = [.. d.Select(kv => kv.Key).Except(known)];
        if (unknown.Length > 0)
        {
            throw new NotSupportedException($"the vector runner cannot build {string.Join(", ", unknown)} for '{(string?)d["type"]}' yet");
        }
    }
}
