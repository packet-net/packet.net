using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Rhp2;

/// <summary>
/// Reads a JSON value that may arrive quoted or unquoted into a
/// <see cref="string"/> property.
/// </summary>
/// <remarks>
/// Real XRouter is inconsistent about the <c>port</c> field: TRACE-mode
/// <c>recv</c> carries it as a JSON number, DGRAM-mode <c>recv</c> and
/// <c>accept</c> carry it as a JSON string — and the PWP-0222 spec's
/// <c>accept</c> example shows a number where XRouter actually sends a
/// string. Rather than model three shapes we normalise everything to
/// <c>string?</c> at the wire boundary. On write we always emit a JSON
/// string (or null), matching what XRouter accepts on requests.
/// </remarks>
public sealed class StringOrIntConverter : JsonConverter<string?>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            // Prefer the integral reading; fall back to double for the
            // (never-observed, but legal-JSON) fractional case.
            JsonTokenType.Number when reader.TryGetInt64(out var integral) => integral.ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => throw new JsonException($"Unexpected {reader.TokenType} token for a string-or-number field."),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}
